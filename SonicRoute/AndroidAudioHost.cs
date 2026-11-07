#if SONICROUTE_PHONE
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

namespace SonicRoute;

internal static class AndroidAudioHost
{
    private static readonly object PhoneOperations = new();
    private static readonly object SilenceGate = new();
    private static readonly object OutputSnapshotGate = new();
    private static IReadOnlyList<AudioDeviceInfo>? _outputSnapshot;
    private static string? _silenceEndpointId;
    private static bool _silenceEndpointWasMuted;
    private static int _silenceLeaseCount;
    private static readonly Dictionary<string, long> ProbeTimestamps = new(StringComparer.Ordinal);
    static AndroidAudioHost()
    {
        PhoneAudioWindowCache.DevicesChanged += OnCachedDevicesChanged;
        L10n.Instance.PropertyChanged += (_, _) => PhoneOutputsChanged();
    }
    private static readonly Lazy<AdbClient> LazyAdb = new(() => new(new AndroidRuntimeBundle(Path.Combine(
        Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SonicRoute"), "PhoneRuntime"))));
    internal static AdbClient Adb => LazyAdb.Value;
    internal static bool IsAdbCreated => LazyAdb.IsValueCreated;
    private static readonly Lazy<AndroidAudioSettingsStore> LazySettings = new(() =>
        new AndroidAudioSettingsStore(Path.Combine(Path.GetDirectoryName(ConfigService.ConfigPath)!, "android-audio.json")));
    private static readonly Lazy<AndroidAudioManager> LazySources = new(() =>
    {
        var sources = new AndroidAudioManager(Adb, LazySettings.Value, (device, pkg) => IsPhoneCreated ? Phone.IsSmartVolumeEnabled(device, pkg) : true);
        sources.Changed += ApplicationAudio.NotifyChanged;
        return sources;
    });
    private static readonly CancellationTokenSource MediaShutdown = new();
    private static readonly HashSet<Task> MediaWatches = new();
    private static readonly Lazy<PcToPhoneAudioManager> LazyPcToPhone = new(() =>
    {
        var manager = new PcToPhoneAudioManager(() => Adb, AcquireLocalSilenceLease);
        manager.Changed += ApplicationAudio.NotifyChanged;
        manager.OutputsObserved += (device, sinks) =>
        {
            if (IsPhoneCreated) Phone.UpdateOutputs(device, sinks);
        };
        manager.DeviceDisconnected += disconnectedDevice =>
        {
            PhoneAudioWindowCache.InvalidateDevices();
            _ = PhoneAudioWindowCache.ReadDevicesAsync(MediaShutdown.Token);
        };
        return manager;
    });
    internal static PcToPhoneAudioManager PcToPhone => LazyPcToPhone.Value;
    internal static bool IsPcToPhoneCreated => LazyPcToPhone.IsValueCreated;
    private static readonly Lazy<AndroidVirtualDisplayManager> LazyDisplays = new(() =>
    {
        var runtime = new VirtualDisplayRuntime(Adb);
        return new AndroidVirtualDisplayManager(() => new VirtualDisplayClient(Adb, runtime, () => LazySettings.Value));
    });
    internal static AndroidAudioManager Sources => LazySources.Value;
    private static readonly Lazy<AndroidPhoneStore> LazyPhone = new(() =>
    {
        var phone = new AndroidPhoneStore(Path.Combine(Path.GetDirectoryName(ConfigService.ConfigPath)!, "android-phone.json"));
        phone.Changed += OnPhoneStoreChanged;
        phone.DisplayNamesChanged += PhoneOutputsChanged;
        phone.OutputsChanged += PhoneOutputsChanged;
        return phone;
    });
    internal static AndroidPhoneStore Phone => LazyPhone.Value;
    internal static bool IsPhoneCreated => LazyPhone.IsValueCreated;
    // File-backed preferences are loaded once on a worker, when a phone view or playback
    // actually needs them. A closing view cancels its wait, not another caller's load.
    private static readonly Lazy<Task<AndroidPhoneStore>> PhoneRead = new(() => Task.Run(() => Phone));
    private static readonly Lazy<Task<AndroidAudioManager>> SourcesRead = new(() => Task.Run(() => Sources));
    internal static async Task PreparePhoneAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!LazyPhone.IsValueCreated) await PhoneRead.Value.WaitAsync(token).ConfigureAwait(false);
    }
    internal static async Task<AndroidAudioSource> AddAudioAsync(AndroidDevice device, AndroidApp app,
        bool keepPhoneAudio = true, bool displayOwned = false)
    {
        MediaShutdown.Token.ThrowIfCancellationRequested();
        await Task.WhenAll(PreparePhoneAsync(MediaShutdown.Token),
            SourcesRead.Value.WaitAsync(MediaShutdown.Token)).ConfigureAwait(false);
        MediaShutdown.Token.ThrowIfCancellationRequested();
        Task<AndroidAudioSource> start;
        lock (PhoneOperations)
        {
            if (Phone.IsRemoved(device)) throw AndroidError.WithCode(new IOException("此手机已移除，请先添加设备。"), AndroidErrorCode.PhoneRemovedAddFirst);
            start = Sources.AddAsync(device, app, keepPhoneAudio, displayOwned: displayOwned);
        }
        _ = ProbeOutputsIfConnectedAsync([device], token: MediaShutdown.Token);
        return await start.ConfigureAwait(false);
    }
    internal static Task<IVirtualDisplayClient> OpenDisplayAsync(AndroidDevice device, AndroidApp app, int x, int y)
    {
        lock (PhoneOperations)
        {
            if (Phone.IsRemoved(device)) throw AndroidError.WithCode(new IOException("此手机已移除，请先添加设备。"), AndroidErrorCode.PhoneRemovedAddFirst);
            return Displays.OpenAsync(device, app, x, y);
        }
    }
    internal static async Task RemovePhoneAsync(string profileId, AndroidDevice device)
    {
        lock (PhoneOperations)
            if (!Phone.RemoveDevice(profileId, PhoneAudioWindowCache.Devices)) return;
        // Complete owned cleanup even if selecting the replacement profile hides this page.
        var cleanup = new List<Task>();
        if (AreDisplaysCreated) cleanup.Add(Displays.CloseDeviceAsync(device));
        if (IsCreated)
            cleanup.AddRange(Sources.Sources.Where(s => AndroidDeviceIdentity.Matches(s.Device, device))
                .Select(s => Sources.RemoveAsync(s.Id)));
        if (IsPcToPhoneCreated) cleanup.Add(PcToPhone.StopPhoneAsync(device));
        await Task.WhenAll(cleanup);
        await Phone.FlushAsync();
    }
    internal static bool IsPhoneOnline(AndroidDevice? device)
    {
        if (device == null) return false;
        if (PhoneAudioWindowCache.Devices is { } cached)
            return cached.Any(d => d.State == "device" && AndroidDeviceIdentity.Matches(d, device));
        return device.State == "device";
    }
    private static void OnCachedDevicesChanged()
    {
        InvalidateOutputSnapshot();
        var cached = PhoneAudioWindowCache.Devices;
        if (LazyPhone.IsValueCreated)
        {
            foreach (var profile in Phone.Profiles)
            {
                if (profile.LastDevice is { } dev && !IsPhoneOnline(dev) && profile.OutputSinks.Count > 1)
                    Phone.UpdateOutputs(dev, [AndroidOutputSink.DefaultSpeaker]);
            }
            ApplicationAudio.NotifyOutputsChanged();
        }
        if (LazyPcToPhone.IsValueCreated && cached != null)
        {
            foreach (var route in PcToPhone.GetActiveRoutes())
            {
                bool online = cached.Any(d => d.State == "device"
                    && string.Equals(AndroidDeviceIdentity.Key(d), route.PhoneKey, StringComparison.OrdinalIgnoreCase));
                if (!online)
                    PcToPhone.StopProcess(route.ProcessId, restorePreviousEndpoint: true);
            }
        }
    }
    internal static IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        if (!LazyPhone.IsValueCreated) return Array.Empty<AudioDeviceInfo>();
        lock (OutputSnapshotGate)
        {
            if (_outputSnapshot != null) return _outputSnapshot;
            return _outputSnapshot = BuildOutputSnapshot();
        }
    }
    private static void OnPhoneStoreChanged()
    {
        (System.Windows.Application.Current as App)?.PhoneClipboardProfilesChanged();
        InvalidateOutputSnapshot();
        if (LazySources.IsValueCreated && LazyPhone.IsValueCreated)
            Sources.SyncSmartVolume(Phone.IsSmartVolumeEnabled);
    }
    internal static void InvalidateOutputSnapshot()
    { lock (OutputSnapshotGate) _outputSnapshot = null; }
    private static void PhoneOutputsChanged()
    {
        InvalidateOutputSnapshot();
        ApplicationAudio.NotifyOutputsChanged();
    }
    private static IReadOnlyList<AudioDeviceInfo> BuildOutputSnapshot()
    {
        var store = Phone;
        var list = new List<AudioDeviceInfo>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in store.Profiles)
        {
            if (profile.LastDevice is not { } device || store.IsRemoved(device) || !IsPhoneOnline(device)) continue;
            string phoneKey = AndroidDeviceIdentity.Key(device);
            var sinks = profile.OutputSinks is { Count: > 0 } ? profile.OutputSinks : [AndroidOutputSink.DefaultSpeaker];
            foreach (var sink in sinks)
            {
                string id = AndroidOutputIdentity.FormatDeviceId(phoneKey, sink.Key);
                if (!seenIds.Add(id)) continue;
                list.Add(new AudioDeviceInfo
                {
                    Id = id,
                    DisplayName = OutputDisplayName(device, sink),
                    Flow = EDataFlow.eRender,
                    IsDefault = false
                });
            }
        }
        return list.AsReadOnly();
    }
    internal static string OutputDisplayName(AndroidDevice? device, AndroidOutputSink sink)
    {
        string phoneName = device == null ? L10n.T("Phone.Title") : LazyPhone.IsValueCreated ? Phone.GetDisplayName(device) : device.Name;
        string sinkName = sink.Name == "蓝牙音频设备" ? L10n.T("Phone.BluetoothAudioDevice") : sink.Name;
        return sink.Kind == AndroidOutputSinkKind.Speaker
            ? string.Format(L10n.T("Phone.PcDropSpeakerTitle"), phoneName) : string.Format(L10n.T("Phone.BluetoothOutputFormat"), sinkName, phoneName);
    }
    internal static AudioDeviceInfo SpeakerOutput(AndroidDevice device) => new()
    {
        Id = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), "speaker"),
        DisplayName = OutputDisplayName(device, AndroidOutputSink.DefaultSpeaker),
        Flow = EDataFlow.eRender
    };
    internal static IReadOnlyList<AudioDeviceInfo> OutputsForDevice(AndroidDevice device)
    {
        var zones = GetDropZonesForDevice(device);
        var result = new List<AudioDeviceInfo>(zones.Count);
        foreach (var (sink, id) in zones)
            result.Add(new AudioDeviceInfo { Id = id, DisplayName = OutputDisplayName(device, sink), Flow = EDataFlow.eRender });
        return result;
    }
    internal static IReadOnlyList<(AndroidOutputSink Sink, string DeviceId)> GetDropZonesForDevice(AndroidDevice? device)
    {
        if (device == null) return Array.Empty<(AndroidOutputSink, string)>();
        string phoneKey = AndroidDeviceIdentity.Key(device);
        IReadOnlyList<AndroidOutputSink> sinks = [AndroidOutputSink.DefaultSpeaker];
        if (LazyPhone.IsValueCreated && Phone.FindProfile(device) is { OutputSinks.Count: > 0 } profile)
            sinks = profile.OutputSinks;
        var result = new List<(AndroidOutputSink, string)>(sinks.Count);
        foreach (var sink in sinks)
            result.Add((sink, AndroidOutputIdentity.FormatDeviceId(phoneKey, sink.Key)));
        return result;
    }
    internal static string? TryGetPcAppPhoneOutput(int processId) =>
        LazyPcToPhone.IsValueCreated ? PcToPhone.GetPersistedEndpoint(processId) : null;
    internal static (bool Success, int HResult, string Message) RoutePcAppToPhone(int processId, string deviceId)
    {
        if (!AndroidOutputIdentity.TryParseDeviceId(deviceId, out string phoneKey, out _))
            return (false, -1, L10n.T("Phone.Err.InvalidPhoneOutputDevice"));
        var connected = PhoneAudioWindowCache.Devices?.FirstOrDefault(d =>
            d.State == "device" && string.Equals(AndroidDeviceIdentity.Key(d), phoneKey, StringComparison.OrdinalIgnoreCase));
        var fallback = connected ?? (LazyPhone.IsValueCreated
            ? Phone.Profiles.FirstOrDefault(p => p.LastDevice != null
                && string.Equals(AndroidDeviceIdentity.Key(p.LastDevice), phoneKey, StringComparison.OrdinalIgnoreCase))?.LastDevice
            : null);
        var res = PcToPhone.RouteProcess(processId, deviceId, fallback);
        return res.Success ? (true, 0, L10n.T("History.Status.success")) : (false, res.HResult, processId <= 0 ? L10n.T("Phone.Err.InvalidProcessId") : L10n.T("Phone.Err.InvalidPhoneOutputId"));
    }
    internal static bool StopPcAppToPhone(int processId, bool restorePreviousEndpoint) =>
        LazyPcToPhone.IsValueCreated && PcToPhone.StopProcess(processId, restorePreviousEndpoint);
    internal static int StopAllPcAppsToPhone(bool restorePreviousEndpoint = true) =>
        LazyPcToPhone.IsValueCreated ? PcToPhone.StopAll(restorePreviousEndpoint) : 0;
    internal static async Task ProbeOutputsIfConnectedAsync(IEnumerable<AndroidDevice>? devices, bool force = false, CancellationToken token = default)
    {
        if (devices == null || !LazyAdb.IsValueCreated || !LazyPhone.IsValueCreated) return;
        long now = Environment.TickCount64;
        foreach (var device in devices)
        {
            if (device.State != "device" || string.Equals(device.Serial, "offline-fixture", StringComparison.Ordinal)) continue;
            lock (ProbeTimestamps)
            {
                if (!force && ProbeTimestamps.TryGetValue(device.Serial, out long last) && now - last < 3000) continue;
                ProbeTimestamps[device.Serial] = now;
            }
            try
            {
                var outputs = await Adb.OutputsAsync(device, token).ConfigureAwait(false);
                Phone.UpdateOutputs(device, outputs);
            }
            catch
            {
                lock (ProbeTimestamps) ProbeTimestamps.Remove(device.Serial);
            }
        }
    }
    private static IPcProcessSilenceLease? AcquireLocalSilenceLease(int processId)
    {
        if (processId <= 0 || (IsPcToPhoneCreated && !PcToPhone.EnableLiveStreaming)) return null;
        lock (SilenceGate)
        {
            if (_silenceEndpointId == null)
            {
                var outputs = AudioService.GetDevices(EDataFlow.eRender);
                string? defaultId = AudioService.GetDefaultDeviceId(EDataFlow.eRender);
                var hidden = ConfigService.Load().HiddenOutputDevices;
                var candidate = outputs
                    .Where(d => !string.Equals(d.Id, defaultId, StringComparison.OrdinalIgnoreCase)
                        && !AndroidOutputIdentity.IsPhoneOutput(d.Id)
                        && d.DisplayName?.Contains("Microphone", StringComparison.OrdinalIgnoreCase) != true
                        && d.DisplayName?.Contains("麦克风", StringComparison.OrdinalIgnoreCase) != true)
                    .OrderBy(d => hidden.Contains(d.Id) ? 0 : 1)
                    .ThenBy(d => d.DisplayName?.Contains("Steam Streaming Speakers", StringComparison.OrdinalIgnoreCase) == true ? 0
                        : d.DisplayName?.Contains("High Definition Audio", StringComparison.OrdinalIgnoreCase) == true ? 1
                        : d.DisplayName?.Contains("Sonar", StringComparison.OrdinalIgnoreCase) != true ? 2 : 3)
                    .FirstOrDefault();
                if (candidate != null)
                {
                    var state = SystemVolumeService.ReadStateChecked(candidate.Id);
                    _silenceEndpointId = candidate.Id;
                    _silenceEndpointWasMuted = state.MuteKnown && state.Muted;
                    SystemVolumeService.SetMute(_silenceEndpointId, true);
                }
            }
            if (_silenceEndpointId == null) return null;
            string? previousEndpoint = AudioService.GetPersistedEndpoint(processId, EDataFlow.eRender);
            _silenceLeaseCount++;
            AudioService.ApplyEndpoint(processId, EDataFlow.eRender, _silenceEndpointId);
            return new LocalSilenceLease(processId, previousEndpoint);
        }
    }
    private sealed class LocalSilenceLease(int processId, string? previousEndpoint) : IPcProcessSilenceLease
    {
        private int _released;
        public void Release(bool restorePreviousEndpoint)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (SilenceGate)
            {
                if (restorePreviousEndpoint)
                {
                    string target = string.IsNullOrWhiteSpace(previousEndpoint)
                        ? AudioService.SystemDefaultDeviceId
                        : previousEndpoint;
                    try { AudioService.ApplyEndpoint(processId, EDataFlow.eRender, target); } catch { }
                }
                if (--_silenceLeaseCount <= 0)
                {
                    _silenceLeaseCount = 0;
                    if (_silenceEndpointId != null && !_silenceEndpointWasMuted)
                    {
                        try { SystemVolumeService.SetMute(_silenceEndpointId, false); } catch { }
                    }
                    _silenceEndpointId = null;
                }
            }
        }
        public void Dispose() => Release(restorePreviousEndpoint: true);
    }
    private static readonly Lazy<AndroidAppCatalogCache> LazyCatalog = new(() =>
        new AndroidAppCatalogCache(Path.Combine(Path.GetDirectoryName(ConfigService.ConfigPath)!, "phone-app-catalog.json"),
            (device, token) => Adb.AppsAsync(device, token)));
    internal static AndroidAppCatalogCache Catalog => LazyCatalog.Value;
    private static bool _isDragging;
    internal static event Action? DragStateChanged;
    internal static bool IsDragging
    {
        get => _isDragging;
        set { if (_isDragging == value) return; _isDragging = value; DragStateChanged?.Invoke(); }
    }
    internal static bool IsCreated => LazySources.IsValueCreated;
    internal static AndroidVirtualDisplayManager Displays => LazyDisplays.Value;
    internal static bool AreDisplaysCreated => LazyDisplays.IsValueCreated;
    internal static void FlushSettings()
    {
        if (LazySettings.IsValueCreated) LazySettings.Value.FlushAsync().GetAwaiter().GetResult();
        if (LazyPhone.IsValueCreated) LazyPhone.Value.FlushAsync().GetAwaiter().GetResult();
    }
    internal static Task WatchMediaAsync(AndroidMediaSessionClient client, AndroidDevice device,
        Action<AndroidMediaState> changed, CancellationToken token)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(token, MediaShutdown.Token);
        IReadOnlyList<AndroidOutputSink>? previousOutputs = null;
        var task = client.WatchAsync(device, state =>
        {
            if (!ReferenceEquals(previousOutputs, state.Outputs))
            {
                if (LazyPhone.IsValueCreated) Phone.UpdateOutputs(device, state.Outputs);
                previousOutputs = state.Outputs;
            }
            changed(state);
        }, linked.Token);
        lock (MediaWatches) MediaWatches.Add(task);
        _ = task.ContinueWith(done =>
        {
            lock (MediaWatches) MediaWatches.Remove(done);
            linked.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }
    internal static void StopMedia()
    {
        MediaShutdown.Cancel();
        if (LazyPcToPhone.IsValueCreated)
        {
            try { PcToPhone.StopAll(restorePreviousEndpoint: true); } catch { }
        }
        Task[] pending; lock (MediaWatches) pending = MediaWatches.ToArray();
        try { Task.WhenAll(pending).GetAwaiter().GetResult(); } catch { }
        // A cancelled view may have left a small configuration read in flight. Finish
        // that existing read before shutdown flushes settings; never start one here.
        if (PhoneRead.IsValueCreated) try { PhoneRead.Value.GetAwaiter().GetResult(); } catch { }
        if (SourcesRead.IsValueCreated) try { SourcesRead.Value.GetAwaiter().GetResult(); } catch { }
    }
}

