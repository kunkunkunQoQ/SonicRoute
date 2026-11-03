using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SonicRoute.AndroidAudio;

public interface IPcProcessSilenceLease : IDisposable
{
    void Release(bool restorePreviousEndpoint);
}

/// <summary>Streams per-process Windows 11 loopback audio to an Android phone's speaker or Bluetooth output sink.</summary>
public sealed class PcToPhoneAudioManager(Func<AdbClient> adbFactory, Func<int, IPcProcessSilenceLease?>? silenceLocalProcess = null) : IAsyncDisposable
{
    public PcToPhoneAudioManager(AdbClient adb, Func<int, IPcProcessSilenceLease?>? silenceLocalProcess = null)
        : this(() => adb, silenceLocalProcess) { }

    private readonly object _gate = new();
    private readonly Dictionary<int, ActiveRoute> _routes = new();
    public bool EnableLiveStreaming { get; set; } = true;
    public event Action? Changed;
    public event Action<AndroidDevice, IReadOnlyList<AndroidOutputSink>>? OutputsObserved;
    public event Action<int, string, int, string>? RoutedDeviceObserved;
    public event Action<AndroidDevice>? DeviceDisconnected;

    public int ActiveCount { get { lock (_gate) return _routes.Count; } }

    public (long BytesStreamed, long NonSilentBytesStreamed, string? RoutedKey, int RoutedType, string? RoutedName)? GetDiagnostics(int processId)
    {
        lock (_gate)
        {
            if (!_routes.TryGetValue(processId, out var route)) return null;
            return (Interlocked.Read(ref route.BytesStreamed), Interlocked.Read(ref route.NonSilentBytesStreamed),
                route.RoutedKey, route.RoutedType, route.RoutedName);
        }
    }

    public string? GetPersistedEndpoint(int processId)
    {
        lock (_gate)
            return _routes.TryGetValue(processId, out var route) ? route.TargetDeviceId : null;
    }

    public IReadOnlyList<(int ProcessId, string TargetDeviceId, string PhoneKey, string SinkKey)> GetActiveRoutes()
    {
        lock (_gate)
            return _routes.Values.Select(r => (r.ProcessId, r.TargetDeviceId, r.PhoneKey, r.SinkKey)).ToArray();
    }

    public (bool Success, int HResult, string Message) RouteProcess(int processId, string targetDeviceId, AndroidDevice? device)
    {
        if (processId <= 0) return (false, -1, "无效的应用进程 ID。");
        if (!AndroidOutputIdentity.TryParseDeviceId(targetDeviceId, out string phoneKey, out string sinkKey))
            return (false, -1, "无效的手机播放设备标识。");

        ActiveRoute? previous = null;
        ActiveRoute route;
        bool samePhoneSwitch = false;

        lock (_gate)
        {
            if (_routes.TryGetValue(processId, out var existing) && !existing.IsStopped
                && string.Equals(existing.PhoneKey, phoneKey, StringComparison.OrdinalIgnoreCase))
            {
                existing.TargetDeviceId = targetDeviceId;
                existing.SinkKey = sinkKey;
                if (device != null) existing.Device = device;
                route = existing;
                samePhoneSwitch = true;
            }
            else
            {
                IPcProcessSilenceLease? carriedLease = null;
                if (_routes.TryGetValue(processId, out previous))
                {
                    carriedLease = previous.DetachSilenceLease();
                    _routes.Remove(processId);
                }
                bool canSilence = device is { State: "device" }
                    && !string.Equals(device.Serial, "offline-fixture", StringComparison.Ordinal);
                var lease = carriedLease ?? (canSilence ? SafeAcquireSilenceLease(processId) : null);
                route = new ActiveRoute(processId, targetDeviceId, phoneKey, sinkKey, device, lease);
                _routes[processId] = route;
            }
        }

        if (samePhoneSwitch)
        {
            route.RequestSinkSwitch(sinkKey);
            if (!route.IsStreamingStarted && EnableLiveStreaming
                && !string.Equals(route.Device?.Serial, "offline-fixture", StringComparison.Ordinal))
            {
                route.StartStreaming(ct => RunStreamAsync(route, route.Device, ct));
            }
            Changed?.Invoke();
            return (true, 0, "成功");
        }

        if (previous != null)
            _ = previous.StopAsync(restorePreviousEndpoint: false);

        if (EnableLiveStreaming
            && !string.Equals(device?.Serial, "offline-fixture", StringComparison.Ordinal))
        {
            route.StartStreaming(ct => RunStreamAsync(route, device, ct));
        }

        Changed?.Invoke();
        return (true, 0, "成功");
    }

