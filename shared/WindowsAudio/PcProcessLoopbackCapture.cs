using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SonicRoute.AndroidAudio;

internal static class PcProcessLoopbackCapture
{
    internal const string CaptureSessionId = ProcessCaptureIdentity.SessionId;
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
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, ref WAVEFORMATEX pFormat, [In] ref Guid audioSessionGuid);
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

    public static Task RunMtaCaptureLoopAsync(int processId, Stream destination, CancellationToken token, Action? onProcessExited,
        Action<int, int>? onPacketStreamed = null, Action? onStarted = null)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                CaptureLoopMta(processId, destination, token, onProcessExited, onPacketStreamed, onStarted);
                tcs.TrySetResult(true);
            }
            catch (OperationCanceledException) { tcs.TrySetCanceled(token); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        })
        {
            IsBackground = true,
            Name = $"SonicRoute-ProcessAudio-{processId}"
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

    private static IAudioClient InitializeCaptureClient(int processId, CancellationToken token,
        AutoResetEvent sampleEvent, out bool useEventCallback)
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

        uint flags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
            | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        var sessionGuid = new Guid(CaptureSessionId);
        var client = ActivateProcessLoopbackClient(processId, token);
        try
        {
            if (client.Initialize(0, flags | AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 200_000, 0, ref wfx, ref sessionGuid) >= 0
                && client.SetEventHandle(sampleEvent.SafeWaitHandle.DangerousGetHandle()) >= 0)
            {
                useEventCallback = true;
                return client;
            }
        }
        catch { Marshal.ReleaseComObject(client); throw; }
        Marshal.ReleaseComObject(client);
        client = ActivateProcessLoopbackClient(processId, token);
        try
        {
            Marshal.ThrowExceptionForHR(client.Initialize(0, flags, 200_000, 0, ref wfx, ref sessionGuid));
            useEventCallback = false;
            return client;
        }
        catch { Marshal.ReleaseComObject(client); throw; }
    }

    private static void CaptureLoopMta(int processId, Stream destination, CancellationToken token, Action? onProcessExited,
        Action<int, int>? onPacketStreamed, Action? onStarted)
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        using var sampleEvent = new AutoResetEvent(false);
        try
        {
            client = InitializeCaptureClient(processId, token, sampleEvent, out bool useEventCallback);
            Guid iidCapture = IID_IAudioCaptureClient;
            int hrService = client.GetService(ref iidCapture, out object capObj);
            if (hrService < 0 || capObj == null) Marshal.ThrowExceptionForHR(hrService < 0 ? hrService : -1);
            capture = (IAudioCaptureClient)capObj!;

            int hrStart = client.Start();
            if (hrStart < 0) Marshal.ThrowExceptionForHR(hrStart);
            onStarted?.Invoke();

            byte[] pcmBuffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                long lastAliveCheck = Stopwatch.GetTimestamp();
                // Local mirrors cancel the token from Process.Exited. An idle event-capable capture can sleep until audio or cancellation.
                int eventWait = onProcessExited == null ? Timeout.Infinite : 50;
                WaitHandle[] waits = [token.WaitHandle, sampleEvent];

                while (!token.IsCancellationRequested)
                {
                    if (useEventCallback)
                        WaitHandle.WaitAny(waits, eventWait);
                    else if (token.WaitHandle.WaitOne(10))
                        break;

                    while (!token.IsCancellationRequested
                        && capture!.GetNextPacketSize(out uint packetFrames) >= 0 && packetFrames > 0)
                    {
                        int hrBuf = capture.GetBuffer(out IntPtr dataPtr, out uint numFrames, out uint flags, out _, out _);
                        if (hrBuf < 0) break;
                        int byteCount = (int)(numFrames * 4);
                        byte[]? oversized = null;
                        try
                        {
                            byte[] workBuffer = pcmBuffer;
                            if (byteCount > 0)
                            {
                                if (byteCount > pcmBuffer.Length)
                                {
                                    oversized = ArrayPool<byte>.Shared.Rent(byteCount);
                                    workBuffer = oversized;
                                }
                                if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || dataPtr == IntPtr.Zero)
                                    Array.Clear(workBuffer, 0, byteCount);
                                else
                                    Marshal.Copy(dataPtr, workBuffer, 0, byteCount);
                            }
                            capture.ReleaseBuffer(numFrames);
                            numFrames = 0;
                            if (byteCount > 0)
                            {
                                destination.Write(workBuffer, 0, byteCount);
                                if (onPacketStreamed != null)
                                {
                                    int nonSilent = 0;
                                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0)
                                    {
                                        for (int i = 0; i < byteCount; i++)
                                        {
                                            if (workBuffer[i] != 0) { nonSilent = byteCount; break; }
                                        }
                                    }
                                    onPacketStreamed(byteCount, nonSilent);
                                }
                            }
                        }
                        finally
                        {
                            if (numFrames > 0)
                                capture.ReleaseBuffer(numFrames);
                            if (oversized != null)
                                ArrayPool<byte>.Shared.Return(oversized);
                        }
                    }

                    if (onProcessExited != null && Stopwatch.GetTimestamp() - lastAliveCheck >= 2 * Stopwatch.Frequency)
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
                ArrayPool<byte>.Shared.Return(pcmBuffer);
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
