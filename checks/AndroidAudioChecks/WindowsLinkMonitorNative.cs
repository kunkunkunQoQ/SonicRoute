using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using SonicRoute.AndroidAudio;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static async Task WindowsLinkMonitorNativeAsync(string output)
    {
        string root = Path.GetFullPath(output);
        if (Directory.Exists(root)) throw new IOException("Use a new directory for the isolated monitor check.");
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(27));
        var token = timeout.Token;
        await using var sender = new WindowsLinkService(Path.Combine(root, "sender"), "Monitor source", port: 0, discovery: false);
        await using var receiver = new WindowsLinkService(Path.Combine(root, "receiver"), "Monitor viewer", port: 0, discovery: false);
        sender.PairingRequested += pair => sender.AcceptPairing(pair.Id, true);
        receiver.PairingRequested += pair => receiver.AcceptPairing(pair.Id, true);
        await sender.EnableAsync(token);
        await receiver.EnableAsync(token);
        await receiver.ConnectAsync(sender.LocalAddress, token: token);
        await WaitUntilAsync(() => sender.Peers.Any(p => p.Connected) && receiver.Peers.Any(p => p.Connected), token);
        var peer = receiver.Peers.Single(p => p.Connected);
        var catalog = await receiver.RequestCatalogAsync(peer.Id, includeIcons: false, token, windowsOnly: true);
        var monitor = catalog.FirstOrDefault(m => m.Display?.IsPrimary == true) ?? catalog.FirstOrDefault(m => m.IsMonitor);
        if (monitor?.Display is not { } display) throw new InvalidOperationException("No active monitor in the remote Windows catalog.");
        Check(peer.Capabilities.HasFlag(LinkCapabilities.MonitorCapture) && display.Width > 0 && display.Height > 0,
            $"paired loopback catalog identifies a {display.Width}x{display.Height} active monitor");
        int initialRenderers = D3D11VideoSurfaceRenderer.ActiveInstanceCount;
        Guid stream = await receiver.RequestPullWindowAsync(peer.Id, monitor, token: token);
        try
        {
            var received = receiver.Streams.Single(s => s.Id == stream && s.Incoming);
            Check(received.InstanceId == monitor.InstanceId && received.Kind == LinkMediaKind.Window
                && !received.AllowsInput && ReadMonitorTrackInput(sender, stream) == null,
                "the pull opens the selected monitor stream with input disabled by default");
            // LinkVideoViewer only calls EnsureVisible after the decoder presents its first frame.
            // Only inspect our viewer's HWND. No image data leaves the in-memory video pipeline.
            await WaitUntilAsync(() => MonitorViewerVisible(received.Name) &&
                D3D11VideoSurfaceRenderer.ActiveInstanceCount > initialRenderers, token);
            var sent = sender.Streams.Single(s => !s.Incoming && s.Id == stream);
            Check(sent.VideoWidth > 0 && sent.VideoHeight > 0,
                $"WGC monitor capture, TLS transport and decoder presented a {sent.VideoWidth}x{sent.VideoHeight} frame");
        }
        finally { await receiver.StopStreamAsync(stream); }
        await WaitUntilAsync(() => sender.Streams.Length == 0 && receiver.Streams.Length == 0
            && D3D11VideoSurfaceRenderer.ActiveInstanceCount == initialRenderers, token);
        Check(true, "stopping the monitor stream releases both tracks and the native renderer");

        var viewerPeer = sender.Peers.Single(p => p.Connected);
        var settings = sender.GetPeerSettings(viewerPeer.Id);
        sender.UpdatePeerSettings(viewerPeer.Id, settings with { AllowRemoteInput = true });
        receiver.UpdatePeerSettings(peer.Id, receiver.GetPeerSettings(peer.Id) with { VideoMaxHeight = 0, VideoEncoder = "h264" });
        Guid controlled = await receiver.RequestPullWindowAsync(peer.Id, monitor, token: token);
        try
        {
            var active = (IReadOnlyDictionary<nint, NativeVideoWindow>)typeof(NativeVideoWindow)
                .GetField("ActiveWindows", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            string title = receiver.Streams.Single(s => s.Id == controlled).Name;
            int width = display.Width & ~1, height = display.Height & ~1;
            await WaitUntilAsync(() => MonitorViewerVisible(title)
                && active.TryGetValue(FindMonitorViewerWindow(null, title), out var window)
                && window.ContentWidth == width && window.ContentHeight == height, token);
            var encoded = sender.Streams.Single(s => s.Id == controlled);
            Check(encoded.VideoWidth == width && encoded.VideoHeight == height,
                $"native monitor capture preserved {width}x{height} through encoding, TLS and the visible decoded viewer");
            Check(sender.Streams.Any(s => s.Id == controlled && !s.Incoming && s.AllowsInput)
                && receiver.Streams.Any(s => s.Id == controlled && s.Incoming && s.AllowsInput)
                && ReadMonitorTrackInput(sender, controlled) is LinkMonitorInput,
                "the paired viewer receives input permission and the sender attaches the monitor input router");
            sender.UpdatePeerSettings(viewerPeer.Id, settings);
            Check(sender.Streams.Any(s => s.Id == controlled && !s.Incoming && !s.AllowsInput)
                && ReadMonitorTrackInput(sender, controlled) == null && receiver.Streams.Any(s => s.Id == controlled),
                "revoking permission immediately detaches input from the active monitor stream while video remains open");
        }
        finally { await receiver.StopStreamAsync(controlled); }
        await WaitUntilAsync(() => sender.Streams.Length == 0 && receiver.Streams.Length == 0
            && D3D11VideoSurfaceRenderer.ActiveInstanceCount == initialRenderers, token);
        Check(true, "the previously controlled stream also releases both tracks and the renderer");
    }

    private static ILinkInput? ReadMonitorTrackInput(WindowsLinkService service, Guid id)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(WindowsLinkService);
        lock (type.GetField("_gate", hidden)!.GetValue(service)!)
        {
            var tracks = (IDictionary)type.GetField("_streams", hidden)!.GetValue(service)!;
            var track = tracks[id] ?? throw new InvalidOperationException("The selected monitor stream is missing.");
            return (ILinkInput?)track.GetType().GetProperty("Input", hidden)!.GetValue(track);
        }
    }

    private static bool MonitorViewerVisible(string title)
    {
        nint hwnd = FindMonitorViewerWindow(null, title);
        if (hwnd == 0 || !IsMonitorViewerVisible(hwnd)) return false;
        GetMonitorViewerProcessId(hwnd, out uint pid);
        return pid == Environment.ProcessId;
    }

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern nint FindMonitorViewerWindow(string? className, string windowName);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    private static extern bool IsMonitorViewerVisible(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetMonitorViewerProcessId(nint hwnd, out uint pid);
}
