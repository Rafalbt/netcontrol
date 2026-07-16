// P/Invoke wrapper around iphlpapi's GetExtendedTcpTable / GetExtendedUdpTable.
//
// WinDivert's packet filter language only exposes processId at the Socket and
// Flow layers, not at the Network layer where actual packet bytes are captured
// (verified in throttle-poc). So connection -> PID mapping is done here, per
// PLAN_WDROZENIA_WINDOWS.md §4, and refreshed periodically by the Throttler.
// Unlike the PoC variant this walks each table once and returns the full
// port -> PID map, so one refresh serves any number of rules.

using System.Runtime.InteropServices;

namespace PrzepustnicaService;

internal static class PortPidResolver
{
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(byte[]? pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(byte[]? pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    /// <summary>Local port (host byte order) -> owning PID, for TCP and UDP.</summary>
    public static (Dictionary<ushort, uint> Tcp, Dictionary<ushort, uint> Udp) GetPortOwners()
    {
        return (GetTcpOwners(), GetUdpOwners());
    }

    private static Dictionary<ushort, uint> GetTcpOwners()
    {
        var owners = new Dictionary<ushort, uint>();
        var size = 0;
        GetExtendedTcpTable(null, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size == 0) return owners;

        var buffer = new byte[size];
        if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return owners;

        var numEntries = BitConverter.ToInt32(buffer, 0);
        // MIB_TCPROW_OWNER_PID: dwState, dwLocalAddr, dwLocalPort, dwRemoteAddr, dwRemotePort, dwOwningPid — 6 x 4 bytes.
        const int rowSize = 24;
        var offset = 4;
        for (var i = 0; i < numEntries && offset + rowSize <= buffer.Length; i++, offset += rowSize)
        {
            var localPortRaw = BitConverter.ToUInt32(buffer, offset + 8);
            var owningPid = BitConverter.ToUInt32(buffer, offset + 20);
            owners[NetworkToHostPort(localPortRaw)] = owningPid;
        }
        return owners;
    }

    private static Dictionary<ushort, uint> GetUdpOwners()
    {
        var owners = new Dictionary<ushort, uint>();
        var size = 0;
        GetExtendedUdpTable(null, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
        if (size == 0) return owners;

        var buffer = new byte[size];
        if (GetExtendedUdpTable(buffer, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0) != 0) return owners;

        var numEntries = BitConverter.ToInt32(buffer, 0);
        // MIB_UDPROW_OWNER_PID: dwLocalAddr, dwLocalPort, dwOwningPid — 3 x 4 bytes.
        const int rowSize = 12;
        var offset = 4;
        for (var i = 0; i < numEntries && offset + rowSize <= buffer.Length; i++, offset += rowSize)
        {
            var localPortRaw = BitConverter.ToUInt32(buffer, offset + 4);
            var owningPid = BitConverter.ToUInt32(buffer, offset + 8);
            owners[NetworkToHostPort(localPortRaw)] = owningPid;
        }
        return owners;
    }

    private static ushort NetworkToHostPort(uint rawPort)
    {
        // The port occupies the low 16 bits in network byte order.
        var bytes = BitConverter.GetBytes((ushort)rawPort);
        return (ushort)((bytes[0] << 8) | bytes[1]);
    }
}
