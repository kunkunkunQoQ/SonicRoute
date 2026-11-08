using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace SonicRoute.AndroidAudio;

public sealed class AdbClient
{
    private readonly object _identityGate = new();
    private readonly Dictionary<string, string?> _identities = new(StringComparer.Ordinal);
    private readonly AndroidRuntimeBundle? _bundle;
    private readonly string? _runtimeDirectory;
    private bool _serverVerified;
    public string RuntimeDirectory => _bundle?.DirectoryPath ?? _runtimeDirectory!;
    public string Executable => Path.Combine(RuntimeDirectory, "adb.exe");
    public string ServerFile => Path.Combine(RuntimeDirectory, "scrcpy-server");
    public const string VerifiedServerSha256 = "BAFEFD8413783E404BC34520B7EA1A9EBAA985720FEFE9E450933CAEA89BDD05";
    public void ValidateServer()
    {
        lock (_identityGate)
        {
            if (_serverVerified) return;
            PrepareRuntime("audio");
            using var file = File.OpenRead(ServerFile);
            if (Convert.ToHexString(SHA256.HashData(file)) != VerifiedServerSha256)
                throw AndroidError.WithCode(new InvalidDataException("手机音频组件校验失败，请重新下载完整版本。"), AndroidErrorCode.ServerVerifyFailed);
            _serverVerified = true;
        }
    }
    public AdbClient(string runtimeDirectory) => _runtimeDirectory = Path.GetFullPath(runtimeDirectory);
    public AdbClient(AndroidRuntimeBundle bundle) => _bundle = bundle;
    internal void PrepareRuntime(string group) => _bundle?.Prepare(group);
    internal Task PrepareRuntimeAsync(string group, CancellationToken token) => _bundle?.PrepareAsync(group, token) ?? Task.CompletedTask;

    public Process Start(params string[] arguments) => StartCore(false, arguments);
    internal Process StartWithInput(params string[] arguments) => StartCore(true, arguments);
    private Process StartCore(bool input, string[] arguments)
    {
        PrepareRuntime("adb");
        if (!File.Exists(Executable)) throw AndroidError.WithCode(new FileNotFoundException("缺少 Android 音频运行文件，请使用完整实验包。", Executable), AndroidErrorCode.MissingRuntime);
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = RuntimeDirectory, RedirectStandardInput = input };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("Unable to start ADB.");
    }

    public async Task<string> RunAsync(CancellationToken token, params string[] arguments)
    {
        token.ThrowIfCancellationRequested();
        await PrepareRuntimeAsync("adb", token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = Start(arguments);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string output = await stdout.ConfigureAwait(false), error = await stderr.ConfigureAwait(false);
            if (output.Length > 8 * 1024 * 1024) throw new InvalidDataException("ADB response exceeds catalogue limit.");
            if (process.ExitCode != 0) throw new IOException($"ADB: {error.Trim()} {output.Trim()}".Trim());
            return output;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    public async Task<IReadOnlyList<AndroidDevice>> DevicesAsync(CancellationToken token, bool deduplicate = true)
    {
        string text = await RunAsync(token, "devices", "-l").ConfigureAwait(false);
        var devices = ParseDevices(text);
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

    public static IReadOnlyList<AndroidDevice> ParseDevices(string text)
    {
        var devices = new List<AndroidDevice>();
        foreach (string line in text.Split('\n'))
        {
            string[] fields = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[0] == "List" || fields[0].StartsWith('*')) continue;
            string name = fields.FirstOrDefault(f => f.StartsWith("model:"))?[6..].Replace('_', ' ') ?? fields[0];
            bool wifi = fields[0].Contains(':') || fields[0].Contains("_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase);
            devices.Add(new AndroidDevice(fields[0], name, fields[1], wifi, TransportId: fields.FirstOrDefault(f => f.StartsWith("transport_id:"))?[13..]));
        }
        return devices;
    }

    public async Task<AndroidEncoderCatalog> EncodersAsync(AndroidDevice device, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        await PrepareRuntimeAsync("audio", deadline.Token).ConfigureAwait(false);
        string helper = Path.Combine(RuntimeDirectory, "sonicroute-catalog.jar");
        string remote = "/data/local/tmp/sonicroute_encoders_" + Guid.NewGuid().ToString("N") + ".jar";
        try
        {
            await RunAsync(deadline.Token, "-s", device.Serial, "push", helper, remote).ConfigureAwait(false);
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
        ValidateServer();
        string catalog = Path.Combine(RuntimeDirectory, "sonicroute-catalog.jar");
        if (!File.Exists(catalog)) throw new FileNotFoundException("Missing Android app catalogue helper.", catalog);
        string remote = "/data/local/tmp/sonicroute_catalog_" + Guid.NewGuid().ToString("N") + ".jar";
        string remoteServer = remote + ".server";
        try
        {
            await RunAsync(token, "-s", device.Serial, "push", catalog, remote).ConfigureAwait(false);
            await RunAsync(token, "-s", device.Serial, "push", ServerFile, remoteServer).ConfigureAwait(false);
            var arguments = new List<string> { "-s", device.Serial, "shell", "CLASSPATH=" + remote + ":" + remoteServer,
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
            try { await RunAsync(cleanup.Token, "-s", device.Serial, "shell", "rm", "-f", remote, remoteServer).ConfigureAwait(false); }
            catch { /* device disconnection is surfaced by the original operation */ }
        }
    }

    public async Task<IReadOnlyList<AndroidOutputSink>> OutputsAsync(AndroidDevice device, CancellationToken token)
    {
        if (device.State != "device") throw AndroidError.WithCode(new IOException("请先在手机上允许 ADB 调试授权。"), AndroidErrorCode.AuthorizeAdbFirst);
        await PrepareRuntimeAsync("audio", token).ConfigureAwait(false);
        ValidateServer();
        string catalog = Path.Combine(RuntimeDirectory, "sonicroute-catalog.jar");
        if (!File.Exists(catalog)) throw new FileNotFoundException("Missing Android app catalogue helper.", catalog);
        string remote = "/data/local/tmp/sonicroute_outputs_" + Guid.NewGuid().ToString("N") + ".jar";
        string remoteServer = remote + ".server";
        try
        {
            await RunAsync(token, "-s", device.Serial, "push", catalog, remote).ConfigureAwait(false);
            await RunAsync(token, "-s", device.Serial, "push", ServerFile, remoteServer).ConfigureAwait(false);
            string result = await RunAsync(token, "-s", device.Serial, "shell",
                "CLASSPATH=" + remote + ":" + remoteServer, "app_process", "/", "org.sonicroute.android.AudioOutputBridge", "list").ConfigureAwait(false);
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
            try { await RunAsync(cleanup.Token, "-s", device.Serial, "shell", "rm", "-f", remote, remoteServer).ConfigureAwait(false); }
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
