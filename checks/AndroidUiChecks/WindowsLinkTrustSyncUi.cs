using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SonicRoute;

internal static partial class Program
{
    private static Task<T[]> EmptyTrustCatalog<T>(bool includeIcons, CancellationToken token) =>
        Task.FromResult(Array.Empty<T>());

    private static int WindowsLinkTrustSyncUi(string? desktopOutput = null)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var hub = typeof(App).Assembly.GetType("SonicRoute.WindowsLinkHub", true)!;
        var original = hub.GetMethod("EnsureService", statics)!.Invoke(null, null)!;
        Type type = original.GetType();
        var ctor = type.GetConstructors(Hidden | BindingFlags.Public).Single();
        var catalog = typeof(Program).GetMethod(nameof(EmptyTrustCatalog), statics)!
            .MakeGenericMethod(type.Assembly.GetType("SonicRoute.WindowsLink.LinkRemoteApp", true)!);
        string root = Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = timeout.Token;

        static T Read<T>(object obj, string name) => (T)obj.GetType().GetProperty(name)!.GetValue(obj)!;
        Task Run(object node, string method, params object?[] args) => (Task)type.GetMethod(method)!.Invoke(node, args)!;
        object Node(string name)
        {
            var args = ctor.GetParameters().Select(p => p.Name switch
            {
                "stateDirectory" => Path.Combine(root, name), "name" => name, "port" => (object)0,
                "discovery" => false, "localCatalogProvider" => catalog.CreateDelegate(p.ParameterType),
                _ => p.DefaultValue
            }).ToArray();
            object node = ctor.Invoke(args);
            type.GetProperty("ProbeAudioCapture", Hidden)!.SetValue(node, (Func<CancellationToken, bool>)(_ => false));
            Action<object> accept = request => type.GetMethod("AcceptPairing")!.Invoke(node, [Read<Guid>(request, "Id"), true]);
            type.GetEvent("PairingRequested")!.AddEventHandler(node, accept);
            return node;
        }
        object? ConnectedPeer(object node) => Read<IEnumerable>(node, "Peers").Cast<object>()
            .SingleOrDefault(p => Read<bool>(p, "Connected"));

        object local = Node("Trust A"), remote = Node("Trust B");
        var active = hub.GetField("_service", statics)!;
        // Exercise the handler installed by the real hub, using loopback-only nodes with fresh pairing identities.
        var forward = type.GetField("FullTrustChanged", Hidden)!.GetValue(original)
            ?? throw new InvalidOperationException("The hub has no trust notification subscription.");
        var trustEvent = type.GetEvent("FullTrustChanged", Hidden)!;
        trustEvent.GetAddMethod(true)!.Invoke(local, [forward]);
        active.SetValue(null, local);
        try
        {
            Pump(Task.WhenAll(Run(local, "EnableAsync", token), Run(remote, "EnableAsync", token)));
            Pump(Run(local, "ConnectAsync", Read<string>(remote, "LocalAddress"), null, token));
            Until(() => ConnectedPeer(local) != null && ConnectedPeer(remote) != null);
            string idB = Read<string>(ConnectedPeer(local)!, "Id"), idA = Read<string>(ConnectedPeer(remote)!, "Id");
            using var page = new PhoneDevicePage();
            typeof(PhoneDevicePage).GetMethod("ShowWindowsLinkPeer", Hidden)!.Invoke(page, [idB]);
            var window = new Window { Content = page, Width = 760, Height = 720, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                typeof(PhoneDevicePage).GetMethod("Activate", Hidden)!.Invoke(page, null);
                DrainUi();
                if (desktopOutput != null)
                {
                    CheckWindowsDesktopPage(page, local, ConnectedPeer(local)!, desktopOutput, token);
                    return 0;
                }
                var status = (TextBlock)page.FindName("WindowsTrustStatus");
                var action = (Button)page.FindName("WindowsTrustAction");
                var code = (TextBox)page.FindName("WindowsTrustLocal");
                var input = (TextBox)page.FindName("WindowsTrustPeerCode");
                var verify = (Button)page.FindName("WindowsTrustVerify");
                var permission = (CheckBox)page.FindName("WindowsLinkAllowInputSwitch");

                void WaitStatus(string key)
                {
                    try { Until(() => status.IsVisible && status.Text == L10n.T("Phone.LinkTrust" + key)); }
                    catch (TimeoutException ex) { throw new InvalidOperationException($"Trust page expected {key}, displayed '{status.Text}'.", ex); }
                }
                WaitStatus("Intro");
                Pump(Run(remote, "BeginFullTrustAsync", idA, token));
                WaitStatus("Invite");
                Check(!code.IsVisible && action.IsEnabled && input.Text.Length == 0,
                    "the open device page receives the other PC's invitation without leaking or creating a local code");
                Pump(Run(remote, "RevokeFullTrustAsync", idA, token));
                WaitStatus("Intro");

                action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitStatus("AskPeer");
                string codeA = code.Text;
                Check(code.IsVisible && codeA.Length == 8 && !input.IsEnabled,
                    "Generate displays this PC's eight-digit code and waits for the other PC");
                Pump(Run(remote, "BeginFullTrustAsync", idA, token));
                WaitStatus("Exchange");
                Check(input.IsEnabled && !verify.IsEnabled && code.Text == codeA,
                    "remote code generation enables the existing peer-code field and preserves this PC's code");
                object remoteState = type.GetMethod("GetFullTrustState")!.Invoke(remote, [idA])!;
                input.Text = Read<string>(remoteState, "LocalCode");
                Check(verify.IsEnabled, "the open device page accepts the matching peer code for verification");
                verify.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitStatus("Waiting");
                Check(!input.IsEnabled && permission.IsEnabled, "one submission shows the waiting state with ordinary permissions");

                var submit = (Task<bool>)Run(remote, "SubmitFullTrustCodeAsync", idA, codeA, token);
                Pump(submit);
                WaitStatus("Enabled");
                Until(() => !permission.IsEnabled && permission.IsChecked == true);
                Check(submit.Result && !code.IsVisible && code.Text.Length == 0 && input.Text.Length == 0
                    && Equals(action.Content, L10n.T("Phone.LinkTrustRevoke")),
                    "the second submission clears both code fields and updates trust and input permission on the same page");
                Pump(Run(remote, "RevokeFullTrustAsync", idA, token));
                WaitStatus("Intro");
                Check(permission.IsEnabled && permission.IsChecked == false && action.IsEnabled
                    && Equals(action.Content, L10n.T("Phone.LinkTrustGenerate")),
                    "remote revocation immediately restores ordinary permissions and the Generate action");
            }
            finally { window.Close(); }
            return 0;
        }
        finally
        {
            trustEvent.GetRemoveMethod(true)!.Invoke(local, [forward]);
            active.SetValue(null, original);
            Pump(Task.WhenAll(((IAsyncDisposable)local).DisposeAsync().AsTask(), ((IAsyncDisposable)remote).DisposeAsync().AsTask()));
            Pump((Task)hub.GetMethod("ShutdownAsync", statics)!.Invoke(null, null)!);
        }
    }
}
