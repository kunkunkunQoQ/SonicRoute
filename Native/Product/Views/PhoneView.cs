#if SONICROUTE_PHONE
using System.Collections;
using QRCoder;
using SonicRoute.AndroidAudio;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

internal sealed partial class ProductViews
{
    private bool phonePage, airPlayPage, phoneWired;
    private CancellationTokenSource? phoneRead, qrCancel, mediaWatch;
    private IReadOnlyList<BitArray>? qrMatrix;
    private IReadOnlyList<AndroidApp> phoneApps = [];
    private IReadOnlyList<AndroidOutputSink> phoneOutputs = [];
    private IReadOnlyList<AndroidStorageFile> phoneFiles = [];
    private AndroidMediaSessionClient? mediaClient;
    private AndroidMediaState? mediaState;
    private AndroidDevice? phoneDevice;
    private string? selectedPackage;
    private string phoneSearch = "", phoneFolder = "/sdcard/Download";
    private bool browsePhone, phoneStreaming;
    private IVirtualDisplayClient? lastDisplay;

    private void OpenPhoneView()
    {
        var phone = host.Phone;
        if (!phoneWired)
        {
            phoneWired = true;
            phone.Changed += PhoneChanged;
        }
        phone.StartTracking();
        Run(() => phone.RefreshAsync(restore: true));
    }

    private void PhoneChanged() => host.TryPost(() =>
    {
        if (Owner == 0 || !phonePage || page != 0) return;
        Refresh();
    });

    private void ReleasePhoneView()
    {
        if (phoneWired) { host.Phone.Changed -= PhoneChanged; phoneWired = false; }
        qrCancel?.Cancel();
        Cancel(ref phoneRead);
        Cancel(ref mediaWatch);
        qrMatrix = null;
        mediaState = null;
        mediaClient = null;
        phoneDevice = null;
        phoneApps = [];
        phoneOutputs = [];
        phoneFiles = [];
    }

    private static void Cancel(ref CancellationTokenSource? token)
    {
        var pending = token;
        token = null;
        pending?.Cancel();
    }

