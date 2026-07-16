using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WindivertDotnet;

namespace PrzepustnicaService;

public sealed record ThrottleStats(long DelayedBytes, long DroppedBytes, long PassedBytes);

// Etap 2 (PLAN_WDROZENIA_WINDOWS.md §4): real limit enforcement, evolved from
// the validated throttle-poc. One WinDivert handle captures inbound TCP/UDP at
// the Network layer; packets whose destination port belongs to a rule's
// process are paced with a GCRA-style virtual scheduler (delayed, not
// dropped). A rule that is enabled but outside its schedule window drops its
// process's inbound packets — the real "Uśpiona".
//
// Port -> PID resolution is event-driven: a second WinDivert handle at the
// Socket layer (sniff, recv-only) reports bind/connect/accept/close with the
// owning PID the moment they happen. The polled GetExtendedTcp/UdpTable
// approach the PoC used misses short-lived connections entirely — a fast
// download can open, transfer and close inside one refresh interval — so the
// table walk is used only once at startup to seed already-open sockets.
public sealed class Throttler : BackgroundService
{
    private sealed class RuleRuntime
    {
        public required string RuleId;
        public double BytesPerSecond;      // > 0 when a limit is enforced
        public bool Blocking;              // enabled but outside schedule window
        public long BurstToleranceTicks;
        public long NextFreeTicks = Stopwatch.GetTimestamp();
        public readonly object PacerLock = new();
        public long DelayedBytes;
        public long DroppedBytes;
        public long PassedBytes; // matched but sent without delay (burst credit)
    }

    private readonly Database _db;
    private readonly EtwTrafficCounter _etw;
    private readonly ProcessCatalog _catalog;
    private readonly ILogger<Throttler> _logger;

    private readonly ConcurrentDictionary<string, RuleRuntime> _runtimes = new();
    private readonly ConcurrentDictionary<ushort, uint> _tcpPortPid = new();
    private readonly ConcurrentDictionary<ushort, uint> _udpPortPid = new();
    // PID -> exe resolved at socket-event time. The ETW process rundown can
    // lag ~1 s behind reality, which is longer than a fast download lives —
    // resolving while the process is provably alive (it just made a socket
    // syscall) closes that blind window.
    private readonly ConcurrentDictionary<uint, string> _socketPidExe = new();
    private volatile Dictionary<string, RuleRuntime> _exeRuntimes = new();
    private WinDivert? _divert;

    public Throttler(Database db, EtwTrafficCounter etw, ProcessCatalog catalog, ILogger<Throttler> logger)
    {
        _db = db;
        _etw = etw;
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>True when the WinDivert handle is open and limits are actually enforced.</summary>
    public bool IsEnforcing => _divert is not null;

    /// <summary>Per-rule bytes delayed/dropped since the previous call (resets counters).</summary>
    public Dictionary<string, ThrottleStats> TakeStats()
    {
        var stats = new Dictionary<string, ThrottleStats>();
        foreach (var (ruleId, runtime) in _runtimes)
        {
            var delayed = Interlocked.Exchange(ref runtime.DelayedBytes, 0);
            var dropped = Interlocked.Exchange(ref runtime.DroppedBytes, 0);
            var passed = Interlocked.Exchange(ref runtime.PassedBytes, 0);
            if (delayed != 0 || dropped != 0 || passed != 0)
                stats[ruleId] = new ThrottleStats(delayed, dropped, passed);
        }
        return stats;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WinDivert divert;
        try
        {
            var filter = Filter.Compile("inbound and (tcp or udp)", WinDivertLayer.Network);
            divert = new WinDivert(filter, WinDivertLayer.Network, 0, WinDivertFlag.None);
        }
        catch (Exception ex)
        {
            // Same degradation policy as the ETW counter: without elevation
            // (or with a broken driver) the service keeps monitoring, it just
            // cannot enforce limits.
            _logger.LogError(ex, "WinDivert unavailable — limits will NOT be enforced (elevation required?)");
            return;
        }

        _divert = divert;
        _logger.LogInformation("WinDivert handle open - limit enforcement active");

        SeedPortOwners();
        var socketTask = SocketEventLoopAsync(stoppingToken);
        var rulesTask = RefreshRulesLoopAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var packet = new WinDivertPacket();
                var address = new WinDivertAddress();
                try
                {
                    await divert.RecvAsync(packet, address, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    packet.Dispose();
                    address.Dispose();
                    break;
                }

                await HandlePacketAsync(divert, packet, address, stoppingToken);
            }
        }
        finally
        {
            _divert = null;
            divert.Dispose();
            try { await Task.WhenAll(socketTask, rulesTask); } catch { }
            _logger.LogInformation("WinDivert handle closed");
        }
    }

