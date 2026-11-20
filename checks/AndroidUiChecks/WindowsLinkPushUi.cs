using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static int WindowsLinkOutputUi(bool incoming)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var hub = typeof(App).Assembly.GetType("SonicRoute.WindowsLinkHub")!;
        hub.GetMethod("EnsureService", statics)!.Invoke(null, null);
        var quick = new PhoneAudioWindow(loadDevicesOnShow: false) { ShowActivated = false };
        var panel = new QuickPanelModernWindow { ShowActivated = false };
        try
        {
            quick.Show();
            panel.Show();
            quick.Left = quick.Top = panel.Top = 8;
            panel.Left = quick.Left + quick.ActualWidth + 10;
            DrainUi();
            if (incoming) CheckIncomingWindowsAudioOutputs(panel, hub);
            else CheckLocalWindowsAudioDrops(quick, panel, hub);
            return 0;
        }
        finally
        {
            panel.Close();
            quick.Close();
            Pump((Task)hub.GetMethod("ShutdownAsync", statics)!.Invoke(null, null)!);
        }
    }

    private static void InvokeOutputButton(Button button, bool control)
    {
        var saved = new byte[256];
        if (!GetKeyboardState(saved)) throw new IOException("Could not read private dispatcher keyboard state.");
        try
        {
            var keys = new byte[256];
            if (control) keys[0x11] = keys[0xa2] = 0x80; // VK_CONTROL and VK_LCONTROL on this UI thread.
            if (!SetKeyboardState(keys) || Keyboard.Modifiers != (control ? ModifierKeys.Control : ModifierKeys.None))
                throw new IOException("Could not set the output button's modifiers.");
            var invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!;
            invoke.Invoke();
            DrainUi();
        }
        finally { SetKeyboardState(saved); }
    }

    private static void CheckPlaybackRange()
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var select = typeof(QuickPanelModernWindow).GetMethod("SelectPlaybackRange", statics)!
            .CreateDelegate<Func<string[], HashSet<string>, string, string?, ModifierKeys, string[]>>();
        string[] order = ["System", "Headset", "Speakers", "KUN"];
        string[] Pick(string[] selected, string clicked, string? anchor, ModifierKeys modifiers) =>
            select(order, new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase), clicked, anchor, modifiers);
        Check(Pick(["System"], "KUN", null, ModifierKeys.Control).SequenceEqual(["System", "KUN"])
            && Pick(["System", "KUN"], "System", "KUN", ModifierKeys.Control).SequenceEqual(["KUN"])
            && Pick(["Headset", "KUN"], "System", "KUN", ModifierKeys.None).SequenceEqual(["System"]),
            "Ctrl toggles only the clicked output and a plain click replaces the selection");
        Check(Pick(["System", "KUN"], "Headset", "KUN", ModifierKeys.Shift).SequenceEqual(["Headset", "Speakers", "KUN"])
            && Pick(["Headset"], "KUN", null, ModifierKeys.Shift).SequenceEqual(["Headset", "Speakers", "KUN"]),
            "Shift replaces the selection with the full inclusive range in either direction");
        Check(Pick(["System", "Speakers"], "KUN", "Speakers", ModifierKeys.Control | ModifierKeys.Shift)
                .SequenceEqual(["System", "Speakers", "KUN"]),
            "Ctrl+Shift adds the contiguous range and preserves previously selected outputs outside it");
    }

    private sealed class PushSpeakerLease(Action ended) : IPcProcessSilenceLease
    {
        public void Release(bool restorePreviousEndpoint) => Dispose();
        public void Dispose() => ended();
    }

    private static void CheckLocalWindowsAudioDrops(PhoneAudioWindow quick, QuickPanelModernWindow panel, Type hub)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = limit.Token;
        string root = Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")!;
        CheckPlaybackRange();
        Directory.CreateDirectory(root);
        string executable = Path.Combine(root, "OutputSelectionProbe.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
        using var child = Process.Start(new ProcessStartInfo(executable, "/d /q")
        {
            UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the silent test application.");
        int dedicated = 0, muted = 0, released = 0;
        var sender = new WindowsLinkService(Path.Combine(root, "mixer"), "Mixer", port: 0, discovery: false,
            acquireLocalSilence: pid =>
            {
                if (pid != child.Id) throw new ArgumentException("Unexpected capture process.");
                Interlocked.Increment(ref dedicated);
                throw new InvalidOperationException("The test application kept its original speaker.");
            })
        {
            ProbeAudioCapture = _ => true, ProbeExclusiveCapture = () => true, ProbeSpeakerFallback = () => true,
            AcquireSpeakerFallback = pid =>
            {
                if (pid != child.Id) throw new ArgumentException("Unexpected fallback process.");
                Interlocked.Increment(ref muted);
                return new PushSpeakerLease(() => Interlocked.Increment(ref released));
            }
        };
        var receiver = new WindowsLinkService(Path.Combine(root, "receiver"), "KUN", port: 0, discovery: false,
            localCatalogProvider: (_, _) => Task.FromResult(Array.Empty<LinkRemoteApp>()),
            audioPlayerFactory: (_, _, _) => new AirPlayFixtureOutput()) { ProbeAudioCapture = _ => false };
        sender.PairingRequested += p => sender.AcceptPairing(p.Id, true);
        receiver.PairingRequested += p => receiver.AcceptPairing(p.Id, true);
        var active = hub.GetField("_service", statics)!;
        var previous = active.GetValue(null);
        var notify = hub.GetMethod("ServiceChanged", statics)!.CreateDelegate<Action>();
        try
        {
            Pump(Task.WhenAll(sender.EnableAsync(token), receiver.EnableAsync(token)));
            Pump(sender.ConnectAsync(receiver.LocalAddress, token: token));
            Until(() => sender.Peers.Any(p => p.Connected) && receiver.Peers.Any(p => p.Connected));
            var kun = sender.Peers.Single();
            active.SetValue(null, sender);
            sender.Changed += notify;
            notify();
            DrainUi();

            var combo = (ComboBox)quick.FindName("DeviceCombo");
            combo.SelectedItem = combo.Items.OfType<LinkPeer>().Single(p => p.Id == kun.Id);
            ((RadioButton)quick.FindName("WindowsLinkAudioChoice")).IsChecked = true;
            var list = (ListBox)quick.FindName("PhoneApps");
            var upper = (Border)typeof(PhoneAudioWindow).GetField("_windowsPushOnly", Hidden)!.GetValue(quick)!;
            string remoteId = WindowsLinkOutputIdentity.FormatDeviceId(kun.Id);
            string localId = AudioService.SystemDefaultDeviceId;
            var app = new AudioAppInfo { ProcessId = (uint)child.Id, ProcessName = "Windows Link fixture" };
            var data = new DataObject("SonicRoute.PcApp.v1", app);

            var panelType = typeof(QuickPanelModernWindow);
            panelType.GetField("_readRowEndpoints", Hidden)!.SetValue(panel,
                (Func<int, bool, (string, string)>)((_, _) => (localId, AudioService.SystemDefaultInputDeviceId)));
            panelType.GetField("_outputDisplay", Hidden)!.SetValue(panel, new List<AudioDeviceInfo>
            {
                new() { Id = localId, DisplayName = "System", Flow = EDataFlow.eRender },
                new() { Id = remoteId, DisplayName = "KUN", Flow = EDataFlow.eRender }
            });
            Pump((Task)panelType.GetMethod("BuildAppRowsAsync", Hidden)!.Invoke(panel, [new List<AudioAppInfo> { app }, ConfigService.Load()])!);
            object row = ((IDictionary)panelType.GetField("_rows", Hidden)!.GetValue(panel)!)[child.Id]!;
            var visual = (FrameworkElement)row.GetType().GetField("RootElement")!.GetValue(row)!;
            Descendants<ToggleButton>(visual).Single().IsChecked = true;
            bool HasActiveOutputs(params string[] expected)
            {
                var buttons = Descendants<Button>(visual).Where(b => b.Tag is AudioDeviceInfo { Flow: EDataFlow.eRender }).ToArray();
                return buttons.Any(b => ((AudioDeviceInfo)b.Tag).Id == remoteId) && buttons.All(b => b.IsEnabled)
                    && buttons.Where(b => b.Style == panel.FindResource("DevButtonActive")).Select(b => ((AudioDeviceInfo)b.Tag).Id)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expected);
            }
            Until(() => HasActiveOutputs(localId));
            bool displayedWarning = false;
            Check(OleDragOver(quick, list, data, .25, drop: true, over: () =>
            {
                quick.UpdateLayout();
                displayedWarning = upper.IsVisible && upper.Opacity == 1 && upper.Background == quick.FindResource("Theme.Overlay")
                    && Descendants<TextBlock>(upper).Any(t => t.Text == L10n.T("Phone.WindowsLinkMuteIfNeededDesc"));
            }) == DragDropEffects.Copy && displayedWarning,
                "a native upper drop from a local application to KUN highlights the target and discloses the speaker mute fallback");
            Until(() => sender.FindOutgoingAppAudio(child.Id)?.DestinationOnly == true && HasActiveOutputs(remoteId));
            Check(dedicated == 1 && muted == 1 && receiver.Streams.Any(s => s.Incoming && s.DestinationOnly),
                "only KUN is highlighted after the upper drop authorizes the speaker fallback for this process");

            Check(OleDragOver(quick, list, data, .75, drop: true) == DragDropEffects.Copy,
                "dropping the same local application in KUN's lower half requests shared playback");
            Until(() => sender.FindOutgoingAppAudio(child.Id)?.DestinationOnly == false && released > 0 && HasActiveOutputs(localId, remoteId));
            Check(muted == 1 && receiver.Streams.Any(s => s.Incoming && !s.DestinationOnly),
                "shared playback releases the speaker mute and highlights both System and KUN in the open mixer row");

            void PressOutput(string id, bool control = false)
            {
                var button = Descendants<Button>(visual).Single(b => b.Tag is AudioDeviceInfo device && device.Id == id);
                InvokeOutputButton(button, control);
                Pump((Task)row.GetType().GetField("PlaybackChange")!.GetValue(row)!);
            }
            PressOutput(remoteId, control: true);
            Until(() => sender.FindOutgoingAppAudio(child.Id) == null && HasActiveOutputs(localId));
            Check(true, "Ctrl-clicking the active KUN output removes its stream and keeps System selected");
            PressOutput(remoteId, control: true);
            Until(() => sender.FindOutgoingAppAudio(child.Id)?.DestinationOnly == false && HasActiveOutputs(localId, remoteId));
            Check(true, "Ctrl-clicking KUN again starts shared playback and lights both output buttons");
            PressOutput(localId);
            Until(() => sender.FindOutgoingAppAudio(child.Id) == null && HasActiveOutputs(localId));
            Check(true, "a plain click on System ends the shared stream and returns the mixer to one selected output");
        }
        finally
        {
            sender.Changed -= notify;
            active.SetValue(null, previous);
            Pump(Task.WhenAll(sender.DisposeAsync().AsTask(), receiver.DisposeAsync().AsTask()));
            child.StandardInput.Close();
            child.WaitForExit();
            File.Delete(executable);
        }
    }
}
