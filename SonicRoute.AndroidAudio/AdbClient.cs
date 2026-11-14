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
    private readonly Dictionary<string, string?> _identities = new(StringComparer.Ordinal);
    private readonly AndroidRuntimeBundle? _bundle;
    private readonly string? _runtimeDirectory;
    private readonly IAdbTransport? _explicitTransport;
    private IAdbTransport? _resolvedTransport;
    private bool _catalogVerified;

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

    public Task<string> RunAsync(CancellationToken token, params string[] arguments)
    {
        token.ThrowIfCancellationRequested();
        if (arguments.Length == 1 && arguments[0] == "version")
            return ExecuteWithTransportAsync(token, transport => transport.VersionAsync(token));

        return ExecuteWithTransportAsync(token, transport => DispatchTransportCommandAsync(transport, arguments, token));
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
        var devices = await ExecuteWithTransportAsync(token, t => t.DevicesAsync(token)).ConfigureAwait(false);
        var resolved = await Task.WhenAll(devices.Select(device => ResolveIdentityAsync(device, token))).ConfigureAwait(false);
        return deduplicate ? AndroidDeviceIdentity.Distinct(resolved) : resolved;
    }

    private async Task<AndroidDevice> ResolveIdentityAsync(AndroidDevice device, CancellationToken token)
    {
        if (device.State != "device") return device;
        string key = device.Serial + "\0" + device.TransportId;
        lock (_identityGate) if (_identities.TryGetValue(key, out var cached)) return device with { HardwareId = cached };
        string? identity = null;
        using var identityDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        identityDeadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            string result = (await RunAsync(identityDeadline.Token, "-s", device.Serial, "shell", "getprop", "ro.serialno").ConfigureAwait(false)).Trim();
            if (result.Length is > 0 and <= 128 && Regex.IsMatch(result, @"\A[A-Za-z0-9_.-]+\z") && result != "unknown") identity = result;
        }
        catch (IOException) { /* The scan still reports unauthorized/disconnected endpoints normally. */ }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { /* Identity enrichment must not hold up device discovery. */ }
        token.ThrowIfCancellationRequested();
        lock (_identityGate)
        {
            if (_identities.Count >= 128) _identities.Clear();
            _identities[key] = identity; // one read per ADB transport, never an idle identity poll
        }
        return device with { HardwareId = identity };
    }

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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        await PrepareRuntimeAsync("audio", deadline.Token).ConfigureAwait(false);
        ValidateCatalog();
        string remote = "/data/local/tmp/sonicroute_encoders_" + Guid.NewGuid().ToString("N") + ".jar";
        try
        {
            await RunAsync(deadline.Token, "-s", device.Serial, "push", CatalogFile, remote).ConfigureAwait(false);
            string output = await RunAsync(deadline.Token, "-s", device.Serial, "shell", "CLASSPATH=" + remote,
                "app_process", "/", "org.sonicroute.android.EncoderCatalog").ConfigureAwait(false);
            return ParseEncoders(output);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await RunAsync(cleanup.Token, "-s", device.Serial, "shell", "rm", "-f", remote).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { Trace.WriteLine("Encoder query cleanup: " + ex.Message); }
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
            return new(Names(audio), Names(video), kinds);
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
        ValidateCatalog();
        if (!File.Exists(CatalogFile)) throw new FileNotFoundException("Missing Android app catalogue helper.", CatalogFile);
        string remote = "/data/local/tmp/sonicroute_catalog_" + Guid.NewGuid().ToString("N") + ".jar";
        try
        {
            await RunAsync(token, "-s", device.Serial, "push", CatalogFile, remote).ConfigureAwait(false);
            var arguments = new List<string> { "-s", device.Serial, "shell", "CLASSPATH=" + remote,
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
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await RunAsync(cleanup.Token, "-s", device.Serial, "shell", "rm", "-f", remote).ConfigureAwait(false); }
            catch { /* device disconnection is surfaced by the original operation */ }
        }
    }

    public async Task<IReadOnlyList<AndroidOutputSink>> OutputsAsync(AndroidDevice device, CancellationToken token)
    {
        if (device.State != "device") throw AndroidError.WithCode(new IOException("请先在手机上允许 ADB 调试授权。"), AndroidErrorCode.AuthorizeAdbFirst);
        await PrepareRuntimeAsync("audio", token).ConfigureAwait(false);
        ValidateCatalog();
        if (!File.Exists(CatalogFile)) throw new FileNotFoundException("Missing Android app catalogue helper.", CatalogFile);
        string remote = "/data/local/tmp/sonicroute_outputs_" + Guid.NewGuid().ToString("N") + ".jar";
        try
        {
            await RunAsync(token, "-s", device.Serial, "push", CatalogFile, remote).ConfigureAwait(false);
            string result = await RunAsync(token, "-s", device.Serial, "shell",
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
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await RunAsync(cleanup.Token, "-s", device.Serial, "shell", "rm", "-f", remote).ConfigureAwait(false); }
            catch { }
        }
    }

    public async Task<int> ResolveUidAsync(AndroidDevice device, string package, CancellationToken token)
    {
        ValidatePackage(package);
        string text = await RunAsync(token, "-s", device.Serial, "shell", "pm", "list", "packages", "-U").ConfigureAwait(false);
        var match = Regex.Match(text, @"(?m)^package:" + Regex.Escape(package) + @"\s+uid:(\d+)\s*$");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int uid) || uid <= 0)
            throw AndroidError.WithCode(new IOException("目标应用或 UID 已不可用，请刷新手机应用列表。"), AndroidErrorCode.AppOrUidUnavailable);
        if (Regex.Matches(text, @"(?m)^package:\S+\s+uid:" + uid + @"\s*$").Count > 1)
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
