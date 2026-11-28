using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SonicRoute.AndroidAudio;

// The single operation port routes supported calls on the attachment already admitted
// by AndroidConnectionManager. A selector must never fall back to ADB on a failed App call.
internal sealed class SrapOperationAdapter(AdbOperationAdapter adb, SrapPhoneTransport app, Func<AndroidDevice, bool>? isCurrent = null) : IPhoneOperationPort
{
    private sealed record Catalog(long Revision, IReadOnlyList<AndroidApp> Apps, bool HasIcons);
    private sealed class SessionCatalog
    {
        internal readonly SemaphoreSlim Read = new(1, 1);
        internal Catalog? Latest;
    }
    private readonly ConditionalWeakTable<SrapSession, SessionCatalog> _catalogs = new();
    private SrapSession? Selected(AndroidDevice device) => isCurrent?.Invoke(device) == false ? null : app.Session(device);
    private SrapSession Require(AndroidDevice device) => Selected(device) ?? throw new SrapException("stale_session");
    private bool Allows(AndroidDevice device, string id) => Selected(device)?.Capabilities.Allows(id) == true;
    public bool CanListApps(AndroidDevice device) => device.IsApp ? Allows(device, "catalog.apps") : adb.CanListApps(device);
    public bool CanListOutputs(AndroidDevice device) => device.IsApp ? Allows(device, "audio.devices") : adb.CanListOutputs(device);
    public bool CanPlayPcm(AndroidDevice device) => device.IsApp ? Selected(device)?.SupportsPcm == true : adb.CanPlayPcm(device);
    public AppCapturePermission? AppCapture(AndroidDevice device) => device.IsApp ? Selected(device)?.Capabilities.Capture : null;
    public bool CanViewSharedScreen(AndroidDevice device) => device.IsApp && Selected(device)?.ScreenGrant != null;
    public bool NeedsScreenConsent(AndroidDevice device) => device.IsApp && Selected(device)?.Capabilities.Entries
        .GetValueOrDefault("display.mirror") is { Scope: "one_projection", State: "needs_authorization" };
    public Task ViewSharedScreenAsync(AndroidDevice device, string title, CancellationToken token) => Require(device).ViewScreenAsync(title, token);
    public bool CanControlVolume(AndroidDevice device) => device.IsApp && Allows(device, "volume.device");
    public bool CanTestRoute(AndroidDevice device) => CanListOutputs(device) && device.IsApp
        && Allows(device, "audio.play") && Allows(device, "audio.route.owned");

