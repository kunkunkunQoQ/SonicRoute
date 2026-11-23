#if SONICROUTE_PHONE
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

namespace SonicRoute;

internal static partial class AndroidAudioHost
{
    private static readonly object PhoneOperations = new();
    private static readonly object OutputSnapshotGate = new();
    private static IReadOnlyList<AudioDeviceInfo>? _outputSnapshot;
    private static readonly Guid OutputMuteContext = Guid.NewGuid();
    private static readonly LocalSilenceLeasePool SilenceLeases = new(
        (owned, reserved) => FindAvailableSilenceOutputs(owned, reserved, id => (MirrorSilenceLeases?.OwnsEndpoint(id) ?? false) || (SourceSpeakers?.OwnsEndpoint(id) ?? false)),
        ReadOutputMute, SetReservedOutputMute, ReadSilenceRoles, WriteSilenceRoles)
        {
            ObservePlayback = (pid, id, revoke) => new LocalPlaybackWatch(pid, id, revoke) { CheckReservedOutput = RevalidateLocalSilence }
        };
    private static readonly LocalSilenceLeasePool MirrorSilenceLeases = new(
        (owned, reserved) => FindAvailableSilenceOutputs(owned, reserved, id => SilenceLeases.OwnsEndpoint(id) || (SourceSpeakers?.OwnsEndpoint(id) ?? false)),
        ReadOutputMute, SetReservedOutputMute, ReadSilenceRoles, WriteSilenceRoles)
        {
            VerifyPlaybackRoute = VerifyWindowsPlaybackRoute,
            ObservePlayback = (pid, id, revoke) => new LocalPlaybackWatch(pid, id, revoke) { CheckReservedOutput = RevalidateLocalSilence }
        };
    private static readonly AlreadyMutedOutputLeases MutedOutputs = new(AllWindowsOutputsUserMuted);
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
        var sources = new AndroidAudioManager(() => new AndroidAudioSession(Adb, LazySettings.Value.GetStreaming, FindOnlinePhone), LazySettings.Value, (device, pkg) => IsPhoneCreated ? Phone.IsSmartVolumeEnabled(device, pkg) : true);
        sources.SourceChanged += ApplicationAudio.NotifySourceChanged;
        sources.ResourcesRetired += OnResourcesRetired;
        return sources;
    });
    private static readonly CancellationTokenSource MediaShutdown = new();
    private static readonly HashSet<Task> MediaWatches = new();
    private static readonly Lazy<PcToPhoneAudioManager> LazyPcToPhone = new(() =>
    {
        var manager = new PcToPhoneAudioManager(() => Adb, AcquireLocalSilenceLease);
        manager.RouteChanged += ApplicationAudio.NotifyRouteChanged;
        manager.ResourcesRetired += OnResourcesRetired;
        manager.OutputsObserved += (device, sinks) =>
        {
            if (IsPhoneCreated) Phone.UpdateOutputs(device, sinks);
        };
        manager.DeviceDisconnected += PhonePlaybackFailed;
        return manager;
    });
    internal static PcToPhoneAudioManager PcToPhone => LazyPcToPhone.Value;
    internal static bool IsPcToPhoneCreated => LazyPcToPhone.IsValueCreated;
    private static readonly Lazy<AndroidVirtualDisplayManager> LazyDisplays = new(() =>
    {
        var runtime = new VirtualDisplayRuntime(Adb);
        var displays = new AndroidVirtualDisplayManager(() => new InProcessVirtualDisplayClient(Adb, runtime, () => LazySettings.Value));
        displays.ResourcesRetired += OnResourcesRetired;
        return displays;
    });
    internal static AndroidAudioManager Sources => LazySources.Value;
    private static readonly Lazy<AndroidPhoneStore> LazyPhone = new(() =>
    {
        var phone = new AndroidPhoneStore(PhoneStorePath, () => L10n.T("Phone.DefaultName"));
        phone.Changed += OnPhoneStoreChanged;
        phone.DisplayNamesChanged += PhoneOutputsChanged;
        phone.OutputsChanged += PhoneOutputsChanged;
        return phone;
    });
    internal static AndroidPhoneStore Phone => LazyPhone.Value;
    internal static bool IsPhoneCreated => LazyPhone.IsValueCreated;
    internal static void PhonePlaybackFailed(AndroidDevice device) =>
        _ = RefreshPhonePlaybackDevicesAsync(device, MediaShutdown.Token);

    internal static async Task RefreshPhonePlaybackDevicesAsync(AndroidDevice? device, CancellationToken token)
    {
        try
        {
            await PreparePhoneAsync(token).ConfigureAwait(false);
            device ??= Phone.Profile.LastDevice;
            if (device == null || Phone.IsRemoved(device)) return;
            var devices = await PhoneAudioWindowCache.ReadDevicesAsync(token).ConfigureAwait(false);
            if (PhoneAudioWindowCache.GetOnlineDevice(device) is { } online)
                await ProbeOutputsIfConnectedAsync([online], token: token).ConfigureAwait(false);
            else
                await PhoneAudioWindowCache.RecoverMissingWirelessAsync(device, devices, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Phone playback discovery: " + ex.Message); }
    }
    // File-backed preferences are loaded once on a worker, when a phone view or playback
    // actually needs them. A closing view cancels its wait, not another caller's load.
    private static readonly Lazy<Task<AndroidPhoneStore>> PhoneRead = new(() => Task.Run(() => Phone));
    private static readonly Lazy<Task<AndroidAudioManager>> SourcesRead = new(() => Task.Run(() => Sources));
    private static readonly Lazy<Task<AndroidAudioSettingsStore>> SettingsRead = new(() => Task.Run(() => LazySettings.Value));
    internal static Task<AndroidAudioSettingsStore> PrepareStreamingAsync(CancellationToken token) => SettingsRead.Value.WaitAsync(token);
    internal static async Task PreparePhoneAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!LazyPhone.IsValueCreated) await PhoneRead.Value.WaitAsync(token).ConfigureAwait(false);
    }
    internal static async Task<AndroidAudioSource> AddAudioAsync(AndroidDevice device, AndroidApp app,
        bool keepPhoneAudio = true, bool displayOwned = false)
    {
        MediaShutdown.Token.ThrowIfCancellationRequested();
        device = await ResolveOperationDeviceAsync(device, AndroidErrorCode.PhoneOffline).ConfigureAwait(false);
        await SourcesRead.Value.WaitAsync(MediaShutdown.Token).ConfigureAwait(false);
        MediaShutdown.Token.ThrowIfCancellationRequested();
        Task<AndroidAudioSource> start;
        lock (PhoneOperations)
        {
            RequireCurrentDevice(device, AndroidErrorCode.PhoneOffline);
            start = Sources.AddAsync(device, app, keepPhoneAudio, displayOwned: displayOwned);
        }
        _ = ProbeOutputsIfConnectedAsync([device], token: MediaShutdown.Token);
        return await start.ConfigureAwait(false);
    }
    internal static Task<IVirtualDisplayClient> OpenDisplayAsync(AndroidDevice device, AndroidApp app, int x, int y)
    {
        return OpenLiveDisplayAsync(device, app, x, y);
    }
    private static async Task<IVirtualDisplayClient> OpenLiveDisplayAsync(AndroidDevice device, AndroidApp app, int x, int y)
    {
        device = await ResolveOperationDeviceAsync(device, AndroidErrorCode.DisplayPhoneNotConnected).ConfigureAwait(false);
        Task<IVirtualDisplayClient> open;
        lock (PhoneOperations)
        {
            RequireCurrentDevice(device, AndroidErrorCode.DisplayPhoneNotConnected);
            open = Displays.OpenAsync(device, app, x, y);
        }
        return await open.ConfigureAwait(false);
    }
    internal static async Task<AndroidDevice> ResolveOperationDeviceAsync(AndroidDevice device, AndroidErrorCode offlineCode)
    {
        var token = MediaShutdown.Token;
        await PreparePhoneAsync(token).ConfigureAwait(false);
        var initial = PhoneAudioWindowCache.Manager.Get(device);
        if (initial.State is AndroidManagedConnectionState.Removed or AndroidManagedConnectionState.ManuallyDisconnected
            or AndroidManagedConnectionState.IdentityConflict) throw ConnectionError(initial.State, offlineCode);
        var live = await PhoneAudioWindowCache.ResolveOnlineDeviceAsync(device, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (live == null) throw ConnectionError(PhoneAudioWindowCache.Manager.Get(device).State, offlineCode);
        RequireCurrentDevice(live, offlineCode);
        return live;
    }
    private static IOException ConnectionError(AndroidManagedConnectionState state, AndroidErrorCode offlineCode) =>
        state == AndroidManagedConnectionState.Removed
            ? AndroidError.WithCode(new IOException("此手机已移除，请先添加设备。"), AndroidErrorCode.PhoneRemovedAddFirst)
            : AndroidError.WithCode(new IOException("设备未连接或身份待确认，请刷新设备后重试。"), offlineCode);

    private static void RequireCurrentDevice(AndroidDevice device, AndroidErrorCode offlineCode)
    {
        if (!PhoneAudioWindowCache.Manager.IsCurrent(device))
            throw ConnectionError(PhoneAudioWindowCache.Manager.Get(device).State, offlineCode);
    }
    internal static async Task RemovePhoneAsync(string profileId, AndroidDevice device)
    {
        IReadOnlyList<AndroidDevice> aliases;
        lock (PhoneOperations)
            if (!PhoneAudioWindowCache.Manager.Remove(profileId, out aliases)) return;
        foreach (var alias in aliases) PhoneAudioWindowCache.CancelRecovery(alias);
        PhoneAudioWindowCache.NotifyManagementChanged();
        try { await CleanupManagedPhoneAsync(aliases); }
        finally { PhoneAudioWindowCache.Manager.CompleteRetirement(aliases); PhoneAudioWindowCache.NotifyManagementChanged(); }
        await Phone.FlushAsync();
    }
    internal static async Task StopManagedPhoneAsync(AndroidDevice device, Func<Task>? finishTransport = null)
    {
        var aliases = (PhoneAudioWindowCache.Discovery.CurrentDevices ?? []).Where(d => AndroidDeviceIdentity.Matches(device, d))
            .Append(device).Distinct().ToArray();
        lock (PhoneOperations)
        {
            PhoneAudioWindowCache.Manager.BeginRetirement(aliases);
            try { PhoneAudioWindowCache.SetAutomaticRecovery(device, false); }
            catch { PhoneAudioWindowCache.Manager.CompleteRetirement(aliases); throw; }
        }
        try
        {
            await CleanupManagedPhoneAsync(aliases);
            if (finishTransport != null) await finishTransport();
        }
        finally { PhoneAudioWindowCache.Manager.CompleteRetirement(aliases); PhoneAudioWindowCache.NotifyManagementChanged(); }
    }
    private static async Task CleanupManagedPhoneAsync(IReadOnlyList<AndroidDevice> aliases)
    {
        // Block new operations first, then retire all owned sessions (including a
        // former USB/Wi-Fi alias). The saved settings themselves are kept.
        // Complete owned cleanup even if selecting the replacement profile hides this page.
        var settings = await PrepareStreamingAsync(MediaShutdown.Token).ConfigureAwait(false);
        foreach (var alias in aliases.DistinctBy(AndroidDeviceIdentity.Key))
        {
            settings.ForgetPhoneRoutes(alias);
            var cleanup = new List<Task> { LocalAppOutputMirrors.PhoneRemovedAsync(alias) };
            if (IsCreated) cleanup.Add(DisconnectAndroidSourcesAsync(alias));
            if (AreDisplaysCreated) cleanup.Add(Displays.CloseDeviceAsync(alias));
            if (IsPcToPhoneCreated) cleanup.Add(PcToPhone.StopPhoneAsync(alias));
            if (AirPlayHost.Source is { } airPlay) cleanup.Add(airPlay.PhoneRemovedAsync(alias));
            await Task.WhenAll(cleanup);
        }
        await settings.FlushAsync();
    }
    internal static Task DisconnectAndroidSourcesAsync(AndroidDevice device) =>
        !IsCreated ? Task.CompletedTask : Sources.RemovePhoneAsync(device);

    internal static bool IsPhoneOnline(AndroidDevice? device)
        => PhoneAudioWindowCache.GetOnlineDevice(device) != null;
    private static void OnCachedDevicesChanged()
    {
        InvalidateOutputSnapshot();
        var cached = PhoneAudioWindowCache.Discovery.CurrentDevices;
        if (LazyPhone.IsValueCreated)
        {
            foreach (var profile in Phone.Profiles)
            {
                if (cached != null && profile.LastDevice is { } dev && !IsPhoneOnline(dev) && profile.OutputSinks.Count > 1)
                    Phone.UpdateOutputs(dev, [AndroidOutputSink.DefaultSpeaker]);
            }
            ApplicationAudio.NotifyOutputsChanged();
        }
        if (LazyPcToPhone.IsValueCreated && cached != null)
        {
            foreach (var route in PcToPhone.GetActiveRoutes())
            {
                bool online = FindOnlinePhone(route.PhoneKey) != null;
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
        ApplicationAudio.NotifyOutputsChanged();
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
            if (PhoneAudioWindowCache.GetOnlineDevice(profile.LastDevice) is not { } device) continue;
            foreach (var (sink, id) in GetDropZonesForDevice(device))
            {
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
        if (sink.Kind == AndroidOutputSinkKind.Current)
        {
            string current = L10n.T("Phone.NativeAudioOutput");
            return PhoneAudioWindowCache.OnlineDevices.Count > 1 ? current + " · " + phoneName : current;
        }
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
        if (PhoneAudioWindowCache.GetOnlineDevice(device) is not { } live) return Array.Empty<(AndroidOutputSink, string)>();
        device = live;
        string phoneKey = AndroidDeviceIdentity.Key(device);
        IReadOnlyList<AndroidOutputSink> sinks = [AndroidOutputSink.DefaultSpeaker];
        if (LazyPhone.IsValueCreated && Phone.FindProfile(device) is { OutputSinks.Count: > 0 } profile)
            sinks = profile.OutputSinks;
        var result = new List<(AndroidOutputSink, string)>(sinks.Count + 1);
        if (IsPhoneOnline(device))
            result.Add((AndroidOutputSink.Current, AndroidOutputIdentity.FormatDeviceId(phoneKey, AndroidOutputSink.Current.Key)));
        foreach (var sink in sinks)
            result.Add((sink, AndroidOutputIdentity.FormatDeviceId(phoneKey, sink.Key)));
        return result;
    }
    internal static AndroidPhonePcmPlayer CreatePhonePcmPlayer(AndroidPcmSource pcm, string endpoint, CancellationToken token, AdbClient? adb = null)
    {
        if (!AndroidOutputIdentity.TryParseDeviceId(endpoint, out string phoneKey, out string sink))
            throw AndroidError.WithCode(new IOException("无效的设备输出。"), AndroidErrorCode.InvalidPhoneOutputDevice);

        var device = FindOnlinePhone(phoneKey);
        if (device == null)
            throw AndroidError.WithCode(new IOException("设备未连接或离线。"), AndroidErrorCode.PhoneOffline);
        if (!GetDropZonesForDevice(device).Any(output => string.Equals(output.DeviceId, endpoint, StringComparison.OrdinalIgnoreCase)))
            throw AndroidError.WithCode(new IOException("所选设备输出已断开。"), AndroidErrorCode.SelectedPhoneOutputDisconnected);

        token.ThrowIfCancellationRequested();
        return new AndroidPhonePcmPlayer(adb ?? Adb, device, pcm, sink, token,
            (connected, outputs) => { if (PhoneAudioWindowCache.Manager.IsCurrent(connected)) Phone.UpdateOutputs(connected, outputs); },
            PhoneAudioWindowCache.Manager.OperationToken(device));
    }

    private static AndroidDevice? FindOnlinePhone(string phoneKey) => PhoneAudioWindowCache.Manager.ResolveKey(phoneKey);
    internal static string? TryGetPcAppPhoneOutput(int processId) =>
        LazyPcToPhone.IsValueCreated ? PcToPhone.GetPersistedEndpoint(processId) : null;
    internal static (bool Success, int HResult, string Message) RoutePcAppToPhone(int processId, string deviceId)
    {
        if (!AndroidOutputIdentity.TryParseDeviceId(deviceId, out string phoneKey, out _))
            return (false, -1, L10n.T("Phone.Err.InvalidPhoneOutputDevice"));
        (bool Success, int HResult, string Message) res;
        lock (PhoneOperations)
        {
            var connected = FindOnlinePhone(phoneKey);
            if (connected == null) return (false, -1, L10n.T("Phone.Err.PhoneOffline"));
            res = PcToPhone.RouteProcess(processId, deviceId, connected);
        }
        return res.Success ? (true, 0, L10n.T("History.Status.success")) : (false, res.HResult, processId <= 0 ? L10n.T("Phone.Err.InvalidProcessId") : L10n.T("Phone.Err.PhoneAudioOutputStopped"));
    }
    internal static bool StopPcAppToPhone(int processId, bool restorePreviousEndpoint) =>
        LazyPcToPhone.IsValueCreated && PcToPhone.StopProcess(processId, restorePreviousEndpoint);
    internal static int StopAllPcAppsToPhone(bool restorePreviousEndpoint = true) =>
        LazyPcToPhone.IsValueCreated ? PcToPhone.StopAll(restorePreviousEndpoint) : 0;
    internal static async Task ProbeOutputsIfConnectedAsync(IEnumerable<AndroidDevice>? devices, bool force = false, CancellationToken token = default)
    {
        if (devices == null || !LazyAdb.IsValueCreated || !LazyPhone.IsValueCreated) return;
        long now = Environment.TickCount64;
        foreach (var device in devices.Select(PhoneAudioWindowCache.GetOnlineDevice).OfType<AndroidDevice>().DistinctBy(AndroidDeviceIdentity.Key))
        {
            if (device.State != "device" || string.Equals(device.Serial, "offline-fixture", StringComparison.Ordinal)) continue;
            lock (ProbeTimestamps)
            {
                if (!force && ProbeTimestamps.TryGetValue(device.Serial, out long last) && now - last < 3000) continue;
                ProbeTimestamps[device.Serial] = now;
            }
            try
            {
                using var read = CancellationTokenSource.CreateLinkedTokenSource(token, PhoneAudioWindowCache.Manager.OperationToken(device));
                var outputs = await Adb.OutputsAsync(device, read.Token).ConfigureAwait(false);
                if (PhoneAudioWindowCache.Manager.IsCurrent(device)) Phone.UpdateOutputs(device, outputs);
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
        return SilenceLeases.Acquire(processId);
    }
    internal static IPcProcessSilenceLease? AcquireMirrorSilenceLease(int processId, out (string? Endpoint, bool Active) original)
    {
        original = default;
        if (processId <= 0 || TryGetPcAppPhoneOutput(processId) != null) return null;
        if (MutedOutputs.Acquire(processId) is { } muted) return muted;
        original = ReadAppSpeaker(processId);
        return MirrorSilenceLeases.AcquireForPlayback(processId, original.Active);
    }

    internal static void RevalidateLocalSilence()
    {
        MutedOutputs.Revalidate();
        SilenceLeases.Revalidate();
        MirrorSilenceLeases.Revalidate();
        SourceSpeakers.Revalidate();
    }
    internal static void OutputUnmuted(string id, Guid context)
    {
        MutedOutputs.NoteUnmuted();
        if (context == OutputMuteContext) return;
        SilenceLeases.NoteUnmuted(id);
        MirrorSilenceLeases.NoteUnmuted(id);
        SourceSpeakers.NoteUnmuted(id);
    }

    private static bool AllWindowsOutputsUserMuted()
    {
        try
        {
            using var devices = new WasapiDeviceLease.Enumerator();
            bool any = false, silent = true;
            foreach (var lease in devices.ActiveRender())
            using (lease)
            {
                var device = lease.Device;
                if (silent) silent = device.AudioEndpointVolume.Mute && !SilenceLeases.OwnsEndpoint(device.ID)
                    && !MirrorSilenceLeases.OwnsEndpoint(device.ID) && !SourceSpeakers.OwnsEndpoint(device.ID);
                any = true;
            }
            return any && silent;
        }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }

    private static (bool Muted, bool Known) ReadOutputMute(string id)
    {
        try
        {
            using var enumerator = new WasapiDeviceLease.Enumerator();
            using var lease = enumerator.GetDevice(id);
            var device = lease.Device;
            return (device.AudioEndpointVolume.Mute, true);
        }
        catch (System.Runtime.InteropServices.COMException) { return (false, false); }
    }

    private static bool SetReservedOutputMute(string id, bool muted) => SetOutputMute(id, muted, allowDefault: false);

    private static bool SetOutputMute(string id, bool muted, bool allowDefault)
    {
        try
        {
            using var enumerator = new WasapiDeviceLease.Enumerator();
            using var lease = enumerator.GetDevice(id);
            var device = lease.Device;
            if (muted && !allowDefault)
                foreach (var role in Enum.GetValues<NAudio.CoreAudioApi.Role>())
                {
                    using var current = enumerator.TryDefault(role);
                    if (string.Equals(current?.Device.ID, id, StringComparison.OrdinalIgnoreCase)) return false;
                }
            var volume = device.AudioEndpointVolume;
            volume.NotificationGuid = OutputMuteContext;
            volume.Mute = muted;
            return true;
        }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }

    // A hardware mute or a bus-backed render device is preferred over another software mixer.
    internal static bool CanSilenceMirrorOutput(NAudio.CoreAudioApi.MMDevice device)
    {
        if (device.AudioEndpointVolume.HardwareSupport.HasFlag(NAudio.CoreAudioApi.EEndpointHardwareSupport.Mute)) return true;
        string? adapter;
        try { adapter = device.DeviceTopology.GetConnector(0).ConnectedToDeviceId; }
        catch (System.Runtime.InteropServices.COMException) { return false; }
        catch (InvalidOperationException) { return false; }
        int start = adapter?.IndexOf(@"\\?\", StringComparison.Ordinal) ?? -1;
        if (start < 0) return false;
        string bus = adapter![(start + 4)..].Split('#', 2)[0];
        return bus.Equals("USB", StringComparison.OrdinalIgnoreCase) || bus.Equals("HDAUDIO", StringComparison.OrdinalIgnoreCase)
            || bus.Equals("INTELAUDIO", StringComparison.OrdinalIgnoreCase) || bus.Equals("PCI", StringComparison.OrdinalIgnoreCase)
            || bus.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) || bus.Equals("ACPI", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> FindAvailableSilenceOutputs(IReadOnlySet<int> owned, string? reserved, Func<string, bool> isOwnedByOtherPool)
    {
        using var devices = new WasapiDeviceLease.Enumerator();
        var defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in Enum.GetValues<NAudio.CoreAudioApi.Role>())
        {
            using var current = devices.TryDefault(role);
            if (current != null) defaults.Add(current.Device.ID);
        }
        var candidates = new List<(string Id, int Rank)>();
        foreach (var lease in devices.ActiveRender())
        using (lease)
        {
            var device = lease.Device;
            string id = device.ID;
            if (defaults.Contains(id) || isOwnedByOtherPool(id)
                || (reserved != null && !string.Equals(reserved, id, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                if (IsMonitorOrInput(device.FriendlyName)) continue;
                bool muted = device.AudioEndpointVolume.Mute;
                bool terminal = CanSilenceMirrorOutput(device);
                var sessions = device.AudioSessionManager.Sessions;
                bool occupied = false, dormant = false;
                for (int i = 0; i < sessions.Count && !occupied; i++)
                {
                    using var session = sessions[i];
                    int pid = (int)session.GetProcessID;
                    var state = session.State;
                    if (state == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired
                        || LocalPlaybackWatch.IsOwnLoopback(session, pid) || owned.Contains(pid)
                        || owned.Any(root => LocalPlaybackWatch.ContainsProcess(root, pid))) continue;
                    if (state == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) occupied = true;
                    else if (pid != 0) dormant = true;
                }
                if (!occupied)
                {
                    if (reserved != null) return [id];
                    // A dormant session can resume. LocalPlaybackWatch subscribes to it before
                    // committing; prefer an empty mixer when the hardware outputs cannot migrate.
                    int rank = (terminal ? 4 : dormant ? 0 : 2) + (muted ? 1 : 0);
                    candidates.Add((id, rank));
                }
            }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        return candidates.OrderByDescending(candidate => candidate.Rank).Select(candidate => candidate.Id).ToArray();
    }

    private static bool IsMonitorOrInput(string? name) => name != null && (
        name.Contains("Microphone", StringComparison.OrdinalIgnoreCase) || name.Contains("麦克风", StringComparison.Ordinal)
        || name.Contains("麥克風", StringComparison.Ordinal) || name.Contains("Sidetone", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Side Tone", StringComparison.OrdinalIgnoreCase) || name.Contains("侧音", StringComparison.Ordinal)
        || name.Contains("側音", StringComparison.Ordinal) || name.EndsWith(" Input", StringComparison.OrdinalIgnoreCase)
        || name.Contains(" Input (", StringComparison.OrdinalIgnoreCase));

    private static bool VerifyWindowsPlaybackRoute(int processId, string targetId, bool wasActive, IReadOnlySet<int> owners)
    {
        using var devices = new WasapiDeviceLease.Enumerator();
        return WaitForPlaybackMigration(wasActive, () => ReadPlaybackState(devices, processId, targetId, owners),
            static () => Thread.Sleep(100));
    }

    // A player briefly closes its old session before reopening it on the requested
    // endpoint (or on the old one). A gap alone does not prove that it migrated.
    internal static bool WaitForPlaybackMigration(bool wasActive, Func<(bool TargetSafe, bool TargetActive, bool OtherActive)> observe, Action wait)
    {
        const int maxSamples = 16, stableSamples = 4;
        int consecutive = 0;
        for (int sample = 0; sample < maxSamples; sample++)
        {
            var state = observe();
            if (!state.TargetSafe) return false;
            wasActive |= state.TargetActive || state.OtherActive;
            if (!state.OtherActive && (!wasActive || state.TargetActive))
            {
                if (++consecutive == stableSamples) return true;
            }
            else consecutive = 0;
            if (sample + 1 < maxSamples) wait();
        }
        return false;
    }

    private static (bool TargetSafe, bool TargetActive, bool OtherActive) ReadPlaybackState(WasapiDeviceLease.Enumerator devices,
        int processId, string? targetId, IReadOnlySet<int>? owners)
    {
        if (targetId != null)
            foreach (var role in Enum.GetValues<NAudio.CoreAudioApi.Role>())
            {
                using var current = devices.TryDefault(role);
                if (string.Equals(current?.Device.ID, targetId, StringComparison.OrdinalIgnoreCase)) return (false, false, false);
            }
        bool targetSafe = false, targetActive = false, otherActive = false;
        foreach (var lease in devices.ActiveRender())
        using (lease)
        {
            var endpoint = lease.Device;
            bool onTarget = string.Equals(endpoint.ID, targetId, StringComparison.OrdinalIgnoreCase);
            if (onTarget) targetSafe = endpoint.AudioEndpointVolume.Mute;
            if (!onTarget && otherActive) continue;
            var sessions = endpoint.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                if (session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                int pid = (int)session.GetProcessID;
                if (LocalPlaybackWatch.IsOwnLoopback(session, pid)) continue;
                if (LocalPlaybackWatch.ContainsProcess(processId, pid))
                {
                    if (onTarget) targetActive = true;
                    else
                    {
                        otherActive = true;
                        if (targetId == null) return (false, false, true);
                        break;
                    }
                }
                else if (onTarget && !owners!.Any(root => LocalPlaybackWatch.ContainsProcess(root, pid)))
                    return (false, targetActive, otherActive);
            }
        }
        return (targetSafe, targetActive, otherActive);
    }

    private static (string? Multimedia, string? Console) ReadSilenceRoles(int pid)
    {
        using var policy = new AudioPolicyConfig(EDataFlow.eRender);
        return (Unpack(policy.GetDefaultEndPoint(pid, ERole.eMultimedia)), Unpack(policy.GetDefaultEndPoint(pid, ERole.eConsole)));

        static string? Unpack(string? value) => string.IsNullOrWhiteSpace(value) ? null : AudioPolicyConfig.UnpackDeviceId(value);
    }

    private static bool WriteSilenceRoles(int pid, (string? Multimedia, string? Console) ids)
    {
        using var policy = new AudioPolicyConfig(EDataFlow.eRender);
        int multimedia = policy.SetDefaultEndPoint(Pack(ids.Multimedia), pid, ERole.eMultimedia);
        int console = policy.SetDefaultEndPoint(Pack(ids.Console), pid, ERole.eConsole);
        return multimedia >= 0 && console >= 0;

        static string? Pack(string? value) => value == null ? null : AudioPolicyConfig.EnsureFullDeviceId(value, EDataFlow.eRender);
    }
    private static readonly Lazy<AndroidAppCatalogCache> LazyCatalog = new(() =>
        new AndroidAppCatalogCache(Path.Combine(Path.GetDirectoryName(ConfigService.ConfigPath)!, "phone-app-catalog.json"),
            (device, token) => Adb.AppsAsync(device, token)));
    private static string PhoneStorePath => Path.Combine(Path.GetDirectoryName(ConfigService.ConfigPath)!, "android-phone.json");
    internal static AndroidAppCatalogCache Catalog => LazyCatalog.Value;
    internal static bool IsCatalogCreated => LazyCatalog.IsValueCreated;
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
    internal static string? TryResolveAndroidName(string package)
    {
        if (string.IsNullOrWhiteSpace(package)) return L10n.T("Phone.AudioWindowTitle");
        if (IsCreated)
        {
            foreach (var source in Sources.Sources)
            {
                if (!string.Equals(source.App.Package, package, StringComparison.OrdinalIgnoreCase)) continue;
                string name = ApplicationAudio.AndroidName(source.App, source.Device);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        if (AreDisplaysCreated && Displays.FindByPackage(package) is { } display)
        {
            string name = ApplicationAudio.AndroidName(display.App, display.Device);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        var cachedDevices = PhoneAudioWindowCache.Devices;
        var store = IsPhoneCreated || File.Exists(PhoneStorePath) ? Phone : null;
        if (string.Equals(package, AndroidApp.DeviceAudioPackage, StringComparison.OrdinalIgnoreCase))
        {
            var live = cachedDevices?.FirstOrDefault(d => d.State == "device") ?? cachedDevices?.FirstOrDefault();
            if (live != null)
            {
                string liveName = store != null ? store.GetDisplayName(live) : live.Name;
                if (!string.IsNullOrWhiteSpace(liveName)) return liveName;
            }
            if (store?.Profiles.FirstOrDefault() is { } profile) return store.GetProfileDisplayName(profile);
            return L10n.T("Phone.AudioWindowTitle");
        }
        if (store != null)
        {
            foreach (var profile in store.Profiles)
            {
                foreach (var app in profile.RetainedApps)
                    if (string.Equals(app.Package, package, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(app.Name))
                        return app.Name;
                foreach (var app in profile.QuickPanelApps)
                    if (string.Equals(app.Package, package, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(app.Name))
                        return app.Name;
            }
        }
        if (IsCatalogCreated || cachedDevices != null || store != null)
        {
            var devices = new List<AndroidDevice>();
            if (cachedDevices != null) devices.AddRange(cachedDevices);
            if (store != null)
                foreach (var profile in store.Profiles)
                    if (profile.LastDevice is { } saved)
                        devices.Add(saved);
            if (IsCatalogCreated || devices.Count > 0)
            {
                string? catalogName = Catalog.TryFindCachedName(package, devices);
                if (!string.IsNullOrWhiteSpace(catalogName)) return catalogName;
            }
        }
        return null;
    }

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
            OnResourcesRetired();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }
    internal static bool HasActiveWork()
    {
        if (IsDragging || LocalAppOutputMirrors.HasActiveRoutes) return true;
        if (IsCreated && Sources.HasActiveWork) return true;
        if (AreDisplaysCreated && (Displays.Count > 0 || HasActiveDisplayResources())) return true;
        if (IsPcToPhoneCreated && PcToPhone.HasActiveWork) return true;
        lock (MediaWatches)
        {
            if (MediaWatches.Count > 0) return true;
        }
        return (System.Windows.Application.Current as App)?.HasActivePhoneClipboard == true;
    }

    // An endpoint-only record may acquire a stable key on manual refresh. Defer
    // that optional identity retry while existing playback is using the old key.
    internal static bool HasActivePlaybackConnections() => LocalAppOutputMirrors.HasActiveRoutes
        || (IsCreated && Sources.HasActiveWork) || (AreDisplaysCreated && Displays.Count > 0)
        || (IsPcToPhoneCreated && PcToPhone.HasActiveWork) || AirPlayHost.Source != null;

    private static void OnResourcesRetired() =>
        (System.Windows.Application.Current as App)?.RequestRetiredPhoneReclaim();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HasActiveDisplayResources() =>
        D3D11VideoSurfaceRenderer.ActiveInstanceCount > 0 || MediaFoundationH264Decoder.ActiveInstanceCount > 0;

    private static bool PhoneUiInUse => (System.Windows.Application.Current is App app && app.IsPhoneWindowOpen)
        || PhoneDevicePage.HasActivePage;

    internal static void TrimForIdle()
    {
        if (!PhoneUiInUse && IsCatalogCreated)
            Catalog.TrimForIdle();
    }

    internal static void ReleaseRetiredConnectionsIfIdle()
    {
        if (PhoneUiInUse || HasActiveWork()) return;
        lock (ProbeTimestamps) ProbeTimestamps.Clear();
        if (IsAdbCreated)
            _ = Adb.ReleaseIdleConnectionsAsync(includeIdleTcp: false).ContinueWith(
                static t => System.Diagnostics.Trace.WriteLine("Idle ADB cleanup: " + t.Exception?.GetBaseException().Message),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal static async Task StopMediaAsync()
    {
        if (LazySources.IsValueCreated) Sources.BeginShutdown();
        MediaShutdown.Cancel();
        var pending = new List<Task>();
        if (LazyPcToPhone.IsValueCreated) pending.Add(PcToPhone.DisposeAsync().AsTask());
        if (SavedSourceStartup.IsValueCreated) pending.Add(SavedSourceStartup.Value);
        lock (MediaWatches) pending.AddRange(MediaWatches);
        // A cancelled view may have left a small configuration read in flight. Finish
        // that existing read before shutdown flushes settings; never start one here.
        if (PhoneRead.IsValueCreated) pending.Add(PhoneRead.Value);
        if (SourcesRead.IsValueCreated) pending.Add(SourcesRead.Value);
        if (SettingsRead.IsValueCreated) pending.Add(SettingsRead.Value);
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Phone media shutdown: " + ex.Message); }
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
    internal static string? WithUsbAccessDetail(string? text, AndroidDevice? selected)
    {
        if (DirectAdbTransport.UsbAccessError(PhoneAudioWindowCache.Discovery.CurrentDevices, selected) is not { } code)
            return text;
        string hint = Format(code);
        return string.IsNullOrEmpty(text) ? hint : text + Environment.NewLine + hint;
    }

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
