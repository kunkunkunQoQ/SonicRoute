using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SonicRoute.AndroidAudio;

/// <summary>Virtual-display runtime facade backed exclusively by SonicRoute's self-authored catalog jar.</summary>
public sealed class VirtualDisplayRuntime(AdbClient adb)
{
    public string DirectoryPath => adb.RuntimeDirectory;
    public string ServerFile => adb.CatalogFile;

    public void Validate() => adb.ValidateCatalog();
    public void ValidateControl() => adb.ValidateCatalog();
}

/// <summary>
/// Compatibility adapter delegating directly to the zero-external-process
/// <see cref="InProcessVirtualDisplayClient"/> without FFmpeg, SDL2, System.Management, or scrcpy.
/// </summary>
public sealed class VirtualDisplayClient : IVirtualDisplayClient
{
    private readonly InProcessVirtualDisplayClient _inner;
    private VirtualDisplayState _state = VirtualDisplayState.Starting;

    public VirtualDisplayClient(AdbClient adb, VirtualDisplayRuntime runtime, Func<AndroidAudioSettingsStore>? settings = null)
    {
        _inner = new InProcessVirtualDisplayClient(adb, runtime, settings);
        _inner.StateChanged += s =>
        {
            _state = s;
            StateChanged?.Invoke(s);
        };
    }

    public Task Completion => _inner.Completion;
    public nint WindowHandle => _inner.WindowHandle;
    public int ProcessId => _inner.ProcessId;
    public VirtualDisplayState State => _state;
    public event Action<VirtualDisplayState>? StateChanged;

    public Task StartAsync(AndroidDevice device, AndroidApp app, int x, int y, CancellationToken token)
        => _inner.StartAsync(device, app, x, y, token);

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

    public void Activate() => _inner.Activate();
    public void Restore() => _inner.Restore();
    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    public static bool IsOwnedServerCommand(string command, string scid)
        => AdbVideoStreamTransport.IsOwnedServerCommand(command, scid);
}
