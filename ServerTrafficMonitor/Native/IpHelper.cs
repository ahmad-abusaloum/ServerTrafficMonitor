using System.Net;
using System.Runtime.InteropServices;

namespace ServerTrafficMonitor.Native;

/// <summary>
/// A single raw connection row read from the OS connection tables.
/// State is the raw MIB_TCP_STATE value (0 for UDP, which is stateless).
/// </summary>
public readonly record struct RawConnection(
    string Protocol,
    IPAddress LocalAddress,
    int LocalPort,
    IPAddress RemoteAddress,
    int RemotePort,
    int State,
    int ProcessId);

/// <summary>
/// Thin P/Invoke wrapper over iphlpapi.dll's GetExtendedTcpTable / GetExtendedUdpTable.
/// Returns every TCP and UDP socket on the machine together with its owning process id.
/// </summary>
public static class IpHelper
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;

    // TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    // UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID
    private const int UDP_TABLE_OWNER_PID = 1;

    private const uint NO_ERROR = 0;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tableClass, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] remoteAddr;
        public uint remoteScopeId;
        public uint remotePort;
        public uint state;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint localAddr;
        public uint localPort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        public uint owningPid;
    }

    /// <summary>Reads all TCP (v4 + v6) and UDP (v4 + v6) sockets in one shot.</summary>
    public static List<RawConnection> GetAllConnections()
    {
        var list = new List<RawConnection>(512);
        ReadTcp4(list);
        ReadTcp6(list);
        ReadUdp4(list);
        ReadUdp6(list);
        return list;
    }

    private static void ReadTcp4(List<RawConnection> list)
    {
        EnumerateTable(
            (IntPtr buf, ref int len) => GetExtendedTcpTable(buf, ref len, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0),
            ptr =>
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(ptr);
                return new RawConnection(
                    "TCP",
                    new IPAddress(BitConverter.GetBytes(row.localAddr)), ParsePort(row.localPort),
                    new IPAddress(BitConverter.GetBytes(row.remoteAddr)), ParsePort(row.remotePort),
                    (int)row.state, (int)row.owningPid);
            },
            Marshal.SizeOf<MIB_TCPROW_OWNER_PID>(), list);
    }

    private static void ReadTcp6(List<RawConnection> list)
    {
        EnumerateTable(
            (IntPtr buf, ref int len) => GetExtendedTcpTable(buf, ref len, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0),
            ptr =>
            {
                var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(ptr);
                return new RawConnection(
                    "TCPv6",
                    new IPAddress(row.localAddr, row.localScopeId), ParsePort(row.localPort),
                    new IPAddress(row.remoteAddr, row.remoteScopeId), ParsePort(row.remotePort),
                    (int)row.state, (int)row.owningPid);
            },
            Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>(), list);
    }

    private static void ReadUdp4(List<RawConnection> list)
    {
        EnumerateTable(
            (IntPtr buf, ref int len) => GetExtendedUdpTable(buf, ref len, false, AF_INET, UDP_TABLE_OWNER_PID, 0),
            ptr =>
            {
                var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(ptr);
                return new RawConnection(
                    "UDP",
                    new IPAddress(BitConverter.GetBytes(row.localAddr)), ParsePort(row.localPort),
                    IPAddress.Any, 0, 0, (int)row.owningPid);
            },
            Marshal.SizeOf<MIB_UDPROW_OWNER_PID>(), list);
    }

    private static void ReadUdp6(List<RawConnection> list)
    {
        EnumerateTable(
            (IntPtr buf, ref int len) => GetExtendedUdpTable(buf, ref len, false, AF_INET6, UDP_TABLE_OWNER_PID, 0),
            ptr =>
            {
                var row = Marshal.PtrToStructure<MIB_UDP6ROW_OWNER_PID>(ptr);
                return new RawConnection(
                    "UDPv6",
                    new IPAddress(row.localAddr, row.localScopeId), ParsePort(row.localPort),
                    IPAddress.IPv6Any, 0, 0, (int)row.owningPid);
            },
            Marshal.SizeOf<MIB_UDP6ROW_OWNER_PID>(), list);
    }

    private delegate uint TableFn(IntPtr buffer, ref int length);

    private static void EnumerateTable(TableFn fn, Func<IntPtr, RawConnection> parseRow, int rowSize, List<RawConnection> sink)
    {
        int size = 0;
        // First call: discover required buffer size.
        uint ret = fn(IntPtr.Zero, ref size);
        if (ret != ERROR_INSUFFICIENT_BUFFER && ret != NO_ERROR)
            return; // provider unavailable / no entries

        IntPtr buffer = IntPtr.Zero;
        try
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size);
                ret = fn(buffer, ref size);
                if (ret == NO_ERROR) break;
                if (ret != ERROR_INSUFFICIENT_BUFFER) return; // unrecoverable
                // else: table grew between calls -> loop with the new (larger) size
            }
            if (ret != NO_ERROR) return;

            int count = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = buffer + 4; // skip dwNumEntries
            for (int i = 0; i < count; i++)
            {
                sink.Add(parseRow(rowPtr));
                rowPtr += rowSize;
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Ports come back as a DWORD with the value in network byte order in the low 16 bits.</summary>
    private static int ParsePort(uint nativePort)
        => (int)(((nativePort & 0xFF) << 8) | ((nativePort & 0xFF00) >> 8));
}
