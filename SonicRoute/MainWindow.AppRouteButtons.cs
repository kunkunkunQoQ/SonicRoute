using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;
#endif
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private string? _appsPlaybackAppIdentity;
        private HashSet<string> _appsConfirmedPlayback = new(StringComparer.OrdinalIgnoreCase);
        private string? _appsConfirmedPrimary;
        private QuickPanelModernWindow.PlaybackIntent? _appsPendingPlayback;
        private string? _appsPlaybackAnchor;
        private Task _appsPlaybackChange = Task.CompletedTask;
        private int _appsPlaybackReadVersion, _appsConfirmedPlaybackRead;
        private Func<AudioAppInfo, string?> _readAppsPersistedOutput = static app => ApplicationAudio.GetPersistedEndpoint(app, EDataFlow.eRender);
        private Func<AudioAppInfo, string?, HashSet<string>> _readAppsPlaybackOutputs = AppPlaybackOutputs.GetSelectedForApp;
        private Func<AudioAppInfo, string[], string?, Task<(bool Success, int HResult, string Message)>> _setAppsPlaybackOutputs = AppPlaybackOutputs.SetSelectedAsync;

        private void ResetAppsPlaybackState(string? appIdentity)
        {
            if (string.Equals(_appsPlaybackAppIdentity, appIdentity, StringComparison.Ordinal)) return;
            _appsPlaybackAppIdentity = appIdentity;
            _appsConfirmedPlayback = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _appsConfirmedPrimary = null;
            _appsPendingPlayback = null;
            _appsPlaybackAnchor = null;
            ++_appsPlaybackReadVersion;
            _appsConfirmedPlaybackRead = 0;
        }

        private void ConfirmAppsPlayback(string appIdentity, int read, string primary, HashSet<string> playback)
        {
            if (string.Equals(_appsPlaybackAppIdentity, appIdentity, StringComparison.Ordinal) && read > _appsConfirmedPlaybackRead)
            {
                _appsConfirmedPlaybackRead = read;
                _appsConfirmedPrimary = primary;
                _appsConfirmedPlayback = playback;
            }
        }

        private Task<(string? Persisted, string Primary, HashSet<string> Playback)> ReadAppsPlaybackAsync(AudioAppInfo app) =>
            _uiLifetime.ReadAsync((App: app, ReadPersisted: _readAppsPersistedOutput, ReadOutputs: _readAppsPlaybackOutputs), static target =>
            {
                string? persisted = target.ReadPersisted(target.App);
                string primary = string.IsNullOrWhiteSpace(persisted) || AudioService.IsSystemDefault(persisted)
                    ? AudioService.SystemDefaultDeviceId
                    : AudioPolicyConfig.UnpackDeviceId(persisted!);
                return (persisted, primary, target.ReadOutputs(target.App, primary));
            });

        private static bool IsMultiOutputExternal(AudioAppInfo app)
        {
#if SONICROUTE_PHONE
            return app.IsExternal && ApplicationAudio.ExternalSession(app) is IMultiOutputAudioSession;
#else
            return false;
#endif
        }

        private static string? LowPriorityOutputId(AudioAppInfo app)
        {
#if SONICROUTE_PHONE
            return app.IsExternal && ApplicationAudio.ExternalSession(app) is AndroidAudioSession
                ? AndroidAudioSession.SourcePhoneOutputId
                : null;
#else
            return null;
#endif
        }

        private string? GetAppsDisplayedPrimary(string? fallbackPrimary)
        {
            if (_appsPendingPlayback is { } pending && _appsSelected != null)
            {
                return QuickPanelModernWindow.SelectOptimisticPrimaryOutput(
                    pending.Outputs,
                    pending.PreferredPrimary ?? _appsConfirmedPrimary ?? fallbackPrimary,
                    preferPhysical: !_appsSelected.IsExternal,
                    lowPriorityId: LowPriorityOutputId(_appsSelected));
            }
            return _appsConfirmedPrimary ?? fallbackPrimary;
        }

        private void SyncAppsOutputComboSelection(string? primaryId)
        {
            string? outShort = string.IsNullOrWhiteSpace(primaryId) || AudioService.IsSystemDefault(primaryId)
                ? null
                : AudioPolicyConfig.UnpackDeviceId(primaryId!);
            bool prev = _suppressDevCombo;
            _suppressDevCombo = true;
            try
            {
                if (outShort != null && _appsSelected != null
                    && !AppsOutputCombo.Items.OfType<AudioDeviceInfo>().Any(d => string.Equals(d.Id, outShort, StringComparison.OrdinalIgnoreCase))
                    && _outputDisplay.Any(d => PanelDevices.AreSameEndpoint(d.Id, outShort)))
                {
                    AppsOutputCombo.ItemsSource = PanelDevices.VisibleForAppOutputs(_appsSelected, _outputDisplay, _config, outShort);
                }
                var outChoices = AppsOutputCombo.Items.OfType<AudioDeviceInfo>().ToList();
                AppsOutputCombo.SelectedItem = outShort == null
                    ? outChoices.FirstOrDefault(d => AudioService.IsSystemDefault(d.Id))
                        ?? outChoices.FirstOrDefault(d => d.IsDefault)
                        ?? outChoices.FirstOrDefault()
                    : outChoices.FirstOrDefault(d => string.Equals(d.Id, outShort, StringComparison.OrdinalIgnoreCase))
                        ?? outChoices.FirstOrDefault(d => d.IsDefault)
                        ?? outChoices.FirstOrDefault();
            }
            finally { _suppressDevCombo = prev; }
        }

        private void SetQuickDeviceStyle(Button btn, string style)
        {
            if (!ReferenceEquals(btn.Style, FindResource(style)))
                btn.SetResourceReference(StyleProperty, style);
        }

        private void RenderAppsRouteButtons()
        {
            // Reuse the shared retained devices, names, wrapping and active style.
            AppsOutputQuickPanel.Items.Clear();
            string? multiSelectHint = _appsSelected == null || !_appsSelected.IsExternal || IsMultiOutputExternal(_appsSelected)
                ? L10n.T("Qp.PlaybackMultiSelect")
                : null;
            foreach (var dev in PanelDevices.VisibleForAppOutputs(_appsSelected,
                PanelDevices.WithSystemDefault(DisplayDevices(_outputs), EDataFlow.eRender, _config), _config))
            {
                var btn = PanelDevices.CreateShortcut(dev);
                btn.SetResourceReference(StyleProperty, "QuickDevButton");
                if (multiSelectHint != null)
                    System.Windows.Automation.AutomationProperties.SetHelpText(btn, multiSelectHint);
                var target = dev;
                btn.Click += async (_, _) => await QueueAppsPlaybackClickAsync(target, System.Windows.Input.Keyboard.Modifiers);
                AppsOutputQuickPanel.Items.Add(btn);
            }
            RenderDeviceQuickButtons(AppsInputQuickPanel,
                _config.ExperimentalMic
                    ? PanelDevices.VisibleInputs(PanelDevices.WithSystemDefault(DisplayDevices(_inputs), EDataFlow.eCapture, _config), _config)
                    : Enumerable.Empty<AudioDeviceInfo>(),
                device => SelectAppsRouteDevice(AppsInputCombo, device), (AppsInputCombo.SelectedItem as AudioDeviceInfo)?.Id);
            HighlightAppsRouteButtons();
        }

        private static void SelectAppsRouteDevice(ComboBox combo, AudioDeviceInfo device)
        {
            // The existing combo selection path applies the route and handles errors.
            combo.SelectedItem = combo.Items.OfType<AudioDeviceInfo>().FirstOrDefault(d =>
                string.Equals(d.Id, device.Id, StringComparison.OrdinalIgnoreCase));
        }

        private void HighlightAppsRouteButtons()
        {
            // SelectionChanged can run while InitializeComponent is still creating the cards.
            if (AppsOutputQuickPanel != null)
            {
                if (_appsSelected == null)
                {
                    HighlightDeviceQuickActive(AppsOutputQuickPanel, (AppsOutputCombo.SelectedItem as AudioDeviceInfo)?.Id);
                }
                else
                {
                    var selected = _appsPendingPlayback?.Outputs.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? _appsConfirmedPlayback;
                    if (!_appsSelected.IsExternal)
                    {
                        string? candidate = _appsPendingPlayback is { } pending
                            ? QuickPanelModernWindow.SelectOptimisticPrimaryOutput(pending.Outputs, pending.PreferredPrimary ?? _appsConfirmedPrimary, preferPhysical: true)
                            : _appsConfirmedPrimary;
                        string? native = candidate != null && !ApplicationAudio.IsExternalOutputDevice(candidate) && selected.Contains(candidate)
                            ? candidate
                            : null;
                        foreach (var item in AppsOutputQuickPanel.Items)
                        {
                            if (item is not Button btn || btn.Tag is not AudioDeviceInfo dev) continue;
                            string style = !selected.Contains(dev.Id) ? "QuickDevButton"
                                : native != null && string.Equals(dev.Id, native, StringComparison.OrdinalIgnoreCase) ? "QuickDevButtonActive"
                                : "QuickDevButtonMirror";
                            SetQuickDeviceStyle(btn, style);
                        }
                    }
                    else
                    {
                        foreach (var item in AppsOutputQuickPanel.Items)
                        {
                            if (item is not Button btn || btn.Tag is not AudioDeviceInfo dev) continue;
                            SetQuickDeviceStyle(btn, selected.Contains(dev.Id) ? "QuickDevButtonActive" : "QuickDevButton");
                        }
                    }
                }
            }
            if (AppsInputQuickPanel != null)
                HighlightDeviceQuickActive(AppsInputQuickPanel, (AppsInputCombo.SelectedItem as AudioDeviceInfo)?.Id);
        }

        private Task QueueAppsPlaybackClickAsync(AudioDeviceInfo device, System.Windows.Input.ModifierKeys modifiers)
        {
            if (_appsSelected == null)
            {
                SelectAppsRouteDevice(AppsOutputCombo, device);
                return Task.CompletedTask;
            }

            var app = _appsSelected;
            string appIdentity = app.Identity;
            ResetAppsPlaybackState(appIdentity);

            if (app.IsExternal && !IsMultiOutputExternal(app))
                modifiers = System.Windows.Input.ModifierKeys.None;
            if (!app.IsExternal
                && modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)
                && modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)
                && ApplicationAudio.IsExternalOutputDevice(device.Id))
                return Task.CompletedTask;

            string[] order = AppsOutputQuickPanel.Items.OfType<Button>()
                .Select(b => (b.Tag as AudioDeviceInfo)?.Id)
                .Where(id => id != null)
                .Cast<string>()
                .ToArray();
            if (!order.Contains(device.Id, StringComparer.OrdinalIgnoreCase))
            {
                if (modifiers == System.Windows.Input.ModifierKeys.None)
                    order = [device.Id];
                else
                    return Task.CompletedTask;
            }

            var pending = _appsPendingPlayback;
            var intent = QuickPanelModernWindow.CreatePlaybackIntent(
                order,
                pending?.Outputs.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? _appsConfirmedPlayback,
                device.Id,
                pending?.Anchor ?? _appsPlaybackAnchor,
                modifiers,
                pending?.PreferredPrimary);

