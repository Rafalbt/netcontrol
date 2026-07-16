// P/Invoke wrapper around iphlpapi's GetExtendedTcpTable / GetExtendedUdpTable.
//
// WinDivert's packet filter language only exposes processId at the Socket and
// Flow layers, not at Network layer where actual packet bytes are available
// (confirmed empirically — see Program.cs comment). So connection -> PID
// mapping has to be done ourselves, exactly as PLAN_WDROZENIA_WINDOWS.md §4
// describes, and refreshed periodically since connections come and go.

using System.Runtime.InteropServices;

namespace ThrottlePoc;

internal static class PortPidResolver
{
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(byte[]? pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(byte[]? pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    /// <summary>
    /// Returns the set of local ports (host byte order) currently owned by
    /// <paramref name="pid"/>, split by protocol.
    /// </summary>
    public static (HashSet<ushort> TcpPorts, HashSet<ushort> UdpPorts) GetPortsForProcess(uint pid)
    {
        return (GetTcpPorts(pid), GetUdpPorts(pid));
    }

    private static HashSet<ushort> GetTcpPorts(uint pid)
    {
        var ports = new HashSet<ushort>();
        var size = 0;
        GetExtendedTcpTable(null, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size == 0)
        {
            return ports;
        }

        var buffer = new byte[size];
        var result = GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (result != 0)
        {
            return ports;
        }

        var numEntries = BitConverter.ToInt32(buffer, 0);
        // MIB_TCPROW_OWNER_PID: dwState, dwLocalAddr, dwLocalPort, dwRemoteAddr, dwRemotePort, dwOwningPid — 6 x 4 bytes.
        const int rowSize = 24;
        var offset = 4;
        for (var i = 0; i < numEntries; i++)
        {
            var localPortRaw = BitConverter.ToUInt32(buffer, offset + 8);
            var owningPid = BitConverter.ToUInt32(buffer, offset + 20);
            if (owningPid == pid)
            {
                ports.Add(NetworkToHostPort(localPortRaw));
            }
            offset += rowSize;
        }

        return ports;
    }

    private static HashSet<ushort> GetUdpPorts(uint pid)
    {
        var ports = new HashSet<ushort>();
        var size = 0;
        GetExtendedUdpTable(null, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
        if (size == 0)
        {
            return ports;
        }

        var buffer = new byte[size];
        var result = GetExtendedUdpTable(buffer, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
        if (result != 0)
        {
            return ports;
        }

        var numEntries = BitConverter.ToInt32(buffer, 0);
        // MIB_UDPROW_OWNER_PID: dwLocalAddr, dwLocalPort, dwOwningPid — 3 x 4 bytes.
        const int rowSize = 12;
        var offset = 4;
        for (var i = 0; i < numEntries; i++)
        {
            var localPortRaw = BitConverter.ToUInt32(buffer, offset + 4);
            var owningPid = BitConverter.ToUInt32(buffer, offset + 8);
            if (owningPid == pid)
            {
                ports.Add(NetworkToHostPort(localPortRaw));
            }
            offset += rowSize;
        }

        return ports;
    }

    private static ushort NetworkToHostPort(uint rawPort)
    {
        // The port occupies the low 16 bits in network byte order.
        var bytes = BitConverter.GetBytes((ushort)rawPort);
        return (ushort)((bytes[0] << 8) | bytes[1]);
    }
}
