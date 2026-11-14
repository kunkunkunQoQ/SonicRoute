using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SonicRoute.AndroidAudio;

public sealed record AndroidConnectionQuality(double? RoundTripMs)
{
    public int SignalLevel => RoundTripMs is not { } value || !double.IsFinite(value) || value < 0 ? 0
        : value <= 60 ? 4 : value <= 150 ? 3 : value <= 300 ? 2 : 1;
}

public sealed record AndroidMediaState(string Package, string Title, string Artist, bool Playing, long Actions,
    byte[]? ArtworkPng = null, long PositionMs = 0, long DurationMs = 0, double Speed = 1)
{
    public long ReceivedTimestamp { get; init; } = Stopwatch.GetTimestamp();
    public IReadOnlyList<string> PlayingPackages { get; init; } = Array.Empty<string>();
    public IReadOnlyList<AndroidOutputSink> Outputs { get; init; } = [AndroidOutputSink.DefaultSpeaker];
    public bool HasSession => Package.Length > 0;
    public bool CanPrevious => HasSession && (Actions & 16) != 0;
    public bool CanNext => HasSession && (Actions & 32) != 0;
    public bool CanToggle => HasSession && (Actions & (512 | (Playing ? 2 : 4))) != 0;
    public bool CanSeek => HasSession && DurationMs > 0 && (Actions & 256) != 0;
    public double CurrentPositionMs => Math.Clamp(PositionMs + (Playing
        ? Stopwatch.GetElapsedTime(ReceivedTimestamp).TotalMilliseconds * Speed : 0), 0, Math.Max(0, DurationMs));
    public static AndroidMediaState Parse(string line) => Parse(line, null);
    /// <summary>A missing artwork field reuses the previous thumbnail only for the same application.
    /// An explicit empty field clears it. Each device subscription owns its own previous snapshot.</summary>
    public static AndroidMediaState Parse(string line, AndroidMediaState? previous)
    {
        using var json = JsonDocument.Parse(line);
        return Parse(json.RootElement, previous);
    }
    internal static AndroidMediaState Parse(JsonElement root, AndroidMediaState? previous = null)
    {
        if (root.TryGetProperty("error", out var error)) throw new IOException(error.GetString());
        static string Text(JsonElement root, string key)
        { string value = root.GetProperty(key).GetString() ?? ""; return value[..Math.Min(value.Length, 256)]; }
        string package = Text(root, "package");
        byte[]? artwork = null;
        if (root.TryGetProperty("artwork", out var art))
        {
            if (art.GetString() is { Length: > 0 } encoded)
            {
                if (encoded.Length > 175000) throw new InvalidDataException("Media artwork exceeds thumbnail limit.");
                artwork = Convert.FromBase64String(encoded);
                if (artwork.Length > 128 * 1024) throw new InvalidDataException("Media artwork exceeds thumbnail limit.");
            }
        }
        else if (package.Length > 0 && package == previous?.Package) artwork = previous.ArtworkPng;
        static long Milliseconds(JsonElement root, string key) => root.TryGetProperty(key, out var value)
            ? Math.Clamp(value.GetInt64(), 0, 604800000) : 0;
        double speed = root.TryGetProperty("speed", out var speedValue) ? speedValue.GetDouble() : 1;
        string[] playingPackages = root.TryGetProperty("playingPackages", out var packages)
            ? packages.EnumerateArray().Take(256).Select(p => p.GetString() ?? "").Where(p => p.Length is > 0 and <= 256).Distinct().ToArray()
            : Array.Empty<string>();
        IReadOnlyList<AndroidOutputSink> outputs = root.TryGetProperty("outputs", out var outputsElement) && outputsElement.ValueKind == JsonValueKind.Array
            ? AndroidOutputSink.ParseList(outputsElement)
            : previous?.Outputs ?? [AndroidOutputSink.DefaultSpeaker];
        return new(package, Text(root, "title"), Text(root, "artist"),
            root.GetProperty("playing").GetBoolean(), root.GetProperty("actions").GetInt64(), artwork,
            Milliseconds(root, "position"), Milliseconds(root, "duration"), double.IsFinite(speed) ? Math.Clamp(speed, 0.1, 8) : 1)
            { PlayingPackages = playingPackages, Outputs = outputs };
    }
}

