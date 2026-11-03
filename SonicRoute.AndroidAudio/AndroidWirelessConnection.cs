using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SonicRoute.AndroidAudio;

public sealed record AndroidMdnsService(string Name, string Type, string Address);
public sealed record AndroidPairingResult(string Address, string? Guid);
public sealed record AndroidConnectionResult(AndroidDevice Device, IReadOnlyList<AndroidDevice> Devices);
public sealed record AndroidQrPairing(string ServiceName, string Password)
{
    public string Payload => $"WIFI:T:ADB;S:{ServiceName};P:{Password};;";
    public static AndroidQrPairing Create() => new("studio-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)),
        Convert.ToHexString(RandomNumberGenerator.GetBytes(12)));
}

/// <summary>All work is scoped to a user operation. No global ADB restarts or perpetual discovery.</summary>
public sealed class AndroidWirelessConnection
{
    private readonly AdbClient? _adb;
    private const string ConnectType = "_adb-tls-connect._tcp";
    private readonly Func<CancellationToken, string[], Task<string>> _run;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AndroidDevice>>>? _devices;
    private readonly Func<string, string?, string?, string?, TimeSpan, CancellationToken, Task<AndroidMdnsService?>>? _discover;
    public AndroidWirelessConnection(AdbClient adb) : this((token, args) => adb.RunAsync(token, args))
    { _adb = adb; _devices = token => adb.DevicesAsync(token, deduplicate: false); _discover = AndroidMdnsDiscovery.FindAsync; }
    public AndroidWirelessConnection(Func<CancellationToken, string[], Task<string>> run,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<string, string?, string?, string?, TimeSpan, CancellationToken, Task<AndroidMdnsService?>>? discover = null)
    { _run = run; _delay = delay ?? Task.Delay; _discover = discover; }
    private async Task<AndroidMdnsService?> DiscoverAsync(string type, string? name, string? host, string? guid,
        TimeSpan duration, CancellationToken token)
    {
        if (_discover == null) return null;
        try { return await _discover(type, name, host, guid, duration, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or SocketException or NetworkInformationException)
        { return null; } // Optional network discovery must never disable ADB's own discovery/connection.
    }
    private Task<string> RunAsync(CancellationToken token, params string[] args)
    { token.ThrowIfCancellationRequested(); return _run(token, args); }
    private async Task<IReadOnlyList<AndroidDevice>> DevicesAsync(CancellationToken token) => _devices != null
        ? await _devices(token).ConfigureAwait(false) : AdbClient.ParseDevices(await RunAsync(token, "devices", "-l").ConfigureAwait(false));
    public static string ValidateAddress(string address)
    {
        address = address.Trim();
        int colon = address.LastIndexOf(':');
        if (colon < 1 || !int.TryParse(address[(colon + 1)..], out int port) || port is < 1 or > 65535
            || !IPAddress.TryParse(address[..colon].Trim('[', ']'), out var ip))
            throw new ArgumentException("请输入手机显示的 IP:端口，例如 192.168.1.100:37099。");
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}";
    }
    private static string Host(string address) => address[..address.LastIndexOf(':')];
    private static string ServiceName(string name, string type)
    {
        name = name.TrimEnd('.');
        if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
        return name.EndsWith("." + type, StringComparison.OrdinalIgnoreCase) ? name[..^(type.Length + 1)] : name;
    }
    public static IReadOnlyList<AndroidMdnsService> ParseServices(string text)
    {
        var services = new List<AndroidMdnsService>();
        foreach (string line in text.Split('\n'))
        {
            var fields = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3) continue;
            string type = fields[^2].TrimEnd('.').ToLowerInvariant();
            if (type.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) type = type[..^6];
            if (type is not ("_adb-tls-pairing._tcp" or ConnectType)) continue;
            try { services.Add(new(ServiceName(string.Join(' ', fields[..^2]), type), type, ValidateAddress(fields[^1]))); }
            catch (ArgumentException) { }
        }
        return services;
    }
    public Task<AndroidPairingResult> PairCodeAsync(string address, string code, CancellationToken token)
    {
        if (!Regex.IsMatch(code, "\\A[0-9]{6}\\z")) throw new ArgumentException("请输入手机显示的 6 位配对码。");
        return PairAsync(ValidateAddress(address), code, token);
    }
    private async Task<AndroidPairingResult> PairAsync(string address, string password, CancellationToken token)
    {
        string result = await RunAsync(token, "pair", address, password).ConfigureAwait(false);
        if (!result.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
            throw new IOException("配对未完成，请检查地址、配对码和手机上的配对窗口。");
        var guid = Regex.Match(result, @"\[guid=([^\]\s]+)\]", RegexOptions.IgnoreCase);
        return new(address, guid.Success ? guid.Groups[1].Value : null);
    }
    public async Task<AndroidPairingResult> PairQrAsync(AndroidQrPairing qr, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using var discoveryLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var discovery = DiscoverAsync("_adb-tls-pairing._tcp", qr.ServiceName, null, null,
            TimeSpan.FromMinutes(2), discoveryLifetime.Token);
        try
        {
            while (true)
            {
                IReadOnlyList<AndroidMdnsService> services = Array.Empty<AndroidMdnsService>();
                try { services = ParseServices(await RunAsync(deadline.Token, "mdns", "services").ConfigureAwait(false)); }
                catch (IOException) { /* A transient shared-daemon discovery failure must not abort a valid QR scan. */ }
                var service = services.FirstOrDefault(s => s.Type == "_adb-tls-pairing._tcp"
                    && MatchesPairingName(s.Name, qr.ServiceName));
                if (service == null && discovery.IsCompletedSuccessfully) service = await discovery.ConfigureAwait(false);
                if (service != null) return await PairAsync(service.Address, qr.Password, deadline.Token).ConfigureAwait(false);
                var delay = _delay(TimeSpan.FromSeconds(2), deadline.Token);
                if (!discovery.IsCompleted) await Task.WhenAny(delay, discovery).ConfigureAwait(false);
                else await delay.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("二维码已过期，点击重新生成；也可以使用配对码连接。"); }
        finally { discoveryLifetime.Cancel(); try { await discovery.ConfigureAwait(false); } catch (OperationCanceledException) { } }
    }
    public static bool MatchesPairingName(string actual, string expected) => actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
        || actual.StartsWith(expected + " (", StringComparison.OrdinalIgnoreCase) && actual.EndsWith(')')
            && int.TryParse(actual.AsSpan(expected.Length + 2, actual.Length - expected.Length - 3), out int suffix) && suffix >= 2;
    private async Task SendConnectAsync(string address, CancellationToken token)
    {
        string result = await RunAsync(token, "connect", address).ConfigureAwait(false);
        if (!result.Contains("connected to", StringComparison.OrdinalIgnoreCase))
            throw new IOException("连接未完成，请填写无线调试首页的连接地址，配对端口不能用于连接。");
    }
    public async Task<AndroidConnectionResult> ConnectAsync(string address, CancellationToken token)
    {
        address = ValidateAddress(address);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await SendConnectAsync(address, deadline.Token).ConfigureAwait(false);
            return await WaitForDeviceAsync(Host(address), address, null, null, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("连接尚未就绪，请确认手机已授权，并检查无线调试首页的当前连接地址。"); }
    }
    public async Task<AndroidConnectionResult> FinishPairingAsync(AndroidPairingResult pairing, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var discoveryLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        string host = Host(ValidateAddress(pairing.Address));
        var discovery = DiscoverAsync(ConnectType, null, host, pairing.Guid,
            TimeSpan.FromSeconds(12), discoveryLifetime.Token);
        try { return await WaitForDeviceAsync(host, null, pairing.Guid, null, deadline.Token, discovery).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("配对已成功，但设备尚未连接。请在下方填写无线调试首页的连接地址，无需重新配对。"); }
        finally { discoveryLifetime.Cancel(); try { await discovery.ConfigureAwait(false); } catch (OperationCanceledException) { } }
    }
    public async Task<AndroidConnectionResult> ReconnectAsync(AndroidDevice device, CancellationToken token)
    {
        if (!device.IsWifi) throw new InvalidOperationException("请连接 USB 并在手机确认调试授权，然后刷新设备。");
        string? address = null, guid = null;
        try { address = ValidateAddress(device.Serial); }
        catch (ArgumentException)
        {
            string name = ServiceName(device.Serial, ConnectType);
            if (!device.Serial.Contains("." + ConnectType, StringComparison.OrdinalIgnoreCase) || !name.StartsWith("adb-", StringComparison.OrdinalIgnoreCase))
                throw new IOException("保存的无线地址已失效，请填写无线调试首页的当前连接地址。");
            int dash = name.LastIndexOf('-');
            guid = dash > 3 ? name[..dash] : name;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var discoveryLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var discovery = DiscoverAsync(ConnectType, null, address == null ? null : Host(address), guid,
            TimeSpan.FromSeconds(12), discoveryLifetime.Token);
        try
        {
            if (address != null)
            {
                try { await SendConnectAsync(address, deadline.Token).ConfigureAwait(false); }
                catch (IOException) { /* discover this host's current TLS endpoint below */ }
            }
            return await WaitForDeviceAsync(address == null ? null : Host(address), address, guid, device.Serial, deadline.Token, discovery).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("设备未连接，请开启手机无线调试，或填写首页的当前连接地址。"); }
        finally { discoveryLifetime.Cancel(); try { await discovery.ConfigureAwait(false); } catch (OperationCanceledException) { } }
    }
    private static bool MatchesGuid(string serial, string? guid)
    {
        if (guid == null || !serial.Contains("." + ConnectType, StringComparison.OrdinalIgnoreCase)) return false;
        string name = ServiceName(serial, ConnectType);
        if (!guid.StartsWith("adb-", StringComparison.OrdinalIgnoreCase)) guid = "adb-" + guid;
        return name.Equals(guid, StringComparison.OrdinalIgnoreCase) || name.StartsWith(guid + "-", StringComparison.OrdinalIgnoreCase);
    }
    private async Task<AndroidConnectionResult> WaitForDeviceAsync(string? host, string? address, string? guid, string? serial, CancellationToken token,
        Task<AndroidMdnsService?>? discovery = null)
    {
        var serials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (serial != null) serials.Add(serial.TrimEnd('.'));
        if (address != null) serials.Add(address);
        var endpoints = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        IOException? lastError = null;
        bool Matches(AndroidDevice d)
        {
            if (d.State != "device" || !d.IsWifi) return false;
            if (serials.Contains(d.Serial.TrimEnd('.')) || MatchesGuid(d.Serial, guid)) return true;
            try { return address == null && host != null && Host(ValidateAddress(d.Serial)) == host; }
            catch (ArgumentException) { return false; }
        }
        for (int attempt = 0; attempt < 15; ++attempt)
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<AndroidDevice> devices;
            try { devices = await DevicesAsync(token).ConfigureAwait(false); }
            catch (IOException ex) { lastError = ex; devices = Array.Empty<AndroidDevice>(); }
            var ready = devices.FirstOrDefault(Matches);
            if (ready != null) return new(ready, devices);
            if (attempt % 2 == 0)
            {
                try
                {
                    IEnumerable<AndroidMdnsService> services = Array.Empty<AndroidMdnsService>();
                    try { services = ParseServices(await RunAsync(token, "mdns", "services").ConfigureAwait(false)); }
                    catch (IOException ex) { lastError = ex; }
                    if (discovery?.IsCompletedSuccessfully == true && await discovery.ConfigureAwait(false) is { } direct)
                        services = services.Append(direct).Distinct();
                    foreach (var service in services.Where(s => s.Type == ConnectType
                        && (host != null && Host(s.Address) == host || guid != null && MatchesGuid(s.Name + "." + ConnectType, guid))))
                    {
                        serials.Add(service.Name + "." + ConnectType);
                        ready = devices.FirstOrDefault(Matches);
                        if (ready != null) return new(ready, devices);
                        if (endpoints.TryGetValue(service.Address, out int previous) && attempt - previous < 4) continue;
                        endpoints[service.Address] = attempt;
                        try { await SendConnectAsync(service.Address, token).ConfigureAwait(false); }
                        catch (IOException ex) { lastError = ex; }
                        address ??= service.Address;
                        serials.Add(service.Address);
                    }
                }
                catch (IOException ex) { lastError = ex; }
                if (address != null)
                {
                    try
                    {
                        string canonical = (await RunAsync(token, "-s", address, "get-serialno").ConfigureAwait(false)).Trim();
                        serials.Add(canonical.TrimEnd('.'));
                        ready = devices.FirstOrDefault(Matches);
                        if (ready != null) return new(ready, devices);
                    }
                    catch (IOException ex) { lastError = ex; }
                }
            }
            if (attempt < 14) await _delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }
        throw new IOException("设备尚未就绪，请在手机确认调试授权；已配对时可填写无线调试首页的连接地址，无需重新配对。", lastError);
    }

    public static string PairingFingerprint(string publicKey)
    {
        string encoded = publicKey.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? throw new InvalidDataException("缺少电脑的 ADB 公钥。");
        byte[] key;
        try { key = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new InvalidDataException("电脑的 ADB 公钥格式不正确。"); }
        if (key.Length is < 64 or > 8192) throw new InvalidDataException("电脑的 ADB 公钥格式不正确。");
        // Android's paired-device identifier is MD5 of its binary public key; this is not an authentication hash.
        return string.Join(':', MD5.HashData(key).Select(b => b.ToString("X2")));
    }
    public async Task UnpairAsync(AndroidDevice device, CancellationToken token)
    {
        var adb = _adb ?? throw new InvalidOperationException("Unpairing requires the bundled ADB runtime.");
        if (device.State != "device") throw new IOException("手机未连接，请先重连，或在手机无线调试中移除此电脑。");
        string publicKey = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "adbkey.pub");
        if (!File.Exists(publicKey) || new FileInfo(publicKey).Length > 16384)
            throw new IOException("无法确认此电脑的配对身份，请在手机无线调试中移除此电脑。");
        string fingerprint = PairingFingerprint(await File.ReadAllTextAsync(publicKey, token).ConfigureAwait(false));
        var runtime = new VirtualDisplayRuntime(adb);
        await Task.Run(runtime.ValidateControl, token).ConfigureAwait(false);
        string remote = "/data/local/tmp/sonicroute-pairing-" + Guid.NewGuid().ToString("N") + ".jar";
        try
        {
            await adb.RunAsync(token, "-s", device.Serial, "push", runtime.ServerFile, remote).ConfigureAwait(false);
            string result;
            try { result = await adb.RunAsync(token, "-s", device.Serial, "shell", "CLASSPATH=" + remote, "app_process", "/",
                "com.genymobile.scrcpy.control.PairingControl", fingerprint, remote).ConfigureAwait(false); }
            catch (IOException ex) when (ex.Message.Contains("SR_UNPAIR_REQUESTED", StringComparison.Ordinal)) { return; }
            if (!result.Contains("SR_UNPAIR_REQUESTED", StringComparison.Ordinal) && !result.Contains("SR_UNPAIR_NOT_PAIRED", StringComparison.Ordinal))
                throw new IOException("手机未确认取消配对，请在无线调试中移除此电脑。");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await adb.RunAsync(cleanup.Token, "-s", device.Serial, "shell", "rm", "-f", remote).ConfigureAwait(false); }
            catch (IOException) { } catch (OperationCanceledException) { }
        }
    }

    public Task DisconnectAsync(AndroidDevice device, CancellationToken token)
    {
        if (!device.IsWifi) throw new InvalidOperationException("USB 连接请拔出数据线；已停止此手机的接收。");
        return RunAsync(token, "disconnect", device.Serial);
    }
}
