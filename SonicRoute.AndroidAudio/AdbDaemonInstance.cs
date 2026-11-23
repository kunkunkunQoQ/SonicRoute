using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SonicRoute.AndroidAudio;

// Numeric ADB transport IDs belong to one daemon process. Look up the owner
// of the server side of this established loopback connection, not the listener
// observed before connecting. No watcher or idle polling is needed.
internal static class AdbDaemonInstance
{
    private const int TcpTableOwnerPidConnections = 4;
    private const int InsufficientBuffer = 122;

    [DllImport("iphlpapi.dll")]
    private static extern int GetExtendedTcpTable([Out] byte[]? table, ref int size,
        bool sort, int family, int tableClass, int reserved);

    internal static string? Read(TcpClient connection)
    {
        if (!OperatingSystem.IsWindows() || connection.Client.RemoteEndPoint is not IPEndPoint server
            || connection.Client.LocalEndPoint is not IPEndPoint client) return null;
        IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        var address = Normalize(server.Address);
        if (!IPAddress.IsLoopback(address)) return null;
        byte[] local = address.GetAddressBytes(), remote = Normalize(client.Address).GetAddressBytes();
        bool ipv6 = local.Length == 16;
        int family = (int)address.AddressFamily, size = 0;
        int result = GetExtendedTcpTable(null, ref size, false, family, TcpTableOwnerPidConnections, 0);
        for (int attempt = 0; result == InsufficientBuffer && attempt < 3; ++attempt)
        {
            var table = new byte[size];
            result = GetExtendedTcpTable(table, ref size, false, family, TcpTableOwnerPidConnections, 0);
            if (result != 0) continue;
            // MIB_TCPROW_OWNER_PID / MIB_TCP6ROW_OWNER_PID (Windows SDK).
            int stride = ipv6 ? 56 : 24;
            int count = BinaryPrimitives.ReadInt32LittleEndian(table);
            var rows = table.AsSpan(sizeof(int));
            for (int i = 0; i < count && rows.Length >= stride; ++i, rows = rows[stride..])
            {
                if (!rows.Slice(ipv6 ? 0 : 4, local.Length).SequenceEqual(local)
                    || !rows.Slice(ipv6 ? 24 : 12, remote.Length).SequenceEqual(remote)
                    || BinaryPrimitives.ReadUInt16BigEndian(rows[(ipv6 ? 20 : 8)..]) != server.Port
                    || BinaryPrimitives.ReadUInt16BigEndian(rows[(ipv6 ? 44 : 16)..]) != client.Port
                    || BinaryPrimitives.ReadInt32LittleEndian(rows[(ipv6 ? 48 : 0)..]) != 5) continue;
                int pid = BinaryPrimitives.ReadInt32LittleEndian(rows[(stride - 4)..]);
                try
                {
                    using var process = Process.GetProcessById(pid);
                    return $"{server.Port:X}-{pid:X}-{process.StartTime.ToUniversalTime().Ticks:X}";
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
            }
        }
        return null;
    }
}
