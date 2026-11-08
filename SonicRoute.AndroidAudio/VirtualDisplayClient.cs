using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SonicRoute.AndroidAudio;

/// <summary>Manifest-pinned client is used exclusively for video and input, never for audio.</summary>
public sealed class VirtualDisplayRuntime(AdbClient adb)
{
    private readonly object _gate = new();
    private bool _verified, _controlVerified;
    public string DirectoryPath => Path.Combine(adb.RuntimeDirectory, "VirtualDisplay");
    public string ServerFile => Path.Combine(DirectoryPath, "scrcpy-server-video");
    public void Validate()
    {
        lock (_gate)
        {
            if (_verified) return;
            adb.PrepareRuntime("video");
            ValidateControl();
            using var manifest = typeof(VirtualDisplayRuntime).Assembly.GetManifestResourceStream(
                "SonicRoute.AndroidAudio.VirtualDisplayRuntime.files.json")!;
            var files = JsonSerializer.Deserialize<RuntimeFile[]>(manifest)!;
            foreach (var file in files.Where(f => f.Name != "scrcpy-server-video"))
            {
                string path = Path.Combine(DirectoryPath, file.Name);
                if (Path.GetFileName(file.Name) != file.Name || !File.Exists(path))
                    throw AndroidError.WithCode(new FileNotFoundException("缺少独立屏幕组件，请使用完整实验包。", path), AndroidErrorCode.MissingDisplayRuntime);
                using var input = File.OpenRead(path);
                if (input.Length != file.Bytes || Convert.ToHexString(SHA256.HashData(input)) != file.SHA256)
                    throw AndroidError.WithCode(new InvalidDataException("独立屏幕组件与实验包不符，请恢复完整文件。"), AndroidErrorCode.InvalidDisplayRuntime);
            }
            _verified = true; // immutable package is checked once, on the first actual request
        }
    }
    public void ValidateControl()
    {
        lock (_gate)
        {
            if (_controlVerified) return;
            adb.PrepareRuntime("control");
            using var manifest = typeof(VirtualDisplayRuntime).Assembly.GetManifestResourceStream(
                "SonicRoute.AndroidAudio.VirtualDisplayRuntime.files.json")!;
            var file = JsonSerializer.Deserialize<RuntimeFile[]>(manifest)!.Single(f => f.Name == "scrcpy-server-video");
            using var input = File.OpenRead(ServerFile);
            if (input.Length != file.Bytes || Convert.ToHexString(SHA256.HashData(input)) != file.SHA256)
                throw AndroidError.WithCode(new InvalidDataException("独立屏幕控制组件校验失败，请恢复完整文件。"), AndroidErrorCode.InvalidDisplayControlRuntime);
            _controlVerified = true;
        }
    }
    private sealed record RuntimeFile(string Name, long Bytes, string SHA256);
}