    public async Task<IReadOnlyList<AndroidApp>> AppsAsync(AndroidDevice device, CancellationToken token, IReadOnlyList<string>? packages = null)
    {
        if (!device.IsApp) return await adb.AppsAsync(device, token, packages).ConfigureAwait(false);
        var session = Require(device);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var cache = _catalogs.GetValue(session, static _ => new());
            var single = packages is { Count: 1 };
            Catalog catalog;
            await cache.Read.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                catalog = single && cache.Latest is { } last && last.Revision == session.Capabilities.Revision
                    ? last : cache.Latest = await ReadCatalogAsync(device, session, deadline.Token).ConfigureAwait(false);
            }
            finally { cache.Read.Release(); }
            if (!ReferenceEquals(Selected(device), session)) throw new SrapException("stale_session");
            if (packages == null) return catalog.Apps;
            var selected = catalog.Apps.Where(a => packages.Contains(a.Package, StringComparer.Ordinal)).ToArray();
            if (single && selected.Length == 1 && catalog.HasIcons)
            {
                var item = selected[0];
                try
                {
                    var reply = await session.RequestAsync("catalog.icon", "catalog.apps",
                        new() { ["appKey"] = item.Package }, deadline.Token).ConfigureAwait(false);
                    selected[0] = item with { IconPng = ReadIcon(reply, item.Package) };
                }
                catch (SrapException error) when (error.Code is "target_unavailable" or "unsupported") { }
            }
            if (!ReferenceEquals(Selected(device), session)) throw new SrapException("stale_session");
            return selected;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new SrapException(session.Token.IsCancellationRequested ? "stale_session" : "timeout");
        }
    }

    private async Task<Catalog> ReadCatalogAsync(AndroidDevice device, SrapSession session, CancellationToken token)
    {
        JsonElement result;
        long revision;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(Selected(device), session)) throw new SrapException("stale_session");
            var snapshot = session.Capabilities;
            if (snapshot.Entries.GetValueOrDefault("catalog.apps") is not { State: "unavailable", Reason: "catalog_loading" })
            {
                try
                {
                    result = await session.RequestAsync("catalog.list", "catalog.apps", new(), token).ConfigureAwait(false);
                    revision = snapshot.Revision;
                    break;
                }
                catch (SrapException ex) when (ex is { Code: "busy", WireReason: "catalog_loading" }) { }
                catch (SrapException ex) when (ex.Code == "unsupported" && session.Capabilities.Revision != snapshot.Revision) { continue; }
            }
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Wake() => changed.TrySetResult();
            session.CapabilitiesChanged += Wake;
            try
            {
                if (session.Capabilities.Revision == snapshot.Revision)
                    await changed.Task.WaitAsync(token).ConfigureAwait(false);
            }
            finally { session.CapabilitiesChanged -= Wake; }
        }
        if (!ReferenceEquals(Selected(device), session)) throw new SrapException("stale_session");
        var apps = new List<AndroidApp>();
        if (!result.TryGetProperty("apps", out var items) || items.ValueKind != JsonValueKind.Array) throw new SrapException("invalid_argument");
        foreach (var item in items.EnumerateArray().Take(1000))
        {
            if (SrapClient.Text(item, "appKey") is not { Length: > 0 and <= 255 } package) continue;
            try { AdbClient.ValidatePackage(package); }
            catch (ArgumentException) { continue; }
            string label = SrapClient.Text(item, "label") ?? package;
            apps.Add(new(package, label.Length <= 1024 ? label : package, 0, ""));
        }
        return new(revision, apps.DistinctBy(a => a.Package).ToArray(), SrapClient.Text(result, "iconFormat") == "png48-v1");
    }

    internal static byte[]? ReadIcon(JsonElement reply, string package)
    {
        if (SrapClient.Text(reply, "appKey") != package || SrapClient.Text(reply, "format") != "png48-v1") return null;
        byte[] png;
        try { png = SrapInvite.Decode(SrapClient.Text(reply, "png"), 33, 16 * 1024); }
        catch (Exception error) when (error is FormatException or InvalidDataException or ArgumentException) { return null; }
        return png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8)) == 13 && png.AsSpan(12, 4).SequenceEqual("IHDR"u8) &&
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)) == 48 && BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)) == 48 ? png : null;
    }

    public async Task<IReadOnlyList<AndroidOutputSink>> OutputsAsync(AndroidDevice device, CancellationToken token)
    {
        if (!device.IsApp) return await adb.OutputsAsync(device, token).ConfigureAwait(false);
        var result = await Require(device).RequestAsync("audio.devices.list", "audio.devices", new(), token).ConfigureAwait(false);
        if (!result.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Array) throw new SrapException("invalid_argument");
        var sinks = AndroidOutputSink.ParseList(outputs).Where(s =>
            outputs.EnumerateArray().Any(o => SrapClient.Text(o, "key") == s.Key)).ToArray();
        return outputs.EnumerateArray().Any(o => SrapClient.Text(o, "key") == "current")
            ? new[] { AndroidOutputSink.Current }.Concat(sinks).ToArray() : sinks;
    }

    public async Task<int> VolumeAsync(AndroidDevice device, int? percent, CancellationToken token)
    {
        if (!device.IsApp) return await adb.VolumeAsync(device, percent, token).ConfigureAwait(false);
        var session = Require(device);
        var result = percent is { } value ? await session.SetVolumeAsync(value, token).ConfigureAwait(false)
            : await session.ReadVolumeAsync(token).ConfigureAwait(false);
        return result.TryGetProperty("percent", out var readback) && readback.TryGetInt32(out int volume) && volume is >= 0 and <= 100
            ? volume : throw new SrapException("invalid_argument");
    }

    public async Task<string> TestRouteAsync(AndroidDevice device, string target, CancellationToken token)
    {
        if (!device.IsApp) return await adb.TestRouteAsync(device, target, token).ConfigureAwait(false);
        var session = Require(device);
        uint stream = await session.StartTrackAsync("current", token).ConfigureAwait(false);
        try { return SrapClient.Text(await session.RouteTrackAsync(stream, target, token).ConfigureAwait(false), "actualKey")!; }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await session.StopTrackAsync(stream, stop.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { session.Dispose(); }
        }
    }

    public Task<AndroidEncoderCatalog> EncodersAsync(AndroidDevice device, CancellationToken token) => device.IsApp
        ? Task.FromException<AndroidEncoderCatalog>(new SrapException("unsupported")) : adb.EncodersAsync(device, token);
    public void InvalidateEncoders(AndroidDevice device) { if (!device.IsApp) adb.InvalidateEncoders(device); }
    public bool CanWatchMedia(AndroidDevice device) => device.IsApp ? Allows(device, "media.control") : adb.CanWatchMedia(device);
    public IPhoneMediaSession OpenMediaSession(AndroidDevice device) =>
        device.IsApp ? new SrapMediaClient(app) : adb.OpenMediaSession(device);
    public bool CanSyncClipboard(AndroidDevice device) => device.IsApp
        ? app.Session(device)?.AllowsFocusedClipboard == true : adb.CanSyncClipboard(device);
    public IAndroidClipboardClient OpenClipboard(AndroidDevice device) =>
        device.IsApp ? new SrapClipboardClient(app) : adb.OpenClipboard(device);
    public IPhoneFileOperations OpenFiles(AndroidDevice device) => device.IsApp ? new SrapFilesClient(Require(device)) : adb.OpenFiles(device);
    public string? FileRoot(AndroidDevice device)
    {
        if (!device.IsApp) return adb.FileRoot(device);
        try { return SrapFilesClient.Root(Require(device)); } catch (SrapException) { return null; }
    }
    public bool CanWriteFiles(AndroidDevice device) => !device.IsApp ? adb.CanWriteFiles(device) :
        Selected(device)?.Capabilities.Entries.TryGetValue("files.transfer", out var cap) == true &&
            cap.State == "available" && cap.Scope == "saf_tree" && cap.Limits?.GetValueOrDefault("write") == "true";
    public AndroidAudioSession CreateCaptureSession(Func<AndroidDevice, AndroidStreamSettings> settings, Func<string, AndroidDevice?> resolvePhone)
    {
        var capture = adb.CreateCaptureSession(settings, key => resolvePhone(key) is { IsApp: false } device ? device : null);
        capture.AppSession = Require;
        return capture;
    }
    public IVirtualDisplayClient CreateDisplay(Func<AndroidAudioSettingsStore> settings) => adb.CreateDisplay(settings);
    public AndroidPhonePcmPlayer CreatePlayback(AndroidDevice device, AndroidPcmSource pcm, string sink, CancellationToken token,
        Action<AndroidDevice, IReadOnlyList<AndroidOutputSink>> outputsObserved, CancellationToken attachment) => device.IsApp
        ? new(pcm, token, attachment, ct => OpenAppPlaybackAsync(device, sink, ct, null))
        : adb.CreatePlayback(device, pcm, sink, token, outputsObserved, attachment);

    internal Task<IPhonePcmConnection> OpenAppPlaybackAsync(AndroidDevice device, string sink, CancellationToken token,
        Action<string, int, string>? routed) => SrapPcmOutput.OpenAsync(Require(device), device, sink, token, routed);
}