/// <summary>One event stream for the selected phone, alive only while its panel is visible.</summary>
public sealed class AndroidMediaSessionClient(AdbClient adb)
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private IAdbShellSession? _session;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly object _latencyGate = new();
    private string? _pingId;
    private long _pingStart;
    private TaskCompletionSource<double>? _pendingPing;
    public event Action<AndroidConnectionQuality>? ConnectionQualityChanged;
    public async Task WatchAsync(AndroidDevice device, Action<AndroidMediaState> changed, CancellationToken token)
    {
        using var probes = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task latency = Task.CompletedTask;
        bool latencyStarted = false;
        AndroidMediaState? previous = null;
        await adb.PrepareRuntimeAsync("audio", token).ConfigureAwait(false);
        adb.ValidateCatalog();
        string id = Guid.NewGuid().ToString("N");
        string remote = "/data/local/tmp/sonicroute_media_" + id + ".jar";
        string? pid = null;
        Task<string>? stderr = null;
        StreamReader? errReader = null;
        try
        {
            await adb.RunAsync(token, "-s", device.Serial, "push", adb.CatalogFile, remote).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var session = await adb.OpenShellAsync(device.Serial,
                "echo SR_MEDIA_PID:$$; CLASSPATH=" + remote + " exec app_process / org.sonicroute.android.MediaSessionBridge " + id,
                redirectInput: true, token).ConfigureAwait(false);
            _session = session;
            _writer = new StreamWriter(session.InputStream, Utf8NoBom, bufferSize: 4096, leaveOpen: true) { NewLine = "\n" };
            errReader = new StreamReader(session.ErrorStream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            stderr = ReadDiagnosticTailAsync(errReader);
            using var stdout = new StreamReader(session.OutputStream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            while (await stdout.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("SR_MEDIA_PID:", StringComparison.Ordinal))
                { string candidate = line[13..].Trim(); if (Regex.IsMatch(candidate, @"\A[1-9]\d*\z")) pid = candidate; }
                else if (line.StartsWith('{'))
                {
                    using var json = JsonDocument.Parse(line);
                    if (json.RootElement.TryGetProperty("pong", out var pong))
                    {
                        lock (_latencyGate)
                            if (pong.GetString() == _pingId) _pendingPing?.TrySetResult(Stopwatch.GetElapsedTime(_pingStart).TotalMilliseconds);
                    }
                    else
                    {
                        previous = AndroidMediaState.Parse(json.RootElement, previous);
                        changed(previous);
                        if (device.IsWifi && !latencyStarted)
                        { latencyStarted = true; latency = WatchLatencyAsync(probes.Token); }
                    }
                }
            }
            await session.WaitForExitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            throw AndroidError.WithCode(new IOException("手机音乐连接已结束；重新打开面板或刷新可重试。"), AndroidErrorCode.MediaConnectionEnded);
        }
        finally
        {
            probes.Cancel();
            try { await latency.ConfigureAwait(false); } catch (OperationCanceledException) { }
            var session = _session; _session = null;
            var writer = _writer; _writer = null;
            if (session != null)
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await session.CloseInputAsync(close.Token).ConfigureAwait(false); } catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException) { }
                try { await session.WaitForExitAsync(close.Token).ConfigureAwait(false); }
                catch { try { session.Kill(); } catch { } }
            }
            writer?.Dispose();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (pid != null)
                {
                    string command = await adb.RunAsync(cleanup.Token, "-s", device.Serial, "shell",
                        "if [ -r /proc/" + pid + "/cmdline ]; then cat /proc/" + pid + "/cmdline; fi").ConfigureAwait(false);
                    if (IsOwnedCommand(command.Replace('\0', ' '), id))
                        await adb.RunAsync(cleanup.Token, "-s", device.Serial, "shell", "kill", "-TERM", pid).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { Trace.WriteLine("Media helper exit: " + ex.Message); }
            try { await adb.RunAsync(cleanup.Token, "-s", device.Serial, "shell", "rm", "-f", remote).ConfigureAwait(false); }
            catch (Exception ex) { Trace.WriteLine("Media helper cleanup: " + ex.Message); }
            if (stderr != null) { try { Trace.WriteLine(await stderr.ConfigureAwait(false)); } catch { } }
            errReader?.Dispose();
            if (session != null) await session.DisposeAsync().ConfigureAwait(false);
        }
    }
    internal const int DiagnosticTailCharacters = 16 * 1024;
    internal static async Task<string> ReadDiagnosticTailAsync(TextReader reader, CancellationToken token = default)
    {
        // Drain the existing pipe without accumulating the helper's entire lifetime.
        // Grow only on actual output; even a newline-free log stays within 32 KiB.
        char[] chunk = new char[1024], tail = new char[1024];
        int count = 0, write = 0, read;
        while ((read = await reader.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (tail.Length < DiagnosticTailCharacters && count + read > tail.Length)
            {
                Array.Resize(ref tail, Math.Min(DiagnosticTailCharacters, Math.Max(count + read, tail.Length * 2)));
                write = count;
            }
            int first = Math.Min(read, tail.Length - write);
            chunk.AsSpan(0, first).CopyTo(tail.AsSpan(write));
            chunk.AsSpan(first, read - first).CopyTo(tail);
            write = (write + read) % tail.Length;
            count = Math.Min(tail.Length, count + read);
        }
        int start = count == tail.Length ? write : 0;
        // Truncating the head must not leave half of a Unicode surrogate pair.
        if (count > 0 && char.IsLowSurrogate(tail[start])) { start = (start + 1) % tail.Length; --count; }
        return string.Create(count, (tail, start), static (text, state) =>
        {
            int first = Math.Min(text.Length, state.tail.Length - state.start);
            state.tail.AsSpan(state.start, first).CopyTo(text);
            state.tail.AsSpan(0, text.Length - first).CopyTo(text[first..]);
        });
    }
    private async Task WatchLatencyAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            string id = Guid.NewGuid().ToString("N");
            var response = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_latencyGate) { _pingId = id; _pingStart = Stopwatch.GetTimestamp(); _pendingPing = response; }
            double? roundTrip = null;
            try
            {
                await SendLineAsync("ping " + id, token).ConfigureAwait(false);
                roundTrip = await response.Task.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
            catch (TimeoutException) { }
            catch (IOException) { }
            catch (InvalidOperationException) { }
            finally { lock (_latencyGate) { _pingId = null; _pendingPing = null; } }
            token.ThrowIfCancellationRequested();
            try { ConnectionQualityChanged?.Invoke(new(roundTrip)); }
            catch (Exception ex) { Trace.WriteLine("Connection quality UI: " + ex.Message); }
            // Required for a changing latency indicator. Reuses this visible panel's existing
            // duplex connection, not a new ADB process or a device/catalogue scan.
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        }
    }
    public static bool IsOwnedCommand(string command, string id) => Regex.IsMatch(id, @"\A[0-9a-f]{32}\z")
        && command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("org.sonicroute.android.MediaSessionBridge")
        && command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(id);

    public async Task SendAsync(string command, CancellationToken token)
    {
        if (command is not ("previous" or "toggle" or "next")) throw new ArgumentException("Unknown media control.");
        await SendLineAsync(command, token).ConfigureAwait(false);
    }
    public Task SeekAsync(string package, long positionMs, CancellationToken token)
    {
        AdbClient.ValidatePackage(package);
        return SendLineAsync("seek " + package + " " + Math.Clamp(positionMs, 0, 604800000).ToString(CultureInfo.InvariantCulture), token);
    }
    private async Task SendLineAsync(string command, CancellationToken token)
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var writer = _writer ?? throw AndroidError.WithCode(new IOException("手机音乐尚未连接。"), AndroidErrorCode.MediaNotConnected);
            await writer.WriteLineAsync(command.AsMemory(), token).ConfigureAwait(false);
            await writer.FlushAsync(token).ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }
}