    public bool StopProcess(int processId, bool restorePreviousEndpoint = true)
    {
        ActiveRoute? removed;
        lock (_gate)
        {
            if (!_routes.TryGetValue(processId, out removed)) return false;
            _routes.Remove(processId);
        }
        _ = removed.StopAsync(restorePreviousEndpoint);
        Changed?.Invoke();
        return true;
    }

    public async Task<bool> StopProcessAsync(int processId, bool restorePreviousEndpoint = true)
    {
        ActiveRoute? removed;
        lock (_gate)
        {
            if (!_routes.TryGetValue(processId, out removed)) return false;
            _routes.Remove(processId);
        }
        await removed.StopAsync(restorePreviousEndpoint).ConfigureAwait(false);
        Changed?.Invoke();
        return true;
    }

    public async Task StopPhoneAsync(AndroidDevice device, bool restorePreviousEndpoint = true)
    {
        string phoneKey = AndroidDeviceIdentity.Key(device);
        ActiveRoute[] matching;
        lock (_gate)
        {
            matching = _routes.Values.Where(r =>
                string.Equals(r.PhoneKey, phoneKey, StringComparison.OrdinalIgnoreCase)
                || (r.Device != null && AndroidDeviceIdentity.Matches(r.Device, device))).ToArray();
            foreach (var r in matching) _routes.Remove(r.ProcessId);
        }
        if (matching.Length == 0) return;
        await Task.WhenAll(matching.Select(r => r.StopAsync(restorePreviousEndpoint))).ConfigureAwait(false);
        Changed?.Invoke();
    }

    public int StopAll(bool restorePreviousEndpoint = true)
    {
        ActiveRoute[] all;
        lock (_gate)
        {
            all = _routes.Values.ToArray();
            _routes.Clear();
        }
        foreach (var route in all)
            _ = route.StopAsync(restorePreviousEndpoint);
        if (all.Length > 0) Changed?.Invoke();
        return all.Length;
    }

    public async ValueTask DisposeAsync()
    {
        ActiveRoute[] all;
        lock (_gate)
        {
            all = _routes.Values.ToArray();
            _routes.Clear();
        }
        await Task.WhenAll(all.Select(r => r.StopAsync(restorePreviousEndpoint: true))).ConfigureAwait(false);
    }

    private IPcProcessSilenceLease? SafeAcquireSilenceLease(int processId)
    {
        try { return silenceLocalProcess?.Invoke(processId); }
        catch (Exception ex) { Trace.WriteLine("PcToPhone silence lease: " + ex.Message); return null; }
    }