#if SONICROUTE_PHONE
            if (app.IsExternal && ApplicationAudio.ExternalSession(app) is AndroidAudioSession android && android.State.Device is { } sourceDevice)
            {
                var next = intent.Outputs;
                bool OwnForcedOutput(string id) => AndroidOutputIdentity.TryParseDeviceId(id, out var key, out _)
                    && string.Equals(key, AndroidDeviceIdentity.Key(sourceDevice), StringComparison.OrdinalIgnoreCase);
                if (next.Contains(AndroidAudioSession.SourcePhoneOutputId, StringComparer.OrdinalIgnoreCase) && next.Any(OwnForcedOutput))
                {
                    next = string.Equals(device.Id, AndroidAudioSession.SourcePhoneOutputId, StringComparison.OrdinalIgnoreCase)
                        ? next.Where(id => !OwnForcedOutput(id)).ToArray()
                        : next.Where(id => !string.Equals(id, AndroidAudioSession.SourcePhoneOutputId, StringComparison.OrdinalIgnoreCase)).ToArray();
                    intent = intent with { Outputs = next };
                }
            }
#endif

            _appsPendingPlayback = intent;
            SyncAppsOutputComboSelection(GetAppsDisplayedPrimary(_appsConfirmedPrimary));
            HighlightAppsRouteButtons();
            var previous = _appsPlaybackChange;
            return _appsPlaybackChange = ApplyClickAsync();

            bool Current() => !_isClosed && _appsSelected?.Identity == appIdentity;

            async Task ApplyClickAsync()
            {
                bool applied = false;
                try
                {
                    await previous;
                    if (!Current()) return;
                    int beforeRead = ++_appsPlaybackReadVersion;
                    var before = await ReadAppsPlaybackAsync(app);
                    if (!Current()) return;
                    ConfirmAppsPlayback(appIdentity, beforeRead, before.Primary, before.Playback);

                    string preferred = QuickPanelModernWindow.SelectOptimisticPrimaryOutput(
                        intent.Outputs,
                        intent.PreferredPrimary ?? before.Primary,
                        preferPhysical: !app.IsExternal,
                        lowPriorityId: LowPriorityOutputId(app));
                    var result = await _uiLifetime.ReadAsync(_setAppsPlaybackOutputs(app, intent.Outputs, preferred));
                    applied = result.Success;
                    if (!Current()) return;
                    if (applied)
                    {
                        _appsPlaybackAnchor = intent.Anchor;
                        if (ApplicationAudio.IsWindowsLinkOutputDevice(device.Id) && intent.Outputs.Contains(device.Id, StringComparer.OrdinalIgnoreCase))
                            ShowToast($"{device.DisplayName} · {result.Message}");
                        if (!app.IsExternal)
                            (Application.Current as App)?.NotifyAppRouteChanged((int)app.ProcessId, caller: this);
                    }
                    else
                    {
                        ShowToast($"✗ {result.Message}");
                    }
                }
                catch (OperationCanceledException) when (!Current()) { }
                catch (Exception ex) { if (Current()) ShowToast($"✗ {ex.Message}"); }
                finally
                {
                    if (Current() && ReferenceEquals(_appsPendingPlayback, intent))
                    {
                        int afterRead = ++_appsPlaybackReadVersion;
                        (string? Persisted, string Primary, HashSet<string> Playback)? confirmed = null;
                        try { confirmed = await ReadAppsPlaybackAsync(app); }
                        catch (OperationCanceledException) when (!Current()) { }
                        catch (Exception ex) { if (Current()) ShowToast($"✗ {ex.Message}"); }

                        if (Current() && ReferenceEquals(_appsPendingPlayback, intent))
                        {
                            if (confirmed is { } actual)
                            {
                                ConfirmAppsPlayback(appIdentity, afterRead, actual.Primary, actual.Playback);
                            }
                            else if (afterRead > _appsConfirmedPlaybackRead)
                            {
                                _appsConfirmedPlaybackRead = afterRead;
                                if (applied)
                                {
                                    _appsConfirmedPlayback = intent.Outputs.ToHashSet(StringComparer.OrdinalIgnoreCase);
                                    _appsConfirmedPrimary = QuickPanelModernWindow.SelectOptimisticPrimaryOutput(
                                        intent.Outputs,
                                        intent.PreferredPrimary ?? _appsConfirmedPrimary,
                                        preferPhysical: !app.IsExternal,
                                        lowPriorityId: LowPriorityOutputId(app));
                                }
                            }
                            _appsPendingPlayback = null;
                            SyncAppsOutputComboSelection(GetAppsDisplayedPrimary(_appsConfirmedPrimary));
                            HighlightAppsRouteButtons();
                        }
                    }
                }
            }
        }
    }
}