    private void BuildPhone()
    {
        var phone = host.Phone;
        if (qrMatrix != null)
        {
            Add(T("Phone.QrRefreshHint"), "", () => qrCancel?.Cancel());
            return;
        }

        Header(Label("安卓设备", "Android devices"), phone.Error ?? "");
        Add(Label("刷新 USB / 无线设备", "Refresh USB and wireless devices"), click: () => Run(() => phone.RefreshAsync(restore: true)));
        Add(Label("无线发现", "Wireless discovery"), click: DiscoverPhones);
        Add(Label("直接连接", "Connect by address"), click: () =>
        {
            if (NativeDialogs.Input(Owner, Label("无线调试 IP:端口", "Wireless debugging IP:port"), "") is { Length: > 0 } address)
                Run(() => WithPhoneTimeout(token => phone.DiscoverAndConnectAsync(address, token)));
        });
        Add(Label("使用配对码", "Pair with a code"), click: () =>
        {
            if (NativeDialogs.Input(Owner, Label("配对 IP:端口", "Pairing IP:port"), "") is not { Length: > 0 } address) return;
            if (NativeDialogs.Input(Owner, Label("六位配对码", "Six-digit pairing code"), "") is { Length: > 0 } code)
                Run(() => WithPhoneTimeout(token => phone.PairAsync(address, code, token)));
        });
        Add(Label("二维码配对", "Pair via QR code"), click: PairQr);

        foreach (var profile in phone.Store.Profiles)
        {
            if (profile.LastDevice is not { } remembered) continue;
            var connection = phone.Connections.Get(remembered);
            bool selected = profile.Id == phone.Store.Profile.Id;
            Add(phone.Store.GetProfileDisplayName(profile), $"{remembered.Serial} · {connection.State}", () =>
            {
                phone.Store.SelectProfile(profile.Id);
                SelectPhone(connection.Device);
                Refresh();
            }, selected: selected);
        }

        var profileNow = phone.Store.Profile;
        if (profileNow.LastDevice is not { } last) return;
        var current = phone.Connections.Get(last).Device;
        Add(Label("重命名设备", "Rename device"), phone.Store.GetProfileDisplayName(profileNow), () =>
        {
            if (NativeDialogs.Input(Owner, Label("设备名称", "Device name"), phone.Store.GetProfileDisplayName(profileNow)) is { } name)
            { phone.Store.Rename(name); Refresh(); }
        });
        if (current == null)
        {
            Add(Label("重连设备", "Reconnect"), last.Serial, () => Run(() => phone.ReconnectAsync(last)));
            Add(Label("移除设备", "Remove device"), click: () =>
            {
                if (NativeDialogs.Confirm(Owner, T("Phone.DefaultName"), Label("移除此测试配置中的设备？", "Remove this device from the preview profile?")))
                { phone.Connections.Remove(profileNow.Id, out _); Refresh(); }
            });
            if (phoneDevice != null) SelectPhone(null);
            return;
        }

        if (!Equals(phoneDevice, current)) SelectPhone(current);
        Add(Label("断开连接", "Disconnect"), current.Serial, () =>
        {
            phone.Clipboard.Disable();
            Run(() => phone.DisconnectAsync(current, false));
        });
        if (current.IsWifi) Add(Label("取消配对", "Unpair local identity"), click: () =>
        {
            if (NativeDialogs.Confirm(Owner, T("Phone.DefaultName"), Label("断开并撤销本测试版的配对？", "Disconnect and revoke this preview's pairing?")))
            {
                phone.Clipboard.Disable();
                Run(() => phone.DisconnectAsync(current, true));
            }
        });
        Add(Label("双向剪贴板", "Two-way clipboard"), phone.Clipboard.Status, () =>
        {
            if (phone.Clipboard.Enabled) phone.Clipboard.Disable();
            else if (NativeDialogs.Confirm(Owner, T("Phone.DefaultName"), Label("在这台设备和 Windows 之间同步文本？", "Sync clipboard text between this device and Windows?")))
                phone.Clipboard.Enable(current);
            Refresh();
        }, selected: phone.Clipboard.Enabled);

        BuildMedia();
        Add(Label("文件", "Files"), phoneFolder, () =>
        {
            browsePhone = !browsePhone;
            if (browsePhone) ReadPhoneFiles(current, phoneFolder);
            Refresh();
        }, selected: browsePhone);
        if (browsePhone) BuildPhoneFiles(current, profileNow);
        Add(Label("编解码和延迟", "Codecs and latency"), click: () => { phoneStreaming = !phoneStreaming; Refresh(); }, selected: phoneStreaming);
        if (phoneStreaming) BuildPhoneStreaming(current);
        BuildPhoneApps(current, profileNow);
    }

    private async Task WithPhoneTimeout(Func<CancellationToken, Task> operation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(host.Phone.Lifetime);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        await operation(deadline.Token).ConfigureAwait(false);
    }

