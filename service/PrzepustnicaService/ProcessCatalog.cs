using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PrzepustnicaService;

public sealed record ProcessInfo(string Name, string ExeMatch, string? Path, int PidCount);

// Periodically refreshed PID -> executable-name map. Rules match on the
// executable name ("chrome.exe"), which also covers multi-process apps:
// every chrome.exe child shares the name, so it aggregates naturally.
public sealed class ProcessCatalog : BackgroundService
{
    private readonly ILogger<ProcessCatalog> _logger;
    private volatile Dictionary<int, string> _pidToExe = new();

    public ProcessCatalog(ILogger<ProcessCatalog> logger) => _logger = logger;

    /// <summary>Current PID -> normalized exe name ("chrome.exe", lowercase).</summary>
    public IReadOnlyDictionary<int, string> PidToExe => _pidToExe;

    public static string NormalizeExe(string exeMatch)
    {
        var name = exeMatch.Trim().ToLowerInvariant();
        return name.EndsWith(".exe") ? name : name + ".exe";
    }

    /// <summary>Distinct running processes for the "Add application" picker.</summary>
    public List<ProcessInfo> ListProcesses(IReadOnlyCollection<int> pidsWithTraffic)
    {
        var byExe = new Dictionary<string, (string Name, string? Path, int Count, bool Traffic)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id <= 4) continue; // Idle/System pseudo-processes
                var exe = process.ProcessName.ToLowerInvariant() + ".exe";
                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    // Access denied for protected processes — name-only entry is fine.
                }

                var traffic = pidsWithTraffic.Contains(process.Id);
                if (byExe.TryGetValue(exe, out var existing))
                {
                    byExe[exe] = (existing.Name, existing.Path ?? path, existing.Count + 1, existing.Traffic || traffic);
                }
                else
                {
                    byExe[exe] = (process.ProcessName, path, 1, traffic);
                }
            }
        }

        return byExe
            .OrderByDescending(e => e.Value.Traffic)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => new ProcessInfo(e.Value.Name, e.Key, e.Value.Path, e.Value.Count))
            .ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        Refresh();
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Refresh();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Refresh()
    {
        try
        {
            var map = new Dictionary<int, string>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    map[process.Id] = process.ProcessName.ToLowerInvariant() + ".exe";
                }
            }
            _pidToExe = map;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Process list refresh failed");
        }
    }
}
