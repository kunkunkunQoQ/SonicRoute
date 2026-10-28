using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SonicRoute.Core;
using SonicRoute.Core.Compat;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace SonicRoute
{
    public partial class QuickPanelModernWindow
    {
        private sealed class OutputVolumeTarget
        {
            internal readonly string Id;
            internal bool Available = true;
            internal int Version;
            internal OutputVolumeTarget(string id) { Id = id; }
        }

        private sealed class OutputVolumeRow
        {
            internal OutputVolumeTarget Target = null!;
            internal TextBlock Name = null!, Percent = null!;
            internal Button Mute = null!;
            internal Slider Slider = null!;
            internal FrameworkElement Root = null!;
            internal bool Updating, Pending, MuteBusy, Muted, NeedsReadback;
            internal int DisplayVolume = -1, MuteVersion;
        }

        private sealed class OutputVolumeLoad
        {
            internal readonly WindowUiLifetime Lifetime = new();
            internal int TopologyDirty = 1, NotificationQueued;
            internal bool ReadAll;
            internal readonly HashSet<string> ReadDevices = new(StringComparer.OrdinalIgnoreCase);
            internal bool PositionDirty = true, PositionQueued, ForcePosition = true;
            internal Rect? CalculatedRect, AppliedRect;
            internal Point AppliedOwner;
        }

        private readonly Dictionary<string, OutputVolumeRow> _outputVolumeRows = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (OutputVolumeTarget Target, int Percent, int Version)> _pendingOutputVolume = new(StringComparer.OrdinalIgnoreCase);
        private OutputVolumeMonitor? _outputVolumeMonitor;
        private OutputVolumeLoad? _outputLoad;
        private Border? _outputPopupSurface;
        private StackPanel? _outputVolumeRowsPanel;
        private TextBlock? _outputVolumeEmptyText;
        private const double OutputVolumeRowHeight = 64;
        private const double OutputVolumeHeaderHeight = 58;
        private DispatcherTimer? _outputRecoveryTimer;
        private bool _outputRefreshRunning, _outputRefreshAgain, _outputRepositionFlip;
        private int _outputPopupVersion;

        private void InitializeOutputVolumes()
        {
            OutputVolumesToggle.IsChecked = ConfigService.Load().QuickPanelShowAllOutputVolumes;
            UpdateOutputToggle();
            // Anchor to the unanimated owner, not the content's entrance transform.
            OutputVolumesPopup.PlacementTarget = this;
            OutputVolumesPopup.CustomPopupPlacementCallback = (_, targetSize, _) =>
            {
                var rect = GetOutputPopupRect();
                // WPF supplies custom-placement target bounds in screen units.
                // Derive the transform from those bounds for all runtime DPI modes.
                double sx = targetSize.Width / Math.Max(1, ActualWidth);
                double sy = targetSize.Height / Math.Max(1, ActualHeight);
                return new[] { new CustomPopupPlacement(new Point(rect.X * sx, rect.Y * sy), PopupPrimaryAxis.None) };
            };
        }

        private void CloseAfterFocusLeaves()
        {
            if (_isClosed || !IsVisible || !_everFocused || _adjustMode) return;
            _uiLifetime.Post(Dispatcher, () =>
            {
                if (!_isClosed && IsVisible && _everFocused && !_adjustMode && !IsActive
                    && _outputPopupSurface?.IsKeyboardFocusWithin != true) Close();
            });
        }

        private void UpdateOutputToggle()
        {
            string text = L10n.T(OutputVolumesToggle.IsChecked == true ? "Qp.CollapseOutputVolumes" : "Qp.ExpandOutputVolumes");
            OutputVolumesToggle.ToolTip = text;
            OutputVolumesToggle.Content = OutputVolumesToggle.IsChecked == true ? "▾" : "▴";
            System.Windows.Automation.AutomationProperties.SetName(OutputVolumesToggle, text);
        }

        private void OutputVolumesToggle_Click(object sender, RoutedEventArgs e)
        {
            var config = ConfigService.Load();
            config.QuickPanelShowAllOutputVolumes = OutputVolumesToggle.IsChecked == true;
            ConfigService.Save(config);
            UpdateOutputToggle();
            if (config.QuickPanelShowAllOutputVolumes) OpenOutputVolumes();
            else StopOutputVolumes();
        }

        private void OpenOutputVolumes()
        {
            if (_isClosed || !IsVisible || !_contentReady || OutputVolumesToggle.IsChecked != true || _adjustMode) return;
            if (_outputVolumeMonitor == null)
            {
                CreateOutputPopupContent();
                LocationChanged += OutputOwnerLocationChanged;
                DpiChanged += OutputOwnerDpiChanged;
                ++_outputPopupVersion;
                var load = _outputLoad = new OutputVolumeLoad();
                var monitor = _outputVolumeMonitor = new OutputVolumeMonitor();
                monitor.Invalidated += topology => OutputVolumesInvalidated(load, topology);
                // Size and populate the first frame from the owner's device snapshot.
                // The asynchronous topology refresh still confirms changes while folded.
                BuildOutputVolumeRows(OutputVolumeDevices(_outputDisplay, ConfigService.Load()),
                    _outputDisplay.FirstOrDefault(d => d.IsDefault)?.Id);
            }
            PositionOutputPopup();
            OutputVolumesPopup.IsOpen = true;
            _ = RefreshOutputVolumesAsync();
        }

        private void CreateOutputPopupContent()
        {
            if (_outputPopupSurface != null) return;
            _outputPopupSurface = (Border)((DataTemplate)FindResource("OutputVolumePopupTemplate")).LoadContent();
            _outputVolumeRowsPanel = (StackPanel)_outputPopupSurface.FindName("OutputVolumeRowsPanel");
            _outputVolumeEmptyText = (TextBlock)_outputPopupSurface.FindName("OutputVolumeEmptyText");
            _outputPopupSurface.LostKeyboardFocus += OutputPopupLostKeyboardFocus;
            OutputVolumesPopup.Child = _outputPopupSurface;
        }

        private Rect CalculateOutputPopupRect()
        {
            var owner = new Rect(0, 0, Math.Max(1, RootBorder.ActualWidth), Math.Max(1, ActualHeight));
            if (!OsdPlacement.TryRelativeWorkArea(this, out var work))
                work = new Rect(-Left, -Top, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
            return OutputVolumePopupLayout.Arrange(owner, work,
                new Size(Math.Min(owner.Width, work.Width * .75),
                    Math.Min(work.Height * .75, Math.Min(owner.Height,
                        OutputVolumeHeaderHeight + (_outputVolumeRows.Count == 0 ? 32 : _outputVolumeRows.Count * OutputVolumeRowHeight)))));
        }

        private static List<AudioDeviceInfo> OutputVolumeDevices(IEnumerable<AudioDeviceInfo> devices, AppConfig config)
        {
            var hidden = config.PanelOutputVolumesRetainedOnly
                ? new HashSet<string>(config.HiddenOutputDevices ?? new List<string>(), StringComparer.OrdinalIgnoreCase) : null;
            return devices.Where(d => d.Flow == EDataFlow.eRender && !string.IsNullOrWhiteSpace(d.Id)
                && !AudioService.IsSystemDefault(d.Id) && hidden?.Contains(d.Id) != true).ToList();
        }

        internal void RefreshOutputVolumeFilter()
        {
            if (_isClosed || !OutputVolumesPopup.IsOpen || _outputVolumeRowsPanel == null) return;
            // Settings edits reuse the visible panel's existing device snapshot.
            // No enumeration or deferred feature creation when the extra panel is closed.
            var config = ConfigService.Load();
            int oldCount = _outputVolumeRows.Count;
            BuildOutputVolumeRows(OutputVolumeDevices(_outputDisplay, config), _outputDisplay.FirstOrDefault(d => d.IsDefault)?.Id);
            if (oldCount != _outputVolumeRows.Count) PositionOutputPopup();
            _ = RefreshOutputVolumesAsync();
        }

        private Rect GetOutputPopupRect()
        {
            var load = _outputLoad;
            if (load == null) return CalculateOutputPopupRect();
            if (load.PositionDirty || !load.CalculatedRect.HasValue)
            {
                load.CalculatedRect = CalculateOutputPopupRect();
                load.PositionDirty = false;
            }
            return load.CalculatedRect.Value;
        }

        private void PositionOutputPopup()
        {
            if (_isClosed || !IsVisible || _outputPopupSurface == null) return;
            var load = _outputLoad;
            if (load == null) { ApplyOutputPopupPosition(); return; }
            load.PositionDirty = true;
            if (!OutputVolumesPopup.IsOpen) { ApplyOutputPopupPosition(); return; }
            if (load.PositionQueued) return;
            load.PositionQueued = true;
            load.Lifetime.Post(Dispatcher, () =>
            {
                load.PositionQueued = false;
                if (ReferenceEquals(load, _outputLoad) && OutputVolumesPopup.IsOpen) ApplyOutputPopupPosition();
            }, DispatcherPriority.Render);
        }

        private void ApplyOutputPopupPosition()
        {
            if (_isClosed || !IsVisible || _outputPopupSurface == null) return;
            var rect = GetOutputPopupRect();
            if (_outputPopupSurface.Width != rect.Width) _outputPopupSurface.Width = rect.Width;
            if (_outputPopupSurface.Height != rect.Height) _outputPopupSurface.Height = rect.Height;
            var load = _outputLoad;
            var owner = new Point(Left, Top);
            if (load != null)
            {
                bool changed = load.ForcePosition || load.AppliedRect != rect || load.AppliedOwner != owner;
                load.AppliedRect = rect; load.AppliedOwner = owner; load.ForcePosition = false;
                if (!changed) return;
            }
            // Popup does not follow an owner move automatically. A property change
            // requests native repositioning without polling or recreating its HWND.
            _outputRepositionFlip = !_outputRepositionFlip;
            OutputVolumesPopup.HorizontalOffset = _outputRepositionFlip ? .001 : 0;
        }

        private void InvalidateOutputPopupScreen()
        {
            if (_outputLoad != null) _outputLoad.ForcePosition = true;
            PositionOutputPopup();
        }

        private void OutputOwnerDpiChanged(object sender, System.Windows.DpiChangedEventArgs e) => InvalidateOutputPopupScreen();

        private void OutputOwnerLocationChanged(object? sender, EventArgs e)
        {
            if (OutputVolumesPopup.IsOpen) PositionOutputPopup();
        }

        private void OutputPopupLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CloseAfterFocusLeaves();

        private void OutputPopup_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        }

        private void OutputVolumesInvalidated(OutputVolumeLoad load, bool topology)
        {
            if (_isClosed || load.Lifetime.Token.IsCancellationRequested) return;
            if (topology) Interlocked.Exchange(ref load.TopologyDirty, 1);
            if (Interlocked.Exchange(ref load.NotificationQueued, 1) != 0) return;
            load.Lifetime.Post(Dispatcher, () =>
            {
                Interlocked.Exchange(ref load.NotificationQueued, 0);
                if (ReferenceEquals(load, _outputLoad) && !_isClosed && OutputVolumesPopup.IsOpen)
                    _ = RefreshOutputVolumesAsync();
            });
        }

        private async Task RefreshOutputVolumesAsync()
        {
            if (_isClosed || !OutputVolumesPopup.IsOpen || _outputVolumeMonitor == null || _outputLoad == null) return;
            if (_outputRefreshRunning) { _outputRefreshAgain = true; return; }
            _outputRefreshRunning = true;
            var monitor = _outputVolumeMonitor;
            var load = _outputLoad;
            int version = _outputPopupVersion;
            bool Current() => !_isClosed && version == _outputPopupVersion && ReferenceEquals(monitor, _outputVolumeMonitor);
            try
            {
                do
                {
                    _outputRefreshAgain = false;
                    bool refreshAll = false;
                    if (Interlocked.Exchange(ref load.TopologyDirty, 0) != 0)
                    {
                        refreshAll = true;
                        var devices = await load.Lifetime.ReadAsync(static () =>
                            (Outputs: AudioService.GetDevices(EDataFlow.eRender), Default: AudioService.GetDefaultDeviceId(EDataFlow.eRender)));
                        if (!Current()) return;
                        var config = ConfigService.Load();
                        var outputs = DisplayDevices(devices.Outputs, config);
                        ApplyOutputDeviceSnapshot(outputs, devices.Default, config);
                        int oldCount = _outputVolumeRows.Count;
                        BuildOutputVolumeRows(OutputVolumeDevices(outputs, config), devices.Default);
                        if (oldCount != _outputVolumeRows.Count) PositionOutputPopup();
                        if (_systemDeviceId == null || !_outputVolumeRows.ContainsKey(_systemDeviceId))
                        {
                            // The owner's read may still be useful after collapse;
                            // the extra panel must stop awaiting it immediately.
                            await load.Lifetime.ReadAsync(RefreshSystemVolumeAsync());
                            if (!Current()) return;
                        }
                    }
                    refreshAll |= load.ReadAll;
                    load.ReadAll = false;
                    var versions = CaptureOutputVolumeVersions();
                    var requested = load.ReadDevices.Count == 0 ? null : load.ReadDevices.ToArray();
                    load.ReadDevices.Clear();
                    var states = await load.Lifetime.ReadAsync(monitor.ReadAsync(versions.Keys, refreshAll, requested));
                    if (!Current()) return;
                    ApplyOutputVolumeSnapshot(states, versions);
                    if (_systemDeviceId == null || !_outputDisplay.Any(d => string.Equals(d.Id, _systemDeviceId, StringComparison.OrdinalIgnoreCase)))
                        ApplySystemVolumeUnavailable();
                } while (_outputRefreshAgain && Current());
            }
            catch (OperationCanceledException) when (load.Lifetime.Token.IsCancellationRequested) { }
            catch
            {
                if (Current()) Interlocked.Exchange(ref load.TopologyDirty, 1);
            }
            finally
            {
                _outputRefreshRunning = false;
                if (Current())
                {
                    // Healthy notification registrations cause no timer wakeups.
                    if (monitor.NeedsRecovery || Volatile.Read(ref load.TopologyDirty) != 0)
                    {
                        if (_outputRecoveryTimer == null)
                        {
                            _outputRecoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                            _outputRecoveryTimer.Tick += OutputRecovery_Tick;
                        }
                        _outputRecoveryTimer.Start();
                    }
                    else _outputRecoveryTimer?.Stop();
                }
                else if (!_isClosed && OutputVolumesPopup.IsOpen) _ = RefreshOutputVolumesAsync();
            }
        }

        private void OutputRecovery_Tick(object? sender, EventArgs e)
        {
            _outputRecoveryTimer?.Stop();
            if (_isClosed || !OutputVolumesPopup.IsOpen) return;
            if (_outputLoad == null) return;
            if (_outputVolumeMonitor?.NeedsTopologyRecovery == true)
                Interlocked.Exchange(ref _outputLoad.TopologyDirty, 1);
            _ = RefreshOutputVolumesAsync();
        }

        private Dictionary<string, (OutputVolumeTarget Target, int Version, int MuteVersion)> CaptureOutputVolumeVersions() =>
            _outputVolumeRows.ToDictionary(p => p.Key, p => (p.Value.Target, p.Value.Target.Version, p.Value.MuteVersion), StringComparer.OrdinalIgnoreCase);

        private void ApplyOutputVolumeSnapshot(Dictionary<string, (int Volume, bool Muted)> states,
            Dictionary<string, (OutputVolumeTarget Target, int Version, int MuteVersion)> versions)
        {
            foreach (var pair in states)
            {
                if (!_outputVolumeRows.TryGetValue(pair.Key, out var row) || !versions.TryGetValue(pair.Key, out var snapshot)
                    || !ReferenceEquals(row.Target, snapshot.Target)) continue;
                if (row.Pending || row.MuteBusy || row.Target.Version != snapshot.Version || row.MuteVersion != snapshot.MuteVersion)
                {
                    // The monitor has consumed this notification. A skipped read
                    // must be reconciled after the explicit action settles.
                    row.NeedsReadback = true;
                    QueueDeferredOutputReadback(row);
                    continue;
                }
                ApplyOutputVolumeState(row, pair.Value.Volume, pair.Value.Muted);
                if (string.Equals(pair.Key, _systemDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    if (pair.Value.Volume >= 0) ApplySystemVolumeControls(pair.Key, pair.Value.Volume, pair.Value.Muted);
                    else ApplySystemVolumeUnavailable();
                }
            }
        }

        private void ApplyOutputDeviceSnapshot(List<AudioDeviceInfo> outputs, string? defaultId, AppConfig config)
        {
            foreach (var device in outputs) device.IsDefault = string.Equals(device.Id, defaultId, StringComparison.OrdinalIgnoreCase);
            var previous = SystemDevCombo.ItemsSource as IList<AudioDeviceInfo>;
            bool same = previous != null && previous.Count == outputs.Count;
            for (int i = 0; same && i < outputs.Count; i++)
                same = string.Equals(previous![i].Id, outputs[i].Id, StringComparison.OrdinalIgnoreCase)
                    && previous[i].DisplayName == outputs[i].DisplayName && previous[i].IsDefault == outputs[i].IsDefault;
            _outputDisplay = PanelDevices.WithSystemDefault(outputs, EDataFlow.eRender, config);
            if (same) return;
            RefreshRowDeviceNames();
            string? selected = _systemDeviceId;
            SetSystemDevices(outputs, defaultId);
            var retained = outputs.FirstOrDefault(d => string.Equals(d.Id, selected, StringComparison.OrdinalIgnoreCase));
            if (retained == null) return;
            _suppressDevCombo = true;
            try { SystemDevCombo.SelectedItem = retained; _systemDeviceId = retained.Id; }
            finally { _suppressDevCombo = false; }
        }

        private void BuildOutputVolumeRows(List<AudioDeviceInfo> devices, string? defaultId)
        {
            var ids = new HashSet<string>(devices.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
            foreach (string id in _outputVolumeRows.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                var row = _outputVolumeRows[id];
                Volatile.Write(ref row.Target.Available, false);
                _pendingOutputVolume.Remove(id);
                _outputVolumeRowsPanel!.Children.Remove(row.Root);
                _outputVolumeRows.Remove(id);
            }
            // Existing row order stays stable during notifications and dragging.
            foreach (var device in devices)
            {
                if (!_outputVolumeRows.TryGetValue(device.Id, out var row))
                {
                    row = CreateOutputVolumeRow(device.Id);
                    _outputVolumeRows.Add(device.Id, row);
                    _outputVolumeRowsPanel!.Children.Add(row.Root);
                    // A rapid filter reversal may reuse a monitor subscription
                    // whose dirty state was consumed by an older, discarded row.
                    if (_outputLoad != null) _outputLoad.ReadAll = true;
                }
                string name = device.DisplayName ?? device.Id;
                string title = name + (string.Equals(device.Id, defaultId, StringComparison.OrdinalIgnoreCase)
                    ? " · " + L10n.T("Qp.DefaultOutputBadge") : "");
                if (row.Name.Text != title) { row.Name.Text = title; row.Name.ToolTip = title; }
                if (System.Windows.Automation.AutomationProperties.GetName(row.Slider) != name)
                    System.Windows.Automation.AutomationProperties.SetName(row.Slider, name);
                string muteName = name + " " + L10n.T("Qp.Mute");
                if (System.Windows.Automation.AutomationProperties.GetName(row.Mute) != muteName)
                    System.Windows.Automation.AutomationProperties.SetName(row.Mute, muteName);
            }
            _outputVolumeRowsPanel!.ToolTip = devices.Count == 0 ? L10n.T("Ov.NoDevice") : null;
            if (_outputVolumeEmptyText != null) _outputVolumeEmptyText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private OutputVolumeRow CreateOutputVolumeRow(string id)
        {
            var row = new OutputVolumeRow { Target = new OutputVolumeTarget(id) };
            var root = new StackPanel { Height = OutputVolumeRowHeight };
            row.Name = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Height = 22 };
            row.Name.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextPrimary");
            root.Children.Add(row.Name);
            var controls = new DockPanel();
            row.Mute = new Button { Content = L10n.T("Qp.Mute"), Style = (Style)FindResource("QuickPanelToolButton"),
                MinWidth = 54, Height = 28, Margin = new Thickness(0, 0, 6, 0), IsEnabled = false };
            DockPanel.SetDock(row.Mute, Dock.Left);
            controls.Children.Add(row.Mute);
            row.Percent = new TextBlock { Text = "—", Width = 42, FontSize = 12.5, TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            row.Percent.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextPrimary");
            DockPanel.SetDock(row.Percent, Dock.Right);
            controls.Children.Add(row.Percent);
            row.Slider = new Slider { Minimum = 0, Maximum = 100, Height = 32, IsMoveToPointEnabled = true, IsEnabled = false };
            controls.Children.Add(row.Slider);
            VolumePresets.Attach(row.Slider);
            root.Children.Add(controls);
            row.Root = root;
            row.Slider.ValueChanged += (_, _) =>
            {
                int displayed = (int)Math.Round(row.Slider.Value);
                if (row.DisplayVolume != displayed) { row.Percent.Text = displayed + "%"; row.DisplayVolume = displayed; }
                if (row.Updating || _isClosed || !row.Target.Available) return;
                row.Pending = true;
                int request = ++row.Target.Version;
                _pendingOutputVolume[id] = (row.Target, (int)Math.Round(row.Slider.Value), request);
                SchedulePendingVolumeFlush();
            };
            row.Slider.PreviewMouseWheel += (_, e) =>
            {
                if (!row.Slider.IsEnabled || e.Delta == 0) return;
                row.Slider.Value = MathEx.Clamp(row.Slider.Value + Math.Sign(e.Delta) * MathEx.Clamp(ConfigService.Load().VolumeStep, 1, 20), 0, 100);
                e.Handled = true;
            };
            row.Mute.Click += async (_, _) =>
            {
                if (row.MuteBusy || !row.Target.Available || _isClosed) return;
                ++row.MuteVersion;
                row.MuteBusy = true;
                row.Mute.IsEnabled = false;
                FlushPendingVolumes();
                try { await _volumeWriteTask; } catch { /* A previous application write must not suppress this explicit device action. */ }
                var result = await Task.Run(() => row.Target.Available
                    ? SystemVolumeService.ToggleMuteChecked(id) : (Muted: false, Applied: false));
                if (_isClosed || !_outputVolumeRows.TryGetValue(id, out var current) || !ReferenceEquals(current, row)) return;
                ApplyOutputMuteReadback(row, result.Muted, result.Applied);
            };
            return row;
        }

        private void ApplyOutputVolumeState(OutputVolumeRow row, int volume, bool muted)
        {
            row.Updating = true;
            try
            {
                if (row.Slider.IsEnabled != (volume >= 0)) row.Slider.IsEnabled = volume >= 0;
                if (row.Mute.IsEnabled != (volume >= 0 && !row.MuteBusy)) row.Mute.IsEnabled = volume >= 0 && !row.MuteBusy;
                if (volume >= 0 && row.Slider.Value != volume) row.Slider.Value = volume;
                if (row.DisplayVolume != volume) { row.Percent.Text = volume >= 0 ? volume + "%" : "—"; row.DisplayVolume = volume; }
                if (row.Muted != muted)
                {
                    row.Muted = muted;
                    if (muted) row.Mute.SetResourceReference(Button.ForegroundProperty, "Theme.Accent");
                    else row.Mute.ClearValue(Button.ForegroundProperty);
                }
            }
            finally { row.Updating = false; }
        }

        private void ApplyOutputVolumeWriteReadback(OutputVolumeTarget target, int version, int actual)
        {
            if (_isClosed || !_outputVolumeRows.TryGetValue(target.Id, out var row)
                || !ReferenceEquals(row.Target, target) || version != target.Version) return;
            row.Pending = false;
            ++target.Version; // Reject monitor reads that started before this confirmed write.
            ApplyOutputVolumeState(row, actual, row.Muted);
            if (actual >= 0 && string.Equals(target.Id, _systemDeviceId, StringComparison.OrdinalIgnoreCase))
                ApplySystemVolumeControls(target.Id, actual, row.Muted);
            if (actual < 0) row.NeedsReadback = true;
            QueueDeferredOutputReadback(row);
        }

        private void ApplyOutputMuteReadback(OutputVolumeRow row, bool muted, bool applied)
        {
            row.MuteBusy = false;
            ++row.MuteVersion;
            if (applied)
            {
                ApplyOutputVolumeState(row, (int)Math.Round(row.Slider.Value), muted);
                if (string.Equals(row.Target.Id, _systemDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    ++_systemVolumeRequest;
                    ApplySystemMuteVisual(muted);
                }
            }
            else
            {
                row.Mute.IsEnabled = row.Slider.IsEnabled;
                ShowOsd(row.Name.Text, L10n.T("Tray.AudioUnavailable"));
                row.NeedsReadback = true;
            }
            QueueDeferredOutputReadback(row);
        }

        private void QueueDeferredOutputReadback(OutputVolumeRow row)
        {
            if (!row.NeedsReadback || row.Pending || row.MuteBusy) return;
            row.NeedsReadback = false;
            RequestOutputVolumeReadback(row.Target.Id);
        }

        private void RequestOutputVolumeReadback(string? deviceId)
        {
            if (_outputLoad == null) return;
            if (deviceId == null) _outputLoad.ReadAll = true;
            else _outputLoad.ReadDevices.Add(deviceId);
            _ = RefreshOutputVolumesAsync();
        }

        private void SyncExpandedOutputReadback(string? id, int actual, bool muted)
        {
            if (id != null && _outputVolumeRows.TryGetValue(id, out var row))
            {
                if (row.Pending || row.MuteBusy) { row.NeedsReadback = true; return; }
                ++row.Target.Version; // A pending older monitor snapshot cannot replace this readback.
                ApplyOutputVolumeState(row, actual, muted);
            }
        }

        private void StopOutputVolumes()
        {
            ++_outputPopupVersion;
            var load = _outputLoad;
            _outputLoad = null;
            load?.Lifetime.Dispose();
            FlushPendingVolumes();
            OutputVolumesPopup.IsOpen = false;
            LocationChanged -= OutputOwnerLocationChanged;
            DpiChanged -= OutputOwnerDpiChanged;
            _outputRecoveryTimer?.Stop();
            if (_outputRecoveryTimer != null) _outputRecoveryTimer.Tick -= OutputRecovery_Tick;
            _outputRecoveryTimer = null;
            var monitor = _outputVolumeMonitor;
            _outputVolumeMonitor = null;
            monitor?.Dispose();
            _outputVolumeRowsPanel?.Children.Clear();
            _outputVolumeRows.Clear();
            if (_outputPopupSurface != null) _outputPopupSurface.LostKeyboardFocus -= OutputPopupLostKeyboardFocus;
            OutputVolumesPopup.Child = null;
            _outputPopupSurface = null;
            _outputVolumeRowsPanel = null;
            _outputVolumeEmptyText = null;
        }
    }
}
