using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PrzepustnicaService;

// The 1 Hz heart of Etap 1: joins ETW per-PID byte counts with the process
// catalog and the rule set, persists per-minute usage, and broadcasts the
// "usage" telemetry message to all IPC clients.
public sealed class TelemetryLoop : BackgroundService
{
    private readonly EtwTrafficCounter _etw;
    private readonly ProcessCatalog _catalog;
    private readonly Database _db;
    private readonly IpcServer _ipc;
    private readonly Throttler _throttler;
    private readonly ILogger<TelemetryLoop> _logger;

    private long _todayBytes;
    private DateTime _todayBytesDay;
    private readonly HashSet<string> _throttledLastTick = new();

    public TelemetryLoop(EtwTrafficCounter etw, ProcessCatalog catalog, Database db, IpcServer ipc,
        Throttler throttler, ILogger<TelemetryLoop> logger)
    {
        _etw = etw;
        _catalog = catalog;
        _db = db;
        _ipc = ipc;
        _throttler = throttler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _todayBytes = _db.GetTodayBytes();
        _todayBytesDay = DateTime.Today;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    Tick(out var usageMessage);
                    _ipc.Broadcast(usageMessage);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Telemetry tick failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Tick(out object usageMessage)
    {
        var snapshot = _etw.TakeSnapshot();
        var pidToExe = _catalog.PidToExe;
        var rules = _db.GetRules();
        var now = DateTime.Now;
        var minuteUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;

        if (DateTime.Today != _todayBytesDay)
        {
            _todayBytes = 0;
            _todayBytesDay = DateTime.Today;
        }

        // Aggregate this second's bytes per exe name once, then match rules.
        // The ETW process rundown is the primary PID -> exe source (it also
        // knows short-lived processes); the polled catalog is the fallback.
        var byExe = new Dictionary<string, (long Down, long Up)>();
        foreach (var (pid, bytes) in snapshot)
        {
            if (!_etw.TryGetExe(pid, out var exe) && !pidToExe.TryGetValue(pid, out exe!)) continue;
            byExe[exe] = byExe.TryGetValue(exe, out var acc)
                ? (acc.Down + bytes.Down, acc.Up + bytes.Up)
                : (bytes.Down, bytes.Up);
        }

        var throttleStats = _throttler.TakeStats();
        var apps = new Dictionary<string, object>();
        double totalMbps = 0;

        foreach (var rule in rules)
        {
            var exe = ProcessCatalog.NormalizeExe(rule.ExeMatch);
            byExe.TryGetValue(exe, out var bytes);

            var mbps = (bytes.Down + bytes.Up) * 8 / 1_000_000.0;
            var withinWindow = ScheduleLogic.IsWithin(rule.Schedule, now);
            throttleStats.TryGetValue(rule.Id, out var stats);

            // "throttled" comes from the enforcement path (packets actually
            // delayed this second); the >=95%-of-limit heuristic remains only
            // for the degraded no-WinDivert mode.
            string status;
            if (!rule.Enabled || !withinWindow) status = "sleeping";
            else if (stats is { DelayedBytes: > 0 }) status = "throttled";
            else if (!_throttler.IsEnforcing && rule.LimitMbps is { } limit && mbps >= limit * 0.95) status = "throttled";
            else status = "active";

            if (stats is not null)
            {
                _logger.LogDebug(
                    "throttle {Rule}: etw {EtwMbps:F1} Mb/s | matched passed {Passed:F1} + delayed {Delayed:F1} Mb/s | dropped {Dropped:F1} Mb/s",
                    rule.Id, mbps, stats.PassedBytes * 8 / 1e6, stats.DelayedBytes * 8 / 1e6, stats.DroppedBytes * 8 / 1e6);
            }

            apps[rule.Id] = new
            {
                mbps,
                downMbps = bytes.Down * 8 / 1_000_000.0,
                upMbps = bytes.Up * 8 / 1_000_000.0,
                throttled = status == "throttled",
                status,
            };
            totalMbps += mbps;

            // "Saved" is the plan's demand-vs-passed estimate: bytes that had
            // to wait in the pacer plus bytes dropped outside the window.
            var savedBytes = stats is null ? 0 : stats.DelayedBytes + stats.DroppedBytes;
            var isThrottling = status == "throttled" || (stats?.DroppedBytes ?? 0) > 0;
            var throttleEvents = isThrottling && !_throttledLastTick.Contains(rule.Id) ? 1 : 0;
            if (isThrottling) _throttledLastTick.Add(rule.Id);
            else _throttledLastTick.Remove(rule.Id);

            if (bytes.Down > 0 || bytes.Up > 0 || savedBytes > 0 || throttleEvents > 0)
            {
                _db.AddUsage(rule.Id, minuteUtc, bytes.Down, bytes.Up,
                    isThrottling ? 1 : 0, savedBytes, throttleEvents);
                _todayBytes += bytes.Down + bytes.Up;
            }
        }

        usageMessage = new
        {
            type = "usage",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            apps,
            totalMbps,
            todayGb = _todayBytes / 1_000_000_000.0,
        };
    }
}
