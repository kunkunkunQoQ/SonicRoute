using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using SonicRoute.Native.Audio;

internal static class Program
{
    private static int _failed;
    private static int _skipped;
    private static void Pass(string group,string details) => Console.WriteLine($"PASS {group} {details}");
    private static void Skip(string group,string details) { _skipped++; Console.WriteLine($"PENDING {group} {details}"); }
    private static void Case(string group,Action action)
    {
        try { action(); }
        catch (Exception ex) { _failed++; Console.WriteLine($"FAIL {group} hr=0x{ex.HResult:X8} {ex}"); }
    }
    private static void Require(bool result,string message)
    {
        if (!result) throw new InvalidOperationException(message);
    }

    private static int Main(string[] args)
    {
        try
        {
            using var mta = new MtaScope();
            if (args.FirstOrDefault() == "--source") return RunSource();
            using (var devices = new Endpoints())
            {
                if (args.FirstOrDefault() == "--watch")
                {
                    int seconds = args.Length == 2 ? Math.Clamp(int.Parse(args[1]),1,300) : 20;
                    var events = new Notice();
                    using (Subscription.ForDevices(devices.Enumerator,events))
                    {
                        Console.WriteLine("READY hardware device notice window");
                        Thread.Sleep(TimeSpan.FromSeconds(seconds));
                    }
                    Console.WriteLine($"WATCH callbackCount={events.Count} added={events.Added} removed={events.Removed} stateChanges={events.StateChanges}");
                    return events.Added + events.Removed + events.StateChanges > 0 ? 0 : 2;
                }
                Console.WriteLine($"RUN AudioProbe os={Environment.OSVersion.Version} arch={RuntimeInformation.ProcessArchitecture} dynamicCode={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported} process={Environment.ProcessId}");
                if (args.FirstOrDefault() == "--stream-metrics")
                {
                    Case("process-loopback-fanout",() => CaptureAndForward(devices,true));
                    Case("cancelled-activation",TestCancelledActivation);
                    devices.Dispose();
                    Case("dispose",() => { Require(Com.Owners == 0,"unbalanced owners"); Pass("dispose","remaining=0"); });
                    Console.WriteLine($"RESULT failed={_failed} pending={_skipped}");
                    return _failed == 0 ? 0 : 1;
                }
                bool sameValue = args.Contains("--same-value",StringComparer.Ordinal);
                foreach (Flow flow in Enum.GetValues<Flow>())
                {
                    Case($"endpoints-{flow}",() => Inspect(devices,flow,sameValue));
                    Case($"app-routes-{flow}",() => CheckRouting(devices,flow));
                }
                Case("device-callback",() => TestDeviceCallback(devices));
                Case("own-session",() => TestSession(devices));
                Case("process-loopback-fanout",() => CaptureAndForward(devices));
                Case("cancelled-activation",TestCancelledActivation);
                Skip("physical-device-change","current devices preserved; no manual unplug or replug");
                Skip("different-system-default-and-master-level","existing device routes and global volume preserved; same-value writes only");
            }
            Case("dispose",() => { Require(Com.Owners == 0,"unbalanced native interface owners"); Pass("dispose","owned wrappers remaining=0"); });
            Console.WriteLine($"RESULT failed={_failed} pending={_skipped}");
            return _failed == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine($"FATAL {ex}"); return 1; }
    }

