using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SonicRoute.Core;
using SonicRoute.Updates;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Binding = System.Windows.Data.Binding;
using Brushes = System.Windows.Media.Brushes;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private UpdateService? _updates;
        private TextBlock? _updateStatus, _updateVersion;
        private TextBox? _updateNotes;
        private ProgressBar? _updateProgress;
        private Button? _updateDownload, _updateInstall, _updateCancel, _updateCheck, _updateIgnore, _updateLater;
        private CheckBox? _updateAutomatic;
        private bool _syncingUpdates;

        private void InitializeUpdateNotifications()
        {
            if (System.Windows.Application.Current is not App app) return;
            _updates = app.Updates;
            _updates.Changed += UpdateChanged;
            L10n.Instance.PropertyChanged += UpdateLanguageChanged;
            RefreshUpdateUi();
        }

        internal void OpenUpdateSettings()
        {
            if (!IsLoaded) { Loaded += OpenUpdatesAfterLoaded; return; }
            NavSettings.IsChecked = true;
            EnsureSettingsPage();
            RefreshUpdateUi();
            _updateStatus?.BringIntoView();
        }
        private void OpenUpdatesAfterLoaded(object sender, RoutedEventArgs e)
        { Loaded -= OpenUpdatesAfterLoaded; OpenUpdateSettings(); }

        private void UpdateChanged(string? notes)
        {
            if (_isClosed || Dispatcher.HasShutdownStarted) return;
            _uiLifetime.Post(Dispatcher, () =>
            {
                if (_isClosed) return;
                if (notes != null && _updateNotes != null)
                    _updateNotes.Text = notes;
                RefreshUpdateUi();
            });
        }
        private void UpdateLanguageChanged(object? sender, PropertyChangedEventArgs e) => RefreshUpdateUi();

        private static void BindUpdateText(FrameworkElement element, DependencyProperty property, string key) =>
            element.SetBinding(property, new Binding("[" + key + "]") { Source = L10n.Instance });

        private Button UpdateButton(string key, Action action, bool primary = false)
        {
            var button = new Button { MinHeight = 32, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 4, 8, 4) };
            button.SetResourceReference(StyleProperty, primary ? "PrimaryButton" : "GhostButton");
            // Existing button templates size to content and do not consume Control.Padding.
            var label = new TextBlock { Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            BindUpdateText(label, TextBlock.TextProperty, key);
            button.Content = label;
            button.Click += (_, _) => action();
            return button;
        }

        private void InitializeUpdateSettings(ContentControl host)
        {
            var stack = new StackPanel();
            var heading = new ContentControl { Tag = AutomationIcons.Clock };
            heading.SetResourceReference(StyleProperty, "SettingsSectionHeader");
            var headingText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            BindUpdateText(headingText, TextBlock.TextProperty, "Upd.Title");
            heading.Content = headingText;
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(heading);
            var headerActions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            _updateCheck = UpdateButton("St.CheckUpdate", () => CheckUpdate_Click(this, new RoutedEventArgs()));
            _updateCheck.Margin = new Thickness(8, 0, 4, 0);
            headerActions.Children.Add(_updateCheck);
            var log = new Button { Content = AutomationIcons.Create(AutomationIcons.History, 17), Margin = new Thickness(0) };
            log.SetResourceReference(StyleProperty, "AutomationIconButton");
            BindUpdateText(log, ToolTipProperty, "Upd.Changelog");
            BindUpdateText(log, AutomationProperties.NameProperty, "Upd.Changelog");
            log.Click += (_, _) => OpenUpdateUrl(_updates?.Release?.Url ?? UpdateProtocol.Repository + "/releases");
            headerActions.Children.Add(log);
            Grid.SetColumn(headerActions, 1);
            header.Children.Add(headerActions);
            stack.Children.Add(header);
            _updateVersion = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
            _updateVersion.SetResourceReference(ForegroundProperty, "Theme.TextSecondary");
            stack.Children.Add(_updateVersion);
            _updateAutomatic = new CheckBox { Tag = AutomationIcons.Clock };
            _updateAutomatic.SetResourceReference(StyleProperty, "SettingsSwitchRow");
            var automaticText = new StackPanel();
            var title = new TextBlock { TextWrapping = TextWrapping.Wrap };
            BindUpdateText(title, TextBlock.TextProperty, "Upd.Automatic"); automaticText.Children.Add(title);
            var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            hint.SetResourceReference(StyleProperty, "ThemeHint");
            BindUpdateText(hint, TextBlock.TextProperty, "Upd.AutomaticHint"); automaticText.Children.Add(hint);
            _updateAutomatic.Content = automaticText;
            _updateAutomatic.Checked += UpdateAutomaticChanged; _updateAutomatic.Unchecked += UpdateAutomaticChanged;
            stack.Children.Add(_updateAutomatic);
            _updateStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 6) };
            _updateStatus.SetResourceReference(ForegroundProperty, "Theme.TextPrimary"); stack.Children.Add(_updateStatus);
            _updateProgress = new ProgressBar { Height = 5, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 3, 0, 8) };
            stack.Children.Add(_updateProgress);
            _updateNotes = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), Margin = new Thickness(0, 5, 0, 8) };
            _updateNotes.SetResourceReference(ForegroundProperty, "Theme.TextSecondary"); stack.Children.Add(_updateNotes);
            var actions = new WrapPanel();
            _updateDownload = UpdateButton("Upd.Download", () => _ = _updates!.DownloadAsync(), true); actions.Children.Add(_updateDownload);
            _updateInstall = UpdateButton("Upd.Restart", InstallUpdateClicked, true); actions.Children.Add(_updateInstall);
            _updateCancel = UpdateButton("Upd.Cancel", () => _updates!.CancelDownload()); actions.Children.Add(_updateCancel);
            _updateLater = UpdateButton("Upd.Later", () => _updates!.Dismiss(false)); actions.Children.Add(_updateLater);
            _updateIgnore = UpdateButton("Upd.Ignore", () => _updates!.Dismiss(true)); actions.Children.Add(_updateIgnore);
            stack.Children.Add(actions);
            var card = new Border { Child = stack, Margin = new Thickness(0, 0, 0, 14) };
            card.SetResourceReference(StyleProperty, "Card"); host.Content = card;
            RefreshUpdateUi();
        }

        private void UpdateAutomaticChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingUpdates || _updateAutomatic == null || !ReferenceEquals(e.OriginalSource, _updateAutomatic)) return;
            _config.AutoCheckUpdates = _updateAutomatic.IsChecked == true;
            ConfigService.Save(_config);
            _updates?.AutomaticSettingChanged(_config.AutoCheckUpdates);
        }

        private void RefreshUpdateUi()
        {
            var updates = _updates;
            if (updates == null || _isClosed) return;
            UpdateNoticeButton.Visibility = updates.ShouldNotify ? Visibility.Visible : Visibility.Collapsed;
            UpdateNoticeText.Text = string.Format(L10n.T("Upd.Notice"), updates.Release?.Tag);
            if (_updateStatus == null) return;
            bool store = updates.IsPackaged;
            _syncingUpdates = true;
            _updateAutomatic!.IsChecked = _config.AutoCheckUpdates;
            _syncingUpdates = false;
            _updateAutomatic.Visibility = store ? Visibility.Collapsed : Visibility.Visible;
            _updateVersion!.Text = string.Format(L10n.T("Upd.Current"), App.DisplayVersion,
                AppRuntimeIdentity.Current.CommandEdition, UpdateBootstrap.Architecture) +
                (updates.Release != null ? "\n" + string.Format(L10n.T("Upd.Newest"), updates.Release.Tag) : "");
            string key = "Upd." + updates.State;
            _updateStatus.Text = store ? L10n.T("Upd.Store") : L10n.T(key);
            if (!store && updates.LastOutcome.Length != 0) _updateStatus.Text += "\n" + L10n.T(updates.LastOutcome);
            if (updates.State == UpdateState.Downloading)
                _updateStatus.Text += string.Format("  {0:F1} / {1:F1} MB · {2:F1} KB/s", updates.Bytes / 1048576.0,
                    (updates.Release?.Package?.Size ?? 0) / 1048576.0, updates.BytesPerSecond / 1024.0);
            if (updates.State == UpdateState.Failed)
                _updateStatus.Text += "\n" + L10n.T(updates.Error.StartsWith("UnauthorizedAccessException", StringComparison.Ordinal) ? "Upd.Permission" : "Upd.RetryHint");
            _updateNotes!.Visibility = _updateNotes.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _updateProgress!.Visibility = updates.State == UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
            _updateProgress.Value = updates.Release?.Package == null ? 0 : updates.Bytes * 100.0 / updates.Release.Package.Size;
            _updateCheck!.IsEnabled = !updates.Busy;
            _updateDownload!.Visibility = !store && updates.Release?.Package != null && !updates.Busy && updates.State != UpdateState.Ready ? Visibility.Visible : Visibility.Collapsed;
            _updateInstall!.Visibility = !store && updates.State == UpdateState.Ready ? Visibility.Visible : Visibility.Collapsed;
            _updateCancel!.Visibility = updates.State == UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
            _updateLater!.Visibility = _updateIgnore!.Visibility = !store && updates.ShouldNotify ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            OpenUpdateSettings();
            if (_updates?.IsPackaged == true) OpenUpdateUrl("ms-windows-store://pdp/?ProductId=9NQZGRTPM1NT");
            else if (_updates != null) _ = _updates.CheckAsync(true);
        }

        private void InstallUpdateClicked()
        {
            if (System.Windows.MessageBox.Show(this, L10n.T("Upd.InstallConfirm"), L10n.T("Upd.Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes && System.Windows.Application.Current is App app)
                _ = app.InstallUpdateAsync();
        }
        private void UpdateNotice_Click(object sender, RoutedEventArgs e) => OpenUpdateSettings();
        private static void OpenUpdateUrl(string url)
        { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

        private void ReleaseUpdateUi()
        {
            if (_updates != null) _updates.Changed -= UpdateChanged;
            L10n.Instance.PropertyChanged -= UpdateLanguageChanged;
            Loaded -= OpenUpdatesAfterLoaded;
            _updates = null;
            _updateStatus = _updateVersion = null; _updateNotes = null; _updateProgress = null; _updateAutomatic = null;
            _updateDownload = _updateInstall = _updateCancel = _updateCheck = _updateIgnore = _updateLater = null;
        }
    }
}