public sealed record AndroidAppDrag(AndroidDevice Device, AndroidApp App)
{
    public const string Format = "SonicRoute.AndroidApp.v1";
}

internal static class AndroidLabRuntime
{
    internal static bool StartInLabMode { get; private set; }
    internal static void Initialize(ref string[] args)
    {
        // WPF builds StartupEventArgs from the process arguments, not this local array.
        StartInLabMode = Array.Exists(args, a => string.Equals(a, "--android-lab", StringComparison.OrdinalIgnoreCase));
        if (!StartInLabMode) return;
        // Every experimental executable defaults to its own configuration and instance identity.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")))
            Environment.SetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY", Path.Combine(AppContext.BaseDirectory, "LabData"));
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SONICROUTE_INSTANCE_ID")))
            Environment.SetEnvironmentVariable("SONICROUTE_INSTANCE_ID", "AndroidLabMultiApp");
    }
}
internal static class AndroidErrorTranslation
{
    internal static string Format(Exception? ex)
    {
        if (ex == null) return "";
        if (AndroidError.TryGetCode(ex, out var code))
            return Format(code, ex.Message);
        return ex.Message ?? "";
    }

    internal static string Format(AndroidAudioState state) =>
        state.ErrorCode is { } code ? Format(code, state.Error) : (state.Error ?? "");

    internal static string Format(AndroidErrorCode code, string? fallback = null)
    {
        string key = "Phone.Err." + code;
        string translated = L10n.T(key);
        return !string.Equals(translated, key, StringComparison.Ordinal) ? translated : (fallback ?? "");
    }
}
#endif
