using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static int SettingsLocalizationChecks(string output)
    {
        CheckPartialBuiltinLanguageOverrides();
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
                string[] expectedLinks = ["BiliLink", "IfdianLink", "GithubLink", "LicenseLink"];
                var hyperlinks = links.Inlines.OfType<Hyperlink>().Select(link => link.Name).ToArray();
                Check(hyperlinks.SequenceEqual(expectedLinks),
                    theme + " settings about links include the author links and GPL license");
                var label = links.Inlines.OfType<Run>().First();
                Check(BindingOperations.GetBinding(label, Run.TextProperty)?.Mode == BindingMode.OneWay,
                    theme + " settings author home label uses a read-only localization binding");
                var authorLinks = links.Inlines.OfType<Hyperlink>().Take(2)
                    .Select(link => link.Inlines.OfType<Run>().Single()).ToArray();
                var title = (TextBlock)window.FindName("HeaderTitleText");
                var titleButtons = ((Grid)title.Parent).Children.OfType<StackPanel>().Single()
                    .Children.OfType<Button>().Where(button => button.Name != "UpdateNoticeButton").ToArray();
                var maximize = (Button)window.FindName("TitlebarMaxButton");
                var appItem = (DataTemplate)window.FindResource("AppItemTemplate");
                var disabledDot = Descendants<System.Windows.Shapes.Ellipse>(appItem.LoadContent()).Single();
                var outputCombo = (ComboBox)window.FindName("AppsOutputCombo");
                var inputCombo = (ComboBox)window.FindName("AppsInputCombo");
                var unnamed = (TextBlock)outputCombo.ItemTemplate.LoadContent();
                unnamed.DataContext = new AudioDeviceInfo { Id = "fixture-output", IsDefault = true };
                const string alias = "书房 USB Mic";
                var named = (TextBlock)inputCombo.ItemTemplate.LoadContent();
                named.DataContext = new AudioDeviceInfo { Id = "fixture-input", DisplayName = alias,
                    Flow = SonicRoute.Core.Interop.EDataFlow.eCapture };
                var systemInput = (TextBlock)inputCombo.ItemTemplate.LoadContent();
                systemInput.DataContext = PanelDevices.WithSystemDefault([], SonicRoute.Core.Interop.EDataFlow.eCapture,
                    new SonicRoute.Core.AppConfig()).Single();
                var shortcuts = new ItemsControl();
                var emojiDevice = new AudioDeviceInfo { Id = "fixture-emoji", DisplayName = "1234567890🎧USB" };
                typeof(MainWindow).GetMethod("RenderDeviceQuickButtons", Hidden)!.Invoke(window,
                    [shortcuts, new[] { (AudioDeviceInfo)systemInput.DataContext, emojiDevice }, (Action<AudioDeviceInfo>)(_ => { }), null]);
                var shortcut = (Button)shortcuts.Items[0];
                shortcut.Style = (Style)window.FindResource("QuickDevButton");
                Check(Equals(((Button)shortcuts.Items[1]).Content, "1234567890🎧…"),
                    theme + " shortened device names preserve emoji at the truncation point");
                var applyLanguage = typeof(MainWindow).GetMethod("ApplyLanguage", Hidden)!;
                if (theme == "light") CheckRetainedAutomationEditorLanguage(window, applyLanguage);
                Check(title.Text == $"SonicRoute {App.DisplayVersion}" && title.Text == window.Title,
                    theme + " Full window starts with its original brand and version");
                maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                foreach (string language in L10n.SupportedLanguages.Select(entry => entry.Code))
                {
                    applyLanguage.Invoke(window, [language]);
                    DrainUi();
                    Check(L10n.CurrentLanguage == language && label.Text == L10n.T("St.AuthorHome")
                        && authorLinks[0].Text == L10n.T("St.Bilibili") && authorLinks[1].Text == L10n.T("St.Afdian"),
                        theme + " open settings about links refresh in " + language);
                    Check(title.Text == $"SonicRoute {App.DisplayVersion}" && title.Text == window.Title
                        && Equals(title.ToolTip, L10n.T("Window.DragToMove")),
                        theme + " title keeps its brand and translates the drag hint in " + language);
                    Check(!ToolTipService.GetIsEnabled(title) && Equals(maximize.ToolTip, L10n.T("Window.Restore"))
                        && AutomationProperties.GetName(maximize) == L10n.T("Window.Restore"),
                        theme + " maximized title bar translates Restore and disables the drag hint in " + language);
                    maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    DrainUi();
                    var captions = new[] { L10n.T("Window.Minimize"), L10n.T("Window.Maximize"), L10n.T("Window.Close") };
                    Check(ToolTipService.GetIsEnabled(title)
                        && titleButtons.Select(button => button.ToolTip as string).SequenceEqual(captions)
                        && titleButtons.Select(button => AutomationProperties.GetName(button)).SequenceEqual(captions),
                        theme + " restored window provides all three localized button hints and accessible names in " + language);
                    Check(Equals(disabledDot.ToolTip, L10n.T("Apps.DisableAutoOn")),
                        theme + " app status dot translates on the existing item in " + language);
                    string inputLabel = L10n.T("Dev.SystemDefaultIn");
                    Check(((AudioDeviceInfo)systemInput.DataContext).DisplayName == inputLabel
                        && Equals(shortcut.ToolTip, inputLabel) && AutomationProperties.GetName(shortcut) == inputLabel
                        && shortcut.Content is string shortLabel && shortLabel.Length <= 12
                        && inputLabel.StartsWith(shortLabel.TrimEnd('…'), StringComparison.Ordinal),
                        theme + " compact device button, full tooltip and keyboard search agree in " + language);
                    Check(unnamed.Text.Contains(L10n.T("Dev.Unknown")) && unnamed.Text.Contains(L10n.T("Ov.Default"))
                        && systemInput.Text.Contains(L10n.T("Dev.SystemDefaultIn"))
                        && named.Text.Contains(alias) && !named.Text.Contains(L10n.T("Ov.Default")),
                        theme + " device selectors translate unknown, default, and system input while preserving a custom name in " + language);
                    maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                links.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, links));
                Check(links.Inlines.OfType<Hyperlink>().Select(link => link.Name).SequenceEqual(expectedLinks),
                    theme + " loading settings again preserves the author links and GPL license");
            }
            finally { window.Close(); }
        }
        L10n.Instance.SetLanguage("zh-CN");
        CheckPhoneLocalizationLifetimes();
        File.WriteAllText(Path.Combine(output, "settings-localization-summary.json"), JsonSerializer.Serialize(new
        {
            Assertions = _checks, Themes = new[] { "light", "dark" },
            Scope = "Partial language overrides; settings language action; title-bar states and accessible names; author links; app badge; device selectors; retained phone page; quick-panel source; typed errors",
            ProductStarted = false, WindowsShown = false, PhoneAccess = false, BrowserOpened = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {_checks} settings localization assertions; no phone or product startup.");
        return 0;
    }

    private static void CheckRetainedAutomationEditorLanguage(MainWindow window, MethodInfo applyLanguage)
    {
        applyLanguage.Invoke(window, ["zh-CN"]);
        typeof(MainWindow).GetMethod("EnsureAutomationPage", Hidden)!.Invoke(window, null);
        var draft = new AutoRule { Name = "作業用の下書き", Trigger = AutoRuleTrigger.Hotkey };
        draft.SetSteps([new AutoRuleStep { Action = AutoRuleAction.SetSystemOutput,
            TargetDeviceId = "disconnected-test-device", DelayMs = 1250, StopOnFailure = true }]);
        typeof(MainWindow).GetMethod("OpenAutomationRuleEditor", Hidden)!.Invoke(window, [draft, false]);
        var steps = (ItemsControl)window.FindName("AutoStepsHost");
        var trigger = (ComboBox)window.FindName("AutoTriggerCombo");
        var selection = trigger.SelectedItem;
        var model = ((List<AutoRuleStep>)typeof(MainWindow).GetField("_autoSteps", Hidden)!.GetValue(window)!)[0];
        ((TextBox)window.FindName("AutoNameBox")).Text = "未保存のルール名";
        Descendants<TextBox>((DependencyObject)steps.Items[0]).Single().Clear();

        applyLanguage.Invoke(window, ["ja-JP"]);
        DrainUi();
        var card = (DependencyObject)steps.Items[0];
        var texts = Descendants<TextBlock>(card).Select(label => label.Text).ToList();
        Check(ReferenceEquals(trigger.SelectedItem, selection) && Equals(((ComboBoxItem)selection).Content, L10n.T("Auto.TriggerHotkey"))
            && ((TextBlock)window.FindName("AutoHotkeyHintText")).Text == L10n.T("Auto.HotkeyHint")
            && ((Button)window.FindName("AutoHotkeyCapture")).Content is TextBlock hotkey && hotkey.Text == L10n.T("Auto.HotkeyUnbound")
            && texts.Contains(L10n.T("Auto.Delete")) && texts.Contains(L10n.T("Auto.TargetDevice")) && texts.Contains(L10n.T("Auto.Delay"))
            && Descendants<CheckBox>(card).Any(check => Equals(check.Content, L10n.T("Auto.StopOnFailure")) && check.IsChecked == true),
            "retained automation editor translates trigger, hotkey, step controls and stop setting on zh-CN to ja-JP switch");
        Check(((TextBox)window.FindName("AutoNameBox")).Text == "未保存のルール名" && model.TargetDeviceId == "disconnected-test-device"
            && model.Action == AutoRuleAction.SetSystemOutput && model.StopOnFailure && model.DelayMs == 1250
            && Descendants<TextBox>(card).Single().Text.Length == 0,
            "language switch retains the unsaved name, step settings, offline device and partially edited delay");
    }

    private static void CheckPartialBuiltinLanguageOverrides()
    {
        string previousLanguage = L10n.CurrentLanguage;
        string fixtureDirectory = Path.Combine(Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")!, "Lang");
        Check(string.Equals(fixtureDirectory, L10n.ExternalLangDir, StringComparison.OrdinalIgnoreCase),
            "language overrides use the isolated fixture directory");
        Directory.CreateDirectory(fixtureDirectory);
        try
        {
            foreach (var (language, expectedClose) in new[] { ("en-US", "Close"), ("zh-CN", "关闭") })
            {
                string path = Path.Combine(fixtureDirectory, language + ".json");
                using (var file = new FileStream(path, FileMode.CreateNew))
                    JsonSerializer.Serialize(file, new Dictionary<string, string> { ["Nav.Apps"] = "Fixture override" });
                try
                {
                    L10n.Instance.ApplyImportedLanguage(language);
                    Check(L10n.T("Nav.Apps") == "Fixture override" && L10n.T("Window.Close") == expectedClose,
                        language + " retains an older external override and fills missing keys from its own built-in translation");
                }
                finally { File.Delete(path); }
            }
        }
        finally { L10n.Instance.ApplyImportedLanguage(previousLanguage); }
    }

    private static int ExperimentalConfigCardChecks(string output)
    {
        foreach (string theme in new[] { "light", "dark" })
        {
            ThemeService.Apply(theme, "#00CBE9");
            var window = new MainWindow();
            try
            {
                typeof(MainWindow).GetMethod("EnsureExperimentalPage", Hidden)!.Invoke(window, null);
                var buttons = new[] { "ExpExportConfigButton", "ExpImportConfigButton", "ExpClearConfigButton" }
                    .Select(name => (Button)typeof(MainWindow).GetField(name, Hidden)!.GetValue(window)!).ToArray();
                DependencyObject parent = buttons[0];
                while (parent is not Border)
                    parent = LogicalTreeHelper.GetParent(parent) ?? throw new InvalidOperationException("Configuration card is missing.");
                var card = (Border)parent;
                Check(buttons.All(Descendants<Button>(card).Contains) && buttons[1].AllowDrop,
                    theme + " clear, export, and drag import share one configuration card");
                var title = Descendants<TextBlock>(card).First();
                foreach (string language in L10n.SupportedLanguages.Select(entry => entry.Code))
                {
                    L10n.Instance.SetLanguage(language);
                    DrainUi();
                    Check(title.Text == L10n.T("Exp.ConfigFiles") && title.Text != "Exp.ConfigFiles"
                        && Equals(buttons[0].Content, L10n.T("Exp.ExportConfig"))
                        && Equals(buttons[1].Content, L10n.T("Exp.ImportConfig"))
                        && Equals(buttons[2].Content, L10n.T("Exp.ClearConfig")),
                        theme + " configuration card translates in " + language);
                    const int narrowWidth = 470;
                    card.Measure(new Size(narrowWidth, double.PositiveInfinity));
                    card.Arrange(new Rect(0, 0, narrowWidth, card.DesiredSize.Height));
                    card.UpdateLayout();
                    var bounds = buttons.Select(button => button.TransformToAncestor(card)
                        .TransformBounds(new Rect(button.RenderSize))).ToArray();
                    Check(bounds.All(rect => rect.Width > 0 && rect.Right <= narrowWidth && Math.Abs(rect.Top - bounds[0].Top) < 1)
                        && bounds[0].Right <= bounds[1].Left && bounds[1].Right <= bounds[2].Left
                        && buttons.All(button => LayoutInformation.GetLayoutClip(Descendants<TextBlock>(button).Single()) == null),
                        theme + " all configuration actions fit on one row in " + language);
                }

                L10n.Instance.SetLanguage("zh-CN");
                DrainUi();
                const int previewWidth = 650;
                card.Measure(new Size(previewWidth, double.PositiveInfinity));
                Render(card, previewWidth, (int)Math.Ceiling(card.DesiredSize.Height), Path.Combine(output, $"config-card-{theme}.png"));
            }
            finally { window.Close(); }
        }
        File.WriteAllText(Path.Combine(output, "experimental-config-card-summary.json"), JsonSerializer.Serialize(new
        { Assertions = _checks, Themes = new[] { "light", "dark" }, ActionInvoked = false }));
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
        store.RememberDevice(device); store.Retain(music);
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
            typeof(MainWindow).GetMethod("InitializePhoneManagement", Hidden)!.Invoke(main, null);
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
            var deviceName = (TextBlock)page.FindName("DeviceName");
            var sidebar = (ListBox)main.FindName("PhoneDevicesList");
            var sidebarLabel = new TextBlock();
            sidebarLabel.SetBinding(TextBlock.TextProperty, new Binding("Label") { Source = sidebar.Items[0] });
            Pump(store.FlushAsync());
            var reloaded = new AndroidPhoneStore(store.FilePath, () => L10n.T("Phone.DefaultName"));
            var outputPicker = (ComboBox)main.FindName("AppsOutputCombo");
            var refreshDevices = typeof(MainWindow).GetMethod("RefreshDeviceDisplays", Hidden)!;
            typeof(MainWindow).GetField("_appsSelected", Hidden)!.SetValue(main,
                new AudioAppInfo { ProcessId = 0, ExternalSourceId = source.Id });
            refreshDevices.Invoke(main, null);
            string speakerId = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), "speaker");
            var suppressSelection = typeof(MainWindow).GetField("_suppressDevCombo", Hidden)!;
            suppressSelection.SetValue(main, true);
            try { outputPicker.SelectedItem = outputPicker.Items.OfType<AudioDeviceInfo>().Single(item => item.Id == speakerId); }
            finally { suppressSelection.SetValue(main, false); }
            string? originalRoute = source.Session.State.OutputId;
            var nativeOutput = outputPicker.Items.OfType<AudioDeviceInfo>().Single(item => item.Id == AndroidAudioSession.SourcePhoneOutputId);
            var nativeLabel = (TextBlock)outputPicker.ItemTemplate.LoadContent();
            nativeLabel.DataContext = nativeOutput;
            var routeError = typeof(App).Assembly.GetType("SonicRoute.AppPlaybackOutputs")!
                .GetMethod("RouteError", statics)!;
            var phoneOffline = AndroidError.WithCode(new IOException("diagnostic"), AndroidErrorCode.PhoneOffline);
            foreach (string language in L10n.SupportedLanguages.Select(entry => entry.Code))
            {
                L10n.Instance.SetLanguage(language);
                DrainUi();
                refreshDevices.Invoke(main, null);
                Check(outputPicker.SelectedItem is AudioDeviceInfo selected && selected.Id == speakerId
                    && source.Session.State.OutputId == originalRoute,
                    language + " refreshing a per-app device list preserves its selection and active audio route");
                string expectedNative = L10n.T("Phone.NativeAudioOutput");
                Check(nativeOutput.DisplayName == expectedNative && nativeLabel.Text.Contains(expectedNative),
                    language + " the existing native playback choice follows the interface language");
                var failure = ((bool Success, int HResult, string Message))routeError.Invoke(null, [phoneOffline])!;
                Check(!failure.Success && failure.HResult == phoneOffline.HResult && failure.Message
                    == string.Format(L10n.T("Dev.RouteFailed"), L10n.T("Phone.Err.PhoneOffline")),
                    language + " multiple-output failures preserve the code and translate the underlying device error");
                string expectedName = L10n.T("Phone.DefaultName");
                Check(expectedName != "Phone.DefaultName" && deviceName.Text == expectedName && sidebarLabel.Text == expectedName
                    && subtitle.Text == string.Format(L10n.T("Phone.FromDevice"), expectedName)
                    && reloaded.GetDisplayName(device) == expectedName,
                    language + " automatic phone name follows the current language in the page, sidebar, panel, and saved profile");
            }
            const string customName = "我的手机";
            store.Rename(customName);
            foreach (string language in new[] { "en-US", "ja-JP", "zh-CN" })
            {
                typeof(PhoneDevicePage).GetMethod("Suspend", Hidden)!.Invoke(page, null);
                L10n.Instance.SetLanguage(language);
                typeof(PhoneDevicePage).GetMethod("Activate", Hidden)!.Invoke(page, null);
                DrainUi();
                Check(ReferenceEquals(choice, choices.Items[0]) && description.Text == L10n.T("Phone.Cat.MediaMusic")
                    && action.Text == L10n.T("Phone.RemoveFromQuickPanel"), language + " cached phone rows refresh after suspended language change");
                Check(subtitle.Text == string.Format(L10n.T("Phone.FromDevice"), customName),
                    language + " retained quick-panel source label notifies its live binding");
                Check(deviceName.Text == customName && sidebarLabel.Text == customName
                    && ((TextBlock)phone.FindName("MediaTitle")).Text == "原始歌名"
                    && ((TextBlock)phone.FindName("MediaSubtitle")).Text.Contains("原始歌手"),
                    language + " explicitly named phone retains the literal Chinese default wording and custom media text");
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
        L10n.Instance.SetLanguage("en-US");
        DrainUi();
        L10n.Instance.SetLanguage("zh-CN");
        DrainUi();
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
