using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static int WindowsToPhoneSinkUi()
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var token = timeout.Token;
        var assembly = typeof(App).Assembly;
        var host = assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var cache = assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        var hub = assembly.GetType("SonicRoute.WindowsLinkHub")!;
        var serviceField = hub.GetField("_service", statics)!;
        var previous = serviceField.GetValue(null);
        var discovery = new AndroidDeviceDiscovery(_ => Task.FromResult<IReadOnlyList<AndroidDevice>>([]));
        cache.GetProperty("Discovery", statics)!.SetValue(null, discovery);
        var store = (AndroidPhoneStore)host.GetProperty("Phone", statics)!.GetValue(null)!;
        var device = new AndroidDevice("offline-fixture", "Fixture phone", "device", true, "kun-to-phone");
        discovery.Publish([device]);
        store.RememberDevice(device);
        var bluetooth = new AndroidOutputSink("bt:aa", AndroidOutputSinkKind.Bluetooth, 5, 8, "Headphones");
        store.UpdateOutputs(device, [AndroidOutputSink.DefaultSpeaker, bluetooth]);
        string root = Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")!;
        string runtime = Directory.CreateDirectory(Path.Combine(root, "adb-runtime")).FullName;
        File.WriteAllText(Path.Combine(runtime, "sonicroute-catalog.jar"), "isolated ADB fixture");
        var server = new FakeAdbServer();
        using var adb = new AdbClient(runtime, new AdbSocketClient(server.EndPoint));
        var create = host.GetMethod("CreatePhonePcmPlayer", statics)!
            .CreateDelegate<Func<AndroidPcmSource, string, CancellationToken, AdbClient?, AndroidPhonePcmPlayer>>();
        int captures = 0, mute = 0, localStarts = 0;
        Action<ReadOnlyMemory<byte>>? write = null;
        var kun = new WindowsLinkService(Path.Combine(root, "kun"), "KUN", port: 0, discovery: false,
            acquireLocalSilence: _ => { Interlocked.Increment(ref mute); return new PushSpeakerLease(() => Interlocked.Decrement(ref mute)); })
        {
            ProbeExclusiveCapture = () => true, ProbeAudioCapture = _ => true,
            CaptureAudio = (_, send, ready, stop) => { captures++; write = send; ready(); return Task.Delay(Timeout.Infinite, stop); }
        };
        var pc = new WindowsLinkService(Path.Combine(root, "pc"), "Mixer", port: 0, discovery: false,
            audioPlayerFactory: (pcm, id, cancel) =>
            {
                if (AndroidOutputIdentity.IsPhoneOutput(id)) return create(pcm, id!, cancel, adb);
                Interlocked.Increment(ref localStarts);
                return new IncomingPcmPlayer(pcm, id);
            });
        kun.PairingRequested += pair => kun.AcceptPairing(pair.Id, true);
        pc.PairingRequested += pair => pc.AcceptPairing(pair.Id, true);
        var panel = new QuickPanelModernWindow { ShowActivated = false, ShowInTaskbar = false, Left = -12000, Top = -12000 };
        try
        {
            panel.Show(); DrainUi();
            Pump(Task.WhenAll(kun.EnableAsync(token), pc.EnableAsync(token)));
            Pump(kun.ConnectAsync(pc.LocalAddress, token: token));
            Until(() => kun.Peers.Any(p => p.Connected) && pc.Peers.Any(p => p.Connected));
            serviceField.SetValue(null, pc);
            string peer = kun.Peers.Single(p => p.Connected).Id;
            string origin = WindowsLinkOutputIdentity.FormatDeviceId(pc.Peers.Single(p => p.Connected).Id);
            string phoneKey = AndroidDeviceIdentity.Key(device);
            string speaker = AndroidOutputIdentity.FormatDeviceId(phoneKey, "speaker");
            string bt = AndroidOutputIdentity.FormatDeviceId(phoneKey, bluetooth.Key);
            const string system = AudioService.SystemDefaultDeviceId;
            var panelType = typeof(QuickPanelModernWindow);
            var outputs = new List<AudioDeviceInfo> { new() { Id = system, DisplayName = "System", Flow = EDataFlow.eRender } };
            outputs.AddRange((IReadOnlyList<AudioDeviceInfo>)host.GetMethod("GetOutputDevices", statics)!.Invoke(null, null)!);
            panelType.GetField("_outputDisplay", Hidden)!.SetValue(panel, outputs);
            var opening = kun.SendAudioAsync(peer, 77, "KUN player", token);
            Pump(opening);
            var source = pc.IncomingAudioSources.Single();
            var session = source.Session;
            session.SetSmartVolume(false);
            var id = source.StreamId;
            panelType.GetMethod("RefreshAndroidRow", Hidden)!.Invoke(panel, [true]);
            var rows = (IDictionary)panelType.GetField("_androidRows", Hidden)!.GetValue(panel)!;
            object row = rows[source.Id]!;
            var rowType = row.GetType();
            ((ToggleButton)rowType.GetField("Expand", Hidden)!.GetValue(row)!).IsChecked = true;
            var visual = (FrameworkElement)rowType.GetField("Root", Hidden)!.GetValue(row)!;
            Button ButtonFor(string endpoint) => Descendants<Button>(visual).Single(b => b.Tag is AudioDeviceInfo d && d.Id == endpoint);
            bool Active(string endpoint) => ButtonFor(endpoint).Style == panel.FindResource("DevButtonActive");
            Task Click(string endpoint, bool ctrl = false)
            {
                InvokeOutputButton(ButtonFor(endpoint), ctrl);
                return (Task)rowType.GetField("PlaybackChange", Hidden)!.GetValue(row)!;
            }
            Check(Active(system) && Active(origin) && !Active(speaker) && !Active(bt),
                "the KUN app mixer offers its source, local system, phone speaker and Bluetooth headphones");
            short[] samples = Enumerable.Range(0, 960).Select(i => (short)(i % 2 == 0 ? 3157 : -1723)).ToArray();
            byte[] pcm = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
            void VerifyAudio(string sink)
            {
                int start = server.ReceivedFor(sink).Length;
                write!(pcm);
                Until(() => server.ReceivedFor(sink).AsSpan(start).IndexOf(pcm) >= 0);
            }
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var primed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.BeforeRouteReport = sink => { primed.TrySetResult(); return held.Task; };
            var starting = Click(speaker, ctrl: true);
            Pump(primed.Task.WaitAsync(token));
            Check(!starting.IsCompleted && !Active(speaker) && session.GetOutputs().SequenceEqual([null]) && mute == 0,
                "wrong Android route report and silent priming leave the original selection intact until the requested device acknowledges");
            held.TrySetResult(); Pump(starting); server.BeforeRouteReport = null;
            VerifyAudio("speaker");
            Check(Active(speaker) && Active(system) && Active(origin) && mute == 0,
                "Ctrl shares KUN with System and the confirmed phone speaker; the actual ADB PCM socket receives the exact samples");
            Pump(Click(speaker)); VerifyAudio("speaker");
            Check(mute == 1 && Active(speaker) && !Active(origin) && !Active(system) && kun.Streams.Single().Id == id,
                "selecting only the Android speaker isolates the KUN app in its existing stream");
            server.RejectBluetooth = true;
            Pump(Click(bt));
            Check(Active(speaker) && !Active(bt) && mute == 1, "Android rejecting Bluetooth preserves the working speaker and KUN mute");
            VerifyAudio("speaker");
            server.RejectBluetooth = false;
            Pump(Click(bt)); VerifyAudio("bt:aa");
            Check(Active(bt) && !Active(speaker) && mute == 1, "Bluetooth acknowledges type 8 and receives the exact KUN PCM");
            server.DropSink("bt:aa");
            Until(() => session.GetOutputs().SequenceEqual([speaker]));
            VerifyAudio("speaker");
            Check(kun.Streams.Single().Id == id && mute == 1, "losing the only Bluetooth sink restores the previous confirmed phone speaker");
            Pump(Click(bt, ctrl: true));
            VerifyAudio("bt:aa");
            server.DropSink("bt:aa");
            Until(() => session.GetOutputs().SequenceEqual([speaker]));
            VerifyAudio("speaker");
            Check(mute == 1 && captures == 1, "when both phone outputs are selected, Bluetooth removal keeps the speaker and the single KUN capture");
            Pump(Click(bt));
            Pump(Click(speaker, ctrl: true));
            VerifyAudio("speaker");
            server.DropSink("bt:aa");
            Until(() => session.GetOutputs().SequenceEqual([speaker]));
            VerifyAudio("speaker");
            Check(kun.Streams.Single().Id == id && mute == 1, "when Bluetooth was the primary output, its removal promotes the selected phone speaker");
            Pump(Click(bt));
            Pump(Click(origin, ctrl: true));
            int before = localStarts;
            server.DropSink("bt:aa");
            Until(() => kun.Streams.Length == 0 && pc.IncomingAudioSources.Length == 0);
            Check(mute == 0 && before == localStarts && captures == 1,
                "if KUN is already playing, losing the last receiver output retires the network stream and keeps KUN playing");
            return 0;
        }
        finally
        {
            serviceField.SetValue(null, previous);
            panel.Close();
            Pump(Task.WhenAll(kun.DisposeAsync().AsTask(), pc.DisposeAsync().AsTask()));
            Pump(server.DisposeAsync().AsTask());
        }
    }
}
