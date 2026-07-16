using System.Text.Json;

namespace PrzepustnicaService;

// Mirrors ui/src/types.ts — the IPC wire format uses camelCase JSON of these shapes.
public sealed class Schedule
{
    public string Mode { get; set; } = "always"; // "always" | "hours"
    public string From { get; set; } = "00:00";
    public string To { get; set; } = "00:00";
}

public sealed class AppRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ExeMatch { get; set; } = "";
    public string IconColor { get; set; } = "";
    public string Initials { get; set; } = "";
    public double? LimitMbps { get; set; }
    public Schedule Schedule { get; set; } = new();
    public bool Enabled { get; set; } = true;
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public static class ScheduleLogic
{
    // Mirrors ui/src/schedule.ts isWithinSchedule, including the overnight
    // (from > to) window wrap.
    public static bool IsWithin(Schedule schedule, DateTime now)
    {
        if (schedule.Mode == "always") return true;

        var nowMinutes = now.Hour * 60 + now.Minute;
        var from = ToMinutes(schedule.From);
        var to = ToMinutes(schedule.To);

        if (from <= to) return nowMinutes >= from && nowMinutes < to;
        return nowMinutes >= from || nowMinutes < to;
    }

    private static int ToMinutes(string hhmm)
    {
        var parts = hhmm.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m))
            return 0;
        return h * 60 + m;
    }
}