    private async Task RunStreamAsync(ActiveRoute route, AndroidDevice? device, CancellationToken token)
    {
        var adb = adbFactory();
        await adb.PrepareRuntimeAsync("audio", token).ConfigureAwait(false);
        adb.ValidateServer();
        string catalog = Path.Combine(adb.RuntimeDirectory, "sonicroute-catalog.jar");
        if (!File.Exists(catalog)) throw new FileNotFoundException("Missing Android audio output helper.", catalog);

        string id = Guid.NewGuid().ToString("N");
        string socketName = "sonicroute_pc_out_" + id;
        string remote = "/data/local/tmp/sonicroute_pc_out_" + id + ".jar";
        string remoteServer = remote + ".server";
        Process? helper = null;
        string? remotePid = null;
        string? forwardedPort = null;
        TcpClient? tcp = null;
        bool unexpectedExit = false;

        try
        {
            if (device is not { State: "device" })
            {
                var currentDevices = await adb.DevicesAsync(token).ConfigureAwait(false);
                device = currentDevices.FirstOrDefault(d =>
                    d.State == "device" && string.Equals(AndroidDeviceIdentity.Key(d), route.PhoneKey, StringComparison.OrdinalIgnoreCase));
                if (device == null)
                    throw new IOException("手机未连接或离线。");
                route.Device = device;
            }

            try
            {
                await adb.RunAsync(token, "-s", device.Serial, "push", catalog, remote).ConfigureAwait(false);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                var currentDevices = await adb.DevicesAsync(token).ConfigureAwait(false);
                var refreshed = currentDevices.FirstOrDefault(d =>
                    d.State == "device" && string.Equals(AndroidDeviceIdentity.Key(d), route.PhoneKey, StringComparison.OrdinalIgnoreCase));
                if (refreshed == null)
                    throw;
                device = refreshed;
                route.Device = device;
                await adb.RunAsync(token, "-s", device.Serial, "push", catalog, remote).ConfigureAwait(false);
            }

            route.EnsureSilenceLease(() => SafeAcquireSilenceLease(route.ProcessId));
            await adb.RunAsync(token, "-s", device.Serial, "push", adb.ServerFile, remoteServer).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            helper = adb.StartWithInput("-s", device.Serial, "shell",
                "echo SR_PCOUT_PID:$$; CLASSPATH=" + remote + ":" + remoteServer + " exec app_process / org.sonicroute.android.AudioOutputBridge play "
                + socketName + " " + QuoteShellArg(route.SinkKey));
            route.AttachHelper(helper);

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdoutTask = Task.Run(async () =>
            {
                try
                {
                    while (await helper.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                    {
                        if (line.StartsWith("SR_PCOUT_PID:", StringComparison.Ordinal))
                        {
                            string candidate = line[13..].Trim();
                            if (Regex.IsMatch(candidate, @"\A[1-9]\d*\z")) remotePid = candidate;
                        }
                        else if (line.StartsWith('{'))
                        {
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("outputs", out var outputsElem) && outputsElem.ValueKind == JsonValueKind.Array)
                            {
                                var sinks = AndroidOutputSink.ParseList(outputsElem);
                                try { OutputsObserved?.Invoke(device, sinks); } catch { }
                            }
                            if (root.TryGetProperty("routed", out var routedElem) && routedElem.ValueKind == JsonValueKind.Object)
                            {
                                string rKey = routedElem.TryGetProperty("key", out var rk) ? (rk.GetString() ?? "") : "";
                                int rType = routedElem.TryGetProperty("type", out var rt) && rt.TryGetInt32(out int parsedType) ? parsedType : 0;
                                string rName = routedElem.TryGetProperty("name", out var rn) ? (rn.GetString() ?? "") : "";
                                route.RoutedKey = rKey;
                                route.RoutedType = rType;
                                route.RoutedName = rName;
                                try { RoutedDeviceObserved?.Invoke(route.ProcessId, rKey, rType, rName); } catch { }
                            }
                            if (root.TryGetProperty("listening", out var listening) && listening.ValueKind == JsonValueKind.True)
                                ready.TrySetResult();
                            else if (root.TryGetProperty("error", out var err))
                                ready.TrySetException(new IOException(err.GetString() ?? "Android AudioOutputBridge failed."));
                        }
                    }
                    ready.TrySetException(new IOException("手机音频输出辅助进程已提前退出。"));
                }
                catch (OperationCanceledException) { ready.TrySetCanceled(token); }
                catch (Exception ex) { ready.TrySetException(ex); }
            }, token);

            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            string portText = (await adb.RunAsync(token, "-s", device.Serial, "forward", "tcp:0", "localabstract:" + socketName).ConfigureAwait(false)).Trim();
            if (!int.TryParse(portText, out int port) || port <= 0)
                throw new IOException("ADB 端口转发失败: " + portText);
            forwardedPort = portText;

            tcp = new TcpClient { NoDelay = true, SendBufferSize = 16384, SendTimeout = 5000 };
            // Synchronous PCM writes use bounded backpressure. Cancel this route's
            // socket as well so a stalled phone cannot retain its capture thread.
            using var cancelSocket = token.UnsafeRegister(static socket => ((TcpClient)socket!).Dispose(), tcp);
            await tcp.ConnectAsync("127.0.0.1", port, token).ConfigureAwait(false);
            using var netStream = tcp.GetStream();

            route.FlushPendingSinkSwitch();
            await PcProcessLoopbackCapture.RunMtaCaptureLoopAsync(route.ProcessId, netStream, token, () =>
            {
                StopProcess(route.ProcessId, restorePreviousEndpoint: true);
            }, (bytes, nonSilent) =>
            {
                Interlocked.Add(ref route.BytesStreamed, bytes);
                if (nonSilent > 0) Interlocked.Add(ref route.NonSilentBytesStreamed, nonSilent);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            unexpectedExit = !token.IsCancellationRequested;
            Trace.WriteLine($"PcToPhone stream ({route.ProcessId}): {ex.Message}");
        }
        finally
        {
            route.DetachHelper();
            try { tcp?.Dispose(); } catch { }
            if (helper != null)
            {
                try { helper.StandardInput.WriteLine("quit"); helper.StandardInput.Flush(); helper.StandardInput.Close(); } catch { }
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await helper.WaitForExitAsync(close.Token).ConfigureAwait(false); }
                catch { try { if (!helper.HasExited) helper.Kill(true); } catch { } }
                helper.Dispose();
            }
            if (device != null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (forwardedPort != null)
                {
                    try { await adb.RunAsync(cleanup.Token, "-s", device.Serial, "forward", "--remove", "tcp:" + forwardedPort).ConfigureAwait(false); }
                    catch { }
                }
                if (remotePid != null)
                {
                    try { await adb.RunAsync(cleanup.Token, "-s", device.Serial, "shell", "kill", "-TERM", remotePid).ConfigureAwait(false); }
                    catch { }
                }
                try { await adb.RunAsync(cleanup.Token, "-s", device.Serial, "shell", "rm", "-f", remote, remoteServer).ConfigureAwait(false); }
                catch { }
            }
            if (!token.IsCancellationRequested && !route.IsStopped)
            {
                StopProcess(route.ProcessId, restorePreviousEndpoint: true);
                if (unexpectedExit && device != null)
                {
                    try { DeviceDisconnected?.Invoke(device); } catch { }
                }
            }
        }
    }

    private static string QuoteShellArg(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private sealed class ActiveRoute(
        int processId,
        string targetDeviceId,
        string phoneKey,
        string sinkKey,
        AndroidDevice? device,
        IPcProcessSilenceLease? silenceLease)
    {
        private readonly object _routeLock = new();
        private readonly CancellationTokenSource _cts = new();
        private IPcProcessSilenceLease? _silenceLease = silenceLease;
        private Process? _helper;
        private string? _pendingSinkKey;
        private Task _streamTask = Task.CompletedTask;
        public int ProcessId { get; } = processId;
        public string TargetDeviceId { get; set; } = targetDeviceId;
        public string PhoneKey { get; } = phoneKey;
        public string SinkKey { get; set; } = sinkKey;
        public AndroidDevice? Device { get; set; } = device;
        public bool IsStopped { get; private set; }
        public bool IsStreamingStarted { get; private set; }
        public long BytesStreamed;
        public long NonSilentBytesStreamed;
        public string? RoutedKey;
        public int RoutedType;
        public string? RoutedName;

        public void StartStreaming(Func<CancellationToken, Task> runner)
        {
            lock (_routeLock)
            {
                if (IsStopped || IsStreamingStarted) return;
                IsStreamingStarted = true;
                _streamTask = Task.Run(() => runner(_cts.Token));
            }
        }

        public void AttachHelper(Process helper)
        {
            lock (_routeLock) _helper = helper;
        }

        public void DetachHelper()
        {
            lock (_routeLock) _helper = null;
        }

        public void RequestSinkSwitch(string sinkKey)
        {
            Process? helper;
            lock (_routeLock)
            {
                SinkKey = sinkKey;
                _pendingSinkKey = sinkKey;
                helper = _helper;
            }
            if (helper != null) TryWriteTarget(helper, sinkKey);
        }

        public void FlushPendingSinkSwitch()
        {
            Process? helper;
            string? sinkKey;
            lock (_routeLock)
            {
                helper = _helper;
                sinkKey = _pendingSinkKey;
                _pendingSinkKey = null;
            }
            if (helper != null && !string.IsNullOrEmpty(sinkKey))
                TryWriteTarget(helper, sinkKey);
        }

        private static void TryWriteTarget(Process helper, string sinkKey)
        {
            try
            {
                helper.StandardInput.WriteLine("target " + sinkKey);
                helper.StandardInput.Flush();
            }
            catch { }
        }

        public void EnsureSilenceLease(Func<IPcProcessSilenceLease?> factory)
        {
            lock (_routeLock)
            {
                if (IsStopped || _silenceLease != null) return;
                _silenceLease = factory();
            }
        }

        public IPcProcessSilenceLease? DetachSilenceLease()
        {
            lock (_routeLock)
            {
                var lease = _silenceLease;
                _silenceLease = null;
                return lease;
            }
        }

        public async Task StopAsync(bool restorePreviousEndpoint)
        {
            IPcProcessSilenceLease? lease;
            Task stream;
            lock (_routeLock)
            {
                if (IsStopped) return;
                IsStopped = true;
                lease = _silenceLease;
                _silenceLease = null;
                stream = _streamTask;
            }
            try { _cts.Cancel(); } catch { }
            try { lease?.Release(restorePreviousEndpoint); } catch { }
            try { await stream.ConfigureAwait(false); } catch { }
            _cts.Dispose();
        }
    }
}

internal static class PcProcessLoopbackCapture
{
    private const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VAD\\Process_Loopback";
    private const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;
    private const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    private const ushort VT_BLOB = 65;

    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public int ActivationType;
        public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLOB
    {
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT_BLOB
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public BLOB blob;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig]
        int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, ref WAVEFORMATEX pFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long phnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, ref WAVEFORMATEX pFormat, IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Ready = new(false);
        public int ActivateHResult = -1;
        public object? Interface;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try { activateOperation.GetActivateResult(out ActivateHResult, out Interface); }
            catch (Exception ex) { ActivateHResult = ex.HResult; }
            finally { Ready.Set(); }
            return 0;
        }
    }

