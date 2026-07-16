using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace PrzepustnicaService;

public sealed class AppSettings
{
    public bool EnforcementEnabled { get; set; } = true;
    public double LinkCapacityMbps { get; set; } = 100;
}

public sealed record HistoryResult(
    string Period, List<string> Labels, Dictionary<string, double[]> SeriesGb,
    double SavedGb, long ThrottleEvents);

// Rules + usage persistence (PLAN_WDROZENIA_WINDOWS.md §2: SQLite).
// Rules are cached in memory; usage is aggregated into per-minute rows.
public sealed class Database : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();
    private List<AppRule> _rules;
    private AppSettings _settings = new();

    public Database(ILogger<Database> logger)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Przepustnica");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "przepustnica.db");

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("""
            CREATE TABLE IF NOT EXISTS rules(
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                exe_match TEXT NOT NULL,
                icon_color TEXT NOT NULL,
                initials TEXT NOT NULL,
                limit_mbps REAL NULL,
                schedule_mode TEXT NOT NULL,
                schedule_from TEXT NOT NULL,
                schedule_to TEXT NOT NULL,
                enabled INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS usage_minutes(
                rule_id TEXT NOT NULL,
                minute_utc INTEGER NOT NULL,
                bytes_down INTEGER NOT NULL DEFAULT 0,
                bytes_up INTEGER NOT NULL DEFAULT 0,
                throttled_seconds INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(rule_id, minute_utc)
            );
            CREATE TABLE IF NOT EXISTS settings(
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
        Migrate();
        _settings = LoadSettings();

        _rules = LoadRules();
        logger.LogInformation("Database open at {Path}, {Count} rule(s)", dbPath, _rules.Count);
    }

    public List<AppRule> GetRules()
    {
        lock (_lock) return _rules.Select(Clone).ToList();
    }

    public void UpsertRule(AppRule rule)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO rules(id, name, exe_match, icon_color, initials, limit_mbps,
                                  schedule_mode, schedule_from, schedule_to, enabled)
                VALUES (@id, @name, @exe, @color, @initials, @limit, @mode, @from, @to, @enabled)
                ON CONFLICT(id) DO UPDATE SET
                    name=@name, exe_match=@exe, icon_color=@color, initials=@initials,
                    limit_mbps=@limit, schedule_mode=@mode, schedule_from=@from,
                    schedule_to=@to, enabled=@enabled;
                """;
            cmd.Parameters.AddWithValue("@id", rule.Id);
            cmd.Parameters.AddWithValue("@name", rule.Name);
            cmd.Parameters.AddWithValue("@exe", rule.ExeMatch);
            cmd.Parameters.AddWithValue("@color", rule.IconColor);
            cmd.Parameters.AddWithValue("@initials", rule.Initials);
            cmd.Parameters.AddWithValue("@limit", (object?)rule.LimitMbps ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mode", rule.Schedule.Mode);
            cmd.Parameters.AddWithValue("@from", rule.Schedule.From);
            cmd.Parameters.AddWithValue("@to", rule.Schedule.To);
            cmd.Parameters.AddWithValue("@enabled", rule.Enabled ? 1 : 0);
            cmd.ExecuteNonQuery();

            var index = _rules.FindIndex(r => r.Id == rule.Id);
            if (index >= 0) _rules[index] = Clone(rule);
            else _rules.Add(Clone(rule));
        }
    }

    public bool DeleteRule(string id)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM rules WHERE id=@id;";
            cmd.Parameters.AddWithValue("@id", id);
            var removed = cmd.ExecuteNonQuery() > 0;
            _rules.RemoveAll(r => r.Id == id);
            return removed;
        }
    }

    public AppRule? ToggleRule(string id)
    {
        lock (_lock)
        {
            var rule = _rules.FirstOrDefault(r => r.Id == id);
            if (rule is null) return null;
            rule.Enabled = !rule.Enabled;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE rules SET enabled=@enabled WHERE id=@id;";
            cmd.Parameters.AddWithValue("@enabled", rule.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
            return Clone(rule);
        }
    }

    private void Migrate()
    {
        var columns = new List<string>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(usage_minutes);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        if (!columns.Contains("saved_bytes"))
            Execute("ALTER TABLE usage_minutes ADD COLUMN saved_bytes INTEGER NOT NULL DEFAULT 0;");
        if (!columns.Contains("throttle_events"))
            Execute("ALTER TABLE usage_minutes ADD COLUMN throttle_events INTEGER NOT NULL DEFAULT 0;");
    }

    public void AddUsage(string ruleId, long minuteUtc, long bytesDown, long bytesUp,
        int throttledSeconds, long savedBytes, int throttleEvents)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO usage_minutes(rule_id, minute_utc, bytes_down, bytes_up, throttled_seconds, saved_bytes, throttle_events)
                VALUES (@rule, @minute, @down, @up, @throttled, @saved, @events)
                ON CONFLICT(rule_id, minute_utc) DO UPDATE SET
                    bytes_down = bytes_down + @down,
                    bytes_up = bytes_up + @up,
                    throttled_seconds = throttled_seconds + @throttled,
                    saved_bytes = saved_bytes + @saved,
                    throttle_events = throttle_events + @events;
                """;
            cmd.Parameters.AddWithValue("@rule", ruleId);
            cmd.Parameters.AddWithValue("@minute", minuteUtc);
            cmd.Parameters.AddWithValue("@down", bytesDown);
            cmd.Parameters.AddWithValue("@up", bytesUp);
            cmd.Parameters.AddWithValue("@throttled", throttledSeconds);
            cmd.Parameters.AddWithValue("@saved", savedBytes);
            cmd.Parameters.AddWithValue("@events", throttleEvents);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Total bytes (down+up) across all rules since local midnight.</summary>
    public long GetTodayBytes()
    {
        var midnightUtc = new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds() / 60;
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT COALESCE(SUM(bytes_down + bytes_up), 0) FROM usage_minutes WHERE minute_utc >= @from;";
            cmd.Parameters.AddWithValue("@from", midnightUtc);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public HistoryResult GetHistory(string period)
    {
        var today = DateTime.Today;
        (DateTime fromLocal, int buckets, Func<DateTime, int> bucketOf, Func<int, string> labelOf) = period switch
        {
            "day" => (today, 24, (Func<DateTime, int>)(t => t.Hour),
                      (Func<int, string>)(i => $"{i:00}")),
            "month" => (today.AddDays(-29), 30, t => (int)(t.Date - today.AddDays(-29)).TotalDays,
                        i => today.AddDays(-29 + i).ToString("dd.MM")),
            _ => (today.AddDays(-6), 7, t => (int)(t.Date - today.AddDays(-6)).TotalDays,
                 i => today.AddDays(-6 + i).ToString("ddd", new System.Globalization.CultureInfo("pl-PL"))),
        };

        var fromMinuteUtc = new DateTimeOffset(fromLocal).ToUnixTimeSeconds() / 60;
        var series = new Dictionary<string, double[]>();
        long savedBytes = 0;
        long throttleEvents = 0;

        lock (_lock)
        {
            foreach (var rule in _rules) series[rule.Id] = new double[buckets];

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT rule_id, minute_utc, bytes_down + bytes_up, saved_bytes, throttle_events
                FROM usage_minutes WHERE minute_utc >= @from;
                """;
            cmd.Parameters.AddWithValue("@from", fromMinuteUtc);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var ruleId = reader.GetString(0);
                var localTime = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1) * 60).LocalDateTime;
                var bytes = reader.GetInt64(2);

                var bucket = bucketOf(localTime);
                if (bucket < 0 || bucket >= buckets) continue;

                savedBytes += reader.GetInt64(3);
                throttleEvents += reader.GetInt64(4);

                if (!series.TryGetValue(ruleId, out var arr))
                {
                    // Usage rows may outlive a deleted rule — skip them.
                    continue;
                }
                arr[bucket] += bytes / 1_000_000_000.0;
            }
        }

        var labels = Enumerable.Range(0, buckets).Select(labelOf).ToList();
        return new HistoryResult(period, labels, series, savedBytes / 1_000_000_000.0, throttleEvents);
    }

    public AppSettings GetSettings()
    {
        lock (_lock)
        {
            return new AppSettings
            {
                EnforcementEnabled = _settings.EnforcementEnabled,
                LinkCapacityMbps = _settings.LinkCapacityMbps,
            };
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO settings(key, value) VALUES
                    ('enforcementEnabled', @enabled), ('linkCapacityMbps', @capacity)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            cmd.Parameters.AddWithValue("@enabled", settings.EnforcementEnabled ? "1" : "0");
            cmd.Parameters.AddWithValue("@capacity", settings.LinkCapacityMbps.ToString(System.Globalization.CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
            _settings = settings;
        }
    }

    private AppSettings LoadSettings()
    {
        var settings = new AppSettings();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM settings;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var value = reader.GetString(1);
            switch (reader.GetString(0))
            {
                case "enforcementEnabled":
                    settings.EnforcementEnabled = value == "1";
                    break;
                case "linkCapacityMbps":
                    if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var capacity) && capacity > 0)
                    {
                        settings.LinkCapacityMbps = capacity;
                    }
                    break;
            }
        }
        return settings;
    }

    private List<AppRule> LoadRules()
    {
        var rules = new List<AppRule>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, exe_match, icon_color, initials, limit_mbps,
                   schedule_mode, schedule_from, schedule_to, enabled
            FROM rules;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rules.Add(new AppRule
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                ExeMatch = reader.GetString(2),
                IconColor = reader.GetString(3),
                Initials = reader.GetString(4),
                LimitMbps = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                Schedule = new Schedule
                {
                    Mode = reader.GetString(6),
                    From = reader.GetString(7),
                    To = reader.GetString(8),
                },
                Enabled = reader.GetInt32(9) != 0,
            });
        }
        return rules;
    }

    private static AppRule Clone(AppRule rule) => new()
    {
        Id = rule.Id,
        Name = rule.Name,
        ExeMatch = rule.ExeMatch,
        IconColor = rule.IconColor,
        Initials = rule.Initials,
        LimitMbps = rule.LimitMbps,
        Schedule = new Schedule { Mode = rule.Schedule.Mode, From = rule.Schedule.From, To = rule.Schedule.To },
        Enabled = rule.Enabled,
    };

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
