using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static int SettingsLocalizationChecks(string output)
    {
        foreach (string theme in new[] { "light", "dark" })
        {
            ThemeService.Apply(theme, "#00CBE9");
            var window = new MainWindow();
            try
            {
                typeof(MainWindow).GetMethod("EnsureSettingsPage", Hidden)!.Invoke(window, null);
                var github = (Hyperlink)typeof(MainWindow).GetField("GithubLink", Hidden | BindingFlags.Public)!.GetValue(window)!;
                var links = (TextBlock)github.Parent;
                links.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, links));
                DrainUi();
                var hyperlinks = links.Inlines.OfType<Hyperlink>().Select(link => link.Name).ToArray();
                Check(hyperlinks.SequenceEqual(["BiliLink", "IfdianLink", "GithubLink"])
                    && !hyperlinks.Contains("OpenSourceLicensesLink"),
                    theme + " settings about links contain only BiliLink, IfdianLink, and GithubLink without OpenSourceLicensesLink");
                var label = links.Inlines.OfType<Run>().First();
                Check(BindingOperations.GetBinding(label, Run.TextProperty)?.Mode == BindingMode.OneWay,
                    theme + " settings author home label uses a read-only localization binding");
                foreach (string language in L10n.SupportedLanguages.Select(entry => entry.Code))
                {
                    L10n.Instance.SetLanguage(language);
                    DrainUi();
                    Check(label.Text == L10n.T("St.AuthorHome") && label.Text != "St.AuthorHome",
                        theme + " open settings about links refresh in " + language);
                }
                links.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, links));
                Check(links.Inlines.OfType<Hyperlink>().Select(link => link.Name).SequenceEqual(["BiliLink", "IfdianLink", "GithubLink"])
                    && links.Inlines.OfType<Hyperlink>().All(link => link.Name != "OpenSourceLicensesLink"),
                    theme + " loading settings again retains only BiliLink, IfdianLink, and GithubLink");
            }
            finally { window.Close(); }
        }
        L10n.Instance.SetLanguage("zh-CN");
        CheckPhoneLocalizationLifetimes();
        File.WriteAllText(Path.Combine(output, "settings-localization-summary.json"), JsonSerializer.Serialize(new
        {
            Assertions = _checks, Themes = new[] { "light", "dark" },
            Scope = "Actual settings Loaded handler; nine-language switch; retained phone page resume; quick-panel source binding; typed errors",
            ProductStarted = false, WindowsShown = false, PhoneAccess = false, BrowserOpened = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {_checks} settings localization assertions; no phone or product startup.");
        return 0;
    }

    private static void CheckPhoneLocalizationLifetimes()
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        var device = new AndroidDevice("offline-fixture", "Fixture model", "device", true, "localization-fixture");
        var music = new AndroidApp("cn.test.localization", "原始应用名", 12101, "media", PlatformCategory: 1);
        cache.GetProperty("Discovery", statics)!.SetValue(null,
            new AndroidDeviceDiscovery(_ => throw new InvalidOperationException("Unexpected device discovery")));
        cache.GetMethod("SetDevices", statics)!.Invoke(null, [new[] { device }]);
        var store = (AndroidPhoneStore)host.GetProperty("Phone", statics)!.GetValue(null)!;
        store.RememberDevice(device); store.Rename("自定义手机名"); store.Retain(music);
        var manager = (AndroidAudioManager)host.GetProperty("Sources", statics)!.GetValue(null)!;
        var source = AddFixture(manager, device, music);
        var main = new MainWindow();
        var phone = new PhoneAudioWindow(false);
        var panel = new QuickPanelModernWindow();
        var statusType = typeof(App).Assembly.GetType("SonicRoute.VirtualDisplayStatusWindow")!;
        var status = (Window)Activator.CreateInstance(statusType, Hidden, null, [new LocalizationDisplay()], null)!;
        var views = new List<object> { main, phone, panel, status };
        try
        {
            typeof(MainWindow).GetMethod("InitializePhoneManagement", Hidden)!.Invoke(main, [false]);
            typeof(MainWindow).GetMethod("SelectPhoneManagement", Hidden)!.Invoke(main, null);
            DrainUi();
            var page = (PhoneDevicePage)((Border)main.FindName("PhoneDetailsHost")).Child;
            views.Add(page);
            var choices = (ItemsControl)page.FindName("RetainedApps");
            var choice = (PhoneAppChoice)choices.Items[0];
            var description = new TextBlock();
            description.SetBinding(TextBlock.TextProperty, new Binding("Item.Description") { Source = choice });
            var action = new TextBlock();
            action.SetBinding(TextBlock.TextProperty, new Binding(nameof(PhoneAppChoice.ActionText)) { Source = choice });
            var subtitle = new TextBlock();
            typeof(PhoneAudioWindow).GetMethod("RetainedAppsChanged", Hidden)!.Invoke(phone, null);
            var media = new AndroidMediaState(music.Package, "原始歌名", "原始歌手", true, 16 | 32 | 512);
            typeof(PhoneAudioWindow).GetMethod("ApplyMediaState", Hidden)!.Invoke(phone, [media]);
            typeof(QuickPanelModernWindow).GetMethod("RefreshAndroidRow", Hidden)!.Invoke(panel, [false]);
            var rows = (System.Collections.IDictionary)typeof(QuickPanelModernWindow).GetField("_androidRows", Hidden)!.GetValue(panel)!;
            var row = rows[source.Id]!;
            var rowItem = (AppItem)row.GetType().GetField("IconItem", Hidden)!.GetValue(row)!;
            subtitle.SetBinding(TextBlock.TextProperty, new Binding(nameof(AppItem.Subtitle)) { Source = rowItem });
            foreach (string language in new[] { "en-US", "ja-JP", "zh-CN" })
            {
                typeof(PhoneDevicePage).GetMethod("Suspend", Hidden)!.Invoke(page, null);
                L10n.Instance.SetLanguage(language);
                typeof(PhoneDevicePage).GetMethod("Activate", Hidden)!.Invoke(page, null);
                DrainUi();
                Check(ReferenceEquals(choice, choices.Items[0]) && description.Text == L10n.T("Phone.Cat.MediaMusic")
                    && action.Text == L10n.T("Phone.RemoveFromQuickPanel"), language + " cached phone rows refresh after suspended language change");
                Check(subtitle.Text == string.Format(L10n.T("Phone.FromDevice"), "自定义手机名"),
                    language + " retained quick-panel source label notifies its live binding");
                Check(((TextBlock)page.FindName("DeviceName")).Text == "自定义手机名"
                    && ((TextBlock)phone.FindName("MediaTitle")).Text == "原始歌名"
                    && ((TextBlock)phone.FindName("MediaSubtitle")).Text.Contains("原始歌手"),
                    language + " custom device and media text are preserved");
                foreach (var code in Enum.GetValues<AndroidErrorCode>())
                {
                    var error = AndroidError.WithCode(new IOException("diagnostic"), code);
                    Check(AndroidError.TryGetCode(new IOException("outer", error), out var found) && found == code,
                        language + " nested error retains code " + code);
                    var translator = typeof(App).Assembly.GetType("SonicRoute.AndroidErrorTranslation")!;
                    string translated = (string)translator.GetMethod("Format", statics, [typeof(Exception)])!.Invoke(null, [error])!;
                    Check(translated == L10n.T("Phone.Err." + code) && !translated.StartsWith("Phone.Err."),
                        language + " typed error translates " + code);
                }
            }
            foreach (var state in new[] { VirtualDisplayState.OnPhone, VirtualDisplayState.Recovering, VirtualDisplayState.Missing })
            {
                statusType.GetMethod("UpdateState", Hidden)!.Invoke(status, [state]);
                foreach (string language in new[] { "en-US", "ja-JP", "zh-CN" })
                {
                    L10n.Instance.SetLanguage(language); DrainUi();
                    string titleKey = state == VirtualDisplayState.OnPhone ? "Phone.DisplayUsingOnPhone"
                        : state == VirtualDisplayState.Recovering ? "Phone.DisplayRecovering" : "Phone.DisplayScreenPaused";
                    var title = (TextBlock)statusType.GetField("_title", Hidden)!.GetValue(status)!;
                    var restore = (Button)statusType.GetField("_restore", Hidden)!.GetValue(status)!;
                    Check(title.Text == L10n.T(titleKey) && restore.IsEnabled == (state != VirtualDisplayState.Recovering),
                        language + " virtual-display status refreshes without changing recovery control: " + state);
                }
            }
            Check(!Directory.Exists(Path.Combine(Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")!, "PhoneRuntime")),
                "settings and phone-language probes prepare no ADB executable or phone helper");
        }
        finally { status.Close(); phone.Close(); panel.Close(); main.Close(); Pump(manager.RemoveAsync(source.Id)); }
        var listeners = (MulticastDelegate?)typeof(L10n).GetField("PropertyChanged", Hidden)!.GetValue(L10n.Instance);
        Check(listeners == null || listeners.GetInvocationList().All(listener => !views.Contains(listener.Target!)),
            "closing phone views removes their strong language-change subscriptions");
    }

    private sealed class LocalizationDisplay : IVirtualDisplayClient
    {
        public Task Completion => Task.CompletedTask;
        public int ProcessId => Environment.ProcessId;
        public Task StartAsync(AndroidDevice device, AndroidApp app, int x, int y, CancellationToken token) =>
            throw new InvalidOperationException("No device access in localization checks");
        public void Activate() => throw new InvalidOperationException("No native window activation in localization checks");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
