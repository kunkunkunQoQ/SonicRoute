using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private const BindingFlags StreamPrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed record StreamTrial(double Milliseconds, long AllocatedBytes);
    private sealed record StreamMetric(string Name, int Operations, StreamTrial[] Before, StreamTrial[] After);

    private static async Task StreamPerformanceAsync(string output, bool pcmOnly)
    {
        Directory.CreateDirectory(output);
        var metrics = new List<StreamMetric>();
        CheckPcm16Conversion(metrics);
        if (!pcmOnly)
        {
            CheckComDelegateReuse(metrics);
            CheckShaderParameterReuse();
            await CheckVideoStartupCleanupAsync(output);
            await CheckOutputReplacementCleanupAsync();
            await CheckRendererResizeRecoveryAsync();
            CheckMediaFoundationH264Decode();
            Check(MediaFoundationH264Decoder.ActiveInstanceCount == 0 && D3D11VideoSurfaceRenderer.ActiveInstanceCount == 0,
                "native decoder and renderer instance counts return to zero");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        {
            Success = true, Assertions = _checks, PcmOnly = pcmOnly, Vector.IsHardwareAccelerated,
            VectorFloatLanes = Vector<float>.Count, Metrics = metrics,
            RealPhoneAccess = false, RealAudioPlayback = false, SharedAdbAccess = false,
            NativeVideo = pcmOnly ? "not used" : "hidden owned window, D3D11 resize failure/recovery and synthetic H.264",
            Scope = "Local hot-path comparisons; not an end-to-end CPU/GPU, latency or audio quality benchmark."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {_checks} stream performance/failure assertions; no real phone or audio playback.");
    }

    private static void CheckPcm16Conversion(List<StreamMetric> metrics)
    {
        float[] special = [0f, -0f, 1f, -1f, 0.5f, -0.5f, 1f / 65536, -1f / 65536,
            float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.Epsilon, -float.Epsilon,
            float.MaxValue, float.MinValue, 2f, -2f, 0.99999994f, -0.99999994f];
        var random = new Random(133);
        float[] values = special.Concat(Enumerable.Range(0, 8192).Select(_ =>
            BitConverter.Int32BitsToSingle(random.Next(int.MinValue, int.MaxValue)))).ToArray();
        byte[] source = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        foreach (int count in new[] { 0, 1, 2, 7, 15, 16, 17, 31, 32, 33, 1919, 1920, values.Length })
        {
            byte[] actual = Enumerable.Repeat((byte)0xCC, count * sizeof(short) + 16).ToArray();
            byte[] expected = actual.ToArray();
            bool active = ScalarPcm16(source, expected, count);
            Check(AndroidPhonePcmPlayer.ConvertPcm(source, actual, count) == active && actual.SequenceEqual(expected),
                $"PCM16 matches finite clipping, truncation, NaN/infinity silence and untouched tail ({count} samples)");
        }
        byte[] silence = new byte[1920 * sizeof(float)], silentOutput = new byte[1920 * sizeof(short)];
        Check(!AndroidPhonePcmPlayer.ConvertPcm(silence, silentOutput, 1920) && silentOutput.All(b => b == 0),
            "zero PCM stays silent without false activity");

        byte[] floats = MemoryMarshal.AsBytes(Enumerable.Range(0, 1920)
            .Select(i => MathF.Sin(i * 0.071f) * 0.9f).ToArray().AsSpan()).ToArray();
        byte[] pcm = new byte[1920 * sizeof(short)];
        const int blocks = 20000;
        metrics.Add(CompareStreamHotPath("pcm-float-to-s16-20ms", blocks,
            () => { for (int i = 0; i < blocks; i++) PreviousPcm16(floats, pcm, 1920); },
            () => { for (int i = 0; i < blocks; i++) AndroidPhonePcmPlayer.ConvertPcm(floats, pcm, 1920); }));
        byte[] reference = new byte[pcm.Length]; PreviousPcm16(floats, reference, 1920);
        Check(reference.SequenceEqual(pcm), "optimized real-range PCM conversion is byte-identical to the previous converter");
    }

    private static bool ScalarPcm16(byte[] floats, byte[] pcm, int count)
    {
        var source = MemoryMarshal.Cast<byte, float>(floats.AsSpan(0, count * sizeof(float)));
        var target = MemoryMarshal.Cast<byte, short>(pcm.AsSpan(0, count * sizeof(short)));
        bool active = false;
        for (int i = 0; i < count; i++)
        {
            target[i] = float.IsFinite(source[i])
                ? (short)(int)Math.Clamp(source[i] * 32768f, short.MinValue, short.MaxValue) : (short)0;
            active |= target[i] != 0;
        }
        return active;
    }

    // Frozen pre-change converter, benchmarked only with the post-gain [-1, 1] source range.
    private static bool PreviousPcm16(byte[] floats, byte[] pcm, int count)
    {
        var source = MemoryMarshal.Cast<byte, float>(floats.AsSpan(0, count * sizeof(float)));
        var target = MemoryMarshal.Cast<byte, short>(pcm.AsSpan(0, count * sizeof(short)));
        bool active = false;
        for (int i = 0; i < count; i++)
        {
            target[i] = float.IsFinite(source[i])
                ? (short)Math.Clamp((int)(source[i] * 32768f), short.MinValue, short.MaxValue) : (short)0;
            active |= target[i] != 0;
        }
        return active;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nint StreamComCall(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nint StreamComCallOther(nint self);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static nint StreamEntryA(nint self) => 11;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static nint StreamEntryB(nint self) => 22;

    private static unsafe void CheckComDelegateReuse(List<StreamMetric> metrics)
    {
        nint table = Marshal.AllocHGlobal(nint.Size), instance = Marshal.AllocHGlobal(nint.Size);
        try
        {
            Marshal.WriteIntPtr(instance, table);
            Marshal.WriteIntPtr(table, (nint)(delegate* unmanaged[Stdcall]<nint, nint>)&StreamEntryA);
            var first = ComVtbl.Get<StreamComCall>(instance, 0);
            Check(first(instance) == 11 && ReferenceEquals(first, ComVtbl.Get<StreamComCall>(instance, 0)),
                "COM hot path reuses the same typed delegate for a native entry point");
            Check(ComVtbl.Get<StreamComCallOther>(instance, 0)(instance) == 11,
                "the same entry point can be cached with distinct delegate types");
            Marshal.WriteIntPtr(table, (nint)(delegate* unmanaged[Stdcall]<nint, nint>)&StreamEntryB);
            Check(ComVtbl.Get<StreamComCall>(instance, 0)(instance) == 22,
                "a changed live vtable selects the new function instead of a stale slot cache");
            Marshal.WriteIntPtr(table, (nint)(delegate* unmanaged[Stdcall]<nint, nint>)&StreamEntryA);
            Parallel.For(0, 64, _ =>
            {
                if (!ReferenceEquals(first, ComVtbl.Get<StreamComCall>(instance, 0)))
                    throw new Exception("COM delegate cache disagreed across threads.");
            });
            Check(true, "concurrent COM delegate lookups return the published delegate");
            const int calls = 100000;
            metrics.Add(CompareStreamHotPath("COM-vtable-lookup-and-call", calls,
                () => { for (int i = 0; i < calls; i++) UncachedStreamCall(instance)(instance); },
                () => { for (int i = 0; i < calls; i++) ComVtbl.Get<StreamComCall>(instance, 0)(instance); }));
        }
        finally { Marshal.FreeHGlobal(instance); Marshal.FreeHGlobal(table); }
    }

    private static StreamComCall UncachedStreamCall(nint instance)
        => Marshal.GetDelegateForFunctionPointer<StreamComCall>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance)));

    [StructLayout(LayoutKind.Sequential)]
    private struct StreamMapped { public nint Data; public uint RowPitch, DepthPitch; }
    private static nint _streamMapData;
    private static int _streamMapCalls, _streamUnmapCalls, _streamMapFail;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int StreamMap(nint self, nint resource, uint subresource, int type, uint flags, StreamMapped* mapped)
    {
        _streamMapCalls++;
        *mapped = new StreamMapped { Data = _streamMapData, RowPitch = 32 };
        return _streamMapFail == 0 ? 0 : unchecked((int)0x80004005);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void StreamUnmap(nint self, nint resource, uint subresource) => _streamUnmapCalls++;

    private static unsafe void CheckShaderParameterReuse()
    {
        nint table = Marshal.AllocHGlobal(nint.Size * 16), instance = Marshal.AllocHGlobal(nint.Size);
        _streamMapData = Marshal.AllocHGlobal(32);
        var context = new ComPtr(instance);
        var constants = new ComPtr(1234);
        try
        {
            new Span<byte>((void*)table, nint.Size * 16).Clear();
            Marshal.WriteIntPtr(instance, table);
            Marshal.WriteIntPtr(table, 14 * nint.Size,
                (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, StreamMapped*, int>)&StreamMap);
            Marshal.WriteIntPtr(table, 15 * nint.Size,
                (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, void>)&StreamUnmap);
            var renderer = (D3D11VideoSurfaceRenderer)RuntimeHelpers.GetUninitializedObject(typeof(D3D11VideoSurfaceRenderer));
            typeof(D3D11VideoSurfaceRenderer).GetField("_context", StreamPrivate)!.SetValue(renderer, context);
            typeof(D3D11VideoSurfaceRenderer).GetField("_constantBuffer", StreamPrivate)!.SetValue(renderer, constants);
            var upload = typeof(D3D11VideoSurfaceRenderer).GetMethod("UpdateConstants", StreamPrivate)!
                .CreateDelegate<Action<float, float, float, float, int>>(renderer);
            _streamMapCalls = _streamUnmapCalls = _streamMapFail = 0;
            for (int i = 0; i < 1000; i++) upload(0, 0, 1, 1, 0);
            Check(_streamMapCalls == 1 && _streamUnmapCalls == 1,
                "1000 identical frame parameters require one constant-buffer upload, previously 1000");
            upload(0.1f, 0.2f, 0.8f, 0.7f, 1);
            Check(_streamMapCalls == 2 && Marshal.PtrToStructure<float>(_streamMapData) == 0.1f
                && Marshal.ReadInt32(_streamMapData, 16) == 1,
                "crop and color changes update the native shader buffer with the correct layout");
            _streamMapFail = 1; upload(0, 0, 1, 1, 2);
            _streamMapFail = 0; upload(0, 0, 1, 1, 2);
            Check(_streamMapCalls == 4 && _streamUnmapCalls == 3,
                "a failed Map is not cached as success and the next frame retries");
        }
        finally
        {
            context.Detach(); constants.Detach();
            Marshal.FreeHGlobal(_streamMapData); _streamMapData = 0;
            Marshal.FreeHGlobal(instance); Marshal.FreeHGlobal(table);
        }
    }

    private sealed class StartupCancelVideoStream(CancellationTokenSource? caller) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            caller?.Cancel();
            return ValueTask.FromException<int>(new OperationCanceledException(token));
        }
    }

    private static async Task CheckVideoStartupCleanupAsync(string output)
    {
        using var adb = new AdbClient(output);
        foreach (bool cancelCaller in new[] { false, true })
        {
            using var caller = new CancellationTokenSource();
            int releases = 0;
            using var stream = new StartupCancelVideoStream(cancelCaller ? caller : null);
            var transport = new DelegateVideoStreamTransport((_, _) => Task.FromResult(new VideoStreamSession(
                stream, null, Task.CompletedTask, null, () => { releases++; return ValueTask.CompletedTask; })));
            await using var client = new InProcessVirtualDisplayClient(adb, transport);
            Exception? error = null;
            try { await client.StartAsync(new AndroidDevice("offline-video", "Test", "device", false), AndroidApp.DeviceAudio, 0, 0, caller.Token); }
            catch (Exception ex) { error = ex; }
            Check(releases == 1 && (cancelCaller ? error is OperationCanceledException
                    : error is IOException && AndroidError.TryGetCode(error, out var code) && code == AndroidErrorCode.DisplayOpenFailed),
                $"video header cancellation releases the owned session before returning (caller={cancelCaller})");
        }
    }

    private sealed class StreamSwapPlayer(bool failDispose = false, bool failPlay = false) : IPcmPlayer
    {
        internal int Plays, Stops, Disposals;
        public void Play() { Plays++; if (failPlay) throw new IOException("new output play failure"); }
        public void Stop() => Stops++;
        public void Dispose() { Disposals++; if (failDispose) throw new IOException("retired output dispose failure"); }
    }

    private static async Task CheckOutputReplacementCleanupAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var first = new StreamSwapPlayer(failDispose: true);
        var second = new StreamSwapPlayer();
        var failed = new StreamSwapPlayer(failDispose: true, failPlay: true);
        var third = new StreamSwapPlayer();
        var players = new Queue<StreamSwapPlayer>([first, second, failed, third]);
        await using var session = new AndroidAudioSession(() => new FakeTransport(BuildWire(), () => { }), (_, _) => players.Dequeue());
        await session.StartAsync(new AndroidDevice("offline-audio", "Test", "device", false),
            new AndroidApp("cn.test.performance", "Test", 123, "media"), token: deadline.Token);
        var source = session.Pcm;
        await session.SetOutputAsync("second");
        Check(first.Disposals == 1 && second.Plays == 1 && session.State.OutputId == "second"
            && ReferenceEquals(source, session.Pcm) && session.State.State == CaptureState.Receiving,
            "old-player disposal failure retains the new active output without capture restart");
        Exception? error = null;
        try { await session.SetOutputAsync("failed"); } catch (Exception ex) { error = ex; }
        Check(error?.Message == "new output play failure" && failed.Disposals == 1 && second.Plays == 2
            && second.Disposals == 0 && session.State.OutputId == "second",
            "failed replacement cleanup still resumes the previous player and preserves the original failure");
        await session.SetOutputAsync("third");
        Check(second.Disposals == 1 && third.Plays == 1 && session.State.OutputId == "third",
            "a later switch retires the correct tracked player after the earlier cleanup failure");
        await session.StopAsync();
        Check(third.Disposals == 1 && session.Pcm == null, "session stop releases the final replacement player once");
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int StreamGetBuffer(nint self, uint index, in Guid iid, out nint buffer);

    private static async Task CheckRendererResizeRecoveryAsync()
    {
        await using var window = new NativeVideoWindow("Hidden stream performance regression", false, true, null, 0, 0, 240, 240);
        nint hwnd = await window.CreateAsync(default);
        using var renderer = new D3D11VideoSurfaceRenderer(hwnd, 240, 240);
        byte[] nv12 = new byte[64 * 64 * 3 / 2];
        nv12.AsSpan(0, 64 * 64).Fill(100); nv12.AsSpan(64 * 64).Fill(128);
        renderer.PresentSoftwareNv12(nv12, 64, 64, 0, 0, 64, 64, VideoColorMatrix.Bt601Limited);
        var swap = (ComPtr)typeof(D3D11VideoSurfaceRenderer).GetField("_swapChain", StreamPrivate)!.GetValue(renderer)!;
        Guid textureId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        ComVtbl.ThrowIfFailed(ComVtbl.Get<StreamGetBuffer>(swap.Handle, 9)(swap.Handle, 0, in textureId, out nint heldBuffer), "test GetBuffer");
        try
        {
            renderer.Resize(320, 260); // The held back-buffer reference deliberately makes DXGI reject this resize.
            var rtv = (ComPtr)typeof(D3D11VideoSurfaceRenderer).GetField("_rtv", StreamPrivate)!.GetValue(renderer)!;
            Check(renderer.SwapWidth == 240 && renderer.SwapHeight == 240 && !rtv.IsNull,
                "rejected DXGI resize restores the old render target and preserves actual buffer dimensions");
            renderer.PresentSoftwareNv12(nv12, 64, 64, 0, 0, 64, 64, VideoColorMatrix.Bt709Limited);
            renderer.RenderLastFrame();
            Check(renderer.HasPresentedFrame, "the next video frame remains drawable after a failed resize");
        }
        finally { Marshal.Release(heldBuffer); }
        renderer.Resize(320, 260);
        Check(renderer.SwapWidth == 320 && renderer.SwapHeight == 260, "resizing recovers once the temporary back-buffer reference is released");
        using (var decoder = new MediaFoundationH264Decoder(renderer.DeviceHandle, 64, 64))
        {
            var (config, idr) = BuildTestBaselineH264_64x64();
            var merger = new ScrcpyPacketMerger(); merger.Process(config, true);
            int hardwareFrames = 0, softwareFrames = 0;
            void Hardware(nint texture, uint slice, int width, int height, int x, int y, int cropW, int cropH, VideoColorMatrix matrix)
            {
                renderer.PresentHardwareTexture(texture, slice, width, height, x, y, cropW, cropH, matrix);
                hardwareFrames++;
            }
            void Software(ReadOnlySpan<byte> frame, int stride, int height, int x, int y, int cropW, int cropH, VideoColorMatrix matrix)
            {
                renderer.PresentSoftwareNv12(frame, stride, height, x, y, cropW, cropH, matrix);
                softwareFrames++;
            }
            // This driver buffers a single hardware-decoded input even when drained;
            // the preserved pre-change DLL behaves the same. Use a multi-frame stream
            // to exercise presentation, rather than require software-only single-frame timing.
            byte[] firstPacket = merger.Process(idr, false).ToArray();
            for (int i = 0; i < 4; i++)
                decoder.DecodePacket(i == 0 ? firstPacket : idr, i * 33333L, Hardware, Software);
            if (hardwareFrames + softwareFrames == 0) decoder.Drain(Hardware, Software);
            Check(hardwareFrames + softwareFrames > 0 && renderer.HasPresentedFrame,
                $"device-bound H.264 decode presents a synthetic frame (hardware={hardwareFrames}, software={softwareFrames})");
        }
        renderer.Dispose(); renderer.RenderLastFrame();
        Check(!IsWindowVisibleForStreamTest(hwnd), "native video checks leave the owned window hidden throughout");
    }

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    private static extern bool IsWindowVisibleForStreamTest(nint hwnd);

    private static async Task StreamVideoProbeAsync(string output)
    {
        Directory.CreateDirectory(output);
        var cases = new List<object>();
        await using var window = new NativeVideoWindow("Hidden decode diagnostic", false, true, null, 0, 0, 240, 240);
        using var renderer = new D3D11VideoSurfaceRenderer(await window.CreateAsync(default), 240, 240);
        foreach (bool bindDevice in new[] { false, true })
        foreach (int inputs in new[] { 1, 4 })
        {
            using var decoder = new MediaFoundationH264Decoder(bindDevice ? renderer.DeviceHandle : 0, 64, 64);
            var (config, idr) = BuildTestBaselineH264_64x64();
            byte[] packet = config.Concat(idr).ToArray();
            int hardware = 0, software = 0, outputs = 0;
            void Hardware(nint tex, uint slice, int w, int h, int x, int y, int cw, int ch, VideoColorMatrix m) => hardware++;
            void Software(ReadOnlySpan<byte> nv12, int stride, int h, int x, int y, int cw, int ch, VideoColorMatrix m) => software++;
            for (int i = 0; i < inputs; i++) outputs += decoder.DecodePacket(packet, i * 33333L, Hardware, Software);
            outputs += decoder.Drain(Hardware, Software);
            var item = new { bindDevice, inputs, outputs, hardware, software, decoder.IsHardwareAccelerated, decoder.Width, decoder.Height };
            cases.Add(item); Console.WriteLine(JsonSerializer.Serialize(item));
        }
        await File.WriteAllTextAsync(Path.Combine(output, "video-probe.json"), JsonSerializer.Serialize(cases, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static StreamMetric CompareStreamHotPath(string name, int operations, Action before, Action after)
    {
        before(); after(); // Warm the JIT and typed delegate cache before measuring.
        var previous = new StreamTrial[5]; var current = new StreamTrial[5];
        for (int i = 0; i < 5; i++)
        {
            if ((i & 1) == 0) { previous[i] = Measure(before); current[i] = Measure(after); }
            else { current[i] = Measure(after); previous[i] = Measure(before); }
        }
        Console.WriteLine($"METRIC {name}: before {previous.Select(t => t.Milliseconds).Order().ElementAt(2):F3} ms / "
            + $"{previous.Select(t => t.AllocatedBytes).Order().ElementAt(2)} B; after {current.Select(t => t.Milliseconds).Order().ElementAt(2):F3} ms / "
            + $"{current.Select(t => t.AllocatedBytes).Order().ElementAt(2)} B ({operations} operations, median of 5).");
        return new StreamMetric(name, operations, previous, current);

        static StreamTrial Measure(Action work)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp();
            work();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            return new StreamTrial(Stopwatch.GetElapsedTime(started).TotalMilliseconds, bytes);
        }
    }
}
