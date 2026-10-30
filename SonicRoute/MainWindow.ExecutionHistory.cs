using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SonicRoute.Core;
using SonicRoute.Core.Models;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private TextBox? _historyLimitBox;
        private Window? _historyWindow;
        private ListBox? _historyList;
        private TextBox? _historyDetails;
        private TextBlock? _historyNotice;
        private string? _historyOptionsLanguage;
        private int _historyReadVersion;
        private CancellationTokenSource? _historyReadCancellation;

        private void CancelHistoryRead()
        {
            ++_historyReadVersion;
            var source = _historyReadCancellation;
            _historyReadCancellation = null;
            source?.Cancel(); // The owning read disposes its source after its continuation finishes.
        }

        private void ExecutionHistory_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsSettingsChange(sender, e)) return;
            bool previous = _config.ExecutionHistoryEnabled;
            _config.ExecutionHistoryEnabled = ExpHistoryCheck.IsChecked == true;
            if (!ConfigService.TrySave(_config))
            {
                _config.ExecutionHistoryEnabled = previous;
                bool suppressed = _suppressSettings;
                _suppressSettings = true;
                ExpHistoryCheck.IsChecked = previous;
                _suppressSettings = suppressed;
                ShowToast(L10n.T("Tray.SaveFailed"));
                return;
            }
            if (!_config.ExecutionHistoryEnabled) AutoRuleService.StopExecutionHistory();
            SyncExecutionHistoryOptions();
        }

        private void SyncExecutionHistoryOptions()
        {
            if (ExpHistoryOptionsHost == null) return;
            bool enabled = _config.ExperimentalMode && _config.ExecutionHistoryEnabled;
            ExpHistoryOptionsHost.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!enabled)
            {
                ReleaseExecutionHistoryUi();
                ExpHistoryOptionsHost.Content = null;
                return;
            }
            if (ExpHistoryOptionsHost.Content != null && _historyOptionsLanguage == L10n.CurrentLanguage) return;
            ReleaseExecutionHistoryUi();
            var panel = new StackPanel();
            var row = new WrapPanel();
            row.Children.Add(new TextBlock { Text = L10n.T("History.Limit"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0) });
            _historyLimitBox = new TextBox { Width = 70, Height = 32, MaxLength = 2, VerticalContentAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("ModernTextBox"), Text = Math.Max(1, Math.Min(64, _config.ExecutionHistoryLimitMb)).ToString(CultureInfo.InvariantCulture) };
            _historyLimitBox.LostKeyboardFocus += async (_, _) => await SaveHistoryLimitAsync();
            _historyLimitBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SaveHistoryLimitAsync(); } };
            row.Children.Add(_historyLimitBox);
            panel.Children.Add(row);
            panel.Children.Add(new TextBlock { Text = L10n.T("History.LimitHint"), Style = (Style)FindResource("ThemeHint"),
                Margin = new Thickness(0, 6, 0, 10), TextWrapping = TextWrapping.Wrap });
            var buttons = new WrapPanel();
            var view = HistoryButton("History.View");
            view.Click += async (_, _) => await OpenHistoryAsync();
            var clear = HistoryButton("History.Clear");
            clear.Click += async (_, _) =>
            {
                var store = AutoRuleService.GetHistoryStore();
                if (store == null) return;
                clear.IsEnabled = false;
                bool ok = await store.ClearAsync();
                if (_isClosed) return;
                clear.IsEnabled = true;
                ShowToast(L10n.T(ok ? "History.Cleared" : "History.ReadError"));
                if (_historyWindow != null) await RefreshHistoryAsync();
            };
            buttons.Children.Add(view); buttons.Children.Add(clear);
            panel.Children.Add(buttons);
            ExpHistoryOptionsHost.Content = panel;
            _historyOptionsLanguage = L10n.CurrentLanguage;
        }

        private Button HistoryButton(string key) => new() { Content = L10n.T(key), Style = (Style)FindResource("GhostButton"),
            MinHeight = 32, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 4) };

        private async Task SaveHistoryLimitAsync()
        {
            var box = _historyLimitBox;
            if (_isClosed || _suppressSettings || box == null || !_config.ExecutionHistoryEnabled) return;
            if (!int.TryParse(box.Text, out int value) || value < 1 || value > 64)
            {
                box.Text = Math.Max(1, Math.Min(64, _config.ExecutionHistoryLimitMb)).ToString(CultureInfo.InvariantCulture);
                ShowToast(L10n.T("History.InvalidLimit")); return;
            }
            if (value == _config.ExecutionHistoryLimitMb) return;
            int previous = _config.ExecutionHistoryLimitMb;
            _config.ExecutionHistoryLimitMb = value;
            if (!ConfigService.TrySave(_config))
            {
                _config.ExecutionHistoryLimitMb = previous;
                box.Text = previous.ToString(CultureInfo.InvariantCulture);
                ShowToast(L10n.T("Tray.SaveFailed")); return;
            }
            var store = AutoRuleService.GetHistoryStore();
            if (store != null)
            {
                await store.SetLimitAsync(value);
                if (!_isClosed && store.LastError != null) ShowToast(L10n.T("History.ReadError"));
            }
        }

        private async Task OpenHistoryAsync()
        {
            if (_isClosed || !_config.ExperimentalMode || !_config.ExecutionHistoryEnabled) return;
            if (_historyWindow != null) { _historyWindow.Activate(); await RefreshHistoryAsync(); return; }
            var window = new Window { Title = L10n.T("History.Title"), Owner = this, Width = 900, Height = 560,
                MinWidth = 680, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            window.SetResourceReference(BackgroundProperty, "Theme.WindowBg");
            window.SetResourceReference(ForegroundProperty, "Theme.TextPrimary");
            var layout = new DockPanel { Margin = new Thickness(16) };
            var refresh = HistoryButton("History.Refresh");
            refresh.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            refresh.Click += async (_, _) => { refresh.IsEnabled = false; await RefreshHistoryAsync(); refresh.IsEnabled = true; };
            DockPanel.SetDock(refresh, Dock.Top); layout.Children.Add(refresh);
            _historyNotice = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
            DockPanel.SetDock(_historyNotice, Dock.Top); layout.Children.Add(_historyNotice);
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.42, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.58, GridUnitType.Star) });
            _historyList = new ListBox { DisplayMemberPath = nameof(HistoryRow.Text), Margin = new Thickness(0, 0, 10, 0) };
            _historyList.SetResourceReference(BackgroundProperty, "Theme.SurfaceBg");
            _historyList.SetResourceReference(ForegroundProperty, "Theme.TextPrimary");
            _historyDetails = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(10), FontSize = 13 };
            _historyDetails.SetResourceReference(BackgroundProperty, "Theme.SurfaceBg");
            _historyDetails.SetResourceReference(ForegroundProperty, "Theme.TextPrimary");
            _historyList.SelectionChanged += (_, _) => { if (_historyDetails != null && _historyList?.SelectedItem is HistoryRow selected) _historyDetails.Text = FormatHistory(selected.Entry); };
            content.Children.Add(_historyList); Grid.SetColumn(_historyDetails, 1); content.Children.Add(_historyDetails);
            layout.Children.Add(content); window.Content = layout;
            _historyWindow = window;
            window.Closed += (_, _) =>
            {
                if (_historyWindow != window) return;
                CancelHistoryRead();
                _historyWindow = null; _historyList = null; _historyDetails = null; _historyNotice = null;
                window.Content = null;
            };
            window.Show();
            await RefreshHistoryAsync();
        }

        private async Task RefreshHistoryAsync()
        {
            var window = _historyWindow;
            if (window == null) return;
            var store = AutoRuleService.GetHistoryStore();
            if (store == null) return;
            CancelHistoryRead();
            int version = _historyReadVersion;
            var source = _historyReadCancellation = new CancellationTokenSource();
            try
            {
                var idle = store.WhenIdleAsync();
                if (!idle.IsCompleted)
                {
                    var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (source.Token.Register(() => canceled.TrySetResult(true)))
                        await Task.WhenAny(idle, canceled.Task);
                }
                source.Token.ThrowIfCancellationRequested();
                var entries = await store.ReadRecentAsync(source.Token);
                if (_isClosed || _historyWindow != window || version != _historyReadVersion || !_config.ExecutionHistoryEnabled || !_config.ExperimentalMode) return;
                var rows = new List<HistoryRow>();
                foreach (var entry in entries)
                    rows.Add(new HistoryRow { Entry = entry, Text = entry.StartedUtc.ToLocalTime().ToString("MM-dd HH:mm:ss") + "  " + entry.RuleName
                        + Environment.NewLine + L10n.T("History.Status." + entry.Status) + " · " + L10n.T("History.Source." + entry.Source) });
                _historyList!.ItemsSource = rows;
                _historyNotice!.Text = store.LastError != null ? L10n.T("History.ReadError") : rows.Count == 0 ? L10n.T("History.Empty") : "";
                if (rows.Count > 0) _historyList.SelectedIndex = 0;
                else _historyDetails!.Clear();
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested) { }
            finally
            {
                if (ReferenceEquals(_historyReadCancellation, source)) _historyReadCancellation = null;
                source.Dispose();
            }
        }

        private static string FormatHistory(ExecutionHistoryEntry entry)
        {
            var text = new StringBuilder();
            text.AppendLine(entry.RuleName).AppendLine(entry.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine(L10n.T("History.Source." + entry.Source)).AppendLine(L10n.T("History.Status." + entry.Status));
            text.AppendLine(string.Format(L10n.T("History.Summary"), entry.Succeeded, entry.Failed, entry.Skipped));
            text.AppendLine(string.Format(L10n.T("History.Elapsed"), entry.ElapsedMs)).AppendLine();
            foreach (var step in entry.Steps)
            {
                string action = Enum.IsDefined(typeof(AutoRuleAction), step.Action) ? L10n.T(AutoActionKey((AutoRuleAction)step.Action)) : step.Action.ToString(CultureInfo.InvariantCulture);
                text.AppendLine(string.Format(L10n.T("History.Step"), step.Number, action));
                if (step.TargetApp.Length > 0) text.AppendLine(step.TargetApp);
                if (step.TargetPid.HasValue) text.Append("PID ").AppendLine(step.TargetPid.Value.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(step.TargetDeviceId)) text.AppendLine(step.TargetDeviceName ?? step.TargetDeviceId);
                if (!string.IsNullOrEmpty(step.TargetRuleName)) text.AppendLine(step.TargetRuleName);
                if (step.TotalTargets.HasValue) text.AppendLine(string.Format(L10n.T("History.TargetCounts"), step.SucceededTargets ?? 0, step.TotalTargets));
                text.Append(L10n.T("History.Status." + step.Status)).Append(" · ").AppendLine(string.Format(L10n.T("History.Elapsed"), step.ElapsedMs));
                if (step.Reason.Length > 0) text.AppendLine(L10n.T("History.Reason." + step.Reason));
                text.AppendLine();
            }
            if (entry.TotalSteps > 100) text.AppendLine(string.Format(L10n.T("History.StepsTruncated"), entry.TotalSteps - 100));
            return text.ToString();
        }

        private sealed class HistoryRow
        {
            public string Text { get; set; } = "";
            public ExecutionHistoryEntry Entry { get; set; } = null!;
        }

        private void ReleaseExecutionHistoryUi()
        {
            CancelHistoryRead();
            _historyWindow?.Close();
            _historyWindow = null; _historyList = null; _historyDetails = null; _historyNotice = null;
            _historyLimitBox = null; _historyOptionsLanguage = null;
        }
    }
}
