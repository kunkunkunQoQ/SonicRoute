using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static void LogSrapRequest(string op, long? revision) => Console.WriteLine($"REQUEST {op} revision={revision}");

    private static async Task RunWithTrustedPhoneAsync(string directory, Func<SrapSession, CancellationToken, Task> action,
        TimeSpan? timeout = null)
    {
        string path = Path.Combine(directory, "android-phone.json");
        if (!File.Exists(path)) throw new FileNotFoundException("The paired Native Full phone profile is required.", path);
        var store = new AndroidPhoneStore(path);
        var saved = store.Profiles.Where(p => p.LastAppAddress != null && !p.AppTrustRevoked).Single();
        await using var transport = new SrapPhoneTransport(store, directory);
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(3));
        void Cancel(object? sender, ConsoleCancelEventArgs args) { args.Cancel = true; deadline.Cancel(); }
        Console.CancelKeyPress += Cancel;
        try
        {
            Console.WriteLine("STAGE trusted_reconnect");
            var reconnect = System.Diagnostics.Stopwatch.StartNew();
            var device = await transport.ReconnectAsync(saved, deadline.Token);
            var session = transport.Session(device) ?? throw new SrapException("stale_session");
            if (session.PeerFingerprint != saved.AppPublicKeySha256) throw new SrapException("identity_conflict");
            Console.WriteLine($"TRUSTED_RECONNECT elapsedMs={reconnect.ElapsedMilliseconds} tls={session.Protocol}");
            session.RequestSent += LogSrapRequest;
            await action(session, deadline.Token);
        }
        finally { Console.CancelKeyPress -= Cancel; }
    }

    private readonly record struct VolumeReadback(int Index, int Min, int Max, bool? Muted, bool Fixed, bool Dnd)
    {
        internal static VolumeReadback Parse(JsonElement body)
        {
            if (SrapClient.Text(body, "scope") != "system_media") throw new InvalidDataException("Unexpected volume scope.");
            var state = new VolumeReadback(body.GetProperty("streamIndex").GetInt32(), body.GetProperty("minIndex").GetInt32(),
                body.GetProperty("maxIndex").GetInt32(), body.TryGetProperty("isMuted", out var muted) ? muted.GetBoolean() : null,
                body.GetProperty("isFixedVolume").GetBoolean(), body.GetProperty("dndRestricted").GetBoolean());
            if (state.Min < 0 || state.Max < state.Min || state.Index < state.Min || state.Index > state.Max)
                throw new InvalidDataException("Inconsistent media volume readback.");
            return state;
        }
        internal int? PercentFor(int target)
        {
            for (int p = 0; p <= 100; p++)
                if (Min + (((long)Max - Min) * p + 50) / 100 == target) return p;
            return null;
        }
    }

    private static async Task SrapVerifyVolumeStepAsync(SrapSession session, CancellationToken token)
    {
        Task<JsonElement> Get(CancellationToken ct) => session.ReadVolumeAsync(ct);
        var original = VolumeReadback.Parse(await Get(token));
        Console.WriteLine($"VOLUME_INITIAL index={original.Index} min={original.Min} max={original.Max} muted={original.Muted}");
        if (original.Index == original.Min || original.Muted == true)
        { Console.WriteLine("SKIP unchanged: media volume is muted or already at minimum."); return; }
        if (original.Muted == null || original.Fixed || original.Dnd)
        { Console.WriteLine("SKIP unchanged: mute readback unavailable or system volume restricted."); return; }
        bool exact = session.CanSetVolumeIndex;
        int? lower = original.PercentFor(original.Index - 1), restore = original.PercentFor(original.Index);
        if (!exact && (!lower.HasValue || !restore.HasValue))
        { Console.WriteLine("SKIP unchanged: the exact original and adjacent indices cannot be addressed in integer percent."); return; }
        Console.WriteLine("VOLUME_METHOD " + (exact ? "streamIndex" : "percent"));
        Task<JsonElement> Set(int index, int? percent, CancellationToken ct) => exact
            ? session.SetVolumeIndexAsync(index, ct) : session.SetVolumeAsync(percent!.Value, ct);
        void Confirm(VolumeReadback value, int target)
        {
            if (value.Min != original.Min || value.Max != original.Max || value.Index != target)
                throw new InvalidDataException($"Expected system media index {target} in {original.Min}..{original.Max}, got {value}.");
        }
        try
        {
            Confirm(VolumeReadback.Parse(await Set(original.Index - 1, lower, token)), original.Index - 1);
            Confirm(VolumeReadback.Parse(await Get(token)), original.Index - 1);
            Console.WriteLine($"VOLUME_LOWERED index={original.Index - 1}");
        }
        finally
        {
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            cleanup.CancelAfter(TimeSpan.FromSeconds(12));
            var actual = VolumeReadback.Parse(await Get(cleanup.Token));
            if (actual.Min != original.Min || actual.Max != original.Max)
                throw new InvalidDataException("Media volume range changed during the test. The original index cannot be restored safely.");
            if (actual.Index != original.Index || actual.Muted != original.Muted)
                Confirm(VolumeReadback.Parse(await Set(original.Index, restore, cleanup.Token)), original.Index);
            var final = VolumeReadback.Parse(await Get(cleanup.Token));
            Confirm(final, original.Index);
            if (final.Muted != original.Muted) throw new InvalidDataException("Media mute state did not return to its original value.");
            Console.WriteLine($"VOLUME_RESTORED index={final.Index} muted={final.Muted}");
        }
        Check(true, "system media volume lowered exactly one index and an independent read confirmed the original index and mute state were restored");
    }

    private static async Task SrapVolumeChecksAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        using var identity = new SrapIdentity(directory);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = SrapFixtureCertificate(signer);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        string parent = Guid.NewGuid().ToString("N"), pin = Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo()));
        var accept = AcceptSrapFixtureAsync(listener, cert, identity.Fingerprint, parent,
            [new JsonObject { ["id"] = "volume.device", ["state"] = "available" }], ct);
        using var session = await new SrapClient(identity).ConnectAsync((IPEndPoint)listener.LocalEndpoint, pin, ct);
        using var peer = await accept;
        int current = 12, min = 1, max = 25;
        bool exact = false;
        async Task Exchange(bool write, bool muted = false, bool error = false, int? expected = null)
        {
            var request = await SrapClient.ReadAsync(peer, ct);
            var body = SrapClient.Body(request);
            if (SrapClient.Text(body, "op") != (write ? "volume.set" : "volume.get")) throw new InvalidDataException("Unexpected volume operation.");
            if (write)
            {
                var p = body.GetProperty("params");
                if (p.TryGetProperty("streamIndex", out var index) != exact || p.TryGetProperty("percent", out var percent) == exact)
                    throw new InvalidDataException("The write must send only the negotiated volume field.");
                current = exact ? index.GetInt32() : min + ((max - min) * percent.GetInt32() + 50) / 100;
                if (current != expected) throw new InvalidDataException("The percentage did not select exactly the intended index.");
            }
            JsonObject response = error ? new() { ["status"] = "error", ["error"] = "io_failure" } :
                new() { ["status"] = "done", ["result"] = new JsonObject { ["scope"] = "system_media", ["streamIndex"] = current,
                    ["minIndex"] = min, ["maxIndex"] = max, ["percent"] = (current - min) * 100 / (max - min), ["isMuted"] = muted,
                    ["isFixedVolume"] = false, ["dndRestricted"] = false } };
            await SrapClient.WriteAsync(peer, "response", response, ct, SrapClient.Text(request, "id"), parent);
        }
        var skipped = SrapVerifyVolumeStepAsync(session, ct);
        await Exchange(false, muted: true); await skipped;
        Check(current == 12, "a muted device with a non-minimal retained index is left unchanged");
        foreach (bool failed in new[] { false, true })
        {
            var result = SrapVerifyVolumeStepAsync(session, ct);
            await Exchange(false);
            await Exchange(true, error: failed, expected: 11);
            if (!failed) await Exchange(false);
            await Exchange(false); await Exchange(true, expected: 12); await Exchange(false);
            try { await result; }
            catch (SrapException e) when (failed && e.Code == "io_failure") { }
            Check(result.IsCompletedSuccessfully != failed && current == 12,
                failed ? "a failed post-write reply restores the original index and propagates the failure"
                    : "both one-step change and restoration were confirmed by independent reads");
        }
        current = 38; min = 0; max = 150;
        var quantized = SrapVerifyVolumeStepAsync(session, ct);
        await Exchange(false); await quantized;
        Check(current == 38, "an older 150-step phone with unrepresentable adjacent indices receives no write");
        var revised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Wake() { if (session.CanSetVolumeIndex) revised.TrySetResult(); }
        session.CapabilitiesChanged += Wake;
        try
        {
            await SrapClient.WriteAsync(peer, "capabilities", new() { ["capabilityRevision"] = 2, ["entries"] = new JsonArray(
                new JsonObject { ["id"] = "volume.device", ["state"] = "available", ["limits"] = new JsonObject { ["indexWrite"] = "1", ["range"] = "0..150" } }) },
                ct, session: parent);
            await revised.Task.WaitAsync(ct);
        }
        finally { session.CapabilitiesChanged -= Wake; }
        exact = true;
        var indexed = SrapVerifyVolumeStepAsync(session, ct);
        await Exchange(false); await Exchange(true, expected: 37); await Exchange(false);
        await Exchange(false); await Exchange(true, expected: 38); await Exchange(false);
        await indexed;
        Check(current == 38, "negotiated exact index writes restore 38 of 150 after checking 37 of 150");
        Console.WriteLine($"PASS {_checks} SRAP volume checks");
    }

    private static Task SrapVerifyCaptureAsync(SrapSession session, CancellationToken token) => SrapVerifyCaptureAsync(session, token, null);

    private static async Task SrapVerifyCaptureAsync(SrapSession session, CancellationToken token, string? fixturePackage)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(session.Token, token);
        var ct = linked.Token;
        var permission = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Grant()
        {
            if (session.Capabilities.Capture?.Package is not { } candidate) return;
            if (fixturePackage != null && candidate != fixturePackage)
                permission.TrySetException(new InvalidOperationException("The Android grant selected another app."));
            else permission.TrySetResult(candidate);
        }
        session.CapabilitiesChanged += Grant;
        string package;
        var permissionWait = Stopwatch.StartNew();
        try
        {
            Grant();
            if (!permission.Task.IsCompleted) Console.WriteLine("CAPTURE_GRANT_REQUIRED");
            package = await permission.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
        {
            Console.WriteLine($"CAPTURE_SESSION_ENDED reason={await session.Completion ?? "connection_closed"} waitingMs={permissionWait.ElapsedMilliseconds}");
            throw;
        }
        finally { session.CapabilitiesChanged -= Grant; }
        Console.WriteLine("CAPTURE_PACKAGE " + package);
        if (fixturePackage != null && await Console.In.ReadLineAsync(ct) != "OWNED_WAV_OPENED")
            throw new InvalidOperationException("The host did not open its dedicated WAV.");
        var receiver = new SrapPcmCapture(session, new(package, package, 0, ""));
        var source = new AndroidPcmSource();
        using var player = new WasapiPcmPlayer(source, null);
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureTime = new Stopwatch();
        long firstFrame = -1, firstNonSilent = -1, firstConsumption = -1;
        void Readback()
        {
            long now = captureTime.ElapsedTicks;
            if (source.NonSilentSamplesWritten > 0) Interlocked.CompareExchange(ref firstNonSilent, now, -1);
            if (source.SamplesConsumed > 0) Interlocked.CompareExchange(ref firstConsumption, now, -1);
            if (firstNonSilent >= 0 && firstConsumption >= 0 && receiver.PacketsDecoded >= 4) rendered.TrySetResult();
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.DataAvailable += Readback;
        player.Play();
        captureTime.Start();
        var receiving = receiver.ReceiveAsync(source,
            () => Interlocked.CompareExchange(ref firstFrame, captureTime.ElapsedTicks, -1), stop.Token);
        try
        {
            var result = await Task.WhenAny(receiving, rendered.Task).WaitAsync(TimeSpan.FromSeconds(15), ct);
            await result;
        }
        finally
        {
            stop.Cancel();
            try { await receiving; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally
            {
                source.DataAvailable -= Readback;
                static string Ms(long ticks) => ticks < 0 ? "unobserved" : (ticks * 1000d / Stopwatch.Frequency)
                    .ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                Console.WriteLine($"CAPTURE_TIMING start=windows_capture_request firstPcmFrameMs={Ms(firstFrame)} firstNonSilentWrittenMs={Ms(firstNonSilent)} firstWasapiConsumptionObservedMs={Ms(firstConsumption)} stereoFramesWritten={source.SamplesWritten / AndroidPcmSource.Channels} channelSamplesConsumed={source.SamplesConsumed}");
            }
        }
        Check(rendered.Task.IsCompletedSuccessfully && receiver.StopConfirmed && !session.Token.IsCancellationRequested,
            $"authorized app PCM received and WASAPI consumed samples; confirmed capture stop (packets={receiver.PacketsDecoded}, nonSilentSamples={source.NonSilentSamplesWritten}, consumedSamples={source.SamplesConsumed})");
    }
}
