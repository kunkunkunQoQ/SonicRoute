using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SonicRoute.AndroidAudio;

public sealed record AndroidMdnsService(string Name, string Type, string Address);
public sealed record AndroidPairingResult(string Address, string? Guid);
public sealed record AndroidConnectionResult(AndroidDevice Device, IReadOnlyList<AndroidDevice> Devices)
{
    // A numeric endpoint bound to this live service-name transport.
    internal string? WirelessAddress { get; init; }
}
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
    private const int MaxAddressesPerDevice = 4;
    private static readonly TimeSpan EndpointProbeTimeout = TimeSpan.FromMilliseconds(550);
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
    private static string? AddressOfService(AndroidDevice device, IEnumerable<AndroidMdnsService> services)
    {
        if (!device.IsWifi || !device.Serial.Contains("." + ConnectType, StringComparison.OrdinalIgnoreCase)) return null;
        var name = ServiceName(device.Serial, ConnectType);
        var endpoints = services.Where(s => s.Type == ConnectType && name.Equals(ServiceName(s.Name, ConnectType), StringComparison.OrdinalIgnoreCase))
            .Select(s => NormalizeEndpoint(s.Address)).OfType<string>().Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return endpoints.Length == 1 ? endpoints[0] : null;
    }

    private static bool SameServiceTransport(string left, string right) =>
        left.Contains("." + ConnectType, StringComparison.OrdinalIgnoreCase) && right.Contains("." + ConnectType, StringComparison.OrdinalIgnoreCase)
        && ServiceName(left, ConnectType).Equals(ServiceName(right, ConnectType), StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<AndroidMdnsService>> ReadConnectionServicesAsync(CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromMilliseconds(1500));
        try { return ParseServices(await RunAsync(budget.Token, "mdns", "services").ConfigureAwait(false)); }
        catch (Exception ex) when (ex is IOException or SocketException or NotSupportedException) { return []; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return []; }
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

        var groups = candidates.GroupBy(c => AndroidDeviceIdentity.Key(c.Device), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Take(MaxAddressesPerDevice).ToArray()).ToArray();
        var reachable = await ProbeEndpointsAsync(groups.SelectMany(g => g).Select(c => c.Address), token).ConfigureAwait(false);
        return groups.Select(g => g.FirstOrDefault(c => reachable.Contains(c.Address))).OfType<AndroidSavedConnection>()
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Address, StringComparer.Ordinal).ToArray();
    }

    private static async Task<HashSet<string>> ProbeEndpointsAsync(IEnumerable<string> candidates, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var addresses = candidates.Distinct(StringComparer.Ordinal).ToArray();
        if (addresses.Length == 0) return new(StringComparer.Ordinal);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(EndpointProbeTimeout);
        var results = await Task.WhenAll(addresses.Select(address => IsEndpointReachableAsync(address, budget.Token))).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return addresses.Where((_, i) => results[i]).ToHashSet(StringComparer.Ordinal);
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
            return await WaitForDeviceAsync(Host(address), address, null, null, deadline.Token, connectedToAddress: true).ConfigureAwait(false);
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

    internal async Task<AndroidConnectionResult> ReconnectAsync(AndroidDevice device, string? discoveredAddress, CancellationToken token,
        IReadOnlyList<AndroidDevice>? initialDevices = null, bool retryMissingIdentity = true)
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
            var currentDevices = initialDevices ?? await ReadReconnectDevicesAsync(deadline.Token).ConfigureAwait(false);
            if (hasSavedIdentity)
                return await ReconnectSavedDeviceAsync(device, address, guid, currentDevices, retryMissingIdentity, deadline.Token).ConfigureAwait(false);

            var ready = FindMatchingReadyDevice(currentDevices, host, address, guid, device);
            if (ready != null) return await EnrichReadyResultAsync(new(ready, currentDevices), retryMissingIdentity, deadline.Token).ConfigureAwait(false);

            bool connected = false;
            if (address != null)
            {
                using var connectProbe = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                connectProbe.CancelAfter(TimeSpan.FromSeconds(3));
                try { await SendConnectAsync(address, connectProbe.Token).ConfigureAwait(false); connected = true; }
                catch (Exception ex) when (ex is IOException or SocketException) { /* look up the current TLS endpoint below */ }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            }

            if (!connected)
            {
                using var discoveryLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var discovery = DiscoverConnectionEndpointsAsync(host, guid, device, discoveryLifetime.Token);
                try
                {
                    return await EnrichReadyResultAsync(await WaitForDeviceAsync(host, address, guid, device.Serial, deadline.Token, discovery).ConfigureAwait(false), retryMissingIdentity, deadline.Token).ConfigureAwait(false);
                }
                finally
                {
                    discoveryLifetime.Cancel();
                    try { await discovery.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
            }

            return await EnrichReadyResultAsync(await WaitForDeviceAsync(host, address, guid, device.Serial, deadline.Token, connectedToAddress: true).ConfigureAwait(false), retryMissingIdentity, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw AndroidError.WithCode(new IOException("设备未连接，请开启手机无线调试，或填写首页的当前连接地址。"), AndroidErrorCode.DeviceNotConnectedWifiHint); }
    }

    // Reading a missing fingerprint on a known hardware ID preserves the key used by active streams.
    private static bool ShouldRetryIdentity(AndroidDevice device, bool allowKeyChange) =>
        string.IsNullOrWhiteSpace(device.HardwareId) ? allowKeyChange : device.IdentityFingerprint == null;

    // Retry on demand. Regular scans retain the per-transport cache, including failures.
    internal async Task<AndroidConnectionResult> EnrichReadyResultAsync(AndroidConnectionResult result, bool retryMissingIdentity, CancellationToken token)
    {
        if (_adb == null) return result;
        var ready = result.Device;
        if (ShouldRetryIdentity(ready, retryMissingIdentity))
            ready = await _adb.RefreshMissingIdentityAsync(ready, token).ConfigureAwait(false);

        bool NeedsAliasProof(AndroidDevice other) => other.State == "device" && other.IsWifi != ready.IsWifi
            && ShouldRetryIdentity(other, retryMissingIdentity) && AndroidDeviceIdentity.SamePhysicalDevice(ready, other);
        if (ready == result.Device && !result.Devices.Any(NeedsAliasProof)) return result;
        var devices = await Task.WhenAll(result.Devices.Select(d => NeedsAliasProof(d)
            ? _adb.RefreshMissingIdentityAsync(d, token) : Task.FromResult(d == result.Device ? ready : d))).ConfigureAwait(false);
        return result with { Device = ready, Devices = devices };
    }

    private async Task<IReadOnlyList<AndroidDevice>> ReadReconnectDevicesAsync(CancellationToken token)
    {
        try { return await DevicesAsync(token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or SocketException) { token.ThrowIfCancellationRequested(); return []; }
    }

    private async Task<AndroidConnectionResult> ReconnectSavedDeviceAsync(AndroidDevice saved, string? savedAddress, string? guid,
        IReadOnlyList<AndroidDevice> initialDevices, bool retryMissingIdentity, CancellationToken token)
    {
        string identity = AndroidDeviceIdentity.StableKey(saved)!;
        bool hasHardwareId = !string.IsNullOrWhiteSpace(saved.HardwareId);
        var checkedAddresses = new HashSet<string>(StringComparer.Ordinal);
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        static (string, string?) Instance(AndroidDevice d) => (d.Serial, d.ConnectionTarget ?? d.TransportId);
        var refreshed = new HashSet<(string, string?)>();
        var unavailable = new HashSet<(string, string?)>();
        var advertised = new HashSet<AndroidMdnsService>();
        var sentConnections = new HashSet<string>(StringComparer.Ordinal);
        bool awaitingTransport = false;

        bool Matches(AndroidDevice device) => device.IsWifi && device.State == "device"
            && !AndroidDeviceIdentity.ConflictingEvidence(saved, device)
            && (saved.IdentityFingerprint == null || saved.IdentityFingerprint == device.IdentityFingerprint) && (hasHardwareId
            ? string.Equals(device.HardwareId, saved.HardwareId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(AndroidDeviceIdentity.StableKey(device), identity, StringComparison.OrdinalIgnoreCase));

        async Task CheckAddressesAsync(IEnumerable<string> addresses) =>
            reachable.UnionWith(await ProbeEndpointsAsync(addresses.Where(checkedAddresses.Add), token).ConfigureAwait(false));

        async Task<AndroidConnectionResult?> ReuseAsync(IReadOnlyList<AndroidDevice> devices)
        {
            var queriedHardware = new HashSet<(string, string?)>();
            if (_adb != null)
            {
                Task<AndroidDevice> VerifyAsync(AndroidDevice candidate)
                {
                    bool missingHardware = string.IsNullOrWhiteSpace(candidate.HardwareId);
                    bool needsProof = hasHardwareId && candidate.IsWifi && missingHardware
                        || AndroidDeviceIdentity.SamePhysicalDevice(saved, candidate) && ShouldRetryIdentity(candidate, retryMissingIdentity);
                    if (candidate.State != "device" || !needsProof || !refreshed.Add(Instance(candidate)))
                        return Task.FromResult(candidate);
                    if (missingHardware) queriedHardware.Add(Instance(candidate));
                    return _adb.RefreshMissingIdentityAsync(candidate, token);
                }
                devices = await Task.WhenAll(devices.Select(VerifyAsync)).ConfigureAwait(false);
            }
            var matching = devices.Where(d => Matches(d) && !unavailable.Contains(Instance(d)))
                .OrderByDescending(d => d.Serial == saved.Serial)
                .Take(MaxAddressesPerDevice).ToArray();
            if (_adb == null)
                await CheckAddressesAsync(matching.Select(d => NormalizeEndpoint(d.Serial)).OfType<string>()).ConfigureAwait(false);
            AndroidDevice? picked = null;
            foreach (var live in matching)
            {
                bool usable;
                if (_adb != null)
                    usable = queriedHardware.Contains(Instance(live)) && live.HardwareId != null
                        || await ConfirmLiveTransportAsync(live, identity, token).ConfigureAwait(false);
                else
                    usable = NormalizeEndpoint(live.Serial) is not { } endpoint || reachable.Contains(endpoint);
                if (usable) { picked ??= live; }
                else unavailable.Add(Instance(live));
            }
            if (picked == null) return null;
            var result = new AndroidConnectionResult(picked, devices.Where(d => !unavailable.Contains(Instance(d))).ToArray());
            if (NormalizeEndpoint(picked.Serial) != null) return result;
            if (AddressOfService(picked, advertised) is { } serviceAddress) return result with { WirelessAddress = serviceAddress };
            foreach (string address in sentConnections)
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
                probe.CancelAfter(EndpointProbeTimeout);
                try
                {
                    string canonical = (await RunAsync(probe.Token, "-s", address, "get-serialno").ConfigureAwait(false)).Trim();
                    if (SameServiceTransport(canonical, picked.Serial)) return result with { WirelessAddress = address };
                }
                catch (Exception ex) when (ex is IOException or SocketException or NotSupportedException) { }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
            return result;
        }

        async Task<AndroidConnectionResult?> TryAddressesAsync(IEnumerable<string> candidates)
        {
            var addresses = candidates.Select(NormalizeEndpoint).OfType<string>().Where(attempted.Add).Take(MaxAddressesPerDevice).ToArray();
            await CheckAddressesAsync(addresses).ConfigureAwait(false);
            foreach (string address in addresses.Where(reachable.Contains))
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                budget.CancelAfter(TimeSpan.FromSeconds(2));
                try { await SendConnectAsync(address, budget.Token).ConfigureAwait(false); sentConnections.Add(address); awaitingTransport = true; }
                catch (Exception ex) when (ex is IOException or SocketException) { continue; }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
                var ready = await ReuseAsync(await ReadReconnectDevicesAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
                if (ready != null) return ready;
            }
            return null;
        }

        IEnumerable<string> Addresses(IReadOnlyList<AndroidMdnsService> services) =>
            services.Where(s => AndroidDeviceIdentity.MatchesWirelessService(saved, s)).Select(s => s.Address);

        if (await ReuseAsync(initialDevices).ConfigureAwait(false) is { } live)
        {
            if (live.Device.Serial.Contains("." + ConnectType, StringComparison.OrdinalIgnoreCase))
                return live with { WirelessAddress = AddressOfService(live.Device, await ReadConnectionServicesAsync(token).ConfigureAwait(false)) };
            return live;
        }
        if (savedAddress != null && await TryAddressesAsync([savedAddress]).ConfigureAwait(false) is { } direct) return direct;

        var native = await DiscoverConnectionEndpointsAsync(savedAddress == null ? null : Host(savedAddress), guid, saved, token).ConfigureAwait(false);
        advertised.UnionWith(native);
        // ADB may have established its own transport while the one-shot mDNS request was in flight.
        if (await ReuseAsync(await ReadReconnectDevicesAsync(token).ConfigureAwait(false)).ConfigureAwait(false) is { } discovered) return discovered;
        if (await TryAddressesAsync(Addresses(native)).ConfigureAwait(false) is { } fromMdns) return fromMdns;

        var backend = await ReadConnectionServicesAsync(token).ConfigureAwait(false);
        advertised.UnionWith(backend);
        if (await TryAddressesAsync(Addresses(backend)).ConfigureAwait(false) is { } fromBackend) return fromBackend;

        for (int attempt = 0; awaitingTransport; ++attempt)
        {
            await _delay(RetryDelay(attempt), token).ConfigureAwait(false);
            if (await ReuseAsync(await ReadReconnectDevicesAsync(token).ConfigureAwait(false)).ConfigureAwait(false) is { } ready) return ready;
            token.ThrowIfCancellationRequested();
        }
        token.ThrowIfCancellationRequested();
        throw AndroidError.WithCode(new IOException("设备未连接，请开启手机无线调试，或填写首页的当前连接地址。"), AndroidErrorCode.DeviceNotConnectedWifiHint);
    }

    private static string? NormalizeEndpoint(string address) =>
        IPEndPoint.TryParse(address, out var endpoint) && endpoint.Port > 0 ? endpoint.ToString() : null;

    private async Task<bool> ConfirmLiveTransportAsync(AndroidDevice device, string identity, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(EndpointProbeTimeout);
        try
        {
            string serial = (await RunAsync(budget.Token, "-s", device.CommandTarget, "shell", "getprop", "ro.serialno").ConfigureAwait(false)).Trim();
            return string.Equals("device:" + serial, identity, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or SocketException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
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
        Task<IReadOnlyList<AndroidMdnsService>>? discovery = null, bool connectedToAddress = false)
    {
        var serials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (serial != null) serials.Add(serial.TrimEnd('.'));
        if (address != null) serials.Add(address);
        var endpoints = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var advertised = new HashSet<AndroidMdnsService>();
        string? canonicalService = null, canonicalAddress = null;
        AndroidConnectionResult Connected(AndroidDevice d, IReadOnlyList<AndroidDevice> devices) => new(d, devices)
        {
            WirelessAddress = canonicalService != null && SameServiceTransport(canonicalService, d.Serial)
                ? canonicalAddress : AddressOfService(d, advertised)
        };
        IOException? lastError = null;
        bool Matches(AndroidDevice d)
        {
            if (d.State != "device" || !d.IsWifi) return false;
            if (serials.Contains(d.Serial.TrimEnd('.')) || MatchesGuid(d.Serial, guid)
                || serials.Any(s => SameServiceTransport(s, d.Serial))) return true;
            try { return guid == null && address == null && host != null && Host(ValidateAddress(d.Serial)) == host; }
            catch (ArgumentException) { return false; }
        }
        bool MatchesService(AndroidMdnsService service) => service.Type == ConnectType
            && (connectedToAddress ? NormalizeEndpoint(service.Address) == address
                : guid != null ? MatchesGuid(service.Name + "." + ConnectType, guid) : host != null && Host(service.Address) == host);
        for (int attempt = 0; !token.IsCancellationRequested; ++attempt)
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<AndroidDevice> devices;
            try { devices = await DevicesAsync(token).ConfigureAwait(false); }
            catch (IOException ex) { lastError = ex; devices = Array.Empty<AndroidDevice>(); }
            var native = discovery?.IsCompletedSuccessfully == true ? await discovery.ConfigureAwait(false) : [];
            advertised.UnionWith(native);
            var ready = devices.FirstOrDefault(Matches);
            if (ready != null && (NormalizeEndpoint(ready.Serial) != null || attempt > 0))
                return Connected(ready, devices);
            if (attempt % 2 == 0)
            {
                try
                {
                    var backend = await ReadConnectionServicesAsync(token).ConfigureAwait(false);
                    advertised.UnionWith(backend);
                    var services = backend.Concat(native).Where(MatchesService).Distinct().ToArray();
                    foreach (var service in services) serials.Add(service.Name + "." + ConnectType);
                    ready = devices.FirstOrDefault(Matches);
                    if (ready != null && (!connectedToAddress || AddressOfService(ready, advertised) != null))
                        return Connected(ready, devices);
                    foreach (var service in services.DistinctBy(s => s.Address, StringComparer.Ordinal))
                    {
                        if (ready != null) break;
                        if (endpoints.TryGetValue(service.Address, out int previous) && attempt - previous < 4) continue;
                        endpoints[service.Address] = attempt;
                        try { await SendConnectAsync(service.Address, token).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is IOException or SocketException) { lastError = ex as IOException ?? new IOException(ex.Message, ex); }
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
                        canonicalService = canonical;
                        canonicalAddress = address;
                        serials.Add(canonical.TrimEnd('.'));
                        ready = devices.FirstOrDefault(Matches);
                        if (ready != null) return Connected(ready, devices);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or NotSupportedException)
                    { lastError = ex as IOException ?? new IOException(ex.Message, ex); }
                }
                if (ready != null) return Connected(ready, devices);
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
        var (boundAdb, fingerprint) = await adb.BindPairingIdentityAsync(token, device);
        await adb.PrepareRuntimeAsync("audio", token);
        adb.ValidateCatalog();
        token.ThrowIfCancellationRequested();
        if (beforeUnpair != null) await beforeUnpair();
        token.ThrowIfCancellationRequested();
        adb = boundAdb;
        string remote = "/data/local/tmp/sonicroute-pairing-" + Guid.NewGuid().ToString("N") + ".jar";
        try
        {
            await adb.RunAsync(token, "-s", device.CommandTarget, "push", adb.CatalogFile, remote).ConfigureAwait(false);
            string result;
            try { result = await adb.RunAsync(token, "-s", device.CommandTarget, "shell", "CLASSPATH=" + remote, "app_process", "/",
                "org.sonicroute.android.PairingControl", fingerprint, remote).ConfigureAwait(false); }
            catch (IOException ex) when (ex.Message.Contains("SR_UNPAIR_REQUESTED", StringComparison.Ordinal)) { return; }
            if (!result.Contains("SR_UNPAIR_REQUESTED", StringComparison.Ordinal) && !result.Contains("SR_UNPAIR_NOT_PAIRED", StringComparison.Ordinal))
                throw AndroidError.WithCode(new IOException("手机未确认取消配对，请在无线调试中移除此电脑。"), AndroidErrorCode.UnpairNotConfirmed);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await adb.RunAsync(cleanup.Token, "-s", device.CommandTarget, "shell", "rm", "-f", remote).ConfigureAwait(false); }
            catch (IOException) { } catch (OperationCanceledException) { }
        }
    }

    public async Task DisconnectAsync(AndroidDevice device, CancellationToken token)
    {
        if (!device.IsWifi) throw AndroidError.WithCode(new InvalidOperationException("USB 连接请拔出数据线；已停止此手机的接收。"), AndroidErrorCode.UsbDisconnectHint);
        if (_adb != null && device.ConnectionTarget != null && AdbTransportTarget.TryParse(device.ConnectionTarget, out bool direct, out _))
        {
            // Shared ADB only provides address-wide disconnect. The host already
            // revoked this app's sessions; only release a transport we own.
            if (direct) await RunAsync(token, "disconnect", device.CommandTarget).ConfigureAwait(false);
            return;
        }
        await RunAsync(token, "disconnect", device.Serial).ConfigureAwait(false);
    }
}