    private static void Inspect(Endpoints devices,Flow flow,bool sameValue)
    {
        var rows = devices.Active(flow);
        Pass($"enumerate-{flow}",$"active={rows.Count} friendlyNames={rows.Count(r => r.Name.Length != 0)}");
        foreach (var role in Enum.GetValues<Role>())
        {
            using var device = devices.Default(flow,role);
            string id = Endpoints.Id(device);
            Require(rows.Any(x => x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)),"default missing from active list");
            if (sameValue && role != Role.Communications)
            {
                using var beforeWrite = devices.Default(flow,role);
                Require(Endpoints.Id(beforeWrite) == id,"default changed concurrently; cancelling write");
                Endpoints.SetDefault(id,role);
                using var after = devices.Default(flow,role);
                Require(Endpoints.Id(after) == id,"default same-ID readback mismatch");
            }
            Pass($"default-{flow}-{role}",sameValue && role != Role.Communications ? "same-ID write and readback" : "read-only");
        }
        using var def = devices.Default(flow,Role.Multimedia);
        using var volume = Endpoints.Activate(def,Endpoints.VolumeInterface);
        var before = Endpoints.ReadVolume(volume,true);
        Require(before.Level >= 0 && before.Level <= 1,"volume out of range");
        var sink = new Notice();
        using (Subscription.ForEndpoint(volume,sink))
        {
            if (sameValue)
            {
                var context = Guid.NewGuid();
                Require(Endpoints.ReadVolume(volume,true) == before,"volume changed concurrently; cancelling write");
                Endpoints.SetVolume(volume,true,before.Level,context);
                Require(Endpoints.ReadVolume(volume,true) == before,"volume changed concurrently; cancelling mute write");
                Endpoints.SetMute(volume,true,before.Muted,context);
                Require(Endpoints.ReadVolume(volume,true) == before,"endpoint state changed");
            }
        }
        Pass($"endpoint-volume-{flow}",$"level={before.Level:R} mute={before.Muted} same-value-write={sameValue} notices={sink.Count} registered-and-unregistered=true");
        if (flow == Flow.Render)
        {
            int sessions = 0;
            foreach (var endpoint in rows)
            {
                using var device = devices.Open(endpoint.Id);
                sessions += Endpoints.Sessions(device).Count;
            }
            Pass("render-sessions",$"count={sessions} endpoints={rows.Count}");
        }
    }

    private static void CheckRouting(Endpoints devices,Flow flow)
    {
        uint pid = (uint)Environment.ProcessId;
        using var device = devices.Default(flow,Role.Multimedia);
        using var policy = new Policy();
        string defaultId = Endpoints.Id(device);
        string target = devices.Active(flow).Select(endpoint => endpoint.Id)
            .FirstOrDefault(id => !string.Equals(defaultId,id,StringComparison.OrdinalIgnoreCase)) ?? defaultId;
        string full = Policy.FullId(target,flow);
        foreach (Role role in new[] {Role.Console,Role.Multimedia})
        {
            string? previous = policy.Read(pid,flow,role);
            bool written = false;
            try
            {
                policy.Write(pid,flow,role,full);
                written = true;
                Require(string.Equals(policy.Read(pid,flow,role),full,StringComparison.OrdinalIgnoreCase),"per-application route mismatch");
            }
            finally
            {
                if (written && string.Equals(policy.Read(pid,flow,role),full,StringComparison.OrdinalIgnoreCase))
                    policy.Write(pid,flow,role,previous);
            }
            Require(string.Equals(policy.Read(pid,flow,role),previous,StringComparison.OrdinalIgnoreCase),"per-application route restoration mismatch");
            Pass($"app-route-{flow}-{role}",$"own PID write/read/compare-and-restore targetDifferentFromSystemDefault={defaultId != target}");
        }
    }

    private static unsafe void TestDeviceCallback(Endpoints devices)
    {
        var sink = new Notice();
        nint callback = (nint)ComInterfaceMarshaller<IDeviceNotifications>.ConvertToUnmanaged(sink);
        try
        {
            using (Subscription.ForDevices(devices.Enumerator,sink))
            {
                // Hold an explicit OS-equivalent reference for deterministic late delivery.
                ((delegate* unmanaged[Stdcall]<nint,nint,int>)Com.Slot(callback,4))(callback,0);
                Require(sink.Count >= 1,"native-to-managed callback was not observed");
            }
            int count = sink.Count;
            ((delegate* unmanaged[Stdcall]<nint,nint,int>)Com.Slot(callback,5))(callback,0);
            Require(sink.Count == count && sink.Late >= 1,"late device callback accepted after unregistration");
            Pass("device-notifications","actual system register/unregister; injected ABI add/remove with late delivery safely rejected; hardware event not generated");
        }
        finally { Com.Release(callback); }
    }

    private static void TestSession(Endpoints devices)
    {
        using var endpoint = devices.Default(Flow.Render,Role.Multimedia);
        using var render = new PcmRender(endpoint,Guid.NewGuid());
        render.Write(new short[960]);
        render.Start();
        using var volume = render.GetSimpleVolume();
        using var control = render.GetControl();
        var before = Endpoints.ReadVolume(volume,false);
        var context = Guid.NewGuid();
        var changed = new Notice();
        float target = before.Level > .5f ? .37f : .67f;
        bool muteChanged = false, volumeChanged = false;
        using (Subscription.ForSession(control,changed))
        {
            try
            {
                int previous = changed.Count;
                Endpoints.SetVolume(volume,false,target,context);
                volumeChanged = true;
                Require(changed.WaitForNext(previous,TimeSpan.FromSeconds(3)),"missing real session volume callback");
                previous = changed.Count;
                Endpoints.SetMute(volume,false,!before.Muted,context);
                muteChanged = true;
                Require(changed.WaitForNext(previous,TimeSpan.FromSeconds(3)),"missing real session mute callback");
                var current = Endpoints.ReadVolume(volume,false);
                Require(Math.Abs(current.Level-target) < .001 && current.Muted != before.Muted,"own session readback");
                Require(Endpoints.Sessions(endpoint).Any(x => x.ProcessId == Environment.ProcessId),"own session not enumerated");
            }
            finally
            {
                var current = Endpoints.ReadVolume(volume,false);
                if (muteChanged && current.Muted != before.Muted) Endpoints.SetMute(volume,false,before.Muted,context);
                if (volumeChanged && Math.Abs(current.Level-target) < .001) Endpoints.SetVolume(volume,false,before.Level,context);
            }
        }
        Require(Endpoints.ReadVolume(volume,false) == before,"own session state restoration");
        Pass("own-session",$"discovered=true changedVolume=true changedMute=true callbacks={changed.Count} restored=true");
    }

    private static void TestCancelledActivation()
    {
        using var cancelled = new CancellationTokenSource();
        var observer = new LoopbackCompletion();
        var delay = new DelayedDelivery(observer);
        using var operation = ProcessCapture.Begin((uint)Environment.ProcessId,delay);
        try
        {
            delay.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            cancelled.Cancel();
            try { using var unexpected = observer.Take(TimeSpan.FromSeconds(5),cancelled.Token); throw new InvalidOperationException("cancelled wait returned a client"); }
            catch (OperationCanceledException) { observer.Abandon(); }
        }
        finally { delay.Resume.TrySetResult(); }
        observer.Completed.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Require(observer.LateResultReleased,"late IAudioClient result was not released");
        Pass("activation-race","real OS completion, test gate deferred delivery until cancelled wait abandoned; late owned IAudioClient released");
    }

    private static int RunSource()
    {
        using var endpoints = new Endpoints();
        using var endpoint = endpoints.Default(Flow.Render,Role.Multimedia);
        using var render = new PcmRender(endpoint,Guid.NewGuid());
        short[] tone = new short[9600];
        for (int i = 0; i < tone.Length; i += 2)
        {
            // -66 dBFS identifiable 400 Hz signal, emitted only by this disposable test session.
            tone[i] = tone[i+1] = (short)(16 * Math.Sin(2*Math.PI*400*(i/2)/48000));
        }
        render.Write(tone);
        render.Start();
        Console.WriteLine("READY");
        Console.Out.Flush();
        // Parent's EOF also ends playback if the parent exits early.
        var stop = Console.In.ReadLineAsync();
        var deadline = Stopwatch.StartNew();
        while (!stop.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(12))
        {
            render.BufferEvent.WaitOne(100);
            render.Write(tone);
        }
        return 0;
    }

    private static void CaptureAndForward(Endpoints devices,bool sampleMetrics = false)
    {
        string binary = Environment.ProcessPath!;
        using var source = Process.Start(new ProcessStartInfo(binary,"--source") {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true
        }) ?? throw new InvalidOperationException("could not launch test source");
        var outputs = new List<PcmRender>();
        try
        {
            Require(source.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult() == "READY","own test source did not start");
            var all = devices.Active(Flow.Render);
            foreach (var endpoint in all)
            {
                if (outputs.Count == 2) break;
                try
                {
                    using var target = devices.Open(endpoint.Id);
                    var output = new PcmRender(target,Guid.NewGuid());
                    try { output.Start(); outputs.Add(output); }
                    catch { output.Dispose(); throw; }
                }
                catch (COMException ex)
                {
                    Console.WriteLine($"DETAIL renderer-open hr=0x{ex.HResult:X8}");
                }
            }
            Require(outputs.Count > 0,"no active output could render");
            var request = Stopwatch.StartNew();
            using var capture = new ProcessCapture((uint)source.Id,Guid.NewGuid(),CancellationToken.None);
            int nonzero = 0, packets = 0, frames = 0, peak = 0;
            using var process = Process.GetCurrentProcess();
            long[] written = new long[outputs.Count], nonSilentPackets = new long[outputs.Count];
            long[] perPacketTicks = new long[2048];
            long firstPacket = 0, firstSignal = 0, firstWrite = 0;
            var duration = new Stopwatch();
            PcmPacket forward = samples =>
            {
                    long begin = Stopwatch.GetTimestamp();
                    if (firstPacket == 0) firstPacket = request.ElapsedTicks;
                    packets++;
                    frames += samples.Length/2;
                    bool signal = false;
                    foreach (var sample in samples)
                    {
                        if (sample != 0) { nonzero++; signal = true; }
                        peak = Math.Max(peak,Math.Abs((int)sample));
                    }
                    if (signal && firstSignal == 0) firstSignal = request.ElapsedTicks;
                    for (int i=0; i<outputs.Count; i++)
                    {
                        int sent = outputs[i].Write(samples);
                        written[i] += sent;
                        if (signal && sent > 0) nonSilentPackets[i]++;
                    }
                    if (signal && firstWrite == 0 && nonSilentPackets[^1] != 0) firstWrite = request.ElapsedTicks;
                    if (packets <= perPacketTicks.Length) perPacketTicks[packets-1] = Stopwatch.GetTimestamp() - begin;
            };
            double initializedMs = request.Elapsed.TotalMilliseconds;
            Console.WriteLine($"ACTIVE parentPid={Environment.ProcessId} sourcePid={source.Id} expectedWindowSeconds={(sampleMetrics ? 6 : 2)} method=event-driven-preferred");
            Console.Out.Flush();
            TimeSpan cpuBefore = process.TotalProcessorTime;
            capture.Start();
            long startTicks = request.ElapsedTicks;
            long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            duration.Start();
            while (duration.Elapsed < TimeSpan.FromSeconds(sampleMetrics ? 6 : 2))
            {
                if (capture.WaitForPacket(250)) capture.Drain(forward);
            }
            long elapsed = duration.ElapsedMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
            process.Refresh();
            TimeSpan cpu = process.TotalProcessorTime - cpuBefore;
            var drain = Stopwatch.StartNew();
            foreach (var output in outputs)
                while (output.Padding > 0 && drain.Elapsed < TimeSpan.FromMilliseconds(500)) output.BufferEvent.WaitOne(30);
            uint[] padding = outputs.Select(output => output.Padding).ToArray();
            Console.WriteLine($"MEASURE loopback pid={source.Id} ms={elapsed} packets={packets} frames={frames} nonzero={nonzero} peak={peak} distinctEndpoints={outputs.Count} framesSubmitted={string.Join(',',written)} packetsWithSignalSubmitted={string.Join(',',nonSilentPackets)} remainingEndpointFrames={string.Join(',',padding)} consumerCpuMs={cpu.TotalMilliseconds:F3} dispatchThreadAllocatedBytes={allocated}");
            int timingCount = Math.Min(packets,perPacketTicks.Length);
            if (timingCount > 0)
            {
                Array.Sort(perPacketTicks,0,timingCount);
                double ms = 1000.0 / Stopwatch.Frequency;
                Console.WriteLine($"TIMING eventDriven={capture.EventDriven} initializeMs={initializedMs:F3} startToFirstPacketMs={(firstPacket-startTicks)*ms:F3} requestToFirstNonzeroMs={firstSignal*ms:F3} requestToFirstNonzeroFanoutMs={firstWrite*ms:F3} packetPathP50Ms={perPacketTicks[(timingCount-1)/2]*ms:F3} packetPathP99Ms={perPacketTicks[(int)Math.Ceiling(timingCount*.99)-1]*ms:F3}");
            }
            Require(nonzero > 0 && nonSilentPackets.All(count => count > 0),"non-silent end-to-end frames missing");
            Require(written.All(value => value == frames),"a sink did not accept every source frame in this run");
            Require(padding.All(value => value == 0),"the engine did not consume all submitted frames");
            Pass("wasapi-forward","own separate source PID; process-only PCM consumed by endpoint engines");
            if (outputs.Count < 2) Skip("multi-output","fewer than two usable endpoints");
            else Pass("multi-output","same captured PCM written to two distinct render clients; physical listening not measured");
            capture.Dispose();
            if (sampleMetrics) CheckNonEventFallback((uint)source.Id);
        }
        finally
        {
            foreach (var output in outputs) output.Dispose();
            try { source.StandardInput.WriteLine("stop"); source.StandardInput.Close(); } catch (IOException) {}
            if (!source.WaitForExit(15_000)) throw new TimeoutException("owned temporary playback fixture did not exit");
        }
    }

    private static void CheckNonEventFallback(uint sourcePid)
    {
        using var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var fallback = new ProcessCapture(sourcePid,Guid.NewGuid(),cancelled.Token,preferEvents:false);
        fallback.Start();
        int nonzero = 0;
        PcmPacket inspect = samples => { foreach (short sample in samples) if (sample != 0) nonzero++; };
        while (nonzero == 0)
        {
            fallback.WaitForPacket(100);
            fallback.Drain(inspect);
        }
        cancelled.Cancel();
        try { fallback.WaitForPacket(Timeout.Infinite); throw new InvalidOperationException("fallback wait ignored cancellation"); }
        catch (OperationCanceledException) { }
        Pass("non-event-fallback",$"forced real 20 ms non-event initialization, 10 ms active-only fallback, nonzeroSamples={nonzero}, cancelled infinite-deadline wait handled");
    }
}