    private void DiscoverPhones()
    {
        var phone = host.Phone;
        Run(async () =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(phone.Lifetime);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var services = AndroidWirelessConnection.ParseServices(await phone.Adb.RunAsync(timeout.Token, "mdns", "services").ConfigureAwait(false));
            var available = services.Where(s => s.Type.StartsWith(AndroidWirelessConnection.ConnectType, StringComparison.OrdinalIgnoreCase)).ToArray();
            host.TryPost(() =>
            {
                if (Owner == 0 || !phonePage) return;
                if (available.Length == 0) { Notice(Label("未发现可连接的无线设备", "No wireless debugging device found")); return; }
                Menu(available.Select<AndroidMdnsService, (string, Action?)>(s =>
                    ($"{s.Name} · {s.Address}", () => Run(() => WithPhoneTimeout(token => phone.DiscoverAndConnectAsync(s.Address, token))))).ToArray());
            });
        });
    }

    private void PairQr()
    {
        if (qrCancel != null) return;
        var phone = host.Phone;
        var pair = AndroidQrPairing.Create();
        using var data = QRCodeGenerator.GenerateQrCode(pair.Payload, QRCodeGenerator.ECCLevel.M);
        qrMatrix = data.ModuleMatrix.Select(row => new BitArray(row)).ToArray();
        scroll = 0;
        var request = CancellationTokenSource.CreateLinkedTokenSource(phone.Lifetime);
        qrCancel = request;
        Refresh();
        _ = Task.Run(async () =>
        {
            string? error = null;
            try { await phone.PairAsync(pair, request.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception failure) { error = failure.Message; }
            host.TryPost(() =>
            {
                if (ReferenceEquals(qrCancel, request))
                {
                    qrCancel = null;
                    qrMatrix = null;
                    if (error != null) Notice(error);
                    Refresh();
                }
                request.Dispose();
            });
        });
    }

    private void PaintPhoneQr(ViewCanvas canvas, float x, float y, float width)
    {
        if (qrMatrix is not { Count: > 0 } code) return;
        float cell = Math.Max(1, MathF.Floor(Math.Min(width, 196) / code.Count));
        float size = code.Count * cell;
        canvas.Fill(new(x, y, size, size), 0xffffff);
        for (int row = 0; row < code.Count; row++)
            for (int column = 0; column < code.Count; column++)
                if (code[row][column]) canvas.Fill(new(x + column * cell, y + row * cell, cell, cell), 0x000000);
    }

    private void SelectPhone(AndroidDevice? device)
    {
        Cancel(ref phoneRead);
        Cancel(ref mediaWatch);
        (phoneDevice, mediaState, mediaClient) = (device, null, null);
        phoneApps = [];
        phoneOutputs = [];
        phoneFiles = [];
        selectedPackage = null;
        lastDisplay = null;
        if (device == null) return;
        ReadPhoneApps(device);
        var phone = host.Phone;
        var watcher = CancellationTokenSource.CreateLinkedTokenSource(phone.Lifetime);
        mediaWatch = watcher;
        var client = new AndroidMediaSessionClient(phone.Adb);
        mediaClient = client;
        _ = Task.Run(async () =>
        {
            try
            {
                await client.WatchAsync(device, state => host.TryPost(() =>
                {
                    if (!ReferenceEquals(mediaWatch, watcher)) return;
                    mediaState = state;
                    Refresh();
                }), watcher.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (watcher.IsCancellationRequested) { }
            catch (Exception error) { host.TryPost(() => { if (ReferenceEquals(mediaWatch, watcher)) Notice(error.Message); }); }
            finally { host.TryPost(() => { if (ReferenceEquals(mediaWatch, watcher)) mediaWatch = null; watcher.Dispose(); }); }
        });
    }

    private void ReadPhoneApps(AndroidDevice device)
    {
        Cancel(ref phoneRead);
        var phone = host.Phone;
        var request = CancellationTokenSource.CreateLinkedTokenSource(phone.Lifetime);
        request.CancelAfter(TimeSpan.FromSeconds(18));
        phoneRead = request;
        _ = Task.Run(async () =>
        {
            try
            {
                var catalog = await phone.Adb.AppsAsync(device, request.Token).ConfigureAwait(false);
                var sinks = await phone.Adb.OutputsAsync(device, request.Token).ConfigureAwait(false);
                host.TryPost(() =>
                {
                    if (!ReferenceEquals(phoneRead, request)) return;
                    phoneApps = catalog;
                    phoneOutputs = sinks;
                    Refresh();
                });
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception error) { host.TryPost(() => { if (ReferenceEquals(phoneRead, request)) Notice(error.Message); }); }
            finally { host.TryPost(() => { if (ReferenceEquals(phoneRead, request)) phoneRead = null; request.Dispose(); }); }
        });
    }

    private void BuildMedia()
    {
        if (mediaState is not { HasSession: true } media || mediaClient is not { } client) return;
        Header(media.Title, media.Artist);
        if (media.CanPrevious) Add(Label("上一首", "Previous"), click: () => Run(() => WithPhoneTimeout(token => client.SendAsync("previous", token))));
        if (media.CanToggle) Add(media.Playing ? Label("暂停", "Pause") : Label("播放", "Play"),
            click: () => Run(() => WithPhoneTimeout(token => client.SendAsync("toggle", token))));
        if (media.CanNext) Add(Label("下一首", "Next"), click: () => Run(() => WithPhoneTimeout(token => client.SendAsync("next", token))));
        if (media.CanSeek)
            Add(Label("播放进度", "Playback position"), slider: value => Run(() => WithPhoneTimeout(token =>
                client.SeekAsync(media.Package, media.DurationMs * value / 100, token))), value: (int)(100 * media.CurrentPositionMs / media.DurationMs));
    }

    private void BuildPhoneApps(AndroidDevice device, AndroidPhoneProfile profile)
    {
        var phone = host.Phone;
        Header(Label("手机应用", "Phone apps"));
        Add(Label("搜索应用", "Search apps"), phoneSearch, () =>
        {
            if (NativeDialogs.Input(Owner, Label("搜索应用", "Search apps"), phoneSearch) is { } filter)
            { phoneSearch = filter; Refresh(); }
        });
        Add(Label("刷新应用目录", "Refresh catalog"), click: () => ReadPhoneApps(device));
        var entries = profile.RetainedApps.Concat(phoneApps).DistinctBy(app => app.Package)
            .Where(app => app.Name.Contains(phoneSearch, StringComparison.OrdinalIgnoreCase) || app.Package.Contains(phoneSearch, StringComparison.OrdinalIgnoreCase));
        foreach (var app in entries)
        {
            var source = phone.Sources.Sources.FirstOrDefault(s => s.App.Package == app.Package && AndroidDeviceIdentity.Matches(device, s.Device));
            bool selected = selectedPackage == app.Package;
            Add(app.Name, source?.Session.State.State.ToString() ?? app.Package, () =>
            { selectedPackage = selected ? null : app.Package; Refresh(); }, selected: selected);
            if (app.IconPng is { Length: > 0 } icon) RowIcon(icon, () => icon);
            if (!selected) continue;
            bool pinned = profile.RetainedApps.Any(a => a.Package == app.Package);
            Add(pinned ? Label("取消固定", "Unpin") : Label("固定应用", "Pin"), click: () =>
            {
                if (pinned) phone.Store.Remove(app.Package); else phone.Store.Retain(app);
                Refresh();
            }, selected: pinned);
            bool smart = phone.Store.IsSmartVolumeEnabled(device, app.Package);
            Add(Label("智能音量", "Smart volume"), click: () =>
            {
                phone.Store.SetSmartVolume(app.Package, !smart);
                phone.Sources.SyncSmartVolume((d, pkg) => phone.Store.IsSmartVolumeEnabled(d, pkg));
                Refresh();
            }, selected: smart);
            if (source == null)
            {
                Add(Label("手机与电脑同时播放", "Play on phone and PC"), click: () => Run(() => phone.Sources.AddAsync(device, app)));
                Add(Label("捕获转发到电脑", "Capture to PC"), click: () => Run(() => phone.Sources.AddAsync(device, app, keepPhoneAudio: false)));
            }
            else
            {
                var audio = source.Session.State;
                Add(Label("音量 / 静音", "Volume / mute"), audio.Error ?? "", () => { source.Session.SetMute(!audio.Muted); Refresh(); },
                    value => { source.Session.SetVolume(value); Refresh(); }, audio.Volume, audio.Muted);
                Add(Label("停止转发", "Stop forwarding"), click: () => Run(() => phone.Sources.RemoveAsync(source.Id)));
                if (source.Session is IMultiOutputAudioSession route)
                {
                    var picked = route.GetOutputs();
                    Add(T("Dev.SystemDefaultOut"), click: () => SelectPhoneAudioOutput(route, null), selected: picked.Contains(null));
                    foreach (var output in outputs.Where(o => !config.HiddenOutputDevices.Contains(o.Id)))
                        Add(DeviceLabel(output.Id, Flow.Render), click: () => SelectPhoneAudioOutput(route, output.Id), selected: picked.Contains(output.Id));
                    foreach (var sink in phoneOutputs)
                    {
                        string id = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.StableKey(device) ?? device.Serial, sink.Key);
                        Add(sink.Name, click: () => SelectPhoneAudioOutput(route, id), selected: picked.Contains(id));
                    }
                }
            }
            Add(Label("打开单应用虚拟屏", "Open virtual display"), click: () =>
            {
                ViewNative.GetCursorPos(out var position);
                Run(async () =>
                {
                    var display = await phone.OpenDisplayAsync(device, app, position.X, position.Y).ConfigureAwait(false);
                    host.TryPost(() => { lastDisplay = display; Refresh(); });
                });
            });
            if (lastDisplay is { } display && display.WindowHandle != 0)
            {
                Add(Label("恢复窗口", "Restore video window"), display.State.ToString(), display.Restore);
                if (display is InProcessVirtualDisplayClient client)
                {
                    Add(Label("置顶", "Always on top"), click: () => client.SetPreviewTopmost(true));
                    Add(Label("取消置顶", "Unpin window"), click: () => client.SetPreviewTopmost(false));
                    Add(Label("全屏", "Fullscreen"), click: () => client.SetPreviewFullscreen(true));
                    Add(Label("退出全屏", "Leave fullscreen"), click: () => client.SetPreviewFullscreen(false));
                }
            }
            Add(Label("关闭此应用的虚拟屏", "Close this virtual display"), click: () => Run(() => phone.Displays.CloseApplicationAsync(device, app.Package)));
        }
    }

    private void SelectPhoneAudioOutput(IMultiOutputAudioSession route, string? id)
    {
        if ((modifiers & 8) == 0) { Run(() => route.SetOutputsAsync([id])); return; }
        var selection = new HashSet<string?>(route.GetOutputs(), StringComparer.OrdinalIgnoreCase);
        if (!selection.Add(id)) selection.Remove(id);
        Run(() => route.SetOutputsAsync(selection.Count == 0 ? [null] : selection.ToArray()));
    }

    private void AppendPcToPhone(int pid)
    {
        if (!host.HasPhone) return;
        var phone = host.Phone;
        var active = phone.Pc.GetActiveRoutes().FirstOrDefault(route => route.ProcessId == pid);
        if (active.ProcessId != 0)
            Add(Label("停止发往手机", "Stop PC-to-phone audio"), active.TargetDeviceId, () => Run(() => phone.Pc.StopProcessAsync(pid)));
        foreach (var device in phone.Connections.OnlineDevices)
        {
            var profile = phone.Store.FindProfile(device);
            if (profile == null) continue;
            Header(phone.Store.GetProfileDisplayName(profile));
            foreach (var sink in profile.OutputSinks)
            {
                string id = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.StableKey(device) ?? device.Serial, sink.Key);
                Add(sink.Name, click: () => Run(() => Task.Run(() =>
                {
                    var routed = phone.Pc.RouteProcess(pid, id, device);
                    if (!routed.Success) throw new InvalidOperationException(routed.Message);
                })), selected: active.TargetDeviceId == id);
            }
        }
    }

    private void BuildPhoneFiles(AndroidDevice device, AndroidPhoneProfile profile)
    {
        var transfer = host.Phone.Transfers.Get(profile.Id);
        if (transfer != null) Add($"{transfer.Phase} · {transfer.Index}/{transfer.Count}", transfer.Text,
            transfer.Phase == AndroidFilePhase.Running ? () => host.Phone.Transfers.Cancel(profile.Id) : null);
        Add(Label("发送文件", "Send file"), phoneFolder, () =>
        {
            if (NativeDialogs.Open(Owner, "Files|*.*") is { } path) TrySendPhoneFiles([path]);
        });
        Add(Label("更改手机目录", "Change phone folder"), phoneFolder, () =>
        {
            if (NativeDialogs.Input(Owner, Label("手机目录", "Phone folder"), phoneFolder) is { } path) ReadPhoneFiles(device, path);
        });
        Add(Label("接收目录", "Receive folder"), profile.ReceiveFolder, () =>
        {
            if (NativeDialogs.Input(Owner, Label("接收目录绝对路径", "Absolute receive folder"), profile.ReceiveFolder) is { } path
                && Path.IsPathFullyQualified(path))
            { host.Phone.Store.SetReceiveFolder(profile.Id, path); Refresh(); }
        });
        foreach (var file in phoneFiles)
        {
            Add((file.Directory ? "▸ " : "") + file.Name, click: () =>
            {
                if (file.Directory) { ReadPhoneFiles(device, phoneFolder.TrimEnd('/') + "/" + file.Name); return; }
                if (NativeDialogs.Confirm(Owner, Label("接收文件", "Receive file"), file.Name)
                    && !host.Phone.StartTransfer(device, [file.Name], profile.ReceiveFolder, phoneFolder))
                    Notice(T("Tray.AudioActionFailed"));
            });
        }
    }

    private bool TrySendPhoneFiles(IReadOnlyList<string> paths)
    {
        if (phoneDevice is not { } device) return false;
        if (!NativeDialogs.Confirm(Owner, Label("发送文件", "Send files"), $"{paths.Count} → {phoneFolder}")) return true;
        if (!host.Phone.StartTransfer(device, paths, phoneFolder, null)) Notice(T("Tray.AudioActionFailed"));
        return true;
    }

    private void ReadPhoneFiles(AndroidDevice device, string folder)
    {
        var phone = host.Phone;
        Run(() => WithPhoneTimeout(async token =>
        {
            var listing = await new AndroidFileTransfer(phone.Adb, device.CommandTarget).ListAsync(folder, token).ConfigureAwait(false);
            host.TryPost(() => { if (Equals(phoneDevice, device)) { phoneFolder = folder; phoneFiles = listing; Refresh(); } });
        }));
    }

    private void BuildPhoneStreaming(AndroidDevice device)
    {
        var preferences = host.Phone.Settings;
        var streaming = preferences.GetStreaming(device);
        void Save(AndroidStreamSettings value) { preferences.RememberStreaming(device, value.Normalize()); Refresh(); }
        void Number(string title, int current, int minimum, int maximum, Func<int, AndroidStreamSettings> change) =>
            Add(title, current.ToString(), () =>
            {
                if (int.TryParse(NativeDialogs.Input(Owner, title, current.ToString()), out int number) && number >= minimum && number <= maximum) Save(change(number));
            });
        void Choice(string name, string? current, IReadOnlyList<string> choices, Func<string, AndroidStreamSettings> change) =>
            Add(name, current ?? "", () => Menu(choices.Select<string, (string, Action?)>(item => (item, () => Save(change(item)))).ToArray()));
        void Encoder(string title, string? current, Func<string?, AndroidStreamSettings> change) =>
            Add(title, current ?? "Auto", () =>
            {
                if (NativeDialogs.Input(Owner, title, current ?? "") is { } name && (name.Length == 0 || AndroidStreamSettings.IsEncoderName(name)))
                    Save(change(name.Length == 0 ? null : name));
            });
        Number("Audio bit rate", streaming.AudioBitRate, 16000, 512000, value => streaming with { AudioBitRate = value });
        Number("Video bit rate", streaming.VideoBitRate, 100000, 50000000, value => streaming with { VideoBitRate = value });
        Number("Max FPS", streaming.MaxFps, 1, 120, value => streaming with { MaxFps = value });
        Choice("Audio decoder", streaming.AudioDecoder, [AndroidStreamSettings.ConcentusDecoder, AndroidStreamSettings.RawPcmDecoder],
            value => streaming with { AudioDecoder = value });
        Choice("Video decoder", streaming.VideoDecoder, [AndroidStreamSettings.WindowsVideoDecoder, AndroidStreamSettings.SoftwareVideoDecoder, AndroidStreamSettings.WindowsHevcDecoder],
            value => streaming with { VideoDecoder = value });
        Choice("Latency", streaming.AudioLatencyMode, [AndroidStreamSettings.LatencyModeUltraLow, AndroidStreamSettings.LatencyModeBalanced, AndroidStreamSettings.LatencyModeStable],
            value => streaming with { AudioLatencyMode = value });
        Encoder("Audio encoder", streaming.AudioEncoder, value => streaming with { AudioEncoder = value });
        Encoder("H.264 encoder", streaming.VideoEncoder, value => streaming with { VideoEncoder = value });
        Encoder("HEVC encoder", streaming.HevcEncoder, value => streaming with { HevcEncoder = value });
    }
}
#endif
