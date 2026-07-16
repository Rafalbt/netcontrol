using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PrzepustnicaService;

// Per-PID network byte counter fed by the kernel TCP/IP ETW provider
// (Microsoft-Windows-Kernel-Network), per PLAN_WDROZENIA_WINDOWS.md Etap 1.
// Requires elevation — a kernel trace session cannot be opened otherwise.
public sealed class EtwTrafficCounter : BackgroundService
{
    private const string SessionName = "PrzepustnicaKernelNetwork";

    private sealed class Counters
    {
        public long Down;
        public long Up;
    }

    private readonly ConcurrentDictionary<int, Counters> _byPid = new();
    // PID -> exe name fed by kernel process events (DCStart delivers a rundown
    // of already-running processes when the session starts). Unlike a polled
    // process list this also catches processes too short-lived to be seen by
    // a 2 s refresh — their traffic would otherwise be silently dropped.
    private readonly ConcurrentDictionary<int, string> _pidExe = new();
    private readonly ConcurrentDictionary<int, DateTime> _exitedAt = new();
    private readonly ILogger<EtwTrafficCounter> _logger;
    private TraceEventSession? _session;

    public EtwTrafficCounter(ILogger<EtwTrafficCounter> logger) => _logger = logger;

    /// <summary>PIDs that produced any traffic since the counter started (for the process picker).</summary>
    public IReadOnlyCollection<int> ActivePids => (IReadOnlyCollection<int>)_byPid.Keys;

    /// <summary>Exe name ("curl.exe", lowercase) for a PID as reported by kernel process events.</summary>
    public bool TryGetExe(int pid, out string exe) => _pidExe.TryGetValue(pid, out exe!);

    /// <summary>Live PIDs whose executable matches the given normalized exe name.</summary>
    public List<uint> GetPidsForExe(string exe)
    {
        var pids = new List<uint>();
        foreach (var (pid, name) in _pidExe)
        {
            if (name == exe && !_exitedAt.ContainsKey(pid)) pids.Add((uint)pid);
        }
        return pids;
    }

    /// <summary>Returns bytes accumulated per PID since the previous call and resets the counters.</summary>
    public Dictionary<int, (long Down, long Up)> TakeSnapshot()
    {
        var result = new Dictionary<int, (long, long)>();
        foreach (var kvp in _byPid)
        {
            var down = Interlocked.Exchange(ref kvp.Value.Down, 0);
            var up = Interlocked.Exchange(ref kvp.Value.Up, 0);
            if (down != 0 || up != 0) result[kvp.Key] = (down, up);
        }

        // Forget exited processes once their trailing events have surely been
        // consumed; keeps _pidExe from growing and avoids stale PID-reuse hits.
        var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(30);
        foreach (var (pid, exitedAt) in _exitedAt)
        {
            if (exitedAt < cutoff && _exitedAt.TryRemove(pid, out _))
            {
                _pidExe.TryRemove(pid, out _);
                _byPid.TryRemove(pid, out _);
            }
        }
        return result;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // TraceEventSession.Source.Process() blocks for the session's lifetime,
        // so it gets a dedicated thread instead of a thread-pool task.
        var thread = new Thread(() =>
        {
            try
            {
                RunSession(stoppingToken);
                completion.TrySetResult();
            }
            catch (Exception ex) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "ETW session ended during shutdown");
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                // Degrade instead of stopping the host: without ETW the
                // service still serves rules and IPC, just with zero counters
                // (typical cause: not elevated).
                _logger.LogError(ex, "ETW session failed — live per-process traffic will be unavailable");
                completion.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = "etw-kernel-network",
        };
        thread.Start();

        stoppingToken.Register(() => _session?.Stop());
        return completion.Task;
    }

    private void RunSession(CancellationToken stoppingToken)
    {
        // Creating a session with an existing name takes it over, so a stale
        // session left by a crashed run does not block startup.
        using var session = new TraceEventSession(SessionName);
        _session = session;

        session.EnableKernelProvider(
            KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Process);

        var kernel = session.Source.Kernel;
        kernel.ProcessStart += data => OnProcessSeen(data.ProcessID, data.ImageFileName);
        kernel.ProcessDCStart += data => OnProcessSeen(data.ProcessID, data.ImageFileName);
        kernel.ProcessStop += data => _exitedAt[data.ProcessID] = DateTime.UtcNow;
        kernel.TcpIpRecv += data => Add(data.ProcessID, data.size, down: true);
        kernel.TcpIpRecvIPV6 += data => Add(data.ProcessID, data.size, down: true);
        kernel.TcpIpSend += data => Add(data.ProcessID, data.size, down: false);
        kernel.TcpIpSendIPV6 += data => Add(data.ProcessID, data.size, down: false);
        kernel.UdpIpRecv += data => Add(data.ProcessID, data.size, down: true);
        kernel.UdpIpRecvIPV6 += data => Add(data.ProcessID, data.size, down: true);
        kernel.UdpIpSend += data => Add(data.ProcessID, data.size, down: false);
        kernel.UdpIpSendIPV6 += data => Add(data.ProcessID, data.size, down: false);

        _logger.LogInformation("ETW kernel network session started");
        session.Source.Process();
        _logger.LogInformation("ETW kernel network session stopped");
    }

    private void OnProcessSeen(int pid, string imageFileName)
    {
        if (pid <= 0 || string.IsNullOrEmpty(imageFileName)) return;
        var exe = imageFileName.ToLowerInvariant();
        if (!exe.EndsWith(".exe")) exe += ".exe";
        _pidExe[pid] = exe;
        _exitedAt.TryRemove(pid, out _);
    }

    private void Add(int pid, int size, bool down)
    {
        if (pid <= 0) return;
        var counters = _byPid.GetOrAdd(pid, _ => new Counters());
        if (down) Interlocked.Add(ref counters.Down, size);
        else Interlocked.Add(ref counters.Up, size);
    }
}