public sealed class VirtualDisplayClient(AdbClient adb, VirtualDisplayRuntime runtime,
    Func<AndroidAudioSettingsStore>? settings = null) : IVirtualDisplayClient
{
    private Process? _process;
    private Task _completion = Task.CompletedTask;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<string> _logs = new();
    private string? _serial, _scid;
    private AndroidAudioSettingsStore? _settings;
    private AndroidDevice? _device;
    private string? _package;
    private bool _isDeviceAudio;
    private bool _disposed;
    private VirtualDisplayState _state = VirtualDisplayState.Starting;
    public Task Completion => _completion;
    public int ProcessId { get; private set; }
    public VirtualDisplayState State => _state;
    public event Action<VirtualDisplayState>? StateChanged;

    public static ProcessStartInfo CreateStartInfo(AdbClient adb, AndroidDevice device, AndroidApp app, int x, int y,
        string? iconPath = null, AndroidVirtualDisplaySize? displaySize = null, AndroidWindowSize? windowSize = null,
        AndroidStreamSettings? streaming = null)
    {
        if (!app.IsDeviceAudio) AdbClient.ValidatePackage(app.Package);
        streaming = (streaming ?? new()).Normalize();
        // Device capture mirrors the existing physical display. Only ordinary app
        // windows create a virtual display and restore its accepted framebuffer size.
        string display = app.IsDeviceAudio ? "--display-id=0" : displaySize?.IsValid() == true
            ? "--new-display=" + displaySize.Width.ToString(CultureInfo.InvariantCulture) + "x" + displaySize.Height.ToString(CultureInfo.InvariantCulture)
            : "--new-display";
        var info = new ProcessStartInfo(Path.Combine(adb.RuntimeDirectory, "VirtualDisplay", "scrcpy.exe"))
        {
            WorkingDirectory = Path.Combine(adb.RuntimeDirectory, "VirtualDisplay"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] {
            "--serial=" + device.Serial, display, "--video-codec=h264",
            "--no-audio", "--no-clipboard-autosync",
            "--force-adb-forward", "--max-fps=" + streaming.MaxFps.ToString(CultureInfo.InvariantCulture),
            "--video-bit-rate=" + streaming.VideoBitRate.ToString(CultureInfo.InvariantCulture), "--window-title=" + app.Name,
            "--window-x=" + x.ToString(CultureInfo.InvariantCulture),
            "--window-y=" + y.ToString(CultureInfo.InvariantCulture) }) info.ArgumentList.Add(argument);
        if (streaming.VideoEncoder != null) info.ArgumentList.Add("--video-encoder=" + streaming.VideoEncoder);
        if (!app.IsDeviceAudio)
        {
            info.ArgumentList.Add("--start-app=" + app.Package);
            info.ArgumentList.Add("--no-vd-system-decorations");
            info.ArgumentList.Add("--display-ime-policy=hide");
        }
        else
        {
            // Retain the GPU renderer validated on this Windows system.
            info.Environment["SDL_RENDER_DRIVER"] = "direct3d11";
        }
        if (windowSize?.IsValid() == true)
        {
            info.ArgumentList.Add("--window-width=" + windowSize.Width.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--window-height=" + windowSize.Height.ToString(CultureInfo.InvariantCulture));
        }
        info.Environment["ADB"] = adb.Executable;
        // Video has its own matching resize protocol; the pinned audio server stays unchanged.
        info.Environment["SCRCPY_SERVER_PATH"] = new VirtualDisplayRuntime(adb).ServerFile;
        // Mirror windows scale at the phone's aspect ratio, without resizing the phone.
        info.Environment["SONICROUTE_FLEX_DISPLAY"] = app.IsDeviceAudio ? "0" : "1";
        info.Environment["SONICROUTE_PHONE_MIRROR"] = app.IsDeviceAudio ? "1" : "0";
        if (!app.IsDeviceAudio) info.Environment["SONICROUTE_APP_PACKAGE"] = app.Package;
        else info.Environment.Remove("SONICROUTE_APP_PACKAGE");
        if (app.Category != "games") info.ArgumentList.Add("--prefer-text");
        // Keep the computer IME's composition/candidate UI available for text input.
        info.Environment["SDL_IME_SHOW_UI"] = "1";
        info.Environment["SDL_IME_INTERNAL_EDITING"] = "1";
        info.Environment["SCRCPY_ICON_PATH"] = iconPath != null ? Path.GetFullPath(iconPath)
            : Path.Combine(adb.RuntimeDirectory, "VirtualDisplay", "phone-icon.png");
        return info;
    }
    public static string? CacheIcon(AndroidApp app, string cacheDirectory)
    {
        if (app.IconPng is not { Length: > 0 and <= 131072 } png) return null;
        // Content addressing avoids package-name paths, repeated writes and stale icons.
        string hash = Convert.ToHexString(SHA256.HashData(png));
        Directory.CreateDirectory(cacheDirectory);
        string path = Path.Combine(cacheDirectory, hash + ".png");
        if (!File.Exists(path)) File.WriteAllBytes(path, png);
        return path;
    }
    public async Task StartAsync(AndroidDevice device, AndroidApp app, int x, int y, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (device.State != "device") throw AndroidError.WithCode(new IOException("手机尚未连接，请刷新设备后重试。"), AndroidErrorCode.DisplayPhoneNotConnected);
        await Task.Run(runtime.Validate, token).ConfigureAwait(false);
        if (!app.IsDeviceAudio)
        {
            int uid = await adb.ResolveUidAsync(device, app.Package, token).ConfigureAwait(false);
            if (uid != app.Uid) throw AndroidError.WithCode(new IOException("应用身份已改变，请刷新手机应用列表。"), AndroidErrorCode.DisplayAppUidChanged);
        }
        token.ThrowIfCancellationRequested(); _serial = device.Serial;
        _device = device; _package = app.Package; _isDeviceAudio = app.IsDeviceAudio;
        _settings = settings?.Invoke(); // Already on the startup worker; unused video never reads preferences.
        string dataRoot = Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SonicRoute");
        string? icon = CacheIcon(app, Path.Combine(dataRoot, "phone-app-icons"));
        var saved = _settings?.Get(device, app.Package);
        _process = Process.Start(CreateStartInfo(adb, device, app, x, y, icon, saved?.DisplaySize, saved?.WindowSize, _settings?.GetStreaming(device)))
            ?? throw AndroidError.WithCode(new IOException("独立屏幕未能启动。"), AndroidErrorCode.DisplayStartFailed);
        ProcessId = _process.Id;
        _completion = ObserveAsync(_process);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            Task first = await Task.WhenAny(_ready.Task, _completion).WaitAsync(deadline.Token).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            if (_process.HasExited) throw ConnectionError("独立屏幕未能打开，请检查手机连接后重试。", AndroidErrorCode.DisplayOpenFailed);
            await Task.Run(DiscoverOwnedServer, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Trace.WriteLine(LastLog());
            throw AndroidError.WithCode(new TimeoutException("打开独立屏幕超时，请检查手机连接。"), AndroidErrorCode.DisplayOpenTimeout);
        }
    }
    private async Task ObserveAsync(Process process)
    {
        var stdout = ReadLogAsync(process.StandardOutput);
        var stderr = ReadLogAsync(process.StandardError);
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        if (process.ExitCode != 0) throw ConnectionError("独立屏幕连接已结束，请检查手机连接后重试。", AndroidErrorCode.DisplayConnectionEnded);
    }
    private async Task ReadLogAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            lock (_logs) { while (_logs.Count >= 80) _logs.Dequeue(); _logs.Enqueue(line.Length > 1024 ? line[..1024] : line); }
            if (TryReadDisplaySize(line) is { } size)
            {
                if (!_isDeviceAudio && _settings != null && _device != null && _package != null) _settings.RememberDisplay(_device, _package, size);
                if (!_isDeviceAudio) _ready.TrySetResult();
            }
            if (_isDeviceAudio && IsFirstFrame(line))
            {
                _state = VirtualDisplayState.Active; StateChanged?.Invoke(_state);
                _ready.TrySetResult();
            }
            if (_settings != null && _device != null && _package != null && TryReadWindowSize(line) is { } window)
                _settings.RememberWindow(_device, _package, window);
            if (TryReadState(line) is { } state && state != _state)
            {
                _state = state; StateChanged?.Invoke(state);
            }
        }
    }
    public static bool IsFirstFrame(string line) => string.Equals(line, "INFO: SonicRoute first-frame", StringComparison.Ordinal);
    public static AndroidVirtualDisplaySize? TryReadDisplaySize(string line)
    {
        if (!TryReadSize(line, "INFO: Texture: ", out int width, out int height)) return null;
        var size = new AndroidVirtualDisplaySize(width, height);
        return size.IsValid() ? size : null;
    }
    public static AndroidWindowSize? TryReadWindowSize(string line)
    {
        if (!TryReadSize(line, "INFO: SonicRoute window-size: ", out int width, out int height)) return null;
        var size = new AndroidWindowSize(width, height);
        return size.IsValid() ? size : null;
    }
    private static bool TryReadSize(string line, string prefix, out int width, out int height)
    {
        width = height = 0;
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var dimensions = line.AsSpan(prefix.Length);
        int split = dimensions.IndexOf('x');
        return split > 0 && int.TryParse(dimensions[..split], NumberStyles.None, CultureInfo.InvariantCulture, out width)
            && int.TryParse(dimensions[(split + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out height);
    }
    public static VirtualDisplayState? TryReadState(string line)
    {
        const string prefix = "[server] INFO: SonicRoute app-state: display=";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
        int offset = line.IndexOf(" state=", StringComparison.Ordinal);
        if (offset < prefix.Length || !int.TryParse(line.AsSpan(prefix.Length, offset - prefix.Length), out int display) || display <= 0) return null;
        return line[(offset + 7)..].Trim() switch
        {
            "active" => VirtualDisplayState.Active, "phone" => VirtualDisplayState.OnPhone,
            "recovering" => VirtualDisplayState.Recovering, "missing" => VirtualDisplayState.Missing,
            "unknown" => VirtualDisplayState.Unknown, _ => null
        };
    }
    public void Restore()
    {
        try
        {
            if (_process == null || _process.HasExited) return;
            _process.Refresh();
            var hwnd = _process.MainWindowHandle;
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, 0x8000 + 73, IntPtr.Zero, IntPtr.Zero);
        }
        catch (InvalidOperationException) { }
    }
    private string LastLog()
    { lock (_logs) return string.Join(Environment.NewLine, _logs.TakeLast(8)); }
    private IOException ConnectionError(string message, AndroidErrorCode code)
    { Trace.WriteLine(LastLog()); return AndroidError.WithCode(new IOException(message), code); }

    // One on-demand query of this exact client's live adb child supplies its random server id.
    // A fallback cleanup may then touch only that id; no global scrcpy/ADB termination is used.
    private void DiscoverOwnedServer()
    {
        var process = _process;
        if (_scid != null || process == null) return;
        try
        {
            using var query = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ParentProcessId="
                + process.Id.ToString(CultureInfo.InvariantCulture) + " AND Name='adb.exe'");
            using var children = query.Get();
            foreach (ManagementObject child in children)
            {
                using (child)
                {
                    string command = child["CommandLine"] as string ?? "";
                    if (!command.Contains("com.genymobile.scrcpy.Server", StringComparison.Ordinal)) continue;
                    var match = Regex.Match(command, @"\bscid=([0-9a-fA-F]{8})\b");
                    if (match.Success) { _scid = match.Groups[1].Value; return; }
                }
            }
        }
        catch (ManagementException) { }
        catch (InvalidOperationException) { }
        catch (COMException) { }
        catch (UnauthorizedAccessException) { }
    }
    public void Activate()
    {
        try
        {
            if (_process == null || _process.HasExited) return;
            _process.Refresh(); var hwnd = _process.MainWindowHandle;
            if (hwnd != IntPtr.Zero) { ShowWindow(hwnd, 9); SetForegroundWindow(hwnd); }
        }
        catch (InvalidOperationException) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        if (_process == null) return;
        bool forced = false;
        try
        {
            if (!_process.HasExited)
            {
                if (_scid == null) await Task.Run(DiscoverOwnedServer).ConfigureAwait(false);
                _process.Refresh(); _process.CloseMainWindow();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                try { await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { forced = true; try { _process.Kill(true); } catch (InvalidOperationException) { } }
            }
            try { await _completion.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); } catch { }
            if (forced || (_process.HasExited && _process.ExitCode != 0)) await CleanupOwnedServerAsync().ConfigureAwait(false);
        }
        finally { ProcessId = 0; _process.Dispose(); _process = null; }
    }
    private async Task CleanupOwnedServerAsync()
    {
        if (_scid == null || _serial == null) return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            string processes = await adb.RunAsync(cleanup.Token, "-s", _serial, "shell", "ps", "-A", "-o", "PID,ARGS").ConfigureAwait(false);
            foreach (string line in processes.Split('\n'))
            {
                var match = Regex.Match(line, @"^\s*(\d+)\s+(.*)$");
                if (!match.Success || !IsOwnedServerCommand(match.Groups[2].Value, _scid)) continue;
                string pid = match.Groups[1].Value;
                string command = await adb.RunAsync(cleanup.Token, "-s", _serial, "shell", "cat", "/proc/" + pid + "/cmdline").ConfigureAwait(false);
                if (IsOwnedServerCommand(command.Replace('\0', ' '), _scid))
                    await adb.RunAsync(cleanup.Token, "-s", _serial, "shell", "kill", "-TERM", pid).ConfigureAwait(false);
            }
            string forwards = await adb.RunAsync(cleanup.Token, "forward", "--list").ConfigureAwait(false);
            foreach (string line in forwards.Split('\n'))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 3 && fields[0] == _serial && fields[2] == "localabstract:scrcpy_" + _scid
                    && Regex.IsMatch(fields[1], @"\Atcp:\d+\z"))
                    await adb.RunAsync(cleanup.Token, "-s", _serial, "forward", "--remove", fields[1]).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { Trace.WriteLine("Owned virtual display cleanup: " + ex.Message); }
    }
    public static bool IsOwnedServerCommand(string command, string scid) => Regex.IsMatch(scid, @"\A[0-9a-fA-F]{8}\z")
        && command.Contains("com.genymobile.scrcpy.Server", StringComparison.Ordinal)
        && Regex.IsMatch(command, @"(?:^|\s)scid=" + Regex.Escape(scid) + @"(?:\s|$)");
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
}
