using System.Text.Json;

namespace SonicRoute.AndroidAudio;

// The single operation port routes supported calls on the attachment already admitted
// by AndroidConnectionManager. A selector must never fall back to ADB on a failed App call.
internal sealed class SrapOperationAdapter(AdbOperationAdapter adb, SrapPhoneTransport app) : IPhoneOperationPort
{
    private SrapSession Require(AndroidDevice device) => app.Session(device) ?? throw new SrapException("stale_session");
    private bool Allows(AndroidDevice device, string id) => app.Session(device)?.Capabilities.Allows(id) == true;
    public bool CanListApps(AndroidDevice device) => device.IsApp ? Allows(device, "catalog.apps") : adb.CanListApps(device);
    public bool CanListOutputs(AndroidDevice device) => device.IsApp ? Allows(device, "audio.devices") : adb.CanListOutputs(device);
    public bool CanControlVolume(AndroidDevice device) => device.IsApp && Allows(device, "volume.device");
    public bool CanTestRoute(AndroidDevice device) => CanListOutputs(device) && device.IsApp
        && Allows(device, "audio.play") && Allows(device, "audio.route.owned");

    public async Task<IReadOnlyList<AndroidApp>> AppsAsync(AndroidDevice device, CancellationToken token, IReadOnlyList<string>? packages = null)
    {
        if (!device.IsApp) return await adb.AppsAsync(device, token, packages).ConfigureAwait(false);
        var result = await Require(device).RequestAsync("catalog.list", "catalog.apps", new(), token).ConfigureAwait(false);
        var apps = new List<AndroidApp>();
        if (!result.TryGetProperty("apps", out var items) || items.ValueKind != JsonValueKind.Array) throw new SrapException("invalid_argument");
        foreach (var item in items.EnumerateArray().Take(1000))
        {
            if (SrapClient.Text(item, "appKey") is not { Length: > 0 and <= 255 } package
                || (packages != null && !packages.Contains(package, StringComparer.Ordinal))) continue;
            try { AdbClient.ValidatePackage(package); }
            catch (ArgumentException) { continue; }
            string label = SrapClient.Text(item, "label") ?? package;
            apps.Add(new(package, label.Length <= 1024 ? label : package, 0, ""));
        }
        return apps.DistinctBy(a => a.Package).ToArray();
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
    public IAndroidClipboardClient OpenClipboard() => adb.OpenClipboard();
    public IPhoneFileOperations OpenFiles(AndroidDevice device) => device.IsApp ? throw new SrapException("unsupported") : adb.OpenFiles(device);
    public AndroidAudioSession CreateCaptureSession(Func<AndroidDevice, AndroidStreamSettings> settings, Func<string, AndroidDevice?> resolvePhone) =>
        adb.CreateCaptureSession(settings, key => resolvePhone(key) is { IsApp: false } device ? device : null);
    public IVirtualDisplayClient CreateDisplay(Func<AndroidAudioSettingsStore> settings) => adb.CreateDisplay(settings);
    public AndroidPhonePcmPlayer CreatePlayback(AndroidDevice device, AndroidPcmSource pcm, string sink, CancellationToken token,
        Action<AndroidDevice, IReadOnlyList<AndroidOutputSink>> outputsObserved, CancellationToken attachment) => device.IsApp
        ? throw new SrapException("unsupported") : adb.CreatePlayback(device, pcm, sink, token, outputsObserved, attachment);
}
