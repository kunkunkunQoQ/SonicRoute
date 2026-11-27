using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SonicRoute.AndroidAudio;

public sealed class AdbClient : IDisposable, IAsyncDisposable
{
    private readonly object _identityGate = new();
    private readonly Dictionary<string, (string? HardwareId, string? Fingerprint)> _identities = new(StringComparer.Ordinal);
    private readonly AndroidRuntimeBundle? _bundle;
    private readonly string? _runtimeDirectory;
    private readonly IAdbTransport? _explicitTransport;
    private IAdbTransport? _resolvedTransport;
    private bool _catalogVerified;
    private readonly object _encoderGate = new();
    private readonly Dictionary<string, AndroidEncoderCatalog> _encoderCatalogs = new(StringComparer.Ordinal);
    private int _encoderRevision;
    private readonly object _jarGate = new();
    private readonly Dictionary<string, JarGateEntry> _jarGates = new(StringComparer.Ordinal);
    private string? _cachedJarPath, _cachedJarTag;
    private long _cachedJarLength, _cachedJarWriteTicks;

    private sealed class JarGateEntry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Refs;
    }

    public string RuntimeDirectory => _bundle?.DirectoryPath ?? _runtimeDirectory!;
    public string CatalogFile => Path.Combine(RuntimeDirectory, "sonicroute-catalog.jar");

    public void ValidateCatalog()
    {
        lock (_identityGate)
        {
            if (_catalogVerified) return;
            PrepareRuntime("audio");
            if (!File.Exists(CatalogFile) || new FileInfo(CatalogFile).Length == 0)
                throw AndroidError.WithCode(new InvalidDataException("手机目录服务组件缺失或损坏，请重新下载完整版本。"), AndroidErrorCode.ServerVerifyFailed);
            _catalogVerified = true;
        }
    }

    internal async Task<string> EnsureCatalogJarAsync(string commandTarget, CancellationToken token)
    {
        if (string.IsNullOrEmpty(commandTarget))
            throw new ArgumentException("Target device is required.", nameof(commandTarget));
        token.ThrowIfCancellationRequested();
        ValidateCatalog();
        var info = new FileInfo(CatalogFile);
        if (!info.Exists || info.Length == 0)
            throw AndroidError.WithCode(new InvalidDataException("手机目录服务组件缺失或损坏，请重新下载完整版本。"), AndroidErrorCode.ServerVerifyFailed);

        (string tag, string expectedLen) = GetCatalogFingerprint(info);
        string remote = "/data/local/tmp/sonicroute-catalog-" + tag + ".jar";

        JarGateEntry entry;
        lock (_jarGate)
        {
            if (!_jarGates.TryGetValue(commandTarget, out entry!))
                _jarGates[commandTarget] = entry = new JarGateEntry();
            entry.Refs++;
        }
        try
        {
            await entry.Semaphore.WaitAsync(token).ConfigureAwait(false);
            try
            {
                string check = await RunAsync(token, "-s", commandTarget, "shell",
                    $"ls -l {remote} 2>/dev/null || echo MISSING").ConfigureAwait(false);
                if (!check.Contains("MISSING", StringComparison.Ordinal) && RemoteJarMatchesSize(check, expectedLen, remote))
                    return remote;

                string tmp = $"{remote}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await RunAsync(token, "-s", commandTarget, "push", info.FullName, tmp).ConfigureAwait(false);
                    await RunAsync(token, "-s", commandTarget, "shell", $"chmod 0644 {tmp} && mv -f {tmp} {remote}").ConfigureAwait(false);
                }
                catch
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await RunAsync(cleanup.Token, "-s", commandTarget, "shell", "rm", "-f", tmp).ConfigureAwait(false); }
                    catch { }
                    throw;
                }
                return remote;
            }
            finally { entry.Semaphore.Release(); }
        }
        finally
        {
            lock (_jarGate)
            {
                if (--entry.Refs == 0 && _jarGates.TryGetValue(commandTarget, out var current) && ReferenceEquals(current, entry))
                {
                    _jarGates.Remove(commandTarget);
                    entry.Semaphore.Dispose();
                }
            }
        }
    }

    private (string Tag, string ExpectedLen) GetCatalogFingerprint(FileInfo info)
    {
        lock (_jarGate)
        {
            if (_cachedJarTag != null
                && string.Equals(_cachedJarPath, info.FullName, StringComparison.OrdinalIgnoreCase)
                && _cachedJarLength == info.Length
                && _cachedJarWriteTicks == info.LastWriteTimeUtc.Ticks)
            {
                return (_cachedJarTag, _cachedJarLength.ToString(CultureInfo.InvariantCulture));
            }
            using var fs = File.OpenRead(info.FullName);
            byte[] hash = SHA256.HashData(fs);
            _cachedJarTag = Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
            _cachedJarPath = info.FullName;
            _cachedJarLength = info.Length;
            _cachedJarWriteTicks = info.LastWriteTimeUtc.Ticks;
            return (_cachedJarTag, _cachedJarLength.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static bool RemoteJarMatchesSize(string output, string expectedLen, string remote)
    {
        string fileName = Path.GetFileName(remote);
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.Contains("MISSING", StringComparison.Ordinal)
                || line.Contains("No such file", StringComparison.OrdinalIgnoreCase)
                || !line.EndsWith(fileName, StringComparison.Ordinal))
                continue;
            string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 2; i < tokens.Length - 1; i++)
            {
                if (tokens[i] == expectedLen) return true;
            }
        }
        return false;
    }

    internal async Task<bool> TerminateRemoteProcessAsync(string commandTarget, string? pid, Func<string, bool> isOwnedCommand,
        Action<string>? onError, CancellationToken token)
    {
        if (string.IsNullOrEmpty(commandTarget) || string.IsNullOrWhiteSpace(pid)) return false;
        try
        {
            string command = await RunAsync(token, "-s", commandTarget, "shell", "cat", $"/proc/{pid}/cmdline").ConfigureAwait(false);
            if (!isOwnedCommand(command.Replace('\0', ' '))) return false;
            await RunAsync(token, "-s", commandTarget, "shell", "kill", "-TERM", pid).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
            return false;
        }
    }

    public AdbClient(string runtimeDirectory) : this(runtimeDirectory, null) { }
    public AdbClient(AndroidRuntimeBundle bundle) : this(bundle, null) { }

    public AdbClient(string runtimeDirectory, IAdbTransport? transport)
    {
        _runtimeDirectory = Path.GetFullPath(runtimeDirectory);
        _explicitTransport = transport;
    }

    public AdbClient(AndroidRuntimeBundle bundle, IAdbTransport? transport)
    {
        _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
        _explicitTransport = transport;
    }

    internal void PrepareRuntime(string group) => _bundle?.Prepare(group);
    internal Task PrepareRuntimeAsync(string group, CancellationToken token) => _bundle?.PrepareAsync(group, token) ?? Task.CompletedTask;

    internal Task<IAdbTransport> ResolveTransportAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_explicitTransport != null) return Task.FromResult(_explicitTransport);
        var existing = Volatile.Read(ref _resolvedTransport);
        if (existing != null) return Task.FromResult(existing);

        var created = new ArbitratedAdbTransport(new IPEndPoint(IPAddress.Loopback, 5037));
        var prior = Interlocked.CompareExchange(ref _resolvedTransport, created, null);
        if (prior != null)
        {
            _ = created.DisposeAsync();
            return Task.FromResult(prior);
        }
        return Task.FromResult<IAdbTransport>(created);
    }

    private async Task<T> ExecuteWithTransportAsync<T>(CancellationToken token, Func<IAdbTransport, Task<T>> operation)
    {
        var transport = await ResolveTransportAsync(token).ConfigureAwait(false);
        try
        {
            return await operation(transport).ConfigureAwait(false);
        }
        catch (SocketException) when (_explicitTransport == null && !token.IsCancellationRequested)
        {
            transport = await ResolveTransportAsync(token).ConfigureAwait(false);
            return await operation(transport).ConfigureAwait(false);
        }
    }

    public Task<IAdbShellSession> OpenShellAsync(string serial, string command, bool redirectInput, CancellationToken token) =>
        ExecuteWithTransportAsync(token, t => t.OpenShellAsync(serial, command, redirectInput, token));

    public Task<Stream> OpenLocalAbstractAsync(string serial, string name, CancellationToken token) =>
        ExecuteWithTransportAsync(token, t => t.OpenLocalAbstractAsync(serial, name, token));

    public async Task TrackDevicesAsync(Action<IReadOnlyList<AndroidDevice>> onChanged, CancellationToken token)
    {
        var transport = await ResolveTransportAsync(token).ConfigureAwait(false);
        await transport.TrackDevicesAsync(onChanged, token).ConfigureAwait(false);
    }

    internal async Task WatchPanelWirelessAsync(Action<string> lost, Action changed, CancellationToken token)
    {
        var transport = await ResolveTransportAsync(token).ConfigureAwait(false);
        switch (transport)
        {
            case ArbitratedAdbTransport arbiter:
                await arbiter.WatchPanelWirelessAsync(lost, changed, token).ConfigureAwait(false);
                break;
            case AdbSocketClient socket:
                await socket.WatchPanelWirelessAsync(lost, changed, token).ConfigureAwait(false);
                break;
            case DirectAdbTransport direct:
                direct.WifiTransportLost += lost;
                try { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); }
                finally { direct.WifiTransportLost -= lost; }
                break;
        }
    }

    public Task<string> RunAsync(CancellationToken token, params string[] arguments)
    {
        token.ThrowIfCancellationRequested();
        if (arguments.Length == 1 && arguments[0] == "version")
            return ExecuteWithTransportAsync(token, transport => transport.VersionAsync(token));

        return ExecuteWithTransportAsync(token, transport => DispatchTransportCommandAsync(transport, arguments, token));
    }

    internal async Task<(AdbClient Client, string Fingerprint)> BindPairingIdentityAsync(CancellationToken token, AndroidDevice? device = null)
    {
        var transport = await ResolveTransportAsync(token).ConfigureAwait(false);
        if (transport is ArbitratedAdbTransport arbitrated)
            transport = device == null ? await arbitrated.SelectAsync(token).ConfigureAwait(false)
                : await arbitrated.SelectDeviceAsync(device.CommandTarget, token).ConfigureAwait(false);
        string? fingerprint = transport is IAdbPairingIdentityProvider identity
            ? await identity.GetPairingFingerprintAsync(token).ConfigureAwait(false) : null;
        if (string.IsNullOrWhiteSpace(fingerprint))
            throw AndroidError.WithCode(new IOException("无法确认此电脑的配对身份，请在手机无线调试中移除此电脑。"), AndroidErrorCode.UnpairIdentityUnavailable);
        return (new AdbClient(RuntimeDirectory, transport), fingerprint);
    }

    internal static bool SupportsAliasForward(AndroidDevice device) => !device.IsApp && device.HardwareId != null
        && device.ConnectionTarget is { } target && AdbTransportTarget.TryParse(target, out bool direct, out _) && direct;

    internal async Task<SrapAdbAttachment> CaptureAliasAttachmentAsync(AndroidDevice device, CancellationToken token)
    {
        if (!SupportsAliasForward(device)) throw new SrapException("unsupported");
        var transport = await ResolveTransportAsync(token).ConfigureAwait(false);
        if (transport is ArbitratedAdbTransport arbiter)
            transport = await arbiter.SelectDeviceAsync(device.ConnectionTarget!, token).ConfigureAwait(false);
        if (transport is not DirectAdbTransport direct) throw new SrapException("unsupported");
        return await direct.CaptureAliasAttachmentAsync(device.ConnectionTarget!, token).ConfigureAwait(false);
    }

    private async Task<string> DispatchTransportCommandAsync(IAdbTransport transport, string[] arguments, CancellationToken token)
    {
        if (arguments.Length is 1 or 2 && arguments[0] == "devices")
        {
            var devices = await transport.DevicesAsync(token).ConfigureAwait(false);
            return FormatDevicesText(devices);
        }
        if (arguments.Length == 2 && arguments[0] == "mdns" && arguments[1] == "services")
        {
            var services = await transport.MdnsServicesAsync(token).ConfigureAwait(false);
            return string.Join('\n', services.Select(s => $"{s.Name}\t{s.Type}\t{s.Address}"));
        }
        if (arguments.Length == 3 && arguments[0] == "pair")
            return await transport.PairAsync(arguments[1], arguments[2], token).ConfigureAwait(false);
        if (arguments.Length == 2 && arguments[0] == "connect")
            return await transport.ConnectAsync(arguments[1], token).ConfigureAwait(false);
        if (arguments.Length == 2 && arguments[0] == "disconnect")
            return await transport.DisconnectAsync(arguments[1], token).ConfigureAwait(false);
        if (arguments.Length == 2 && arguments[0] == "forward" && arguments[1] == "--list")
        {
            var rules = await transport.ListForwardsAsync(token).ConfigureAwait(false);
            return string.Join('\n', rules.Select(r => $"{r.Serial} {r.Local} {r.Remote}"));
        }

        if (arguments.Length >= 3 && arguments[0] == "-s")
        {
            string serial = arguments[1];
            string verb = arguments[2];
            if (verb == "shell" && arguments.Length >= 4)
            {
                string cmd = string.Join(' ', arguments.Skip(3));
                var result = await transport.RunShellAsync(serial, cmd, token).ConfigureAwait(false);
                if (result.ExitCode != 0)
                    throw new IOException($"ADB: {result.Error.Trim()} {result.Output.Trim()}".Trim());
                return result.Output;
            }
            if (verb == "push" && arguments.Length == 5)
            {
                await transport.PushAsync(serial, arguments[3], arguments[4], token).ConfigureAwait(false);
                return "";
            }
            if (verb == "forward" && arguments.Length == 5 && arguments[3] == "tcp:0"
                && arguments[4].StartsWith("localabstract:", StringComparison.Ordinal))
            {
                int port = await transport.ForwardTcpToLocalAbstractAsync(serial, arguments[4]["localabstract:".Length..], token).ConfigureAwait(false);
                return port.ToString(CultureInfo.InvariantCulture);
            }
            if (verb == "forward" && arguments.Length == 5 && arguments[3] == "--remove")
            {
                await transport.RemoveForwardAsync(serial, arguments[4], token).ConfigureAwait(false);
                return "";
            }
            if (verb == "get-serialno" && arguments.Length == 3)
                return await transport.GetSerialNoAsync(serial, token).ConfigureAwait(false);
        }

        throw new NotSupportedException("Unsupported ADB transport command: " + string.Join(' ', arguments));
    }

    public Task ReleaseIdleConnectionsAsync(bool includeIdleTcp = false)
    {
        var transport = Volatile.Read(ref _resolvedTransport) ?? _explicitTransport;
        if (transport is ArbitratedAdbTransport arbitrated)
            return arbitrated.ReleaseIdleConnectionsAsync(includeIdleTcp);
        if (transport is DirectAdbTransport direct)
            return direct.ReleaseIdleConnectionsAsync(includeIdleTcp);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        var resolved = Interlocked.Exchange(ref _resolvedTransport, null);
        if (resolved is IAsyncDisposable asyncDisposable)
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        else if (resolved is IDisposable disposable)
            disposable.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        var resolved = Interlocked.Exchange(ref _resolvedTransport, null);
        if (resolved is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (resolved is IDisposable disposable)
            disposable.Dispose();
    }

    private static string FormatDevicesText(IReadOnlyList<AndroidDevice> devices)
    {
        var sb = new StringBuilder("List of devices attached\n");
        foreach (var d in devices)
        {
            sb.Append(d.Serial).Append('\t').Append(d.State);
            if (!string.IsNullOrWhiteSpace(d.Name) && d.Name != d.Serial)
                sb.Append(" model:").Append(d.Name.Replace(' ', '_'));
            if (!string.IsNullOrWhiteSpace(d.TransportId))
                sb.Append(" transport_id:").Append(d.TransportId);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public async Task<IReadOnlyList<AndroidDevice>> DevicesAsync(CancellationToken token, bool deduplicate = true)
    {
        var transport = await ResolveTransportAsync(token).ConfigureAwait(false);
        var devices = await transport.DevicesAsync(token).ConfigureAwait(false);
        var resolved = await Task.WhenAll(devices.Select(device =>
        {
            string? target = AdbTransportTarget.Create(device, transport);
            if (device.State == "device" && target == null && transport is DirectAdbTransport or ArbitratedAdbTransport or AdbSocketClient)
                return Task.FromResult(device with { State = "offline" });
            return ResolveIdentityAsync(device with { ConnectionTarget = target }, token);
        })).ConfigureAwait(false);
        return deduplicate ? AndroidDeviceIdentity.Distinct(resolved) : resolved;
    }

    private async Task<AndroidDevice> ResolveIdentityAsync(AndroidDevice device, CancellationToken token, bool retryMissing = false)
    {
        if (device.State != "device") return device;
        string key = device.Serial + "\0" + (device.ConnectionTarget ?? device.TransportId);
        string? identity = retryMissing && device.ConnectionTarget != null ? device.HardwareId : null;
        string? fingerprint = null;
        lock (_identityGate) if (device.TransportId != null && _identities.TryGetValue(key, out var cached))
        {
            if (!retryMissing || cached.HardwareId != null && cached.Fingerprint != null)
                return device with { HardwareId = cached.HardwareId, IdentityFingerprint = cached.Fingerprint };
            identity ??= cached.HardwareId;
        }
        using var identityDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        identityDeadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            if (identity == null)
            {
                try
                {
                    string result = (await RunAsync(identityDeadline.Token, "-s", device.CommandTarget, "shell", "getprop", "ro.serialno").ConfigureAwait(false)).Trim();
                    if (IsHardwareIdentity(result, device)) identity = result;
                }
                catch (Exception ex) when (ex is IOException or SocketException) { /* Try transport metadata below. */ }
            }
            // The direct connection may already know the hardware serial from its
            // authorized handshake even if the shell property is unavailable.
            if (identity == null)
            {
                try
                {
                    string result = (await RunAsync(identityDeadline.Token, "-s", device.CommandTarget, "get-serialno").ConfigureAwait(false)).Trim();
                    if (IsHardwareIdentity(result, device)) identity = result;
                }
                catch (NotSupportedException) { /* Optional transport metadata fallback. */ }
            }
            // A serial alone can be duplicated on vendor ROMs. Compare the hashed
            // device-local Android ID when deciding whether USB/Wi-Fi are aliases.
            if (identity != null)
            {
                string androidId = (await RunAsync(identityDeadline.Token, "-s", device.CommandTarget,
                    "shell", "settings", "get", "secure", "android_id").ConfigureAwait(false)).Trim();
                if (Regex.IsMatch(androidId, @"\A[0-9a-fA-F]{16}\z") && androidId.Any(c => c != '0'))
                    fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(androidId.ToLowerInvariant())));
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException) { /* Identity enrichment must not fail the device scan. */ }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { /* Identity enrichment must not hold up device discovery. */ }
        token.ThrowIfCancellationRequested();
        lock (_identityGate)
        {
            if (device.TransportId != null && _identities.TryGetValue(key, out var resolved))
            { identity ??= resolved.HardwareId; fingerprint ??= resolved.Fingerprint; }
            if (_identities.Count >= 128) _identities.Clear();
            if (device.TransportId != null) _identities[key] = (identity, fingerprint); // once per transport
        }
        return device with { HardwareId = identity, IdentityFingerprint = fingerprint };
    }

    private static bool IsHardwareIdentity(string value, AndroidDevice device) => value.Length is > 0 and <= 128
        && Regex.IsMatch(value, @"\A[A-Za-z0-9_.-]+\z") && !value.Equals("unknown", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("_adb-tls-connect", StringComparison.OrdinalIgnoreCase)
        && (!device.IsWifi || !value.Equals(device.Serial, StringComparison.OrdinalIgnoreCase));

    internal Task<AndroidDevice> RefreshMissingIdentityAsync(AndroidDevice device, CancellationToken token) =>
        ResolveIdentityAsync(device, token, retryMissing: true);

    private static readonly Regex KnownDeviceStateRegex = new(
        @"\s+(?<state>no permissions(?:\s*\([^)]*\))?(?:;\s*see\s+\S+)?|device|offline|unauthorized|authorizing|connecting|bootloader|recovery|sideload|rescue|host|unknown)(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.RightToLeft | RegexOptions.Compiled);

    private static bool IsDeviceMetadataToken(string token)
    {
        int colon = token.IndexOf(':');
        if (colon <= 0) return false;
        for (int i = 0; i < colon; i++)
        {
            char c = token[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        }
        return true;
    }

    private static bool TryParseDeviceLine(string rawLine, out string serial, out string state, out string[] metadata)
    {
        serial = "";
        state = "";
        metadata = Array.Empty<string>();
        string trimmed = rawLine.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('*')
            || trimmed.Equals("List of devices attached", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
            return false;

        int tab = trimmed.IndexOf('\t');
        if (tab > 0)
        {
            serial = trimmed[..tab].Trim();
            string rest = trimmed[(tab + 1)..].TrimStart();
            if (rest.Length == 0) return false;
            var parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            state = parts[0];
            metadata = parts.Length > 1 ? parts[1..] : Array.Empty<string>();
            return serial.Length > 0 && state.Length > 0;
        }

        // Preserve serials containing spaces (e.g. mDNS collision aliases like "adb-id (2)._adb-tls-connect._tcp").
        foreach (Match match in KnownDeviceStateRegex.Matches(trimmed))
        {
            Group stateGroup = match.Groups["state"];
            string candidateSerial = trimmed[..match.Index].TrimEnd();
            if (candidateSerial.Length == 0) continue;
            string tail = trimmed[(stateGroup.Index + stateGroup.Length)..];
            string[] candidateMeta = tail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!candidateMeta.All(IsDeviceMetadataToken)) continue;
            serial = candidateSerial;
            state = stateGroup.Value;
            metadata = candidateMeta;
            return true;
        }

        string[] fields = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 || fields[0] == "List") return false;
        int firstMeta = -1;
        for (int i = fields.Length - 1; i >= 2 && IsDeviceMetadataToken(fields[i]); i--)
            firstMeta = i;
        if (firstMeta >= 2)
        {
            serial = string.Join(' ', fields[..(firstMeta - 1)]);
            state = fields[firstMeta - 1];
            metadata = fields[firstMeta..];
            return true;
        }

        serial = fields[0];
        state = fields[1];
        metadata = fields.Length > 2 ? fields[2..] : Array.Empty<string>();
        return true;
    }

    public static IReadOnlyList<AndroidDevice> ParseDevices(string text)
    {
        var devices = new List<AndroidDevice>();
        foreach (string line in text.Split('\n'))
        {
            if (!TryParseDeviceLine(line, out string serial, out string state, out string[] metadata)) continue;
            string name = metadata.FirstOrDefault(f => f.StartsWith("model:", StringComparison.Ordinal))?[6..].Replace('_', ' ') ?? serial;
            bool wifi = serial.Contains(':') || serial.Contains("_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase);
            string? transportId = metadata.FirstOrDefault(f => f.StartsWith("transport_id:", StringComparison.Ordinal))?[13..];
            devices.Add(new AndroidDevice(serial, name, state, wifi, TransportId: transportId));
        }
        return devices;
    }

    public async Task<AndroidEncoderCatalog> EncodersAsync(AndroidDevice device, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string key = AndroidDeviceIdentity.Key(device);
        int revision;
        lock (_encoderGate)
        {
            if (_encoderCatalogs.TryGetValue(key, out var cached)) return cached;
            revision = _encoderRevision;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        await PrepareRuntimeAsync("audio", deadline.Token).ConfigureAwait(false);
        string remote = await EnsureCatalogJarAsync(device.CommandTarget, deadline.Token).ConfigureAwait(false);
        string output = await RunAsync(deadline.Token, "-s", device.CommandTarget, "shell", "CLASSPATH=" + remote,
            "app_process", "/", "org.sonicroute.android.EncoderCatalog").ConfigureAwait(false);
        var catalog = ParseEncoders(output);
        deadline.Token.ThrowIfCancellationRequested();
        lock (_encoderGate)
        {
            // Small process-local capability cache; no disk work, expiry timer or idle scan.
            if (revision == _encoderRevision)
            {
                if (_encoderCatalogs.Count >= 4 && !_encoderCatalogs.ContainsKey(key))
                    _encoderCatalogs.Remove(_encoderCatalogs.Keys.First());
                _encoderCatalogs[key] = catalog;
            }
        }
        return catalog;
    }

    internal bool IsHevcEncoderUnavailable(AndroidDevice device)
    {
        string key = AndroidDeviceIdentity.Key(device);
        lock (_encoderGate)
            return _encoderCatalogs.TryGetValue(key, out var catalog) && catalog.Hevc is { Count: 0 };
    }

    public void InvalidateEncoders(AndroidDevice device)
    {
        lock (_encoderGate)
        {
            ++_encoderRevision; // An older in-flight query cannot undo an explicit refresh.
            _encoderCatalogs.Remove(AndroidDeviceIdentity.Key(device));
        }
    }

    public static AndroidEncoderCatalog ParseEncoders(string output)
    {
        foreach (string line in output.Split('\n'))
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            using var json = JsonDocument.Parse(line);
            if (!json.RootElement.TryGetProperty("audio", out var audio)
                || !json.RootElement.TryGetProperty("video", out var video)
                || audio.ValueKind != JsonValueKind.Array
                || video.ValueKind != JsonValueKind.Array) continue;
            static string[] Names(JsonElement array) => array.EnumerateArray().Take(256)
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null)
                .Where(AndroidStreamSettings.IsEncoderName)
                .Select(x => x!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            Dictionary<string, AndroidEncoderKind>? kinds = null;
            if (json.RootElement.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object)
            {
                kinds = new(StringComparer.Ordinal);
                foreach (var entry in details.EnumerateObject().Take(512))
                {
                    if (!AndroidStreamSettings.IsEncoderName(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object) continue;
                    bool hw = entry.Value.TryGetProperty("hw", out var hwProp) && hwProp.ValueKind == JsonValueKind.True;
                    bool sw = entry.Value.TryGetProperty("sw", out var swProp) && swProp.ValueKind == JsonValueKind.True;
                    if (hw) kinds[entry.Name] = AndroidEncoderKind.Hardware;
                    else if (sw) kinds[entry.Name] = AndroidEncoderKind.Software;
                }
            }
            var hevc = json.RootElement.TryGetProperty("hevc", out var hevcNames) && hevcNames.ValueKind == JsonValueKind.Array
                ? Names(hevcNames) : Array.Empty<string>();
            return new(Names(audio), Names(video), kinds, hevc);
        }
        throw new InvalidDataException("The phone did not return an encoder catalogue.");
    }

    public async Task<IReadOnlyList<AndroidApp>> AppsAsync(AndroidDevice device, CancellationToken token, IReadOnlyList<string>? packages = null)
    {
        if (packages != null)
        {
            foreach (string package in packages) ValidatePackage(package);
            if (packages.Count == 0) return Array.Empty<AndroidApp>();
            if (packages.Count > 1000) throw new ArgumentException("Too many retained applications.");
        }
        if (device.State != "device") throw AndroidError.WithCode(new IOException("请先在手机上允许 ADB 调试授权。"), AndroidErrorCode.AuthorizeAdbFirst);
        await PrepareRuntimeAsync("audio", token).ConfigureAwait(false);
        string remote = await EnsureCatalogJarAsync(device.CommandTarget, token).ConfigureAwait(false);
        var arguments = new List<string> { "-s", device.CommandTarget, "shell", "CLASSPATH=" + remote,
            "app_process", "/", "org.sonicroute.android.Catalog" };
        if (packages != null) { arguments.Add("--packages"); arguments.AddRange(packages.Distinct(StringComparer.Ordinal)); }
        string result = await RunAsync(token, arguments.ToArray()).ConfigureAwait(false);
        var apps = new List<AndroidApp>();
        foreach (string line in result.Split('\n'))
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out var error)) throw new IOException(error.GetString());
            string package = root.GetProperty("package").GetString()!;
            ValidatePackage(package);
            string? icon = root.TryGetProperty("icon", out var iconValue) ? iconValue.GetString() : null;
            byte[]? png = string.IsNullOrEmpty(icon) ? null : Convert.FromBase64String(icon);
            if (png?.Length > 128 * 1024) throw new InvalidDataException("Application icon is too large.");
            apps.Add(new AndroidApp(package, root.GetProperty("name").GetString() ?? package,
                root.GetProperty("uid").GetInt32(), Category(root.GetProperty("category").GetInt32()), png,
                root.GetProperty("category").GetInt32()));
        }
        return apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<AndroidOutputSink>> OutputsAsync(AndroidDevice device, CancellationToken token)
    {
        if (device.State != "device") throw AndroidError.WithCode(new IOException("请先在手机上允许 ADB 调试授权。"), AndroidErrorCode.AuthorizeAdbFirst);
        await PrepareRuntimeAsync("audio", token).ConfigureAwait(false);
        string remote = await EnsureCatalogJarAsync(device.CommandTarget, token).ConfigureAwait(false);
        string result = await RunAsync(token, "-s", device.CommandTarget, "shell",
            "CLASSPATH=" + remote, "app_process", "/", "org.sonicroute.android.AudioOutputBridge", "list").ConfigureAwait(false);
        foreach (string line in result.Split('\n'))
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty("error", out var error))
                throw new IOException(error.GetString());
            if (json.RootElement.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
                return AndroidOutputSink.ParseList(outputs);
        }
        return [AndroidOutputSink.DefaultSpeaker];
    }

    public Task<int> ResolveUidAsync(AndroidDevice device, string package, CancellationToken token) =>
        ResolveUidAsync(device, package, null, token);

    internal async Task<int> ResolveUidAsync(AndroidDevice device, string package, int? catalogUid, CancellationToken token)
    {
        ValidatePackage(package);
        // Android UIDs encode the user/profile ID in the high part (UserHandle.PER_USER_RANGE).
        // A package name alone cannot distinguish the primary installation from its clone.
        string user = catalogUid is > 0 ? (catalogUid.Value / 100_000).ToString(CultureInfo.InvariantCulture) : "current";
        string text = await RunAsync(token, "-s", device.CommandTarget, "shell", "pm", "list", "packages", "-U", "--user", user)
            .ConfigureAwait(false);
        var matches = Regex.Matches(text, @"(?m)^package:(\S+)[ \t]+uid:([0-9]+)[ \t\r]*$");
        var candidates = matches.Where(match => match.Groups[1].Value == package).ToArray();
        if (candidates.Length != 1 || !int.TryParse(candidates[0].Groups[2].Value, out int uid) || uid <= 0)
            throw AndroidError.WithCode(new IOException("目标应用或 UID 已不可用，请刷新手机应用列表。"), AndroidErrorCode.AppOrUidUnavailable);
        if (matches.Any(match => match.Groups[1].Value != package
            && int.TryParse(match.Groups[2].Value, out int otherUid) && uid == otherUid))
            throw AndroidError.WithCode(new IOException("目标应用与其他包共享 UID，无法保证单应用隔离；此实验版拒绝启动该目标。"), AndroidErrorCode.SharedUidUnsupported);
        return uid;
    }

    public static void ValidatePackage(string package)
    {
        if (!Regex.IsMatch(package, @"\A[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+\z"))
            throw new ArgumentException("Invalid Android package name.");
    }
    private static string Category(int value) => value switch { 0 => "games", 1 or 2 or 3 => "media", 4 => "social", _ => "other" };
}
