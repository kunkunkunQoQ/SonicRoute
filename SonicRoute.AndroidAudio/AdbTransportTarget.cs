namespace SonicRoute.AndroidAudio;

// Transport selectors are internal, process-local capabilities, not addresses or
// device identities. In particular, a direct Wi-Fi endpoint can be reused by a
// different phone while a previous helper is still unwinding.
internal static class AdbTransportTarget
{
    private const string Prefix = "@sonicroute-transport:";
    internal static string CreateDirect(string id) => Prefix + "direct:" + id;
    internal static string? Create(AndroidDevice device, IAdbTransport transport)
    {
        if (transport is not (DirectAdbTransport or ArbitratedAdbTransport or AdbSocketClient)) return null;
        if (device.ConnectionTarget != null) return device.ConnectionTarget;
        if (device.TransportId is not { Length: > 0 } id) return null;
        if (id.StartsWith("wifi-", StringComparison.Ordinal) || id.StartsWith("usb-", StringComparison.Ordinal))
            return CreateDirect(id);
        return null;
    }
    internal static string? CreateSocket(string? id, string? daemon) => daemon != null && ulong.TryParse(id, out _)
        ? $"{Prefix}socket:{daemon}/{id}" : null;
    internal static bool TrySocketOwner(AndroidDevice device, out string daemon)
    {
        daemon = "";
        if (device.ConnectionTarget is not { } target || !TryParse(target, out bool direct, out string qualified) || direct
            || device.TransportId is not { } id || !ulong.TryParse(id, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) || number == 0) return false;
        int slash = qualified.IndexOf('/');
        if (slash <= 0 || !qualified.AsSpan(slash + 1).SequenceEqual(id)) return false;
        daemon = qualified[..slash];
        return true;
    }
    internal static bool TryParse(string target, out bool direct, out string id)
    {
        direct = false; id = "";
        if (!target.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var rest = target[Prefix.Length..];
        direct = rest.StartsWith("direct:", StringComparison.Ordinal);
        if (!direct && !rest.StartsWith("socket:", StringComparison.Ordinal)) throw new IOException("Invalid ADB transport selector.");
        id = rest[7..];
        if (id.Length == 0) throw new IOException("Invalid ADB transport selector.");
        return true;
    }
    internal static void CheckSocketOwner(string? selector, System.Net.Sockets.TcpClient client)
    {
        if (selector == null || !TryParse(selector, out bool direct, out var qualified)) return;
        if (direct || CreateSocket(SocketId(qualified), AdbDaemonInstance.Read(client)) != selector)
            throw new IOException("The ADB daemon for this connection has changed.");
    }
    private static string SocketId(string qualified)
    {
        int split = qualified.IndexOf('/');
        if (split <= 0 || !ulong.TryParse(qualified[(split + 1)..], out _)) throw new IOException("Invalid ADB transport selector.");
        return qualified[(split + 1)..];
    }
    internal static string SocketHostCommand(string serial, string verb) => TryParse(serial, out bool direct, out string id)
        ? !direct ? $"host-transport-id:{SocketId(id)}:{verb}" : throw new IOException("ADB transport changed.")
        : $"host-serial:{serial}:{verb}";
    internal static string SocketSelectCommand(string serial) => TryParse(serial, out bool direct, out string id)
        ? !direct ? "host:transport-id:" + SocketId(id) : throw new IOException("ADB transport changed.")
        : "host:transport:" + serial;
}
