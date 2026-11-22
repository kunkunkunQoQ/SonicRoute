using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace SonicRoute.AndroidAudio;

/// <summary>
/// Pure C# in-process ADB transport supporting WinUSB direct devices and TLS 1.3 / SPAKE2 wireless debugging
/// without spawning adb.exe or loading external Win32 ADB DLLs.
/// </summary>
internal sealed class DirectAdbTransport : IAdbTransport, IAsyncDisposable, IAdbPairingIdentityProvider
{
    private readonly AdbKeyStore _keyStore;
    private readonly Func<IReadOnlyList<WinUsbDeviceDescriptor>> _enumerateUsb;
    private readonly Func<string, IAdbPacketLink?> _openUsbLink;
    private readonly Func<Action, IDisposable?> _watchUsb;
    private readonly Func<string, int, string, CancellationToken, Task<string>> _pairImpl;
    private readonly Func<TimeSpan, CancellationToken, Task<IReadOnlyList<AndroidMdnsService>>> _mdnsImpl;
    private readonly Func<string, int, CancellationToken, Task<IAdbPacketLink>> _openTcpLink;

    private readonly SemaphoreSlim _usbSyncGate = new(1, 1);
    private readonly ConcurrentDictionary<string, UsbDeviceSlot> _usbDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TcpDeviceSlot> _tcpDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ForwardSession> _forwards = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Action> _deviceWatchSubscribers = new();
    private static long _nextTransportId;
    private long _nextSubscriberId;
    private int _disposed;

    private sealed class UsbDeviceSlot(string devicePath, string fallbackSerial, string transportId)
    {
        public string DevicePath { get; } = devicePath;
        public string FallbackSerial { get; } = fallbackSerial;
        public string TransportId { get; } = transportId;
        public CancellationTokenSource Lifetime { get; } = new();
        public AdbMuxConnection? Connection { get; set; }
        public Task? PendingHandshake { get; set; }
        public string? NegotiatedSerial { get; set; }
        public Exception? AuthFailure { get; set; }
        public string EffectiveSerial => string.IsNullOrWhiteSpace(NegotiatedSerial) ? FallbackSerial : NegotiatedSerial;
    }

    private sealed class TcpDeviceSlot(string address, string transportId, AdbMuxConnection connection)
    {
        public string Address { get; } = address;
        public string TransportId { get; } = transportId;
        public AdbMuxConnection Connection { get; } = connection;
    }

    private sealed class ForwardSession : IAsyncDisposable
    {
        public required string Serial { get; init; }
        public required string LocalSpec { get; init; }
        public required string RemoteSpec { get; init; }
        public required TcpListener Listener { get; init; }
        public required CancellationTokenSource Lifetime { get; init; }
        public required Task AcceptTask { get; init; }

        public async ValueTask DisposeAsync()
        {
            Lifetime.Cancel();
            try { Listener.Stop(); } catch { }
            try { await AcceptTask.ConfigureAwait(false); } catch { }
            Lifetime.Dispose();
        }
    }

    public DirectAdbTransport() : this(new AdbKeyStore(), ownsKeyStore: true) { }

