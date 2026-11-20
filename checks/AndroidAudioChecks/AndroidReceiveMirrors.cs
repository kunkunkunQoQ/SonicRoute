using System.Buffers.Binary;
using System.Threading.Channels;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task CheckAndroidReceiveMirrorsAsync(string directory)
    {
        var device = new AndroidDevice("android-input-fixture", "Fixture", "device", true);
        var app = new AndroidApp("cn.test.music", "Music", 1001, "media");
        var wire = BuildWire(rebase: true);
        BinaryPrimitives.WriteInt16LittleEndian(wire.AsSpan(32), -1536);
        var modes = new List<bool>();
        var outputs = new Dictionary<string, AndroidPcmSource>();
        var disposed = new List<string>();
        int liveCaptures = 0, peakCaptures = 0;
        string phoneId = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), "speaker");
        string runtime = await FakeAudioOutputRuntimeAsync(directory);
        await using var fakePhone = new FakeAdbServer();
        var adb = new AdbClient(runtime, new AdbSocketClient(fakePhone.EndPoint));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        await using var session = new AndroidAudioSession(() => new FakeTransport(wire, () => liveCaptures--, mode =>
        {
            modes.Add(mode);
            peakCaptures = Math.Max(peakCaptures, ++liveCaptures);
        }), (pcm, id) =>
        {
            if (id == "bad") throw new IOException("Output unavailable");
            outputs[id ?? "default"] = pcm;
            if (id == phoneId) return new AndroidPhonePcmPlayer(adb, device, pcm, "speaker", token);
            return new StartupPlayer(() => { }, () => disposed.Add(id ?? "default"));
        });
        var multi = (IMultiOutputAudioSession)session;
        await multi.SetOutputsAsync([null, "speaker-a", "speaker-b", "speaker-b"]);
        Check(outputs.Count == 0 && modes.Count == 0, "choosing outputs for an idle Android source opens no capture, phone helper or playback device");
        await session.StartAsync(device, app, true, token);
        var queues = new[] { outputs["default"], outputs["speaker-a"], outputs["speaker-b"] };
        Check(modes.SequenceEqual([true]) && session.PacketsDecoded == 5 && session.TimestampDiscontinuities == 1 &&
            queues.All(q => q.NonSilentSamplesWritten == queues[0].NonSilentSamplesWritten && q.NonSilentSamplesWritten > 0
                && q.BufferedSamples == queues[0].BufferedSamples && Math.Abs(q.HeaderGain - 0.501187f) < 0.001f),
            "one Android capture and one Opus decode feed all three queues, preserving gain and timestamp resets");
        int bytes = queues[0].BufferedSamples * sizeof(float);
        var readings = queues.Select(q => { var buffer = new byte[bytes]; q.Read(buffer, 0, bytes); return buffer; }).ToArray();
        Check(readings.Skip(1).All(b => b.SequenceEqual(readings[0])), "each independently read output produces identical non-silent PCM");
        session.SetMute(true); session.SetSmartVolume(true);
        Check(queues.All(q => q.Volume == 0 && q.SmartVolume), "mute and automatic volume update all Android receiver outputs");
        session.SetMute(false); session.SetVolume(31);
        Check(queues.All(q => Math.Abs(q.Volume - 0.31f) < 0.001f), "volume is synchronized across every receiver output");
        try { await multi.SetOutputsAsync([null, "new", "bad"]); throw new InvalidOperationException("An invalid output was accepted."); }
        catch (IOException) { }
        Check(disposed.Contains("new") && !disposed.Contains("speaker-a") && multi.GetOutputs().SequenceEqual([null, "speaker-a", "speaker-b"]),
            "failed staging releases new players and preserves the previous selection");
        await session.SetOutputAsync(null);
        Check(disposed.Contains("speaker-a") && disposed.Contains("speaker-b") && modes.Count == 1 && multi.GetOutputs().SequenceEqual([null]),
            "single selection releases both extras without another phone capture");
        await multi.SetOutputsAsync([null, phoneId]);
        session.Pcm!.Write(Enumerable.Repeat(0.3f, 1920).ToArray());
        await fakePhone.WaitForLocalAbstractBytesAsync(7680, TimeSpan.FromSeconds(3));
        Check(modes.SequenceEqual([true, false]) && peakCaptures == 1 && multi.GetOutputs().Contains(phoneId) &&
              outputs[phoneId].SamplesConsumed > 0, "explicitly including the phone speaker verifies its route and uses one exclusive capture plus two outputs");
        await session.SetOutputAsync(null);
        Check(modes.SequenceEqual([true, false, true]) && peakCaptures == 1 && multi.GetOutputs().SequenceEqual([null]),
            "deselecting the phone speaker retires its renderer and restores the source playback mode");
        await session.StopAsync();
        Check(liveCaptures == 0 && session.Pcm == null, "stopping the source releases all capture and playback resources");
        modes.Clear(); outputs.Clear();
        await multi.SetOutputsAsync(["speaker-a", "speaker-b"]);
        await session.StartAsync(device, AndroidApp.DeviceAudio, false, token);
        Check(session.State.App?.IsDeviceAudio == true && modes.SequenceEqual([false]) && peakCaptures == 1 &&
            session.PacketsDecoded == 5 && outputs.Count == 2 && outputs.Values.All(pcm => pcm.NonSilentSamplesWritten > 0),
            "whole-phone audio uses the same single decoder and sends PCM only to the two selected local outputs");
        await session.SetOutputAsync("speaker-b");
        Check(multi.GetOutputs().SequenceEqual(["speaker-b"]) && liveCaptures == 1 && session.PacketsDecoded == 5,
            "whole-phone single selection hands off playback without restarting its transport");
        await CheckWirelessPlaybackBacklogAsync(runtime, fakePhone, device, token);
        await CheckSavedPhoneSelectionAsync(directory);
    }

    private static async Task CheckWirelessPlaybackBacklogAsync(string runtime, FakeAdbServer server, AndroidDevice device, CancellationToken token)
    {
        using var stream = new CreditPcmStream();
        var socket = new AdbSocketClient(server.EndPoint);
        await using var adb = new AdbClient(runtime, new CreditAudioTransport(socket, stream));
        var source = new AndroidPcmSource();
        using var player = new AndroidPhonePcmPlayer(adb, device, source, "speaker", token);
        player.Play();
        // Hold each ADB write while another four 10 ms capture blocks arrive.
        var block = new float[AndroidPcmSource.SampleRate * AndroidPcmSource.Channels / 100];
        using var expected = new MemoryStream();
        int sequence = 0;
        void CaptureBlock()
        {
            for (int i = 0; i < block.Length; ++i)
            {
                short value = (short)(++sequence % 24000 - 12000);
                block[i] = value / 32768f;
                expected.WriteByte((byte)value);
                expected.WriteByte((byte)(value >> 8));
            }
            source.Write(block);
        }
        CaptureBlock();
        Check(await stream.Packets.Reader.ReadAsync(token) == block.Length * 2, "phone playback starts on the first 10 ms block");
        for (int i = 0; i < 30; ++i)
        {
            for (int n = 0; n < 4; n++) CaptureBlock();
            stream.Credit.Release();
            if (await stream.Packets.Reader.ReadAsync(token) != block.Length * 8)
                throw new InvalidDataException("Wireless PCM writer fell behind a 40 ms acknowledgement");
        }
        stream.Credit.Release();
        Check(source.DroppedSamples == 0 && source.UnderflowSamples == 0 && stream.ToArray().SequenceEqual(expected.ToArray()),
            "forty-millisecond wireless credits preserve every PCM sample in order with no queue drops");
    }

    private sealed class CreditAudioTransport(AdbSocketClient socket, Stream pcm) : PairingFixtureTransport, IAdbTransport
    {
        Task<IAdbShellSession> IAdbTransport.OpenShellAsync(string serial, string command, bool input, CancellationToken token) =>
            socket.OpenShellAsync(serial, command, input, token);
        Task<Stream> IAdbTransport.OpenLocalAbstractAsync(string serial, string name, CancellationToken token) => Task.FromResult(pcm);
    }

    private sealed class CreditPcmStream : MemoryStream
    {
        internal readonly Channel<int> Packets = Channel.CreateUnbounded<int>();
        internal readonly SemaphoreSlim Credit = new(0);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await base.WriteAsync(buffer, token);
            Packets.Writer.TryWrite(buffer.Length);
            await Credit.WaitAsync(token);
        }
    }
}