    private async ValueTask HandlePacketAsync(WinDivert divert, WinDivertPacket packet, WinDivertAddress address, CancellationToken ct)
    {
        var runtime = ResolveRuntime(packet);

        if (runtime is null)
        {
            // Common path: not our traffic — re-inject immediately, inline.
            try
            {
                await divert.SendAsync(packet, address, ct);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                packet.Dispose();
                address.Dispose();
            }
            return;
        }

        var length = packet.Length;

        if (runtime.Blocking)
        {
            // Outside schedule window: swallow the packet ("Uśpiona").
            Interlocked.Add(ref runtime.DroppedBytes, length);
            packet.Dispose();
            address.Dispose();
            return;
        }

        // GCRA virtual scheduler (same as throttle-poc): each packet reserves
        // a service slot; departures serialize at the configured rate instead
        // of every packet independently computing a small delay and then
        // bursting out together.
        TimeSpan delay;
        lock (runtime.PacerLock)
        {
            var now = Stopwatch.GetTimestamp();

            var floor = now - runtime.BurstToleranceTicks;
            if (runtime.NextFreeTicks < floor) runtime.NextFreeTicks = floor;

            var delayTicks = Math.Max(0, runtime.NextFreeTicks - now);
            delay = delayTicks > 0
                ? TimeSpan.FromSeconds(delayTicks / (double)Stopwatch.Frequency)
                : TimeSpan.Zero;

            if (delayTicks > 0) Interlocked.Add(ref runtime.DelayedBytes, length);

            var serviceTicks = (long)(length / runtime.BytesPerSecond * Stopwatch.Frequency);
            runtime.NextFreeTicks = Math.Max(now, runtime.NextFreeTicks) + serviceTicks;
        }

        if (delay == TimeSpan.Zero)
        {
            Interlocked.Add(ref runtime.PassedBytes, length);
            try
            {
                await divert.SendAsync(packet, address, ct);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                packet.Dispose();
                address.Dispose();
            }
            return;
        }

        // Delayed re-injection happens off the receive loop so one queued
        // packet never stalls capture.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ct);
                await divert.SendAsync(packet, address, ct);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Delayed re-injection failed");
            }
            finally
            {
                packet.Dispose();
                address.Dispose();
            }
        }, ct);
    }

    private RuleRuntime? ResolveRuntime(WinDivertPacket packet)
    {
        var exeRuntimes = _exeRuntimes;
        if (exeRuntimes.Count == 0) return null;

        ushort dstPort;
        bool isTcp;
        unsafe
        {
            var parsed = packet.GetParseResult();
            if (parsed.TcpHeader != null)
            {
                isTcp = true;
                dstPort = parsed.TcpHeader->DstPort;
            }
            else if (parsed.UdpHeader != null)
            {
                isTcp = false;
                dstPort = parsed.UdpHeader->DstPort;
            }
            else
            {
                return null;
            }
        }

        var portPid = isTcp ? _tcpPortPid : _udpPortPid;
        if (!portPid.TryGetValue(dstPort, out var pid)) return null;

        if (!_socketPidExe.TryGetValue(pid, out var exe) &&
            !_etw.TryGetExe((int)pid, out exe!) &&
            !_catalog.PidToExe.TryGetValue((int)pid, out exe!))
        {
            return null;
        }

        return exeRuntimes.GetValueOrDefault(exe);
    }

    // Socket-layer events carry the owning PID and fire the moment a socket
    // binds/connects, before any data flows — so even a connection that lives
    // 200 ms is attributed correctly.
    private async Task SocketEventLoopAsync(CancellationToken ct)
    {
        try
        {
            var filter = Filter.Compile("tcp or udp", WinDivertLayer.Socket);
            using var socketDivert = new WinDivert(
                filter, WinDivertLayer.Socket, 1, WinDivertFlag.Sniff | WinDivertFlag.RecvOnly);
            using var packet = new WinDivertPacket();
            using var address = new WinDivertAddress();

            while (!ct.IsCancellationRequested)
            {
                await socketDivert.RecvAsync(packet, address, ct);

                uint pid;
                ushort localPort;
                System.Net.Sockets.ProtocolType protocol;
                WinDivertEvent socketEvent;
                unsafe
                {
                    var socket = address.Socket;
                    pid = (uint)socket->ProcessId;
                    localPort = socket->LocalPort;
                    protocol = socket->Protocol;
                    socketEvent = address.Event;
                }

                if (localPort == 0) continue;
                var map = protocol == System.Net.Sockets.ProtocolType.Udp ? _udpPortPid : _tcpPortPid;

                switch (socketEvent)
                {
                    case WinDivertEvent.SocketBind:
                    case WinDivertEvent.SocketConnect:
                    case WinDivertEvent.SocketListen:
                    case WinDivertEvent.SocketAccept:
                        map[localPort] = pid;
                        ResolvePidExe(pid);
                        break;
                    case WinDivertEvent.SocketClose:
                        if (map.TryGetValue(localPort, out var owner) && owner == pid)
                        {
                            map.TryRemove(localPort, out _);
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Socket event loop failed — falling back to startup port table only");
        }
    }

    private void ResolvePidExe(uint pid)
    {
        if (pid == 0 || _socketPidExe.ContainsKey(pid)) return;
        if (_etw.TryGetExe((int)pid, out var exe))
        {
            _socketPidExe[pid] = exe;
            return;
        }
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            _socketPidExe[pid] = process.ProcessName.ToLowerInvariant() + ".exe";
        }
        catch
        {
            // Process already gone or access denied — packet-time fallbacks
            // (ETW rundown, catalog) may still catch it.
        }

        // Crude size guard against unbounded PID accumulation.
        if (_socketPidExe.Count > 8192) _socketPidExe.Clear();
    }

    private void SeedPortOwners()
    {
        try
        {
            var (tcp, udp) = PortPidResolver.GetPortOwners();
            foreach (var (port, pid) in tcp) _tcpPortPid[port] = pid;
            foreach (var (port, pid) in udp) _udpPortPid[port] = pid;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial port owner seed failed");
        }
    }

    // Rules -> per-exe pacer runtimes. Pacer state is keyed by rule id and
    // survives refreshes, so edits never reset in-flight pacing.
    private async Task RefreshRulesLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            RefreshRules();
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    RefreshRules();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Throttle rule refresh failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RefreshRules()
    {
        var exeRuntimes = new Dictionary<string, RuleRuntime>();
        var activeRuleIds = new HashSet<string>();

        // Global kill switch from Settings: keep capturing (cheap) but match
        // nothing, so all traffic passes untouched.
        if (!_db.GetSettings().EnforcementEnabled)
        {
            foreach (var ruleId in _runtimes.Keys) _runtimes.TryRemove(ruleId, out _);
            _exeRuntimes = exeRuntimes;
            return;
        }

        var rules = _db.GetRules();
        var now = DateTime.Now;

        foreach (var rule in rules)
        {
            var withinWindow = ScheduleLogic.IsWithin(rule.Schedule, now);
            var blocking = rule.Enabled && !withinWindow;
            var limiting = rule.Enabled && withinWindow && rule.LimitMbps is > 0;
            if (!blocking && !limiting) continue;

            activeRuleIds.Add(rule.Id);
            var runtime = _runtimes.GetOrAdd(rule.Id, id => new RuleRuntime { RuleId = id });
            runtime.Blocking = blocking;
            if (limiting)
            {
                runtime.BytesPerSecond = rule.LimitMbps!.Value * 1_000_000 / 8.0;
                // Allow ~1.5 s worth of idle credit to build up (burst),
                // mirroring the PoC's burst = 1.5x rate.
                runtime.BurstToleranceTicks = (long)(1.5 * Stopwatch.Frequency);
            }

            exeRuntimes[ProcessCatalog.NormalizeExe(rule.ExeMatch)] = runtime;
        }

        foreach (var ruleId in _runtimes.Keys)
        {
            if (!activeRuleIds.Contains(ruleId)) _runtimes.TryRemove(ruleId, out _);
        }

        _exeRuntimes = exeRuntimes;
    }
}
