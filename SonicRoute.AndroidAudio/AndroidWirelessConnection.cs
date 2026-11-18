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
    internal const string ConnectType = "_adb-tls-connect._tcp";
    private readonly Func<CancellationToken, string[], Task<string>> _run;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _timeProvider;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AndroidDevice>>>? _devices;
    private readonly Func<string, string?, string?, string?, TimeSpan, CancellationToken, Task<AndroidMdnsService?>>? _discover;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AndroidMdnsService>>>? _browseConnections;
    public AndroidWirelessConnection(AdbClient adb) : this((token, args) => adb.RunAsync(token, args))
    {
        _adb = adb;
        _devices = token => adb.DevicesAsync(token, deduplicate: false);
        _discover = AndroidMdnsDiscovery.FindAsync;
        _browseConnections = AndroidMdnsDiscovery.DiscoverConnectionsAsync;
    }
    public AndroidWirelessConnection(Func<CancellationToken, string[], Task<string>> run,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<string, string?, string?, string?, TimeSpan, CancellationToken, Task<AndroidMdnsService?>>? discover = null,
        TimeProvider? timeProvider = null)
    { _run = run; _delay = delay ?? Task.Delay; _discover = discover; _timeProvider = timeProvider ?? TimeProvider.System; }
    private async Task<AndroidMdnsService?> DiscoverAsync(string type, string? name, string? host, string? guid,
        TimeSpan duration, CancellationToken token)
    {
        if (_discover == null) return null;
        try { return await _discover(type, name, host, guid, duration, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or SocketException or NetworkInformationException)
        { return null; } // Optional network discovery must never disable ADB's own discovery/connection.
    }
    private async Task<IReadOnlyList<AndroidMdnsService>> DiscoverConnectionEndpointsAsync(string? host, string? guid,
        AndroidDevice? saved, CancellationToken token)
    {
        if (AndroidDeviceIdentity.StableKey(saved) != null && _browseConnections != null)
        {
            try { return await _browseConnections(token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or SocketException or NetworkInformationException) { return []; }
        }

        var service = await DiscoverAsync(ConnectType, null, host, guid, TimeSpan.FromSeconds(12), token).ConfigureAwait(false);
        return service == null ? [] : [service];
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
            throw AndroidError.WithCode(new ArgumentException("请输入手机显示的 IP:端口，例如 192.168.1.100:37099。"), AndroidErrorCode.InvalidAddress);
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
    internal static async Task<IReadOnlyList<AndroidSavedConnection>> VerifySavedConnectionsAsync(
        IReadOnlyList<AndroidSavedConnection> candidates, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (candidates.Count == 0) return [];

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromMilliseconds(550));
        var groups = candidates.GroupBy(c => AndroidDeviceIdentity.Key(c.Device), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Take(4).ToArray()).ToArray();
        var probes = groups.SelectMany(g => g).Select(c => c.Address).Distinct(StringComparer.Ordinal)
            .ToDictionary(address => address, address => IsEndpointReachableAsync(address, budget.Token), StringComparer.Ordinal);
        await Task.WhenAll(probes.Values).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return groups.Select(g => g.FirstOrDefault(c => probes[c.Address].Result)).OfType<AndroidSavedConnection>()
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Address, StringComparer.Ordinal).ToArray();
    }

    private static async Task<bool> IsEndpointReachableAsync(string address, CancellationToken token)
    {
        if (!IPEndPoint.TryParse(address, out var endpoint) || endpoint.Port == 0) return false;
        try
        {
            // TCP only. The explicit connection still authenticates and checks the device identity.
            using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return false; }
    }

    public async Task<AndroidPairingResult> PairCodeAsync(string address, string code, CancellationToken token)
    {
        if (!Regex.IsMatch(code, "\\A[0-9]{6}\\z")) throw AndroidError.WithCode(new ArgumentException("请输入手机显示的 6 位配对码。"), AndroidErrorCode.InvalidPairCode);
        address = ValidateAddress(address);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), _timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try { return await PairAsync(address, code, deadline.Token).ConfigureAwait(false); }
        catch (Exception ex) when (deadline.IsCancellationRequested
            && ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            if (token.IsCancellationRequested) throw new OperationCanceledException("Pairing cancelled.", ex, token);
            throw AndroidError.WithCode(new TimeoutException("配对超时，请重新打开手机上的配对码窗口后重试。", ex), AndroidErrorCode.PairTimedOut);
        }
    }
    private async Task<AndroidPairingResult> PairAsync(string address, string password, CancellationToken token)
    {
        string result = await RunAsync(token, "pair", address, password).ConfigureAwait(false);
        if (!result.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
            throw AndroidError.WithCode(new IOException("配对未完成，请检查地址、配对码和手机上的配对窗口。"), AndroidErrorCode.PairFailed);
        var guid = Regex.Match(result, @"\[guid=([^\]\s]+)\]", RegexOptions.IgnoreCase);
        return new(address, guid.Success ? guid.Groups[1].Value : null);
    }
    public async Task<AndroidPairingResult> PairQrAsync(AndroidQrPairing qr, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2), _timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
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
        { throw AndroidError.WithCode(new TimeoutException("二维码已过期，点击重新生成；也可以使用配对码连接。"), AndroidErrorCode.QrExpired); }
        finally { discoveryLifetime.Cancel(); try { await discovery.ConfigureAwait(false); } catch (OperationCanceledException) { } }
    }
    public static bool MatchesPairingName(string actual, string expected) => actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
        || actual.StartsWith(expected + " (", StringComparison.OrdinalIgnoreCase) && actual.EndsWith(')')
            && int.TryParse(actual.AsSpan(expected.Length + 2, actual.Length - expected.Length - 3), out int suffix) && suffix >= 2;
    private async Task SendConnectAsync(string address, CancellationToken token)
    {
        string result = await RunAsync(token, "connect", address).ConfigureAwait(false);
        if (!result.Contains("connected to", StringComparison.OrdinalIgnoreCase))
            throw AndroidError.WithCode(new IOException("连接未完成，请填写无线调试首页的连接地址，配对端口不能用于连接。"), AndroidErrorCode.ConnectFailed);
    }
    public async Task<AndroidConnectionResult> ConnectAsync(string address, CancellationToken token)
    {
        address = ValidateAddress(address);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20), _timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try
        {
            await SendConnectAsync(address, deadline.Token).ConfigureAwait(false);
            return await WaitForDeviceAsync(Host(address), address, null, null, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw AndroidError.WithCode(new IOException("连接尚未就绪，请确认手机已授权，并检查无线调试首页的当前连接地址。"), AndroidErrorCode.ConnectNotReady); }
    }
    public async Task<AndroidConnectionResult> FinishPairingAsync(AndroidPairingResult pairing, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20), _timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        using var discoveryLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        string host = Host(ValidateAddress(pairing.Address));
        var discovery = DiscoverConnectionEndpointsAsync(host, pairing.Guid, null, discoveryLifetime.Token);
        try { return await WaitForDeviceAsync(host, null, pairing.Guid, null, deadline.Token, discovery).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw AndroidError.WithCode(new IOException("配对已成功，但设备尚未连接。请在下方填写无线调试首页的连接地址，无需重新配对。"), AndroidErrorCode.PairedButNotConnected); }
        finally { discoveryLifetime.Cancel(); try { await discovery.ConfigureAwait(false); } catch (OperationCanceledException) { } }
    }
    public Task<AndroidConnectionResult> ReconnectAsync(AndroidDevice device, CancellationToken token) => ReconnectAsync(device, null, token);

    internal async Task<AndroidConnectionResult> ReconnectAsync(AndroidDevice device, string? discoveredAddress, CancellationToken token)
    {
        if (!device.IsWifi) throw AndroidError.WithCode(new InvalidOperationException("请连接 USB 并在手机确认调试授权，然后刷新设备。"), AndroidErrorCode.UsbReconnectHint);
        bool hasSavedIdentity = AndroidDeviceIdentity.StableKey(device) != null;
        string? address = null, guid = null;
        try { address = ValidateAddress(device.Serial); }
        catch (ArgumentException)
        {
            string name = ServiceName(device.Serial, ConnectType);
            if (device.Serial.Contains("." + ConnectType, StringComparison.OrdinalIgnoreCase) && name.StartsWith("adb-", StringComparison.OrdinalIgnoreCase))
            {
                int dash = name.LastIndexOf('-');
                guid = dash > 3 ? name[..dash] : name;
            }
            else if (!hasSavedIdentity && discoveredAddress == null)
                throw AndroidError.WithCode(new IOException("保存的无线地址已失效，请填写无线调试首页的当前连接地址。"), AndroidErrorCode.SavedWifiAddressExpired);
        }
        if (discoveredAddress != null) address = ValidateAddress(discoveredAddress);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20), _timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        string? host = address == null ? null : Host(address);
        try
        {
            IReadOnlyList<AndroidDevice> currentDevices;
            try { currentDevices = await DevicesAsync(deadline.Token).ConfigureAwait(false); }
            catch (IOException) { currentDevices = Array.Empty<AndroidDevice>(); }
            var ready = FindMatchingReadyDevice(currentDevices, host, address, guid, device);
            if (ready != null) return new(ready, currentDevices);

            bool connected = false;
            if (address != null)
            {
                using var connectProbe = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                connectProbe.CancelAfter(TimeSpan.FromSeconds(3));
                try { await SendConnectAsync(address, connectProbe.Token).ConfigureAwait(false); connected = true; }
                catch (Exception ex) when (ex is IOException or SocketException) { /* look up the current TLS endpoint below */ }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            }

            if (!connected || hasSavedIdentity)
            {
                using var discoveryLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var discovery = DiscoverConnectionEndpointsAsync(host, guid, device, discoveryLifetime.Token);
                try
                {
                    return await WaitForDeviceAsync(host, address, guid, device.Serial, deadline.Token, discovery, device).ConfigureAwait(false);
                }
                finally
                {
                    discoveryLifetime.Cancel();
                    try { await discovery.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
            }

            return await WaitForDeviceAsync(host, address, guid, device.Serial, deadline.Token, saved: device).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw AndroidError.WithCode(new IOException("设备未连接，请开启手机无线调试，或填写首页的当前连接地址。"), AndroidErrorCode.DeviceNotConnectedWifiHint); }
    }
    private static bool MatchesGuid(string serial, string? guid)
    {
        if (guid == null || !serial.Contains("." + ConnectType, StringComparison.OrdinalIgnoreCase)) return false;
        string name = ServiceName(serial, ConnectType);
        if (!guid.StartsWith("adb-", StringComparison.OrdinalIgnoreCase)) guid = "adb-" + guid;
        return name.Equals(guid, StringComparison.OrdinalIgnoreCase) || name.StartsWith(guid + "-", StringComparison.OrdinalIgnoreCase);
    }
    private static AndroidDevice? FindMatchingReadyDevice(IReadOnlyList<AndroidDevice> devices, string? host, string? address,
        string? guid, AndroidDevice saved)
    {
        if (!string.IsNullOrWhiteSpace(saved.HardwareId))
            return devices.FirstOrDefault(d => d.State == "device" && d.IsWifi
                && string.Equals(d.HardwareId, saved.HardwareId, StringComparison.OrdinalIgnoreCase));
        if (AndroidDeviceIdentity.StableKey(saved) is { } identity)
            return devices.FirstOrDefault(d => d.State == "device" && d.IsWifi
                && string.Equals(AndroidDeviceIdentity.StableKey(d), identity, StringComparison.OrdinalIgnoreCase));
        if (guid != null)
            return devices.FirstOrDefault(d => d.State == "device" && d.IsWifi && MatchesGuid(d.Serial, guid));
        foreach (var d in devices)
        {
            if (d.State != "device" || !d.IsWifi) continue;
            if (string.Equals(d.Serial.TrimEnd('.'), saved.Serial.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
                return d;
            if (address != null && string.Equals(d.Serial.TrimEnd('.'), address, StringComparison.OrdinalIgnoreCase))
                return d;
            if (MatchesGuid(d.Serial, guid))
                return d;
            if (address == null && host != null)
            {
                try { if (Host(ValidateAddress(d.Serial)) == host) return d; }
                catch (ArgumentException) { }
            }
        }
        return null;
    }
    private static TimeSpan RetryDelay(int attempt) => attempt switch
    {
        < 2 => TimeSpan.FromMilliseconds(150),
        < 5 => TimeSpan.FromMilliseconds(350),
        _ => TimeSpan.FromMilliseconds(750),
    };
    private async Task<AndroidConnectionResult> WaitForDeviceAsync(string? host, string? address, string? guid, string? serial, CancellationToken token,
        Task<IReadOnlyList<AndroidMdnsService>>? discovery = null, AndroidDevice? saved = null)
    {
        string? hardwareId = saved?.HardwareId;
        string? savedIdentity = AndroidDeviceIdentity.StableKey(saved);
        bool hasSavedIdentity = savedIdentity != null;
        var serials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (serial != null) serials.Add(serial.TrimEnd('.'));
        if (address != null) serials.Add(address);
        var endpoints = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (hasSavedIdentity && address != null) endpoints[address] = 0;
        var identityRefreshes = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<AndroidMdnsService> backendServices = [];
        bool readBackendServices = false;
        IOException? lastError = null;
        bool Matches(AndroidDevice d)
        {
            if (d.State != "device" || !d.IsWifi) return false;
            if (!string.IsNullOrWhiteSpace(hardwareId))
                return string.Equals(d.HardwareId, hardwareId, StringComparison.OrdinalIgnoreCase);
            if (savedIdentity != null)
                return string.Equals(AndroidDeviceIdentity.StableKey(d), savedIdentity, StringComparison.OrdinalIgnoreCase);
            if (serials.Contains(d.Serial.TrimEnd('.')) || MatchesGuid(d.Serial, guid)) return true;
            try { return guid == null && address == null && host != null && Host(ValidateAddress(d.Serial)) == host; }
            catch (ArgumentException) { return false; }
        }
        bool MatchesService(AndroidMdnsService service) => service.Type == ConnectType && (hasSavedIdentity
            ? AndroidDeviceIdentity.MatchesWirelessService(saved!, service)
            : guid != null ? MatchesGuid(service.Name + "." + ConnectType, guid) : host != null && Host(service.Address) == host);
        for (int attempt = 0; !token.IsCancellationRequested; ++attempt)
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<AndroidDevice> devices;
            try { devices = await DevicesAsync(token).ConfigureAwait(false); }
            catch (IOException ex) { lastError = ex; devices = Array.Empty<AndroidDevice>(); }
            if (_adb != null && !string.IsNullOrWhiteSpace(hardwareId))
            {
                // One on-demand retry per transport repairs a cached transient identity failure.
                // Keep the strong check without turning the readiness loop into identity polling.
                devices = await Task.WhenAll(devices.Select(d => d.State == "device" && d.IsWifi
                    && string.IsNullOrWhiteSpace(d.HardwareId) && identityRefreshes.Add(d.Serial + "\0" + d.TransportId)
                        ? _adb.RefreshMissingIdentityAsync(d, token) : Task.FromResult(d))).ConfigureAwait(false);
            }
            var ready = devices.FirstOrDefault(Matches);
            if (ready != null) return new(ready, devices);
            if (attempt % 2 == 0 && (!hasSavedIdentity || discovery is not { IsCompleted: false }))
            {
                try
                {
                    var native = discovery?.IsCompletedSuccessfully == true ? await discovery.ConfigureAwait(false) : [];
                    if (!hasSavedIdentity || (!readBackendServices && !native.Any(s => MatchesService(s) && !endpoints.ContainsKey(s.Address))))
                    {
                        readBackendServices = true;
                        try { backendServices = ParseServices(await RunAsync(token, "mdns", "services").ConfigureAwait(false)); }
                        catch (Exception ex) when (ex is IOException or SocketException) { lastError = ex as IOException ?? new IOException(ex.Message, ex); }
                    }
                    var services = hasSavedIdentity ? native.Concat(backendServices) : backendServices.Concat(native);
                    foreach (var service in services.Where(MatchesService).DistinctBy(s => s.Address, StringComparer.Ordinal))
                    {
                        serials.Add(service.Name + "." + ConnectType);
                        ready = devices.FirstOrDefault(Matches);
                        if (ready != null) return new(ready, devices);
                        if (endpoints.TryGetValue(service.Address, out int previous) && (hasSavedIdentity || attempt - previous < 4)) continue;
                        endpoints[service.Address] = attempt;
                        using var endpointBudget = hasSavedIdentity ? CancellationTokenSource.CreateLinkedTokenSource(token) : null;
                        endpointBudget?.CancelAfter(TimeSpan.FromSeconds(2));
                        try { await SendConnectAsync(service.Address, endpointBudget?.Token ?? token).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is IOException or SocketException) { lastError = ex as IOException ?? new IOException(ex.Message, ex); }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                        address ??= service.Address;
                        serials.Add(service.Address);
                        if (hasSavedIdentity) break; // Recheck the ADB transport before trying the next address.
                    }
                }
                catch (IOException ex) { lastError = ex; }
                if (address != null && !hasSavedIdentity)
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
            if (!token.IsCancellationRequested)
            {
                var delay = _delay(RetryDelay(attempt), token);
                if (discovery is { IsCompleted: false })
                {
                    if (await Task.WhenAny(delay, discovery).ConfigureAwait(false) == delay)
                        await delay.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
                else
                {
                    await delay.ConfigureAwait(false);
                }
            }
        }
        token.ThrowIfCancellationRequested();
        throw AndroidError.WithCode(new IOException("设备尚未就绪，请在手机确认调试授权；已配对时可填写无线调试首页的连接地址，无需重新配对。", lastError), AndroidErrorCode.DeviceNotReadyConfirmAuth);
    }

    public static string PairingFingerprint(string publicKey)
    {
        string encoded = publicKey.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? throw AndroidError.WithCode(new InvalidDataException("缺少电脑的 ADB 公钥。"), AndroidErrorCode.MissingAdbPublicKey);
        byte[] key;
        try { key = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw AndroidError.WithCode(new InvalidDataException("电脑的 ADB 公钥格式不正确。"), AndroidErrorCode.InvalidAdbPublicKey); }
        if (key.Length is < 64 or > 8192) throw AndroidError.WithCode(new InvalidDataException("电脑的 ADB 公钥格式不正确。"), AndroidErrorCode.InvalidAdbPublicKey);
        // Android's paired-device identifier is MD5 of its binary public key; this is not an authentication hash.
        return string.Join(':', MD5.HashData(key).Select(b => b.ToString("X2")));
    }
    public async Task UnpairAsync(AndroidDevice device, CancellationToken token, Func<Task>? beforeUnpair = null)
    {
        var adb = _adb ?? throw new InvalidOperationException("Unpairing requires the bundled ADB runtime.");
        if (device.State != "device") throw AndroidError.WithCode(new IOException("手机未连接，请先重连，或在手机无线调试中移除此电脑。"), AndroidErrorCode.UnpairPhoneOffline);
        // The caller's cleanup callback may own UI resources; retain its context until it runs.
        var (boundAdb, fingerprint) = await adb.BindPairingIdentityAsync(token);
        await adb.PrepareRuntimeAsync("audio", token);
        adb.ValidateCatalog();
        token.ThrowIfCancellationRequested();
        if (beforeUnpair != null) await beforeUnpair();
        token.ThrowIfCancellationRequested();
        adb = boundAdb;
        string remote = "/data/local/tmp/sonicroute-pairing-" + Guid.NewGuid().ToString("N") + ".jar";
        try
        {
            await adb.RunAsync(token, "-s", device.Serial, "push", adb.CatalogFile, remote).ConfigureAwait(false);
            string result;
            try { result = await adb.RunAsync(token, "-s", device.Serial, "shell", "CLASSPATH=" + remote, "app_process", "/",
                "org.sonicroute.android.PairingControl", fingerprint, remote).ConfigureAwait(false); }
            catch (IOException ex) when (ex.Message.Contains("SR_UNPAIR_REQUESTED", StringComparison.Ordinal)) { return; }
            if (!result.Contains("SR_UNPAIR_REQUESTED", StringComparison.Ordinal) && !result.Contains("SR_UNPAIR_NOT_PAIRED", StringComparison.Ordinal))
                throw AndroidError.WithCode(new IOException("手机未确认取消配对，请在无线调试中移除此电脑。"), AndroidErrorCode.UnpairNotConfirmed);
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
        if (!device.IsWifi) throw AndroidError.WithCode(new InvalidOperationException("USB 连接请拔出数据线；已停止此手机的接收。"), AndroidErrorCode.UsbDisconnectHint);
        return RunAsync(token, "disconnect", device.Serial);
    }
}
