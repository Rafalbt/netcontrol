// Przepustnica — WinDivert throttling proof-of-concept.
//
// Verifies the riskiest technical assumption from PLAN_WDROZENIA_WINDOWS.md §4:
// that a per-process token-bucket built on WinDivert can actually throttle a
// running application's traffic to a target Mb/s.
//
// Usage (must run elevated — WinDivert requires admin/SYSTEM):
//   dotnet run -- <processName> <rateMbps>
// Example:
//   dotnet run -- chrome 5
//
// Architecture note: WinDivert's packet filter language only exposes
// processId at the Socket/Flow layers, not at the Network layer where actual
// packet bytes are captured (confirmed by testing — "processId == X" is
// rejected as a bad token when compiled for WinDivertLayer.Network). So this
// mirrors the plan's own approach: capture broadly at the Network layer, and
// resolve connection -> PID ourselves via GetExtendedTcpTable/UdpTable
// (PortPidResolver.cs), refreshed periodically since connections churn.
//
// What it does:
//   1. Finds the PID of the named process.
//   2. Opens one WinDivert handle at the Network layer for inbound TCP/UDP.
//   3. Every 250ms, refreshes the set of local ports owned by the target PID.
//   4. For packets whose destination port belongs to that PID, runs a
//      token-bucket: packets arriving faster than the configured rate are
//      delayed (not dropped) before re-injection. Everything else passes
//      through untouched immediately.
//   5. Prints live pass/queue telemetry once per second, mirroring the
//      "usage" stream the real service would push to the UI over IPC.

using System.Diagnostics;
using ThrottlePoc;
using WindivertDotnet;

if (args.Length < 2 || !double.TryParse(args[1], out var rateMbps))
{
    Console.WriteLine("Usage: dotnet run -- <processName> <rateMbps>");
    Console.WriteLine("Example: dotnet run -- chrome 5");
    return 1;
}

var processName = args[0];
var processes = Process.GetProcessesByName(processName);
if (processes.Length == 0)
{
    Console.WriteLine($"No running process named '{processName}' found.");
    return 1;
}

var pid = (uint)processes[0].Id;
Console.WriteLine($"Target: {processName} (PID {pid}), limit {rateMbps} Mb/s");

var bytesPerSecond = rateMbps * 1_000_000 / 8.0;
var burstCapacity = bytesPerSecond * 1.5;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Live-refreshed connection -> PID mapping (see architecture note above).
var tcpPorts = new HashSet<ushort>();
var udpPorts = new HashSet<ushort>();
var portsLock = new object();

_ = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        var (tcp, udp) = PortPidResolver.GetPortsForProcess(pid);
        lock (portsLock)
        {
            tcpPorts = tcp;
            udpPorts = udp;
        }
        try
        {
            await Task.Delay(250, cts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
});

WinDivert divert;
try
{
    var filter = Filter.Compile("inbound and (tcp or udp)", WinDivertLayer.Network);
    divert = new WinDivert(filter, WinDivertLayer.Network, 0, WinDivertFlag.None);
}
catch (Exception ex)
{
    Console.WriteLine($"Failed to open WinDivert handle: {ex.Message}");
    Console.WriteLine("This almost always means the process isn't elevated (admin/SYSTEM required) or the WinDivert driver failed to load.");
    return 1;
}

Console.WriteLine("WinDivert handle open. Throttling started. Press Ctrl+C to stop.");

// Virtual-scheduling (GCRA-style) pacer: nextFreeTicks is the earliest
// Stopwatch timestamp at which the next packet may depart. Each packet
// reserves a slot of length/bytesPerSecond starting no earlier than
// max(now, nextFreeTicks), which serializes departures at the target rate
// instead of computing each packet's delay independently — the earlier
// per-packet-independent version let concurrently-arriving packets all
// compute similar small delays and then release in the same instant,
// producing multi-Mb/s bursts against a 1 Mb/s limit.
var burstToleranceTicks = (long)(burstCapacity / bytesPerSecond * Stopwatch.Frequency);
var nextFreeTicks = Stopwatch.GetTimestamp();
var bucketLock = new object();

long bytesPassedThisSecond = 0;
long bytesQueuedThisSecond = 0;
long matchedPacketsThisSecond = 0;
long packetsInFlight = 0;

_ = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(1000, cts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }

        var passed = Interlocked.Exchange(ref bytesPassedThisSecond, 0);
        var queued = Interlocked.Exchange(ref bytesQueuedThisSecond, 0);
        var matched = Interlocked.Exchange(ref matchedPacketsThisSecond, 0);
        var mbps = passed * 8 / 1_000_000.0;
        Console.WriteLine($"[usage] {mbps,6:F2} Mb/s passed | {queued,8} B queued/delayed | {matched} throttled-path packets | {Interlocked.Read(ref packetsInFlight)} in flight | limit {rateMbps} Mb/s");
    }
});

try
{
    while (!cts.IsCancellationRequested)
    {
        var packet = new WinDivertPacket();
        var address = new WinDivertAddress();

        await divert.RecvAsync(packet, address, cts.Token);

        var parsed = packet.GetParseResult();
        ushort? dstPort;
        unsafe
        {
            dstPort = parsed.TcpHeader != null ? parsed.TcpHeader->DstPort
                : parsed.UdpHeader != null ? parsed.UdpHeader->DstPort
                : (ushort?)null;
        }

        bool belongsToTarget;
        lock (portsLock)
        {
            belongsToTarget = dstPort is { } p && (tcpPorts.Contains(p) || udpPorts.Contains(p));
        }

        var length = packet.Length;

        if (!belongsToTarget)
        {
            // Not our target process's traffic — pass straight through.
            await divert.SendAsync(packet, address, cts.Token);
            packet.Dispose();
            address.Dispose();
            continue;
        }

        Interlocked.Increment(ref matchedPacketsThisSecond);

        TimeSpan delay;
        lock (bucketLock)
        {
            var now = Stopwatch.GetTimestamp();

            // Forgive idle time beyond the burst window instead of letting
            // unbounded "credit" build up while there's nothing to send.
            var floor = now - burstToleranceTicks;
            if (nextFreeTicks < floor)
            {
                nextFreeTicks = floor;
            }

            var delayTicks = Math.Max(0, nextFreeTicks - now);
            delay = delayTicks > 0
                ? TimeSpan.FromSeconds(delayTicks / (double)Stopwatch.Frequency)
                : TimeSpan.Zero;

            if (delayTicks > 0)
            {
                Interlocked.Add(ref bytesQueuedThisSecond, length);
            }

            var serviceTicks = (long)(length / bytesPerSecond * Stopwatch.Frequency);
            nextFreeTicks = Math.Max(now, nextFreeTicks) + serviceTicks;
        }

        Interlocked.Increment(ref packetsInFlight);

        // Re-inject on a background task so we keep receiving new packets
        // while this one waits in its token-bucket "queue" — otherwise a
        // single delayed packet would stall the whole capture loop.
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cts.Token);
                }
                await divert.SendAsync(packet, address, cts.Token);
                Interlocked.Add(ref bytesPassedThisSecond, length);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            finally
            {
                Interlocked.Decrement(ref packetsInFlight);
                packet.Dispose();
                address.Dispose();
            }
        }, cts.Token);
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C
}
finally
{
    divert.Dispose();
    Console.WriteLine("Stopped.");
}

return 0;
