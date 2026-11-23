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
    private sealed record StreamTrial(double Milliseconds, long AllocatedBytes, int Gen0 = 0, int Gen1 = 0, int Gen2 = 0);
    private sealed record StreamMetric(string Name, int Operations, StreamTrial[] Before, StreamTrial[] After);
    private sealed record DecodeBenchmarkSummary(
        string Name,
        int Operations,
        double BeforeMedianMs,
        double AfterMedianMs,
        double BeforeAvgFrameUs,
        double AfterAvgFrameUs,
        long BeforeAllocatedBytes,
        long AfterAllocatedBytes,
        int BeforeGen0,
        int AfterGen0,
        int BeforeGen1,
        int AfterGen1,
        int BeforeGen2,
        int AfterGen2,
        double BeforeFirstFrameMs,
        double AfterFirstFrameMs,
        int PoolSlots,
        int PoolNativeBytes,
        bool HardwareAccelerated,
        int OutputFramesPerTrial);

    private static async Task StreamPerformanceAsync(string output, bool pcmOnly)
    {
        Directory.CreateDirectory(output);
        var metrics = new List<StreamMetric>();
        var decodeSummaries = new List<DecodeBenchmarkSummary>();
        CheckPcm16Conversion(metrics);
        if (!pcmOnly)
        {
            CheckComDelegateReuse(metrics);
            CheckShaderParameterReuse();
            await CheckVideoStartupCleanupAsync(output);
            await CheckOutputReplacementCleanupAsync();
            await CheckRendererResizeRecoveryAsync();
            CheckMediaFoundationH264Decode();
            CheckScrcpyProtocolAndSyncFrameThrottling();
            await CheckInputSamplePoolSafetyAndLifecycleAsync();
            await BenchmarkMediaFoundationDecodeHotPathsAsync(metrics, decodeSummaries);
            Check(MediaFoundationH264Decoder.ActiveInstanceCount == 0 && D3D11VideoSurfaceRenderer.ActiveInstanceCount == 0,
                "native decoder and renderer instance counts return to zero");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        {
            Success = true, Assertions = _checks, PcmOnly = pcmOnly, Vector.IsHardwareAccelerated,
            VectorFloatLanes = Vector<float>.Count, Metrics = metrics, DecodeBenchmarks = decodeSummaries,
            RealPhoneAccess = false, RealAudioPlayback = false, SharedAdbAccess = false,
            NativeVideo = pcmOnly ? "not used" : "hidden owned window, D3D11 resize failure/recovery and synthetic H.264/HEVC",
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
            if (hardwareFrames > 0)
            {
                bool Empty(params string[] names) => names.All(name =>
                    ((ComPtr)typeof(D3D11VideoSurfaceRenderer).GetField(name, StreamPrivate)!.GetValue(renderer)!).IsNull);
                Check(Empty("_swYTex", "_swUvTex", "_swYSrv", "_swUvSrv"),
                    "hardware presentation releases the previous software textures and views");
                renderer.PresentSoftwareNv12(nv12, 64, 64, 0, 0, 64, 64, VideoColorMatrix.Bt601Limited);
                Check(Empty("_hwNv12Tex", "_hwYSrv", "_hwUvSrv"),
                    "software fallback releases the previous hardware staging texture and views");
                // Resume hardware rendering while the decoder remains alive; staging is
                // recreated on demand and the two modes can alternate without losing frames.
                int framesBeforeReturn = hardwareFrames;
                decoder.DecodePacket(idr, 5 * 33333L, Hardware, Software);
                Check(hardwareFrames > framesBeforeReturn && Empty("_swYTex", "_swUvTex", "_swYSrv", "_swUvSrv"),
                    "returning to hardware recreates only its active textures and continues presenting");
            }
        }
        renderer.Dispose(); renderer.RenderLastFrame();
        Check(!IsWindowVisibleForStreamTest(hwnd), "native video checks leave the owned window hidden throughout");
    }

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    private static extern bool IsWindowVisibleForStreamTest(nint hwnd);

    private static async Task DisplayMemoryAsync(string output)
    {
        Directory.CreateDirectory(output);
        await CheckRendererResizeRecoveryAsync();
        Check(D3D11VideoSurfaceRenderer.ActiveInstanceCount == 0 && MediaFoundationH264Decoder.ActiveInstanceCount == 0,
            "display memory checks release every owned decoder and renderer");
        await File.WriteAllTextAsync(Path.Combine(output, "display-memory.json"), JsonSerializer.Serialize(new
        {
            Success = true, Assertions = _checks, WindowsShown = false, PhoneAccess = false,
            Scope = "Real hidden D3D11/MFT frames, resize recovery and software/hardware texture retirement; GPU bytes not measured."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {_checks} display memory assertions; owned hidden window only.");
    }

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
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long allocated = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp();
            work();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return new StreamTrial(
                ms,
                bytes,
                GC.CollectionCount(0) - g0,
                GC.CollectionCount(1) - g1,
                GC.CollectionCount(2) - g2);
        }
    }

    private static (byte[] ConfigNal, byte[] IdrNal) BuildTestBaselineH264(int width, int height, byte yFill = 0x66)
    {
        if (width <= 0 || height <= 0 || (width & 15) != 0 || (height & 15) != 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Dimensions must be positive multiples of 16.");

        int mbW = width / 16;
        int mbH = height / 16;
        uint levelIdc = mbW * mbH > 1620 ? 40u : 30u;

        var sps = new H264BitWriter();
        sps.WriteByte(0x67);
        sps.WriteBits(66, 8);
        sps.WriteBits(0xC0, 8);
        sps.WriteBits(levelIdc, 8);
        sps.WriteUe(0);
        sps.WriteUe(0);
        sps.WriteUe(2);
        sps.WriteUe(1);
        sps.WriteBit(0);
        sps.WriteUe((uint)(mbW - 1));
        sps.WriteUe((uint)(mbH - 1));
        sps.WriteBit(1);
        sps.WriteBit(0);
        sps.WriteBit(0);
        sps.WriteBit(0);
        sps.WriteRbspTrailingBits();

        var pps = new H264BitWriter();
        pps.WriteByte(0x68);
        pps.WriteUe(0);
        pps.WriteUe(0);
        pps.WriteBit(0);
        pps.WriteBit(0);
        pps.WriteUe(0);
        pps.WriteUe(0);
        pps.WriteUe(0);
        pps.WriteBit(0);
        pps.WriteBits(0, 2);
        pps.WriteSe(0);
        pps.WriteSe(0);
        pps.WriteSe(0);
        pps.WriteBit(0);
        pps.WriteBit(0);
        pps.WriteBit(0);
        pps.WriteRbspTrailingBits();

        byte[] spsBytes = sps.ToAnnexB();
        byte[] ppsBytes = pps.ToAnnexB();
        byte[] configNal = new byte[spsBytes.Length + ppsBytes.Length];
        spsBytes.CopyTo(configNal, 0);
        ppsBytes.CopyTo(configNal, spsBytes.Length);

        var idr = new H264BitWriter();
        idr.WriteByte(0x65);
        idr.WriteUe(0);
        idr.WriteUe(7);
        idr.WriteUe(0);
        idr.WriteBits(0, 4);
        idr.WriteUe(0);
        idr.WriteBit(0);
        idr.WriteBit(0);
        idr.WriteSe(0);
        int mbCount = mbW * mbH;
        for (int mb = 0; mb < mbCount; mb++)
        {
            idr.WriteUe(25);
            idr.AlignToByteZero();
            for (int y = 0; y < 256; y++) idr.WriteByteRaw(yFill);
            for (int c = 0; c < 128; c++) idr.WriteByteRaw(0x80);
        }
        idr.WriteRbspTrailingBits();

        return (configNal, idr.ToAnnexB());
    }

    private static (byte[] ConfigNal, byte[] IdrNal) GetSyntheticHevcPackets() => (
        Convert.FromBase64String("AAAAAUABDAH//wFgAAADAJAAAAMAAAMAPLoCQAAAAAFCAQEBYAAAAwCQAAADAAADADygGCAUFlukpMLmgIAAAAMAgAAADwQAAAABRAHAcYMS"),
        Convert.FromBase64String(
            "AAAAAU4BBf///////////xMsot4JtRdH27tVpP5/wvxOeDI2NSAoYnVpbGQgMjE1KSAtIDQuMSsxLTFkMTE3YmU6W1dpbmRvd3NdW0dDQyAxNS4yLjBdWzY0IGJpdF0gOGJpdCsxMGJpdCsxMmJpdCAtIEguMjY1L0hFVkMgY29kZWMgLSBDb3B5cmlnaHQgMjAxMy0yMDE4IChjKSBNdWx0aWNvcmV3YXJlLCBJbmMgLSBodHRwOi8veDI2NS5vcmcgLSBvcHRpb25zOiBjcHVpZD0xMTExMDM5IGZyYW1lLXRocmVhZHM9MSBudW1hLXBvb2xzPTEgd3BwIG5vLXBtb2RlIG5vLXBtZSBuby1wc25yIG5vLXNzaW0gbG9nLWxldmVsPTAgYml0ZGVwdGg9OCBpbnB1dC1jc3A9MSBmcHM9MzAvMSBpbnB1dC1yZXM9MTkyeDMyMCBpbnRlcmxhY2U9MCB0b3RhbC1mcmFtZXM9MCBsZXZlbC1pZGM9MCBoaWdoLXRpZXI9MSB1aGQtYmQ9MCByZWY9MSBuby1hbGxvdy1ub24tY29uZm9ybWFuY2UgcmVwZWF0LWhlYWRlcnMgYW5uZXhiIG5vLWF1ZCBuby1lb2Igbm8tZW9zIG5vLWhyZCBpbmZvIGhhc2g9MCB0ZW1wb3JhbC1sYXllcnM9MCBvcGVuLWdvcCBtaW4ta2V5aW50PTI1IGtleWludD0yNTAgZ29wLWxvb2thaGVhZD0wIGJmcmFtZXM9MCBiLWFkYXB0PTAgbm8tYi1weXJhbWlkIGJmcmFtZS1iaWFzPTAgcmMtbG9va2FoZWFkPTAgbG9va2FoZWFkLXNsaWNlcz0wIHNjZW5lY3V0PTAgbm8taGlzdC1zY2VuZWN1dCByYWRsPTAgbm8tc3BsaWNlIG5vLWludHJhLXJlZnJlc2ggY3R1PTMyIG1pbi1jdS1zaXplPTE2IG5vLXJlY3Qgbm8tYW1wIG1heC10dS1zaXplPTMyIHR1LWludGVyLWRlcHRoPTEgdHUtaW50cmEtZGVwdGg9MSBsaW1pdC10dT0wIHJkb3EtbGV2ZWw9MCBkeW5hbWljLXJkPTAuMDAgbm8tc3NpbS1yZCBuby1zaWduaGlkZSBuby10c2tpcCBuci1pbnRyYT0wIG5yLWludGVyPTAgbm8tY29uc3RyYWluZWQtaW50cmEgc3Ryb25nLWludHJhLXNtb290aGluZyBtYXgtbWVyZ2U9MiBsaW1pdC1yZWZzPTAgbm8tbGltaXQtbW9kZXMgbWU9MCBzdWJtZT0wIG1lcmFuZ2U9NTcgdGVtcG9yYWwtbXZwIG5vLWZyYW1lLWR1cCBuby1obWUgbm8td2VpZ2h0cCBuby13ZWlnaHRiIG5vLWFuYWx5emUtc3JjLXBpY3MgZGVibG9jaz0wOjAgbm8tc2FvIG5vLXNhby1ub24tZGVibG9jayByZD0yIHNlbGVjdGl2ZS1zYW89MCBlYXJseS1za2lwIHJza2lwIGZhc3QtaW50cmEgbm8tdHNraXAtZmFzdCBuby1jdS1sb3NzbGVzcyBuby1iLWludHJhIG5vLXNwbGl0cmQtc2tpcCByZHBlbmFsdHk9MCBwc3ktcmQ9Mi4wMCBwc3ktcmRvcT0wLjAwIG5vLXJkLXJlZmluZSBuby1sb3NzbGVzcyBjYnFwb2Zmcz0wIGNycXBvZmZzPTAgcmM9Y3JmIGNyZj0yOC4wIHFjb21wPTAuNjAgcXBzdGVwPTQgc3RhdHMtd3JpdGU9MCBzdGF0cy1yZWFkPTAgaXByYXRpbz0xLjQwIGFxLW1vZGU9MCBhcS1zdHJlbmd0aD0wLjAwIG5vLXJjLWdyYWluIHFwbWF4PTY5IHFwbWluPTAgbm8tY29uc3QtdmJ2IHNhcj0wIG92ZXJzY2FuPTAgdmlkZW9mb3JtYXQ9NSByYW5nZT0wIGNvbG9ycHJpbT0yIHRyYW5zZmVyPTIgY29sb3JtYXRyaXg9MiBjaHJvbWFsb2M9MCBkaXNwbGF5LXdpbmRvdz0wIGNsbD0wLDAgbWluLWx1bWE9MCBtYXgtbHVtYT0yNTUgbG9nMi1tYXgtcG9jLWxzYj04IHZ1aS10aW1pbmctaW5mbyB2dWktaHJkLWluZm8gc2xpY2VzPTEgbm8tb3B0LXFwLXBwcyBuby1vcHQtcmVmLWxpc3QtbGVuZ3RoLXBwcyBuby1tdWx0aS1wYXNzLW9wdC1ycHMgc2NlbmVjdXQtYmlhcz0wLjA1IG5vLW9wdC1jdS1kZWx0YS1xcCBuby1hcS1tb3Rpb24gbm8taGRyMTAgbm8taGRyMTAtb3B0IG5vLWRoZHIxMC1vcHQgbm8taWRyLXJlY292ZXJ5LXNlaSBhbmFseXNpcy1yZXVzZS1sZXZlbD0wIGFuYWx5c2lzLXNhdmUtcmV1c2UtbGV2ZWw9MCBhbmFseXNpcy1sb2FkLXJldXNlLWxldmVsPTAgc2NhbGUtZmFjdG9yPTAgcmVmaW5lLWludHJhPTAgcmVmaW5lLWludGVyPTAgcmVmaW5lLW12PTEgcmVmaW5lLWN0dS1kaXN0b3J0aW9uPTAgbm8tbGltaXQtc2FvIGN0dS1pbmZvPTAgbm8tbG93cGFzcy1kY3QgcmVmaW5lLWFuYWx5c2lzLXR5cGU9MCBjb3B5LXBpYz0xIG1heC1hdXNpemUtZmFjdG9yPTEuMCBuby1keW5hbWljLXJlZmluZSBuby1zaW5nbGUtc2VpIG5vLWZyYW1lLXJjIG5vLWhldmMtYXEgbm8tc3Z0IG5vLWZpZWxkIHFwLWFkYXB0YXRpb24tcmFuZ2U9MS4wMCBzY2VuZWN1dC1hd2FyZS1xcD0wY29uZm9ybWFuY2Utd2luZG93LW9mZnNldHMgcmlnaHQ9MCBib3R0b209MCBkZWNvZGVyLW1heC1yYXRlPTAgbm8tdmJ2LWxpdmUtbXVsdGktcGFzcyBuby1tY3N0ZiBuby1zYnJjIG5vLWZyYW1lLXJjgAAAAAEoAa3FE5VRDMzgx6f/lXH/z6qWf+PvDyVpEsRuvXdkBydcJG6ACWP25DANTjCugA7gA4YAABTwAAAwIAAAq4AAAeU="));

    private static nint _corruptStubSample;
    private static int _corruptStubEmitted;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int StubMftProcessMessage(nint self, uint msg, nint param) => 0;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int StubMftProcessInput(nint self, uint streamId, nint sample, uint flags) => 0;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int StubMftProcessOutput(nint self, uint flags, uint count, nint outputBufPtr, uint* status)
    {
        if (_corruptStubEmitted > 0) return unchecked((int)0xC00D6D72); // MF_E_TRANSFORM_NEED_MORE_INPUT
        _corruptStubEmitted++;
        Marshal.AddRef(_corruptStubSample);
        // MftOutputDataBuffer: dwStreamID (4 + pad 4 on x64), pSample (8), dwStatus (4 + pad 4), pEvents (8)
        Marshal.WriteIntPtr(outputBufPtr, 8, _corruptStubSample);
        Marshal.WriteInt32(outputBufPtr, 16, 0);
        Marshal.WriteIntPtr(outputBufPtr, 24, 0);
        *status = 0;
        return 0;
    }

    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out nint ppIMFSample);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint cbMaxLength, out nint ppBuffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TestAttrSetUint32Fn(nint self, in Guid key, uint val);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TestAttrGetUint32Fn(nint self, in Guid key, out uint val);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TestBufferLockFn(nint self, out nint ptr, out uint maxLen, out uint curLen);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TestBufferUnlockFn(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TestBufferSetLenFn(nint self, uint len);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TestSampleSetTimeFn(nint self, long hns);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TestSampleAddBufferFn(nint self, nint buf);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TestSampleGetBufFn(nint self, uint index, out nint buf);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TestProcessMessageFn(nint self, uint msg, nint param);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TestProcessInputFn(nint self, uint streamId, nint sample, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TestProcessOutputFn(nint self, uint flags, uint count, nint outputSamples, out uint status);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TestDxgiBufGetResourceFn(nint self, in Guid riid, out nint ppv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TestDxgiBufGetSubresourceFn(nint self, out uint index);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TestD3D11TexGetDescFn(nint self, out TestD3D11TexDesc desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TestLock2DFn(nint self, out nint scan0, out int pitch);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TestUnlock2DFn(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TestGetMaxLenFn(nint self, out uint maxLen);

    [StructLayout(LayoutKind.Sequential)]
    private struct TestD3D11TexDesc
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CPUAccessFlags, MiscFlags;
    }

    private static unsafe void CheckScrcpyProtocolAndSyncFrameThrottling()
    {
        // 1. TypeRequestSyncFrame (20) serialization
        Span<byte> cmd = stackalloc byte[1];
        int written = ScrcpyControlMessageWriter.WriteRequestSyncFrame(cmd);
        Check(ScrcpyControlMessageWriter.TypeRequestSyncFrame == 20 && written == 1 && cmd[0] == 20,
            "ScrcpyControlMessageWriter writes 1-byte TypeRequestSyncFrame (20)");
        bool emptyRejected = false;
        try { ScrcpyControlMessageWriter.WriteRequestSyncFrame(Span<byte>.Empty); }
        catch (ArgumentException) { emptyRejected = true; }
        Check(emptyRejected, "WriteRequestSyncFrame rejects empty destination spans");

        // 2. ScrcpyPacketMerger.TryConsumePendingConfig + 2-span DecodePacket equivalence
        var (cfg, idr) = BuildTestBaselineH264(64, 64);
        var merger = new ScrcpyPacketMerger();
        Check(!merger.TryConsumePendingConfig(out byte[]? emptyCfg) && emptyCfg == null,
            "TryConsumePendingConfig returns false when no config is pending");
        Check(merger.Process(cfg, isConfig: true).IsEmpty && merger.HasPendingConfig,
            "Process(config) caches pending config for zero-copy consumption");
        Check(merger.TryConsumePendingConfig(out byte[]? consumedCfg)
            && consumedCfg != null && consumedCfg.SequenceEqual(cfg)
            && !merger.HasPendingConfig
            && !merger.TryConsumePendingConfig(out _),
            "TryConsumePendingConfig returns config once and clears pending state");
        merger.Process(cfg, isConfig: true);
        merger.Reset();
        Check(!merger.TryConsumePendingConfig(out _), "ScrcpyPacketMerger.Reset clears pending config");

        byte[] singleSpanOut = Array.Empty<byte>();
        byte[] twoSpanOut = Array.Empty<byte>();
        using (var d1 = new MediaFoundationH264Decoder(0, 64, 64))
        {
            byte[] merged = cfg.Concat(idr).ToArray();
            d1.DecodePacket(merged, 0, null, (nv12, _, _, _, _, _, _, _) => singleSpanOut = nv12.ToArray());
            if (singleSpanOut.Length == 0)
                d1.Drain(null, (nv12, _, _, _, _, _, _, _) => singleSpanOut = nv12.ToArray());
        }
        using (var d2 = new MediaFoundationH264Decoder(0, 64, 64))
        {
            d2.DecodePacket(cfg, idr, 0, null, (nv12, _, _, _, _, _, _, _) => twoSpanOut = nv12.ToArray());
            if (twoSpanOut.Length == 0)
                d2.Drain(null, (nv12, _, _, _, _, _, _, _) => twoSpanOut = nv12.ToArray());
        }
        Check(singleSpanOut.Length > 0 && singleSpanOut.SequenceEqual(twoSpanOut),
            "2-span DecodePacket(prefix, payload) produces byte-identical NV12 output without intermediate copy");

        // 3. CorruptFrameSkipped + thread-safe >= 500ms throttled TypeRequestSyncFrame delivery
        using var adb = new AdbClient(AppContext.BaseDirectory);
        var client = new InProcessVirtualDisplayClient(adb);
        var queue = new InProcessVirtualDisplayClient.ControlQueue();
        typeof(InProcessVirtualDisplayClient).GetField("_controlOutbound", StreamPrivate)!.SetValue(client, queue);

        using var outStream = new MemoryStream();
        Task sendTask = queue.SendAsync(outStream, _ => true, default);

        nint stubVtbl = Marshal.AllocHGlobal(nint.Size * 28);
        nint stubInst = Marshal.AllocHGlobal(nint.Size);
        new Span<byte>((void*)stubVtbl, nint.Size * 28).Clear();
        Marshal.WriteIntPtr(stubInst, stubVtbl);
        Marshal.WriteIntPtr(stubVtbl, 23 * nint.Size, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, int>)&StubMftProcessMessage);
        Marshal.WriteIntPtr(stubVtbl, 24 * nint.Size, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)&StubMftProcessInput);
        Marshal.WriteIntPtr(stubVtbl, 25 * nint.Size, (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, nint, uint*, int>)&StubMftProcessOutput);

        using var realDecoder = new MediaFoundationH264Decoder(0, 64, 64)
        {
            SkipCorruptFrames = true,
            CorruptFrameSkipped = () => client.TryRequestSyncFrame()
        };
        var mftField = typeof(MediaFoundationH264Decoder).GetField("_mft", StreamPrivate)!;
        var origMft = (ComPtr)mftField.GetValue(realDecoder)!;
        var providesField = typeof(MediaFoundationH264Decoder).GetField("_mftProvidesOutputSamples", StreamPrivate)!;
        bool origProvides = (bool)providesField.GetValue(realDecoder)!;

        ComVtbl.ThrowIfFailed(MFCreateSample(out _corruptStubSample), "test MFCreateSample(corrupt)");
        Guid corruptKey = new("b4dd4a8c-0beb-44c4-8b75-b02b913b04f0");
        ComVtbl.ThrowIfFailed(ComVtbl.Get<TestAttrSetUint32Fn>(_corruptStubSample, 21)(_corruptStubSample, in corruptKey, 1), "set corrupt attr");
        _corruptStubEmitted = 0;

        try
        {
            mftField.SetValue(realDecoder, new ComPtr(stubInst));
            providesField.SetValue(realDecoder, true);

            int decodedFrames = realDecoder.DecodePacket(idr, 0, null, (_, _, _, _, _, _, _, _) => { });
            Check(decodedFrames == 0 && realDecoder.CorruptFramesSkipped == 1,
                "SkipCorruptFrames drops MFSampleExtension_FrameCorruption frames and increments CorruptFramesSkipped");

            // Parallel bursts within < 500ms must be throttled to the single request already emitted above.
            int extraSent = 0;
            Parallel.For(0, 32, _ =>
            {
                if (client.TryRequestSyncFrame()) Interlocked.Increment(ref extraSent);
            });
            Check(extraSent == 0, "concurrent CorruptFrameSkipped bursts within 500ms are throttled");

            // Simulate >= 500ms elapsed -> second request allowed.
            bool afterCooldown = client.TryRequestSyncFrame(Stopwatch.GetTimestamp() + Stopwatch.Frequency);
            Check(afterCooldown, "TypeRequestSyncFrame is permitted again once >= 500ms elapses");
        }
        finally
        {
            mftField.SetValue(realDecoder, origMft);
            providesField.SetValue(realDecoder, origProvides);
            Marshal.Release(_corruptStubSample);
            _corruptStubSample = 0;
            Marshal.FreeHGlobal(stubInst);
            Marshal.FreeHGlobal(stubVtbl);
        }

        queue.Close();
        sendTask.GetAwaiter().GetResult();
        byte[] sent = outStream.ToArray();
        Check(sent.SequenceEqual(new byte[] { 20, 20 }),
            "InProcessVirtualDisplayClient enqueues 1-byte TypeRequestSyncFrame (20) over ControlQueue with >= 500ms rate limit");
    }

    private static async Task CheckInputSamplePoolSafetyAndLifecycleAsync()
    {
        static byte[] ReadNativeBufferBytes(nint bufferPtr, int length)
        {
            byte[] copy = new byte[length];
            ComVtbl.ThrowIfFailed(ComVtbl.Get<TestBufferLockFn>(bufferPtr, 3)(bufferPtr, out nint ptr, out _, out _), "test Buffer.Lock");
            try { Marshal.Copy(ptr, copy, 0, length); }
            finally { ComVtbl.Get<TestBufferUnlockFn>(bufferPtr, 4)(bufferPtr); }
            return copy;
        }

        var (cfg64, idr64A) = BuildTestBaselineH264(64, 64, 0x55);
        var (_, idr64B) = BuildTestBaselineH264(64, 64, 0x99);
        var (cfg128, idr128) = BuildTestBaselineH264(128, 128, 0x77);
        var (cfg640, idr640) = BuildTestBaselineH264(640, 640, 0x60);
        Guid discontinuityKey = new("9cdf01d9-a0f0-43ba-b077-eaa06cbd728a");

        await using var window = new NativeVideoWindow("Hidden pool safety check", false, true, null, 0, 0, 240, 240);
        using var renderer = new D3D11VideoSurfaceRenderer(await window.CreateAsync(default), 240, 240);

        // 1. COM refcount protection (IMFSample AddRef, IMFMediaBuffer-only AddRef, full-busy transient, >512KiB transient, discontinuity attr clear)
        using (var decoder = new MediaFoundationH264Decoder(0, 64, 64))
        {
            decoder.DecodePacket(cfg64, idr64A, 0, null, null);
            bool gotSlot0 = decoder.TryGetInputSamplePoolSlotForTests(0, out nint s0, out nint b0, out int cap0, out _);
            Check(decoder.InputSamplePoolSlotCount == 1
                && gotSlot0
                && cap0 <= MediaFoundationH264Decoder.MaxPooledInputBufferBytes,
                "first small frame populates 1 bounded input sample pool slot");

            byte[] slot0Snapshot = ReadNativeBufferBytes(b0, cfg64.Length + idr64A.Length);
            Marshal.AddRef(s0); // Simulate MFT retaining IMFSample after ProcessInput
            try
            {
                decoder.DecodePacket(idr64B, 33333L, null, null);
                byte[] slot0After = ReadNativeBufferBytes(b0, slot0Snapshot.Length);
                Check(decoder.InputSamplePoolBusySkips >= 1
                    && decoder.InputSamplePoolSlotCount == 2
                    && slot0After.SequenceEqual(slot0Snapshot),
                    "external AddRef on IMFSample prevents overwriting pooled buffer and allocates slot 1");
            }
            finally { Marshal.Release(s0); }

            Check(decoder.TryGetInputSamplePoolSlotForTests(1, out nint s1, out nint b1, out _, out _),
                "slot 1 is inspectable in the pool");
            byte[] slot1Snapshot = ReadNativeBufferBytes(b1, idr64B.Length);
            Marshal.AddRef(s0); // Keep slot 0 busy
            Marshal.AddRef(b1); // Hold ONLY IMFMediaBuffer ref on slot 1 (s1 refcount == 1, b1 refcount == 3)
            try
            {
                decoder.DecodePacket(idr64A, 66666L, null, null);
                byte[] slot1After = ReadNativeBufferBytes(b1, slot1Snapshot.Length);
                Check(decoder.InputSamplePoolSlotCount == 3 && slot1After.SequenceEqual(slot1Snapshot),
                    "external AddRef on IMFMediaBuffer alone (while IMFSample refcount==1) still blocks reuse");
            }
            finally
            {
                Marshal.Release(b1);
                Marshal.Release(s0);
            }

            long hitsBeforeRelease = decoder.InputSamplePoolHits;
            decoder.DecodePacket(idr64B, 99999L, null, null);
            Check(decoder.InputSamplePoolHits == hitsBeforeRelease + 1 && decoder.InputSamplePoolSlotCount == 3,
                "releasing held COM references allows slot 0 to be safely reused");

            // Fill 4th slot and hold all 4 slots busy simultaneously -> 5th frame takes transient path
            Marshal.AddRef(s0);
            Marshal.AddRef(s1);
            decoder.TryGetInputSamplePoolSlotForTests(2, out nint s2, out _, out _, out _);
            Marshal.AddRef(s2);
            try
            {
                decoder.DecodePacket(idr64A, 133333L, null, null); // allocates slot 3
                bool gotSlot3 = decoder.TryGetInputSamplePoolSlotForTests(3, out nint s3, out _, out _, out _);
                Check(decoder.InputSamplePoolSlotCount == 4 && gotSlot3,
                    "pool fills up to MaxPooledInputSamples (4) when 3 existing slots are held");
                Marshal.AddRef(s3);
                try
                {
                    long transientBefore = decoder.InputSampleTransientAllocations;
                    decoder.DecodePacket(idr64B, 166666L, null, null);
                    Check(decoder.InputSamplePoolSlotCount == 4
                        && decoder.InputSampleTransientAllocations == transientBefore + 1
                        && decoder.InputSamplePoolTotalBytes <= MediaFoundationH264Decoder.MaxTotalPooledInputBytes,
                        "when all 4 pool slots are held by MFT, extra frame uses one-shot transient sample within 2 MiB cap");
                }
                finally { Marshal.Release(s3); }
            }
            finally
            {
                Marshal.Release(s2);
                Marshal.Release(s1);
                Marshal.Release(s0);
            }

            // > 512 KiB oversized keyframe must never stay in the pool
            int oversizedBytes = cfg640.Length + idr640.Length;
            Check(oversizedBytes > MediaFoundationH264Decoder.MaxPooledInputBufferBytes,
                $"640x640 synthetic I_PCM keyframe ({oversizedBytes} B) exceeds 512 KiB pool threshold");
            long transientBeforeLarge = decoder.InputSampleTransientAllocations;
            decoder.DecodePacket(cfg640, idr640, 200000L, null, null);
            decoder.Drain(null, null);
            Check(decoder.Width == 640 && decoder.Height == 640
                && decoder.InputSampleTransientAllocations == transientBeforeLarge + 1
                && decoder.InputSamplePoolSlotCount <= MediaFoundationH264Decoder.MaxPooledInputSamples
                && decoder.InputSamplePoolTotalBytes <= MediaFoundationH264Decoder.MaxTotalPooledInputBytes,
                "oversized >512 KiB keyframe decodes via transient path without inflating resident native pool");

            // Discontinuity Flush releases old pool, sets attribute, and next normal reuse clears it via AttrDeleteItemFn (slot 19)
            decoder.DecodePacket(cfg64, idr64A, 233333L, null, null, discontinuity: true);
            bool gotDisc = decoder.TryGetInputSamplePoolSlotForTests(0, out nint discSample, out _, out _, out bool hasDisc);
            Check(decoder.InputSamplePoolSlotCount == 1
                && gotDisc
                && hasDisc
                && ComVtbl.Get<TestAttrGetUint32Fn>(discSample, 7)(discSample, in discontinuityKey, out uint discVal) >= 0
                && discVal == 1,
                "discontinuity Flush resets prior pool and marks active input sample with MfSampleExtensionDiscontinuity");

            long hitsBeforeNormal = decoder.InputSamplePoolHits;
            decoder.DecodePacket(idr64B, 266666L, null, null, discontinuity: false);
            Check(decoder.InputSamplePoolHits == hitsBeforeNormal + 1
                && decoder.TryGetInputSamplePoolSlotForTests(0, out nint reusedSample, out _, out _, out bool stillHasDisc)
                && reusedSample == discSample
                && !stillHasDisc
                && ComVtbl.Get<TestAttrGetUint32Fn>(reusedSample, 7)(reusedSample, in discontinuityKey, out _) < 0,
                "reusing pooled sample on a normal frame deletes MfSampleExtensionDiscontinuity via AttrDeleteItemFn");
        }

        // 2. Dynamic resolution switching (64x64 <-> 128x128) + Drain + Dispose across both D3D11 hardware & Software CPU
        foreach (nint device in new[] { renderer.DeviceHandle, (nint)0 })
        {
            using var decoder = new MediaFoundationH264Decoder(device, 64, 64);
            int sw = 0, hw = 0;
            void OnHw(nint t, uint s, int w, int h, int x, int y, int cw, int ch, VideoColorMatrix m) => hw++;
            void OnSw(ReadOnlySpan<byte> b, int st, int h, int x, int y, int cw, int ch, VideoColorMatrix m) => sw++;

            decoder.DecodePacket(cfg64, idr64A, 0, OnHw, OnSw);
            if (hw + sw == 0) decoder.Drain(OnHw, OnSw);
            Check(decoder.Width == 64 && decoder.Height == 64 && hw + sw > 0,
                $"dynamic resolution phase 1 decodes 64x64 (device={(device != 0 ? "D3D11" : "SW")})");

            int before128 = hw + sw;
            decoder.DecodePacket(cfg128, idr128, 33333L, OnHw, OnSw, discontinuity: true);
            if (hw + sw == before128) decoder.Drain(OnHw, OnSw);
            Check(decoder.Width == 128 && decoder.Height == 128 && hw + sw > before128,
                $"dynamic resolution phase 2 switches cleanly to 128x128 (device={(device != 0 ? "D3D11" : "SW")})");

            int before64 = hw + sw;
            decoder.DecodePacket(cfg64, idr64B, 66666L, OnHw, OnSw, discontinuity: true);
            if (hw + sw == before64) decoder.Drain(OnHw, OnSw);
            Check(decoder.Width == 64 && decoder.Height == 64 && hw + sw > before64,
                $"dynamic resolution phase 3 switches back to 64x64 (device={(device != 0 ? "D3D11" : "SW")})");

            Check(decoder.InputSamplePoolSlotCount > 0 && decoder.InputSamplePoolTotalBytes > 0,
                "active decoder holds bounded pooled sample(s) prior to Dispose");

            var refreshDimensions = typeof(MediaFoundationH264Decoder)
                .GetMethod("RefreshOutputDimensions", StreamPrivate)!
                .CreateDelegate<Action>(decoder);
            refreshDimensions(); // Warm JIT before checking stackalloc path
            long allocBeforeRefresh = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 64; i++) refreshDimensions();
            long refreshAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBeforeRefresh;
            Check(refreshAlloc == 0,
                $"RefreshOutputDimensions allocates zero managed bytes with stackalloc (device={(device != 0 ? "D3D11" : "SW")})");

            decoder.Dispose();
            Check(decoder.InputSamplePoolSlotCount == 0 && decoder.InputSamplePoolTotalBytes == 0,
                $"Dispose releases all pooled IMFSample/IMFMediaBuffer refs (device={(device != 0 ? "D3D11" : "SW")})");
        }
    }

    private sealed class LegacyMfDecodeHarness : IDisposable
    {
        private static readonly Guid IidDxgiBuf = new("e7174cfa-1c9e-48b1-8866-626226bfc258");
        private static readonly Guid Iid2DBuf = new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");
        private static readonly Guid IidTex2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        private const int MfNeedMoreInput = unchecked((int)0xC00D6D72);
        private const int MfStreamChange = unchecked((int)0xC00D6D61);
        private const int MfNotAccepting = unchecked((int)0xC00D36B5);
        private const uint MftOutputFormatChange = 0x100;
        private const uint MftOutputNoSample = 0x300;

        public readonly MediaFoundationH264Decoder Decoder;
        private readonly nint _mft;
        private readonly bool _mftProvidesOutput;
        private readonly Action _configureOutputType;
        private ComPtr _reusableOut = new();
        private byte[] _swCopy = Array.Empty<byte>();

        public LegacyMfDecodeHarness(nint d3d11Device, int width, int height, uint codec = ScrcpyVideoStreamHeader.CodecH264)
        {
            Decoder = new MediaFoundationH264Decoder(d3d11Device, width, height, codec: codec);
            _mft = ((ComPtr)typeof(MediaFoundationH264Decoder).GetField("_mft", StreamPrivate)!.GetValue(Decoder)!).Handle;
            _mftProvidesOutput = (bool)typeof(MediaFoundationH264Decoder).GetField("_mftProvidesOutputSamples", StreamPrivate)!.GetValue(Decoder)!;
            _configureOutputType = typeof(MediaFoundationH264Decoder).GetMethod("ConfigureOutputType", StreamPrivate)!.CreateDelegate<Action>(Decoder);
        }

        public int DecodeLegacy(
            ReadOnlySpan<byte> annexBData,
            long ptsUs,
            DecodedHardwareFrameHandler? onHardware,
            DecodedSoftwareFrameHandler? onSoftware)
        {
            using var sample = CreateInputSampleLegacy(annexBData, ptsUs);
            int hr = ComVtbl.Get<TestProcessInputFn>(_mft, 24)(_mft, 0, sample.Handle, 0);
            int frames = 0;
            if (hr == MfNotAccepting)
            {
                frames += DrainOutputLegacy(onHardware, onSoftware);
                hr = ComVtbl.Get<TestProcessInputFn>(_mft, 24)(_mft, 0, sample.Handle, 0);
            }
            ComVtbl.ThrowIfFailed(hr, "Legacy.ProcessInput");
            frames += DrainOutputLegacy(onHardware, onSoftware);
            return frames;
        }

        public int DrainLegacy(DecodedHardwareFrameHandler? onHardware, DecodedSoftwareFrameHandler? onSoftware)
        {
            ComVtbl.Get<TestProcessMessageFn>(_mft, 23)(_mft, 0x10000002 /* EOS */, 0);
            ComVtbl.Get<TestProcessMessageFn>(_mft, 23)(_mft, 1 /* DRAIN */, 0);
            return DrainOutputLegacy(onHardware, onSoftware);
        }

        private static ComPtr CreateInputSampleLegacy(ReadOnlySpan<byte> data, long ptsUs)
        {
            ComVtbl.ThrowIfFailed(MFCreateMemoryBuffer((uint)data.Length, out nint bufPtr), "Legacy.MFCreateMemoryBuffer");
            using var buf = new ComPtr(bufPtr);
            ComVtbl.ThrowIfFailed(ComVtbl.Get<TestBufferLockFn>(buf.Handle, 3)(buf.Handle, out nint dstPtr, out _, out _), "Legacy.Lock");
            try
            {
                unsafe { data.CopyTo(new Span<byte>((void*)dstPtr, data.Length)); }
            }
            finally { ComVtbl.Get<TestBufferUnlockFn>(buf.Handle, 4)(buf.Handle); }

            ComVtbl.ThrowIfFailed(ComVtbl.Get<TestBufferSetLenFn>(buf.Handle, 6)(buf.Handle, (uint)data.Length), "Legacy.SetLen");
            ComVtbl.ThrowIfFailed(MFCreateSample(out nint samplePtr), "Legacy.MFCreateSample");
            var sample = new ComPtr(samplePtr);
            try
            {
                ComVtbl.ThrowIfFailed(ComVtbl.Get<TestSampleAddBufferFn>(sample.Handle, 42)(sample.Handle, buf.Handle), "Legacy.AddBuffer");
                ComVtbl.Get<TestSampleSetTimeFn>(sample.Handle, 36)(sample.Handle, ptsUs * 10);
                return sample;
            }
            catch
            {
                sample.Dispose();
                throw;
            }
        }

        private unsafe int DrainOutputLegacy(DecodedHardwareFrameHandler? onHardware, DecodedSoftwareFrameHandler? onSoftware)
        {
            int count = 0;
            nint outBuf = Marshal.AllocHGlobal(32);
            try
            {
                while (true)
                {
                    nint provided = 0;
                    if (!_mftProvidesOutput)
                    {
                        EnsureSwOut();
                        provided = _reusableOut.Handle;
                        ComVtbl.Get<TestSampleGetBufFn>(provided, 40)(provided, 0, out nint bPtr);
                        if (bPtr != 0)
                        {
                            ComVtbl.Get<TestBufferSetLenFn>(bPtr, 6)(bPtr, 0);
                            Marshal.Release(bPtr);
                        }
                    }

                    Marshal.WriteInt32(outBuf, 0, 0);
                    Marshal.WriteIntPtr(outBuf, 8, provided);
                    Marshal.WriteInt32(outBuf, 16, 0);
                    Marshal.WriteIntPtr(outBuf, 24, 0);

                    int hr = ComVtbl.Get<TestProcessOutputFn>(_mft, 25)(_mft, 0, 1, outBuf, out _);
                    nint pEvents = Marshal.ReadIntPtr(outBuf, 24);
                    if (pEvents != 0) Marshal.Release(pEvents);
                    nint pOut = Marshal.ReadIntPtr(outBuf, 8);
                    uint dwStatus = (uint)Marshal.ReadInt32(outBuf, 16);

                    if (hr == MfNeedMoreInput || (hr >= 0 && dwStatus == MftOutputNoSample))
                    {
                        if (_mftProvidesOutput && pOut != 0) Marshal.Release(pOut);
                        break;
                    }
                    if (hr == MfStreamChange || dwStatus == MftOutputFormatChange)
                    {
                        if (_mftProvidesOutput && pOut != 0) Marshal.Release(pOut);
                        _configureOutputType();
                        continue;
                    }
                    if (hr < 0)
                    {
                        if (_mftProvidesOutput && pOut != 0) Marshal.Release(pOut);
                        ComVtbl.ThrowIfFailed(hr, "Legacy.ProcessOutput");
                    }

                    if (pOut != 0)
                    {
                        try
                        {
                            EmitLegacy(pOut, onHardware, onSoftware);
                            count++;
                        }
                        finally
                        {
                            if (_mftProvidesOutput) Marshal.Release(pOut);
                        }
                    }
                }
                return count;
            }
            finally { Marshal.FreeHGlobal(outBuf); }
        }

        private void EnsureSwOut()
        {
            uint needed = (uint)Math.Max(1, Decoder.Width * Decoder.Height * 3 / 2);
            if (!_reusableOut.IsNull)
            {
                if (ComVtbl.Get<TestSampleGetBufFn>(_reusableOut.Handle, 40)(_reusableOut.Handle, 0, out nint curBuf) >= 0 && curBuf != 0)
                {
                    using (new ComPtr(curBuf))
                    {
                        if (ComVtbl.Get<TestGetMaxLenFn>(curBuf, 7)(curBuf, out uint maxLen) >= 0 && maxLen >= needed)
                            return;
                    }
                }
                _reusableOut.Dispose();
            }

            ComVtbl.ThrowIfFailed(MFCreateSample(out nint sPtr), "Legacy.OutSample");
            var sample = new ComPtr(sPtr);
            ComVtbl.ThrowIfFailed(MFCreateMemoryBuffer(needed, out nint bPtr), "Legacy.OutBuf");
            using (var buf = new ComPtr(bPtr))
                ComVtbl.ThrowIfFailed(ComVtbl.Get<TestSampleAddBufferFn>(sample.Handle, 42)(sample.Handle, buf.Handle), "Legacy.OutAddBuf");
            _reusableOut = sample;
        }

        private void EmitLegacy(nint samplePtr, DecodedHardwareFrameHandler? onHardware, DecodedSoftwareFrameHandler? onSoftware)
        {
            if (ComVtbl.Get<TestSampleGetBufFn>(samplePtr, 40)(samplePtr, 0, out nint bufPtr) < 0 || bufPtr == 0)
                return;
            using var mediaBuf = new ComPtr(bufPtr);

            if (onHardware != null && mediaBuf.TryQueryInterface(in IidDxgiBuf, out var dxgiBuf) && dxgiBuf != null)
            {
                using (dxgiBuf)
                {
                    Guid texIid = IidTex2D;
                    if (ComVtbl.Get<TestDxgiBufGetResourceFn>(dxgiBuf.Handle, 3)(dxgiBuf.Handle, in texIid, out nint texPtr) >= 0 && texPtr != 0)
                    {
                        using var tex = new ComPtr(texPtr);
                        ComVtbl.Get<TestDxgiBufGetSubresourceFn>(dxgiBuf.Handle, 4)(dxgiBuf.Handle, out uint subresource);
                        ComVtbl.Get<TestD3D11TexGetDescFn>(tex.Handle, 10)(tex.Handle, out var desc);
                        onHardware(tex.Handle, subresource, (int)desc.Width, (int)desc.Height, 0, 0, Decoder.Width, Decoder.Height, VideoColorMatrix.Bt601Limited);
                        return;
                    }
                }
            }

            if (onSoftware == null) return;
            if (mediaBuf.TryQueryInterface(in Iid2DBuf, out var buf2d) && buf2d != null)
            {
                using (buf2d)
                {
                    if (ComVtbl.Get<TestLock2DFn>(buf2d.Handle, 3)(buf2d.Handle, out nint scan0, out int pitch) >= 0)
                    {
                        try
                        {
                            int absPitch = Math.Abs(pitch);
                            int h = Math.Max(2, Decoder.Height);
                            int total = absPitch * (h + (h + 1) / 2);
                            if (scan0 != 0 && absPitch > 0 && total > 0)
                            {
                                if (pitch > 0)
                                {
                                    ReadOnlySpan<byte> span;
                                    unsafe { span = new ReadOnlySpan<byte>((void*)scan0, total); }
                                    onSoftware(span, absPitch, h, 0, 0, Decoder.Width, Decoder.Height, VideoColorMatrix.Bt601Limited);
                                }
                                else
                                {
                                    if (_swCopy.Length < total) _swCopy = new byte[total];
                                    for (int r = 0; r < h + (h + 1) / 2; r++)
                                        Marshal.Copy(scan0 + r * pitch, _swCopy, r * absPitch, absPitch);
                                    onSoftware(_swCopy.AsSpan(0, total), absPitch, h, 0, 0, Decoder.Width, Decoder.Height, VideoColorMatrix.Bt601Limited);
                                }
                            }
                            return;
                        }
                        finally { ComVtbl.Get<TestUnlock2DFn>(buf2d.Handle, 4)(buf2d.Handle); }
                    }
                }
            }

            if (ComVtbl.Get<TestBufferLockFn>(mediaBuf.Handle, 3)(mediaBuf.Handle, out nint ptr, out _, out uint curLen) >= 0)
            {
                try
                {
                    if (ptr != 0 && curLen > 0)
                    {
                        int len = checked((int)curLen);
                        int stride = Math.Max(1, Decoder.Width);
                        int codedH = Math.Max(2, Decoder.Height > 0 ? Decoder.Height : len * 2 / (3 * stride));
                        ReadOnlySpan<byte> span;
                        unsafe { span = new ReadOnlySpan<byte>((void*)ptr, len); }
                        onSoftware(span, stride, codedH, 0, 0, Decoder.Width, codedH, VideoColorMatrix.Bt601Limited);
                    }
                }
                finally { ComVtbl.Get<TestBufferUnlockFn>(mediaBuf.Handle, 4)(mediaBuf.Handle); }
            }
        }

        public void Dispose()
        {
            _reusableOut.Dispose();
            Decoder.Dispose();
        }
    }

    private static async Task BenchmarkMediaFoundationDecodeHotPathsAsync(
        List<StreamMetric> metrics,
        List<DecodeBenchmarkSummary> summaries)
    {
        await using var window = new NativeVideoWindow("Hidden MF benchmark", false, true, null, 0, 0, 240, 240);
        using var renderer = new D3D11VideoSurfaceRenderer(await window.CreateAsync(default), 240, 240);

        var (cfg64, idr64) = BuildTestBaselineH264(64, 64);
        byte[] h264Packet64 = cfg64.Concat(idr64).ToArray();
        var (cfg384, idr384) = BuildTestBaselineH264(384, 384);
        byte[] h264Packet384 = cfg384.Concat(idr384).ToArray();

        RunDecodeComparison(
            "mf-h264-64x64-d3d11",
            renderer.DeviceHandle,
            64, 64,
            ScrcpyVideoStreamHeader.CodecH264,
            h264Packet64,
            operations: 400,
            metrics, summaries);

        RunDecodeComparison(
            "mf-h264-64x64-software",
            0,
            64, 64,
            ScrcpyVideoStreamHeader.CodecH264,
            h264Packet64,
            operations: 400,
            metrics, summaries);

        RunDecodeComparison(
            "mf-h264-384x384-d3d11",
            renderer.DeviceHandle,
            384, 384,
            ScrcpyVideoStreamHeader.CodecH264,
            h264Packet384,
            operations: 120,
            metrics, summaries);

        RunDecodeComparison(
            "mf-h264-384x384-software",
            0,
            384, 384,
            ScrcpyVideoStreamHeader.CodecH264,
            h264Packet384,
            operations: 120,
            metrics, summaries);

        RunConfigKeyframe2SpanComparison(
            "mf-config-keyframe-2span-d3d11",
            renderer.DeviceHandle,
            384, 384,
            cfg384, idr384,
            operations: 100,
            metrics, summaries);

        if (MediaFoundationH264Decoder.SupportsHevc)
        {
            var (hevcCfg, hevcIdr) = GetSyntheticHevcPackets();
            byte[] hevcPacket = hevcCfg.Concat(hevcIdr).ToArray();

            RunDecodeComparison(
                "mf-hevc-d3d11",
                renderer.DeviceHandle,
                192, 320,
                ScrcpyVideoStreamHeader.CodecH265,
                hevcPacket,
                operations: 200,
                metrics, summaries);

            RunDecodeComparison(
                "mf-hevc-software",
                0,
                192, 320,
                ScrcpyVideoStreamHeader.CodecH265,
                hevcPacket,
                operations: 200,
                metrics, summaries);
        }
    }

    private static void RunDecodeComparison(
        string name,
        nint d3dDevice,
        int width,
        int height,
        uint codec,
        byte[] packet,
        int operations,
        List<StreamMetric> metrics,
        List<DecodeBenchmarkSummary> summaries)
    {
        double beforeFirstMs = MeasureFirstFrameLatency(legacy: true, d3dDevice, width, height, codec, packet);
        double afterFirstMs = MeasureFirstFrameLatency(legacy: false, d3dDevice, width, height, codec, packet);

        using var legacy = new LegacyMfDecodeHarness(d3dDevice, width, height, codec);
        using var pooled = new MediaFoundationH264Decoder(d3dDevice, width, height, codec: codec);

        int hwCount = 0, swCount = 0;
        DecodedHardwareFrameHandler onHw = (_, _, _, _, _, _, _, _, _) => hwCount++;
        DecodedSoftwareFrameHandler onSw = (_, _, _, _, _, _, _, _) => swCount++;

        int legacyLastOut = 0, pooledLastOut = 0;
        long ptsLegacy = 0, ptsPooled = 0;

        void RunBefore()
        {
            int produced = 0;
            for (int i = 0; i < operations; i++)
            {
                produced += legacy.DecodeLegacy(packet, ptsLegacy, onHw, onSw);
                ptsLegacy += 33333;
            }
            legacyLastOut = produced;
        }

        void RunAfter()
        {
            int produced = 0;
            for (int i = 0; i < operations; i++)
            {
                produced += pooled.DecodePacket(packet, ptsPooled, onHw, onSw);
                ptsPooled += 33333;
            }
            pooledLastOut = produced;
        }

        StreamMetric metric = CompareStreamHotPath(name, operations, RunBefore, RunAfter);
        metrics.Add(metric);

        Check(legacyLastOut > 0 && pooledLastOut > 0,
            $"{name} produces real decoded frames in both legacy ({legacyLastOut}) and pooled ({pooledLastOut}) paths");
        Check(pooled.InputSamplePoolSlotCount <= MediaFoundationH264Decoder.MaxPooledInputSamples
            && pooled.InputSamplePoolTotalBytes <= MediaFoundationH264Decoder.MaxTotalPooledInputBytes,
            $"{name} stays within bounded pool limits (slots={pooled.InputSamplePoolSlotCount}, bytes={pooled.InputSamplePoolTotalBytes})");

        var summary = BuildDecodeSummary(name, metric, beforeFirstMs, afterFirstMs,
            pooled.InputSamplePoolSlotCount, pooled.InputSamplePoolTotalBytes, pooled.IsHardwareAccelerated, pooledLastOut);
        Check(summary.AfterAllocatedBytes == 0, $"{name} steady-state decode allocates zero managed bytes");
        summaries.Add(summary);
        Console.WriteLine($"DECODE-DETAIL {name}: avg_us={summary.BeforeAvgFrameUs:F1}->{summary.AfterAvgFrameUs:F1}, "
            + $"first_ms={summary.BeforeFirstFrameMs:F2}->{summary.AfterFirstFrameMs:F2}, "
            + $"gen0={summary.BeforeGen0}->{summary.AfterGen0}, gen1={summary.BeforeGen1}->{summary.AfterGen1}, "
            + $"pool={summary.PoolSlots} slots/{summary.PoolNativeBytes} B, hw={summary.HardwareAccelerated}");
    }

    private static void RunConfigKeyframe2SpanComparison(
        string name,
        nint d3dDevice,
        int width,
        int height,
        byte[] cfg,
        byte[] idr,
        int operations,
        List<StreamMetric> metrics,
        List<DecodeBenchmarkSummary> summaries)
    {
        using var legacy = new LegacyMfDecodeHarness(d3dDevice, width, height);
        using var pooled = new MediaFoundationH264Decoder(d3dDevice, width, height);
        var legacyMerger = new ScrcpyPacketMerger();
        var pooledMerger = new ScrcpyPacketMerger();

        int outCount = 0;
        DecodedHardwareFrameHandler onHw = (_, _, _, _, _, _, _, _, _) => outCount++;
        DecodedSoftwareFrameHandler onSw = (_, _, _, _, _, _, _, _) => outCount++;

        int legacyLastOut = 0, pooledLastOut = 0;
        long ptsLegacy = 0, ptsPooled = 0;

        void RunBefore()
        {
            int produced = 0;
            for (int i = 0; i < operations; i++)
            {
                legacyMerger.Process(cfg, isConfig: true);
                var merged = legacyMerger.Process(idr, isConfig: false);
                produced += legacy.DecodeLegacy(merged, ptsLegacy, onHw, onSw);
                ptsLegacy += 33333;
            }
            legacyLastOut = produced;
        }

        void RunAfter()
        {
            int produced = 0;
            for (int i = 0; i < operations; i++)
            {
                pooledMerger.Process(cfg, isConfig: true);
                pooledMerger.TryConsumePendingConfig(out byte[]? pending);
                produced += pooled.DecodePacket(pending.AsSpan(), idr, ptsPooled, onHw, onSw);
                ptsPooled += 33333;
            }
            pooledLastOut = produced;
        }

        StreamMetric metric = CompareStreamHotPath(name, operations, RunBefore, RunAfter);
        metrics.Add(metric);
        Check(legacyLastOut > 0 && pooledLastOut > 0, $"{name} produces real decoded frames");

        var summary = BuildDecodeSummary(name, metric, 0, 0,
            pooled.InputSamplePoolSlotCount, pooled.InputSamplePoolTotalBytes, pooled.IsHardwareAccelerated, pooledLastOut);
        summaries.Add(summary);
        Console.WriteLine($"DECODE-DETAIL {name}: avg_us={summary.BeforeAvgFrameUs:F1}->{summary.AfterAvgFrameUs:F1}, "
            + $"alloc={summary.BeforeAllocatedBytes}->{summary.AfterAllocatedBytes} B, "
            + $"gen0={summary.BeforeGen0}->{summary.AfterGen0}, pool={summary.PoolSlots} slots/{summary.PoolNativeBytes} B");
    }

    private static double MeasureFirstFrameLatency(bool legacy, nint d3dDevice, int width, int height, uint codec, byte[] packet)
    {
        var samples = new double[5];
        DecodedHardwareFrameHandler noopHw = (_, _, _, _, _, _, _, _, _) => { };
        DecodedSoftwareFrameHandler noopSw = (_, _, _, _, _, _, _, _) => { };

        for (int i = 0; i < samples.Length; i++)
        {
            long started = Stopwatch.GetTimestamp();
            if (legacy)
            {
                using var h = new LegacyMfDecodeHarness(d3dDevice, width, height, codec);
                int frames = h.DecodeLegacy(packet, 0, noopHw, noopSw);
                if (frames == 0) h.DrainLegacy(noopHw, noopSw);
            }
            else
            {
                using var d = new MediaFoundationH264Decoder(d3dDevice, width, height, codec: codec);
                int frames = d.DecodePacket(packet, 0, noopHw, noopSw);
                if (frames == 0) d.Drain(noopHw, noopSw);
            }
            samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        return samples.Order().ElementAt(samples.Length / 2);
    }

    private static DecodeBenchmarkSummary BuildDecodeSummary(
        string name,
        StreamMetric metric,
        double beforeFirstMs,
        double afterFirstMs,
        int poolSlots,
        int poolBytes,
        bool hwAccel,
        int outFrames)
    {
        double beforeMs = metric.Before.Select(t => t.Milliseconds).Order().ElementAt(2);
        double afterMs = metric.After.Select(t => t.Milliseconds).Order().ElementAt(2);
        long beforeAlloc = metric.Before.Select(t => t.AllocatedBytes).Order().ElementAt(2);
        long afterAlloc = metric.After.Select(t => t.AllocatedBytes).Order().ElementAt(2);
        return new DecodeBenchmarkSummary(
            name,
            metric.Operations,
            beforeMs,
            afterMs,
            beforeMs * 1000.0 / metric.Operations,
            afterMs * 1000.0 / metric.Operations,
            beforeAlloc,
            afterAlloc,
            metric.Before.Sum(t => t.Gen0),
            metric.After.Sum(t => t.Gen0),
            metric.Before.Sum(t => t.Gen1),
            metric.After.Sum(t => t.Gen1),
            metric.Before.Sum(t => t.Gen2),
            metric.After.Sum(t => t.Gen2),
            beforeFirstMs,
            afterFirstMs,
            poolSlots,
            poolBytes,
            hwAccel,
            outFrames);
    }
}