    internal DirectAdbTransport(
        AdbKeyStore keyStore,
        bool ownsKeyStore = false,
        Func<IReadOnlyList<WinUsbDeviceDescriptor>>? enumerateUsb = null,
        Func<string, IAdbPacketLink?>? openUsbLink = null,
        Func<Action, IDisposable?>? watchUsb = null,
        Func<string, int, string, CancellationToken, Task<string>>? pairImpl = null,
        Func<TimeSpan, CancellationToken, Task<IReadOnlyList<AndroidMdnsService>>>? mdnsImpl = null,
        Func<string, int, CancellationToken, Task<IAdbPacketLink>>? openTcpLink = null)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _ownsKeyStore = ownsKeyStore;
        _enumerateUsb = enumerateUsb ?? WinUsbAdbLink.EnumeratePresentDevices;
        _openUsbLink = openUsbLink ?? (path => WinUsbAdbLink.TryOpen(path));
        _watchUsb = watchUsb ?? (onChanged => new WinUsbDeviceWatcher(onChanged));
        _pairImpl = pairImpl ?? ((host, port, password, token) => SspiPairingTls.PairAsync(host, port, password, _keyStore, token));
        _mdnsImpl = mdnsImpl ?? ((duration, token) => AndroidMdnsDiscovery.DiscoverAllAsync(duration, token));
        _openTcpLink = openTcpLink ?? OpenDefaultTcpLinkAsync;
    }

    private readonly bool _ownsKeyStore;

    public event Action<string>? UsbAccessDenied;
    internal event Action? WifiChanged;
    internal event Action<string>? WifiTransportLost;

    private const string UsbInUse = "usb_in_use";
    private const string UsbUnavailable = "usb_unavailable";

    internal static AndroidErrorCode? UsbAccessError(IReadOnlyList<AndroidDevice>? devices, AndroidDevice? selected)
    {
        if (devices == null) return null;
        // The PnP instance serial may differ in case from the serial reported by Android.
        // Use this comparison only for presenting the diagnostic, never for admitting a device.
        foreach (var device in devices)
        {
            if (device.IsWifi || (device.State != UsbInUse && device.State != UsbUnavailable)) continue;
            if (selected != null && !string.Equals(AndroidDeviceIdentity.StableKey(device),
                AndroidDeviceIdentity.StableKey(selected), StringComparison.OrdinalIgnoreCase)) continue;
            return device.State == UsbInUse ? AndroidErrorCode.UsbInterfaceInUse : AndroidErrorCode.UsbInterfaceUnavailable;
        }
        return null;
    }

    public Task<string> VersionAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult("Android Debug Bridge (SonicRoute C# ADB protocol v41)");
    }

    public Task<IReadOnlyList<AndroidDevice>> DevicesAsync(CancellationToken token)
        => DevicesAsync(token, retryFailedHandshakes: true);

    public async Task TrackDevicesAsync(Action<IReadOnlyList<AndroidDevice>> onChanged, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

        long subId = Interlocked.Increment(ref _nextSubscriberId);
        _deviceWatchSubscribers[subId] = () => signal.Writer.TryWrite(true);
        IDisposable? usbWatcher = null;
        try
        {
            try { usbWatcher = _watchUsb(() => signal.Writer.TryWrite(true)); }
            catch { /* Headless/virtual environments can still wake via internal connect/disconnect events. */ }

            IReadOnlyList<AndroidDevice>? previous = null;
            IReadOnlyList<AndroidDevice> initial = await DevicesAsync(token, retryFailedHandshakes: false).ConfigureAwait(false);
            onChanged(initial);
            previous = initial;

            while (await signal.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (signal.Reader.TryRead(out _)) { }
                // Coalesce bursty Win32 interface notifications cleanly without an idle polling loop.
                await Task.Delay(50, token).ConfigureAwait(false);
                while (signal.Reader.TryRead(out _)) { }

                IReadOnlyList<AndroidDevice> current = await DevicesAsync(token, retryFailedHandshakes: false).ConfigureAwait(false);
                if (previous == null || !DeviceListsEqual(previous, current))
                {
                    onChanged(current);
                    previous = current;
                }
            }
        }
        finally
        {
            _deviceWatchSubscribers.TryRemove(subId, out _);
            usbWatcher?.Dispose();
        }
    }

    public async Task<IReadOnlyList<string>> FeaturesAsync(string serial, CancellationToken token)
    {
        var conn = await ResolveConnectionAsync(serial, token).ConfigureAwait(false);
        return conn.Features;
    }

    public async Task<string> GetSerialNoAsync(string serial, CancellationToken token)
    {
        var conn = await ResolveConnectionAsync(serial, token).ConfigureAwait(false);
        if (conn.DeviceProperties.TryGetValue("ro.serialno", out string? propSerial) && !string.IsNullOrWhiteSpace(propSerial))
            return propSerial;
        return serial;
    }

    public async Task<AdbShellResult> RunShellAsync(string serial, string command, CancellationToken token)
    {
        var conn = await ResolveConnectionAsync(serial, token).ConfigureAwait(false);
        return await conn.RunShellAsync(command, token).ConfigureAwait(false);
    }

    public async Task<IAdbShellSession> OpenShellAsync(string serial, string command, bool redirectInput, CancellationToken token)
    {
        var conn = await ResolveConnectionAsync(serial, token).ConfigureAwait(false);
        return await conn.OpenShellAsync(command, redirectInput, token).ConfigureAwait(false);
    }

    public async Task<Stream> OpenFileSyncAsync(string serial, CancellationToken token)
    {
        var connection = await ResolveConnectionAsync(serial, token).ConfigureAwait(false);
        return await connection.OpenStreamAsync("sync:", token).ConfigureAwait(false);
    }

    public async Task PushAsync(string serial, string localPath, string remotePath, CancellationToken token)
    {
        var conn = await ResolveConnectionAsync(serial, token).ConfigureAwait(false);
        await conn.PushAsync(localPath, remotePath, token).ConfigureAwait(false);
    }

    public async Task<Stream> OpenLocalAbstractAsync(string serial, string name, CancellationToken token)
    {
        var conn = await ResolveConnectionAsync(serial, token).ConfigureAwait(false);
        return await conn.OpenLocalAbstractAsync(name, token).ConfigureAwait(false);
    }

    public async Task<int> ForwardTcpToLocalAbstractAsync(string serial, string localAbstractName, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        ArgumentException.ThrowIfNullOrEmpty(localAbstractName);
        var conn = await ResolveConnectionAsync(serial, token).ConfigureAwait(false);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string localSpec = "tcp:" + port.ToString(CultureInfo.InvariantCulture);
        string remoteSpec = "localabstract:" + localAbstractName;
        var cts = new CancellationTokenSource();
        Task acceptTask = Task.Run(() => RunForwardLoopAsync(listener, conn, localAbstractName, cts.Token));
        var session = new ForwardSession
        {
            Serial = serial,
            LocalSpec = localSpec,
            RemoteSpec = remoteSpec,
            Listener = listener,
            Lifetime = cts,
            AcceptTask = acceptTask
        };
        if (!_forwards.TryAdd(localSpec, session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new IOException("Duplicate local forward port allocation.");
        }
        return port;
    }

    public async Task RemoveForwardAsync(string serial, string localSpec, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(serial);
        ArgumentException.ThrowIfNullOrEmpty(localSpec);
        if (_forwards.TryGetValue(localSpec, out var session) && session.Serial == serial
            && _forwards.TryRemove(new KeyValuePair<string, ForwardSession>(localSpec, session)))
            await session.DisposeAsync().ConfigureAwait(false);
    }

    public Task<IReadOnlyList<AdbForwardRule>> ListForwardsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        IReadOnlyList<AdbForwardRule> rules = _forwards.Values
            .Select(f => new AdbForwardRule(f.Serial, f.LocalSpec, f.RemoteSpec))
            .OrderBy(r => r.Local, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(rules);
    }

    public Task<string> PairAsync(string address, string password, CancellationToken token)
    {
        string normalized = AndroidWirelessConnection.ValidateAddress(address);
        var (host, port) = SplitHostPort(normalized);
        return _pairImpl(host, port, password, token);
    }

    Task<string?> IAdbPairingIdentityProvider.GetPairingFingerprintAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return Task.FromResult(_keyStore.LoadedFingerprint);
    }

    public async Task<string> ConnectAsync(string address, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        string normalized = AndroidWirelessConnection.ValidateAddress(address);
        if (_tcpDevices.TryGetValue(normalized, out var existing))
        {
            if (existing.Connection.IsConnected)
                return "already connected to " + normalized;
            if (_tcpDevices.TryRemove(new KeyValuePair<string, TcpDeviceSlot>(normalized, existing)))
                await existing.Connection.DisposeAsync().ConfigureAwait(false);
        }

        var (host, port) = SplitHostPort(normalized);
        IAdbPacketLink? link = null;
        AdbMuxConnection? conn = null;
        try
        {
            link = await _openTcpLink(host, port, token).ConfigureAwait(false);
            conn = await AdbMuxConnection.ConnectAsync(link, _keyStore, targetHost: host, token: token).ConfigureAwait(false);
            link = null;
            string tid = "wifi-" + Interlocked.Increment(ref _nextTransportId).ToString(CultureInfo.InvariantCulture);
            var slot = new TcpDeviceSlot(normalized, tid, conn);
            conn.Disconnected += () =>
            {
                if (_tcpDevices.TryRemove(new KeyValuePair<string, TcpDeviceSlot>(normalized, slot)))
                    NotifyWifiChanged();
                WifiTransportLost?.Invoke(AdbTransportTarget.CreateDirect(slot.TransportId));
                _ = slot.Connection.DisposeAsync();
            };
            if (!_tcpDevices.TryAdd(normalized, slot))
            {
                await conn.DisposeAsync().ConfigureAwait(false);
                return "already connected to " + normalized;
            }
            if (!conn.IsConnected)
            {
                _tcpDevices.TryRemove(new KeyValuePair<string, TcpDeviceSlot>(normalized, slot));
                throw new EndOfStreamException("Wi-Fi ADB closed during connection setup.");
            }
            conn = null;
            NotifyWifiChanged();
            return "connected to " + normalized;
        }
        finally
        {
            if (conn != null) await conn.DisposeAsync().ConfigureAwait(false);
            if (link != null) await link.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<string> DisconnectAsync(string address, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(address);
        if (AdbTransportTarget.TryParse(address, out bool direct, out string id))
        {
            if (!direct) throw new IOException("ADB transport changed.");
            var admitted = _tcpDevices.Values.FirstOrDefault(s => s.TransportId == id);
            if (admitted != null && _tcpDevices.TryRemove(new KeyValuePair<string, TcpDeviceSlot>(admitted.Address, admitted)))
            { await admitted.Connection.DisposeAsync().ConfigureAwait(false); NotifyWifiChanged(); }
            return "disconnected admitted transport";
        }
        if (address.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string key in _tcpDevices.Keys.ToList())
            {
                if (_tcpDevices.TryRemove(key, out var slot))
                    await slot.Connection.DisposeAsync().ConfigureAwait(false);
            }
            NotifyWifiChanged();
            return "disconnected everything";
        }

        string normalized = AndroidWirelessConnection.ValidateAddress(address);
        if (_tcpDevices.TryRemove(normalized, out var removed))
        {
            await removed.Connection.DisposeAsync().ConfigureAwait(false);
            NotifyWifiChanged();
            return "disconnected " + normalized;
        }
        return "";
    }

    public Task<IReadOnlyList<AndroidMdnsService>> MdnsServicesAsync(CancellationToken token) =>
        _mdnsImpl(TimeSpan.FromMilliseconds(1200), token);

    /// <summary>
    /// Closes all active WinUSB handles and pending USB handshakes immediately, e.g., when external adb.exe takes port 5037.
    /// </summary>
    public async Task ReleaseUsbConnectionsAsync()
    {
        // Join a scan even when it has not published its first slot yet.
        await _usbSyncGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (string key in _usbDevices.Keys.ToList())
            {
                if (_usbDevices.TryRemove(key, out var slot))
                    await DisposeUsbSlotAsync(slot).ConfigureAwait(false);
            }
        }
        finally
        {
            _usbSyncGate.Release();
        }
    }

    /// <summary>
    /// Releases idle WinUSB and disconnected TCP connections without interrupting active streams,
    /// forwards, or handshakes. Live idle TCP connections remain unless <paramref name="includeIdleTcp"/> is true.
    /// </summary>
    public async Task ReleaseIdleConnectionsAsync(bool includeIdleTcp = false)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        if (!_usbDevices.IsEmpty)
        {
            await _usbSyncGate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (string key in _usbDevices.Keys.ToList())
                {
                    if (!_usbDevices.TryGetValue(key, out var slot))
                        continue;
                    if (slot.PendingHandshake is { IsCompleted: false }
                        || slot.Connection is { HasActiveStreams: true }
                        || HasActiveForward(slot.EffectiveSerial, slot.FallbackSerial, slot.TransportId))
                    {
                        continue;
                    }
                    if (_usbDevices.TryRemove(new KeyValuePair<string, UsbDeviceSlot>(key, slot)))
                        await DisposeUsbSlotAsync(slot).ConfigureAwait(false);
                }
            }
            finally
            {
                _usbSyncGate.Release();
            }
        }

        foreach (string tcpKey in _tcpDevices.Keys.ToList())
        {
            if (!_tcpDevices.TryGetValue(tcpKey, out var tcp))
                continue;
            if (HasActiveForwardForTcp(tcp.Address, tcp.TransportId))
                continue;
            if (tcp.Connection.IsConnected && (!includeIdleTcp || tcp.Connection.HasActiveStreams))
                continue;
            if (_tcpDevices.TryRemove(new KeyValuePair<string, TcpDeviceSlot>(tcpKey, tcp)))
                await tcp.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool HasActiveForward(string primarySerial, string? alternateSerial = null, string? transportId = null)
    {
        foreach (var fwd in _forwards.Values)
        {
            if (fwd.Serial.Equals(primarySerial, StringComparison.Ordinal)
                || (transportId != null && AdbTransportTarget.TryParse(fwd.Serial, out bool direct, out string id) && direct && id == transportId)
                || (alternateSerial != null && fwd.Serial.Equals(alternateSerial, StringComparison.Ordinal)))
            {
                return true;
            }
        }
        return false;
    }

    private bool HasActiveForwardForTcp(string normalizedAddress, string transportId)
    {
        foreach (var fwd in _forwards.Values)
        {
            if (fwd.Serial.Equals(normalizedAddress, StringComparison.OrdinalIgnoreCase))
                return true;
            if (AdbTransportTarget.TryParse(fwd.Serial, out bool direct, out string id) && direct && id == transportId) return true;
            if (TryNormalizeAddress(fwd.Serial, out string? norm)
                && norm!.Equals(normalizedAddress, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private async Task<AdbMuxConnection> ResolveConnectionAsync(string serial, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        if (AdbTransportTarget.TryParse(serial, out bool direct, out string id))
        {
            token.ThrowIfCancellationRequested();
            if (!direct) throw new IOException("ADB transport changed.");
            var wifi = _tcpDevices.Values.FirstOrDefault(s => s.TransportId == id);
            if (wifi?.Connection is { IsConnected: true } tcpConnection) return tcpConnection;
            var usb = _usbDevices.Values.FirstOrDefault(s => s.TransportId == id);
            if (usb?.Connection is { IsConnected: true } usbConnection) return usbConnection;
            throw new IOException("The admitted ADB transport is no longer connected.");
        }
        if (_tcpDevices.TryGetValue(serial, out var tcp) && tcp.Connection.IsConnected)
            return tcp.Connection;

        if (TryNormalizeAddress(serial, out string? norm) && _tcpDevices.TryGetValue(norm!, out var normTcp) && normTcp.Connection.IsConnected)
            return normTcp.Connection;

        var usbMatch = FindUsbSlotBySerial(serial);
        if (usbMatch == null)
        {
            await DevicesAsync(token, retryFailedHandshakes: false).ConfigureAwait(false);
            usbMatch = FindUsbSlotBySerial(serial);
        }
        if (usbMatch != null)
        {
            if (usbMatch.Connection is { IsConnected: true } active)
                return active;
            throw AndroidError.WithCode(new IOException("请先在手机上允许 ADB 调试授权。"), AndroidErrorCode.AuthorizeAdbFirst);
        }

        throw new IOException($"ADB device '{serial}' is not connected.");
    }

    private UsbDeviceSlot? FindUsbSlotBySerial(string serial)
    {
        foreach (var slot in _usbDevices.Values)
        {
            if (slot.EffectiveSerial.Equals(serial, StringComparison.Ordinal))
                return slot;
            if (slot.FallbackSerial.Equals(serial, StringComparison.Ordinal))
                return slot;
        }
        return null;
    }

    private async Task<IReadOnlyList<AndroidDevice>> DevicesAsync(CancellationToken token, bool retryFailedHandshakes)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _usbSyncGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var present = _enumerateUsb();
            var presentMap = new Dictionary<string, WinUsbDeviceDescriptor>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in present)
                presentMap[d.DevicePath] = d;

            foreach (var entry in _usbDevices)
            {
                var slot = entry.Value;
                if (!presentMap.ContainsKey(entry.Key) || slot.Connection is { IsConnected: false }
                    || (retryFailedHandshakes && slot.AuthFailure != null))
                {
                    if (_usbDevices.TryRemove(entry))
                        await DisposeUsbSlotAsync(slot).ConfigureAwait(false);
                }
            }

            var waitTasks = new List<Task>();
            List<AndroidDevice>? unavailable = null;
            foreach (var desc in present)
            {
                if (_usbDevices.ContainsKey(desc.DevicePath)) continue;

                IAdbPacketLink? link = null;
                try
                {
                    link = _openUsbLink(desc.DevicePath);
                }
                catch (IOException ex)
                {
                    UsbAccessDenied?.Invoke(ex.Message);
                    string serial = ExtractFallbackSerial(desc.InstanceId, desc.DevicePath);
                    string state = ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 }
                        ? UsbInUse : UsbUnavailable;
                    (unavailable ??= []).Add(new AndroidDevice(serial, serial, state, IsWifi: false) { UsbInterfacePath = desc.DevicePath });
                    continue;
                }
                if (link == null) continue;

                string usbSerial = (link as WinUsbAdbLink)?.SerialNumber
                    ?? ExtractFallbackSerial(desc.InstanceId, desc.DevicePath);
                string tid = "usb-" + Interlocked.Increment(ref _nextTransportId).ToString(CultureInfo.InvariantCulture);
                var slot = new UsbDeviceSlot(desc.DevicePath, usbSerial, tid);
                var ownedLink = link;
                slot.PendingHandshake = Task.Run(() => CompleteUsbHandshakeAsync(slot, ownedLink));
                _usbDevices[desc.DevicePath] = slot;

                // Wait briefly so authorized devices appear immediately as "device" without stalling unauthorized prompts.
                waitTasks.Add(WaitInitialHandshakeAsync(slot.PendingHandshake, token));
            }

            if (waitTasks.Count > 0)
                await Task.WhenAll(waitTasks).ConfigureAwait(false);
            return SnapshotDevices(unavailable);
        }
        finally
        {
            _usbSyncGate.Release();
        }
    }

    private static async Task WaitInitialHandshakeAsync(Task handshakeTask, CancellationToken token)
    {
        using var slice = CancellationTokenSource.CreateLinkedTokenSource(token);
        slice.CancelAfter(TimeSpan.FromMilliseconds(900));
        try { await handshakeTask.WaitAsync(slice.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
    }

    private async Task CompleteUsbHandshakeAsync(UsbDeviceSlot slot, IAdbPacketLink link)
    {
        try
        {
            var conn = await AdbMuxConnection.ConnectAsync(link, _keyStore, token: slot.Lifetime.Token).ConfigureAwait(false);
            if (slot.Lifetime.IsCancellationRequested)
            {
                await conn.DisposeAsync().ConfigureAwait(false);
                return;
            }
            if (conn.DeviceProperties.TryGetValue("ro.serialno", out string? roSerial)
                && !string.IsNullOrWhiteSpace(roSerial)
                && roSerial != "unknown"
                && slot.FallbackSerial.StartsWith("usb-", StringComparison.Ordinal))
            {
                slot.NegotiatedSerial = roSerial;
            }
            slot.Connection = conn;
            conn.Disconnected += () =>
            {
                if (_usbDevices.TryRemove(new KeyValuePair<string, UsbDeviceSlot>(slot.DevicePath, slot)))
                    NotifyDevicesChanged();
                _ = conn.DisposeAsync();
            };
            NotifyDevicesChanged();
        }
        catch (OperationCanceledException)
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            slot.AuthFailure = ex;
            await link.DisposeAsync().ConfigureAwait(false);
            NotifyDevicesChanged();
        }
    }

    private IReadOnlyList<AndroidDevice> SnapshotDevices(List<AndroidDevice>? unavailable)
    {
        var list = unavailable ?? [];
        foreach (var slot in _usbDevices.Values.OrderBy(s => s.TransportId, StringComparer.Ordinal))
        {
            if (slot.Connection is { IsConnected: true } conn)
            {
                string model = conn.DeviceProperties.GetValueOrDefault("ro.product.model")
                    ?? conn.DeviceProperties.GetValueOrDefault("ro.product.device")
                    ?? slot.EffectiveSerial;
                string state = string.IsNullOrEmpty(conn.DeviceType) ? "device" : conn.DeviceType;
                list.Add(new AndroidDevice(slot.EffectiveSerial, model.Replace('_', ' '), state, IsWifi: false, TransportId: slot.TransportId) { UsbInterfacePath = slot.DevicePath });
            }
            else
            {
                string state = slot.AuthFailure == null && slot.PendingHandshake is { IsCompleted: false }
                    ? "unauthorized" : "offline";
                list.Add(new AndroidDevice(slot.EffectiveSerial, slot.EffectiveSerial, state, IsWifi: false, TransportId: slot.TransportId) { UsbInterfacePath = slot.DevicePath });
            }
        }

        AddWifiDevices(list);
        return list;
    }

    internal bool OwnsWifiConnection(string serial) =>
        _tcpDevices.TryGetValue(serial, out var slot) && slot.Connection.IsConnected;

    internal IReadOnlyList<AndroidDevice> IncludeOwnedWifi(IReadOnlyList<AndroidDevice> external)
    {
        if (_tcpDevices.IsEmpty) return external;
        var list = new List<AndroidDevice>();
        AddWifiDevices(list);
        var owned = list.Select(d => d.Serial).ToHashSet(StringComparer.OrdinalIgnoreCase);
        list.AddRange(external.Where(d => !owned.Contains(d.Serial)));
        return list;
    }

    private void AddWifiDevices(List<AndroidDevice> list)
    {
        foreach (var slot in _tcpDevices.Values.OrderBy(s => s.TransportId, StringComparer.Ordinal))
        {
            if (!slot.Connection.IsConnected) continue;
            string model = slot.Connection.DeviceProperties.GetValueOrDefault("ro.product.model")
                ?? slot.Connection.DeviceProperties.GetValueOrDefault("ro.product.device")
                ?? slot.Address;
            list.Add(new AndroidDevice(slot.Address, model.Replace('_', ' '), "device", IsWifi: true, TransportId: slot.TransportId));
        }
    }

    private static async Task RunForwardLoopAsync(TcpListener listener, AdbMuxConnection conn, string localAbstractName, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                _ = BridgeForwardClientAsync(client, conn, localAbstractName, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task BridgeForwardClientAsync(TcpClient client, AdbMuxConnection conn, string localAbstractName, CancellationToken token)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                await using Stream remote = await conn.OpenLocalAbstractAsync(localAbstractName, token).ConfigureAwait(false);
                await using NetworkStream local = client.GetStream();
                using var pairCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var t1 = local.CopyToAsync(remote, pairCts.Token);
                var t2 = remote.CopyToAsync(local, pairCts.Token);
                await Task.WhenAny(t1, t2).ConfigureAwait(false);
                pairCts.Cancel();
                try { await Task.WhenAll(t1, t2).ConfigureAwait(false); } catch { }
            }
        }
        catch { }
    }

    private static async Task<IAdbPacketLink> OpenDefaultTcpLinkAsync(string host, int port, CancellationToken token)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            IPAddress ip = IPAddress.Parse(host.Trim('[', ']'));
            await tcp.ConnectAsync(new IPEndPoint(ip, port), token).ConfigureAwait(false);
            return new StreamAdbPacketLink(tcp.GetStream(), tcp, supportsTlsUpgrade: true);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static async Task DisposeUsbSlotAsync(UsbDeviceSlot slot)
    {
        slot.Lifetime.Cancel();
        // A handshake that passed its cancellation check can still publish its connection.
        if (slot.PendingHandshake != null)
        {
            try { await slot.PendingHandshake.ConfigureAwait(false); } catch { }
        }
        if (slot.Connection != null)
        {
            try { await slot.Connection.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        slot.Lifetime.Dispose();
    }

    private void NotifyDevicesChanged()
    {
        foreach (var cb in _deviceWatchSubscribers.Values)
            cb();
    }

    private void NotifyWifiChanged()
    {
        NotifyDevicesChanged();
        WifiChanged?.Invoke();
    }

    private static (string Host, int Port) SplitHostPort(string normalizedAddress)
    {
        int colon = normalizedAddress.LastIndexOf(':');
        string host = normalizedAddress[..colon].Trim('[', ']');
        int port = int.Parse(normalizedAddress[(colon + 1)..], CultureInfo.InvariantCulture);
        return (host, port);
    }

    private static bool TryNormalizeAddress(string value, out string? normalized)
    {
        try { normalized = AndroidWirelessConnection.ValidateAddress(value); return true; }
        catch (ArgumentException) { normalized = null; return false; }
    }

    private static string ExtractFallbackSerial(string? instanceId, string devicePath)
    {
        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            int slash = instanceId.LastIndexOf('\\');
            if (slash >= 0 && slash + 1 < instanceId.Length)
            {
                string tail = instanceId[(slash + 1)..];
                if (!tail.Contains('&', StringComparison.Ordinal))
                    return tail;
            }
        }
        uint hash = 2166136261u;
        foreach (char c in devicePath.ToUpperInvariant())
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return "usb-" + hash.ToString("x8", CultureInfo.InvariantCulture);
    }

    private static bool DeviceListsEqual(IReadOnlyList<AndroidDevice> a, IReadOnlyList<AndroidDevice> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        foreach (string fwdKey in _forwards.Keys.ToList())
        {
            if (_forwards.TryRemove(fwdKey, out var fwd))
                await fwd.DisposeAsync().ConfigureAwait(false);
        }

        await ReleaseUsbConnectionsAsync().ConfigureAwait(false);

        foreach (string tcpKey in _tcpDevices.Keys.ToList())
        {
            if (_tcpDevices.TryRemove(tcpKey, out var tcp))
                await tcp.Connection.DisposeAsync().ConfigureAwait(false);
        }

        // A queued scan may still resume and release this async-only managed gate.
        if (_ownsKeyStore) _keyStore.Dispose();
    }
}

/// <summary>
/// Non-invasively probes 127.0.0.1:5037 before device operations: delegates to external adb.exe server when present
/// so SonicRoute never steals exclusive WinUSB handles from other user tools; otherwise uses DirectAdbTransport.
/// </summary>
internal sealed class ArbitratedAdbTransport : IAdbTransport, IAsyncDisposable
{
    internal async Task WatchPanelWirelessAsync(Action<string> lost, Action changed, CancellationToken token)
    {
        string[] shadowed = [];
        void ObserveAliases(IReadOnlyList<AndroidDevice> external, IReadOnlyList<AndroidDevice> merged)
        {
            string[] aliases = ReferenceEquals(external, merged) ? [] : merged
                .Where(d => d.IsWifi && d.TransportId?.StartsWith("wifi-", StringComparison.Ordinal) == true
                    && external.Any(other => other.State == "device" && other.IsWifi && other.ConnectionTarget != null
                        && d.Serial.Equals(other.Serial, StringComparison.OrdinalIgnoreCase)))
                .Select(d => AdbTransportTarget.CreateDirect(d.TransportId!)).ToArray();
            Volatile.Write(ref shadowed, aliases);
        }
        void DirectLost(string target)
        {
            lost(target);
            // A direct session shadows an externally owned connection at the same
            // address. A fresh identity read can now expose that surviving session.
            if (Volatile.Read(ref shadowed).Contains(target)) changed();
        }
        MergedExternalDevices += ObserveAliases;
        _directTransport.WifiTransportLost += DirectLost;
        try
        {
            if (_socketTransport is AdbSocketClient socket)
                await socket.WatchPanelWirelessAsync(lost, changed, token).ConfigureAwait(false);
            else await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        }
        finally
        {
            _directTransport.WifiTransportLost -= DirectLost;
            MergedExternalDevices -= ObserveAliases;
        }
    }

    private readonly Func<CancellationToken, Task<bool>> _isSocketActiveAsync;
    private readonly IAdbTransport _socketTransport;
    private readonly DirectAdbTransport _directTransport;
    private readonly bool _ownsDirect;
    private int _disposed;
    private event Action<IReadOnlyList<AndroidDevice>, IReadOnlyList<AndroidDevice>>? MergedExternalDevices;

    public ArbitratedAdbTransport(IPEndPoint? socketEndpoint = null)
        : this(
            token => AdbSocketClient.IsServerReachableAsync(socketEndpoint ?? new IPEndPoint(IPAddress.Loopback, 5037), token),
            new AdbSocketClient(socketEndpoint ?? new IPEndPoint(IPAddress.Loopback, 5037)),
            new DirectAdbTransport(),
            ownsDirect: true)
    { }

    internal ArbitratedAdbTransport(
        Func<CancellationToken, Task<bool>> isSocketActiveAsync,
        IAdbTransport socketTransport,
        DirectAdbTransport directTransport,
        bool ownsDirect = false)
    {
        _isSocketActiveAsync = isSocketActiveAsync ?? throw new ArgumentNullException(nameof(isSocketActiveAsync));
        _socketTransport = socketTransport ?? throw new ArgumentNullException(nameof(socketTransport));
        _directTransport = directTransport ?? throw new ArgumentNullException(nameof(directTransport));
        _ownsDirect = ownsDirect;
    }

    public async Task<IAdbTransport> SelectAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (await _isSocketActiveAsync(token).ConfigureAwait(false))
        {
            await _directTransport.ReleaseIdleConnectionsAsync().ConfigureAwait(false);
            return _socketTransport;
        }
        return _directTransport;
    }

    public Task ReleaseIdleConnectionsAsync(bool includeIdleTcp = false)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return Task.CompletedTask;
        return _directTransport.ReleaseIdleConnectionsAsync(includeIdleTcp);
    }

    public async Task<string> VersionAsync(CancellationToken token) =>
        await (await SelectAsync(token).ConfigureAwait(false)).VersionAsync(token).ConfigureAwait(false);

    internal Task<IAdbTransport> SelectDeviceAsync(string serial, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (AdbTransportTarget.TryParse(serial, out bool direct, out _))
            return Task.FromResult<IAdbTransport>(direct ? _directTransport : _socketTransport);
        return _directTransport.OwnsWifiConnection(serial)
            ? Task.FromResult<IAdbTransport>(_directTransport) : SelectAsync(token);
    }

    public async Task<IReadOnlyList<AndroidDevice>> DevicesAsync(CancellationToken token)
    {
        var transport = await SelectAsync(token).ConfigureAwait(false);
        var devices = await transport.DevicesAsync(token).ConfigureAwait(false);
        var merged = transport == _socketTransport ? _directTransport.IncludeOwnedWifi(devices) : devices;
        MergedExternalDevices?.Invoke(devices, merged);
        return merged;
    }

    public async Task TrackDevicesAsync(Action<IReadOnlyList<AndroidDevice>> onChanged, CancellationToken token)
    {
        var transport = await SelectAsync(token).ConfigureAwait(false);
        if (transport != _socketTransport)
        {
            await transport.TrackDevicesAsync(onChanged, token).ConfigureAwait(false);
            return;
        }
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(token);
        var changed = Channel.CreateBounded<bool>(1);
        IReadOnlyList<AndroidDevice>? external = null;
        void Signal() => changed.Writer.TryWrite(true);
        async Task WatchDaemonAsync()
        {
            try
            {
                await transport.TrackDevicesAsync(list => { Volatile.Write(ref external, list); Signal(); }, watch.Token).ConfigureAwait(false);
            }
            finally { changed.Writer.TryComplete(); }
        }
        _directTransport.WifiChanged += Signal;
        var daemon = WatchDaemonAsync();
        try
        {
            await foreach (var _ in changed.Reader.ReadAllAsync(token).ConfigureAwait(false))
                if (Volatile.Read(ref external) is { } list) onChanged(_directTransport.IncludeOwnedWifi(list));
        }
        finally
        {
            _directTransport.WifiChanged -= Signal;
            watch.Cancel();
            await daemon.ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<string>> FeaturesAsync(string serial, CancellationToken token) =>
        await (await SelectDeviceAsync(serial, token).ConfigureAwait(false)).FeaturesAsync(serial, token).ConfigureAwait(false);

    public async Task<string> GetSerialNoAsync(string serial, CancellationToken token) =>
        await (await SelectDeviceAsync(serial, token).ConfigureAwait(false)).GetSerialNoAsync(serial, token).ConfigureAwait(false);

    public async Task<AdbShellResult> RunShellAsync(string serial, string command, CancellationToken token) =>
        await (await SelectDeviceAsync(serial, token).ConfigureAwait(false)).RunShellAsync(serial, command, token).ConfigureAwait(false);

    public async Task<IAdbShellSession> OpenShellAsync(string serial, string command, bool redirectInput, CancellationToken token) =>
        await (await SelectDeviceAsync(serial, token).ConfigureAwait(false)).OpenShellAsync(serial, command, redirectInput, token).ConfigureAwait(false);

    public async Task<Stream> OpenFileSyncAsync(string serial, CancellationToken token) =>
        await (await SelectDeviceAsync(serial, token).ConfigureAwait(false)).OpenFileSyncAsync(serial, token).ConfigureAwait(false);

    public async Task PushAsync(string serial, string localPath, string remotePath, CancellationToken token) =>
        await (await SelectDeviceAsync(serial, token).ConfigureAwait(false)).PushAsync(serial, localPath, remotePath, token).ConfigureAwait(false);

    public async Task<int> ForwardTcpToLocalAbstractAsync(string serial, string localAbstractName, CancellationToken token) =>
        await (await SelectDeviceAsync(serial, token).ConfigureAwait(false)).ForwardTcpToLocalAbstractAsync(serial, localAbstractName, token).ConfigureAwait(false);

    public async Task RemoveForwardAsync(string serial, string localSpec, CancellationToken token) =>
        await (await SelectDeviceAsync(serial, token).ConfigureAwait(false)).RemoveForwardAsync(serial, localSpec, token).ConfigureAwait(false);

    public async Task<IReadOnlyList<AdbForwardRule>> ListForwardsAsync(CancellationToken token)
    {
        var transport = await SelectAsync(token).ConfigureAwait(false);
        var rules = await transport.ListForwardsAsync(token).ConfigureAwait(false);
        if (transport != _socketTransport) return rules;
        var owned = await _directTransport.ListForwardsAsync(token).ConfigureAwait(false);
        return owned.Count == 0 ? rules : [.. owned, .. rules];
    }

    public async Task<Stream> OpenLocalAbstractAsync(string serial, string name, CancellationToken token) =>
        await (await SelectDeviceAsync(serial, token).ConfigureAwait(false)).OpenLocalAbstractAsync(serial, name, token).ConfigureAwait(false);

    public async Task<string> PairAsync(string address, string password, CancellationToken token) =>
        await (await SelectAsync(token).ConfigureAwait(false)).PairAsync(address, password, token).ConfigureAwait(false);

    public async Task<string> ConnectAsync(string address, CancellationToken token) =>
        await (await SelectDeviceAsync(address, token).ConfigureAwait(false)).ConnectAsync(address, token).ConfigureAwait(false);

    public async Task<string> DisconnectAsync(string address, CancellationToken token)
    {
        var transport = await SelectDeviceAsync(address, token).ConfigureAwait(false);
        if (transport == _socketTransport && address.Equals("all", StringComparison.OrdinalIgnoreCase))
            await _directTransport.DisconnectAsync(address, token).ConfigureAwait(false);
        return await transport.DisconnectAsync(address, token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AndroidMdnsService>> MdnsServicesAsync(CancellationToken token) =>
        await (await SelectAsync(token).ConfigureAwait(false)).MdnsServicesAsync(token).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ownsDirect) await _directTransport.DisposeAsync().ConfigureAwait(false);
    }
}