    public static Task RunMtaCaptureLoopAsync(int processId, Stream destination, CancellationToken token, Action onProcessExited, Action<int, int>? onPacketStreamed = null)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                CaptureLoopMta(processId, destination, token, onProcessExited, onPacketStreamed);
                tcs.TrySetResult();
            }
            catch (OperationCanceledException) { tcs.TrySetCanceled(token); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        })
        {
            IsBackground = true,
            Name = $"SonicRoute-PcToPhone-{processId}"
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return tcs.Task;
    }

    private static IAudioClient ActivateProcessLoopbackClient(int processId, CancellationToken token)
    {
        var actParams = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
            {
                TargetProcessId = (uint)processId,
                ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            }
        };
        int paramSize = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr pBlob = Marshal.AllocCoTaskMem(paramSize);
        IntPtr pVar = Marshal.AllocCoTaskMem(Marshal.SizeOf<PROPVARIANT_BLOB>());
        try
        {
            Marshal.StructureToPtr(actParams, pBlob, false);
            var propVar = new PROPVARIANT_BLOB
            {
                vt = VT_BLOB,
                blob = new BLOB { cbSize = (uint)paramSize, pBlobData = pBlob }
            };
            Marshal.StructureToPtr(propVar, pVar, false);

            var handler = new CompletionHandler();
            Guid iid = IID_IAudioClient;
            int hr = ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, ref iid, pVar, handler, out var op);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try
            {
                if (!handler.Ready.Wait(5000, token))
                    throw new TimeoutException("ActivateAudioInterfaceAsync timed out.");
                if (handler.ActivateHResult < 0 || handler.Interface == null)
                    Marshal.ThrowExceptionForHR(handler.ActivateHResult < 0 ? handler.ActivateHResult : -1);
                return (IAudioClient)handler.Interface!;
            }
            finally
            {
                if (op != null) Marshal.ReleaseComObject(op);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pVar);
            Marshal.FreeCoTaskMem(pBlob);
        }
    }

    private static void CaptureLoopMta(int processId, Stream destination, CancellationToken token, Action onProcessExited, Action<int, int>? onPacketStreamed)
    {
        var wfx = new WAVEFORMATEX
        {
            wFormatTag = 1,
            nChannels = 2,
            nSamplesPerSec = 48000,
            wBitsPerSample = 16,
            nBlockAlign = 4,
            nAvgBytesPerSec = 48000 * 4,
            cbSize = 0
        };

        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        using var sampleEvent = new AutoResetEvent(false);
        bool useEventCallback = true;

        try
        {
            client = ActivateProcessLoopbackClient(processId, token);
            uint eventFlags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY | AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
            int hrInit = client.Initialize(0, eventFlags, 200_000, 0, ref wfx, IntPtr.Zero);
            if (hrInit >= 0)
            {
                int hrEvt = client.SetEventHandle(sampleEvent.SafeWaitHandle.DangerousGetHandle());
                if (hrEvt < 0) useEventCallback = false;
            }
            else
            {
                Marshal.ReleaseComObject(client);
                client = ActivateProcessLoopbackClient(processId, token);
                useEventCallback = false;
                uint pollFlags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                    | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
                hrInit = client.Initialize(0, pollFlags, 200_000, 0, ref wfx, IntPtr.Zero);
                if (hrInit < 0) Marshal.ThrowExceptionForHR(hrInit);
            }

            Guid iidCapture = IID_IAudioCaptureClient;
            int hrService = client.GetService(ref iidCapture, out object capObj);
            if (hrService < 0 || capObj == null) Marshal.ThrowExceptionForHR(hrService < 0 ? hrService : -1);
            capture = (IAudioCaptureClient)capObj!;

            int hrStart = client.Start();
            if (hrStart < 0) Marshal.ThrowExceptionForHR(hrStart);

            byte[] pcmBuffer = new byte[48000 * 4];
            long lastAliveCheck = Stopwatch.GetTimestamp();
            WaitHandle[] waits = [token.WaitHandle, sampleEvent];

            while (!token.IsCancellationRequested)
            {
                if (useEventCallback)
                    WaitHandle.WaitAny(waits, 50);
                else if (token.WaitHandle.WaitOne(10))
                    break;

                while (!token.IsCancellationRequested
                    && capture!.GetNextPacketSize(out uint packetFrames) >= 0 && packetFrames > 0)
                {
                    int hrBuf = capture.GetBuffer(out IntPtr dataPtr, out uint numFrames, out uint flags, out _, out _);
                    if (hrBuf < 0) break;
                    int byteCount = (int)(numFrames * 4);
                    if (byteCount > 0)
                    {
                        if (byteCount > pcmBuffer.Length) pcmBuffer = new byte[byteCount];
                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || dataPtr == IntPtr.Zero)
                            Array.Clear(pcmBuffer, 0, byteCount);
                        else
                            Marshal.Copy(dataPtr, pcmBuffer, 0, byteCount);
                    }
                    capture.ReleaseBuffer(numFrames);
                    if (byteCount > 0)
                    {
                        destination.Write(pcmBuffer, 0, byteCount);
                        if (onPacketStreamed != null)
                        {
                            int nonSilent = 0;
                            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0)
                            {
                                for (int i = 0; i < byteCount; i++)
                                {
                                    if (pcmBuffer[i] != 0) { nonSilent = byteCount; break; }
                                }
                            }
                            onPacketStreamed(byteCount, nonSilent);
                        }
                    }
                }

                if (Stopwatch.GetElapsedTime(lastAliveCheck).TotalSeconds >= 2)
                {
                    lastAliveCheck = Stopwatch.GetTimestamp();
                    if (!IsProcessAlive(processId))
                    {
                        try { onProcessExited(); } catch { }
                        break;
                    }
                }
            }
        }
        finally
        {
            if (client != null)
            {
                try { client.Stop(); } catch { }
            }
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch { return true; }
    }
}
