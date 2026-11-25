using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using SonicRoute.Core;
using SonicRoute.Core.Models;
using ComboBox = System.Windows.Controls.ComboBox;

namespace SonicRoute;

public partial class MainWindow
{
#if SONICROUTE_PHONE
    private bool _externalAudioSubscribed;
    private readonly PendingAudioUiChange _externalAudioQueued = new();
    private void UpdateExternalAudioSubscription()
    {
        bool active = IsVisible && WindowState != WindowState.Minimized && !_isClosed;
        if (active == _externalAudioSubscribed) return;
        _externalAudioSubscribed = active;
        if (active) { ApplicationAudio.UiChanged += ExternalAudioChanged; L10n.Instance.PropertyChanged += ExternalAudioLanguageChanged; RefreshExternalAudioLists(); }
        else { ApplicationAudio.UiChanged -= ExternalAudioChanged; L10n.Instance.PropertyChanged -= ExternalAudioLanguageChanged; }
    }
    private void ExternalAudioLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ExternalAudioChanged(AudioUiChange.All);
    private void ExternalAudioChanged(AudioUiChange change)
    {
        if (_isClosed || !_externalAudioQueued.Add(change)) return;
        _uiLifetime.Post(Dispatcher, () =>
        {
            var pending = _externalAudioQueued.Take();
            if (!_externalAudioSubscribed) return;
            // The apps/settings/automation navigation paths load current candidates on entry.
            if (pending.Has(AudioUiScope.SourceList | AudioUiScope.Outputs)
                && (AppsPage.IsVisible || SettingsPageHost.IsVisible || AutomationPageHost.IsVisible))
            {
                RefreshExternalPages(AppsPage.IsVisible, SettingsPageHost.IsVisible, AutomationPageHost.IsVisible);
                return;
            }
            if (pending.Has(AudioUiScope.Outputs)) SyncExternalOutputDevices();
            if (!AppsPage.IsVisible) return;
            if (pending.Has(AudioUiScope.SourceState))
                foreach (var item in _appItems)
                    if (item.Info.IsExternal && (pending.SourceId == null || pending.SourceId == item.Info.ExternalSourceId))
                        item.IsMuted = ApplicationAudio.IsMuted(item.Info);
            if (_appsSelected is not { } selected) return;
            bool selectedChanged = selected.IsExternal
                ? pending.Has(AudioUiScope.SourceState) && (pending.SourceId == null || pending.SourceId == selected.ExternalSourceId)
                : pending.Has(AudioUiScope.Routes) && (pending.Pid == 0 || pending.Pid == (int)selected.ProcessId);
            if (selectedChanged) _ = RefreshAppsSelectionAsync();
        });
    }
    private void SyncExternalOutputDevices()
    {
        if (_isClosed) return;
        var merged = ApplicationAudio.MergeExternalOutputs(_outputs);
        bool changed = merged.Count != _outputs.Count
            || !_outputs.Zip(merged, static (a, b) => a.Id == b.Id && a.DisplayName == b.DisplayName && a.IsDefault == b.IsDefault).All(static same => same);
        if (!changed) return;
        _outputs = merged;
        if (_autoOutputs != null) _autoOutputs = ApplicationAudio.MergeExternalOutputs(_autoOutputs);
        ReloadDeviceDisplay();
    }
    private List<AppItem> MergeExternalItems(IEnumerable<AppItem> current)
    {
        var previous = current.ToDictionary(item => item.Identity);
        var apps = ApplicationAudio.MergeExternal(previous.Values.Select(item => item.Info));
        var items = apps.Select(info => previous.TryGetValue(info.Identity, out var item) && SameAutoApp(item.Info, info)
            ? ReuseExternalItem(item, info) : AppItem.From(info)).ToList();
        foreach (var item in items.Where(item => item.Info.IsExternal))
        { item.RefreshName(); item.RefreshAutoSwitchState(); item.IsMuted = ApplicationAudio.IsMuted(item.Info); }
        AppItem.LoadIconsAsync(items.Where(item => item.Info.IsExternal), _uiLifetime.Token);
        return items;
    }
    private static AppItem ReuseExternalItem(AppItem item, AudioAppInfo info)
    {
        if (item.Info.IsExternal)
        {
            item.Info.DisplayName = info.DisplayName;
            item.RefreshSourceLabel(info.SourceLabel);
        }
        return item;
    }
    private void RefreshExternalAudioLists() => RefreshExternalPages(true, true, true);
    private void RefreshExternalPages(bool apps, bool settings, bool automation)
    {
        // Merge into existing Windows snapshots. A phone event never re-enumerates Windows sessions.
        if (_isClosed) return;
        SyncExternalOutputDevices();
        var selected = (AppsListBox.SelectedItem as AppItem)?.Identity;
        var previousApps = _appItems;
        var merged = apps ? MergeExternalItems(_appItems) : _appItems;
        // Controls sharing one prior candidate list also share its refreshed result.
        var candidates = new Dictionary<IEnumerable<AppItem>, List<AppItem>>();
        if (apps) candidates[previousApps] = merged;
        if (!_appItems.SequenceEqual(merged))
        {
            _appItems = merged; AppsListBox.ItemsSource = merged;
            AppsListBox.SelectedItem = merged.FirstOrDefault(item => item.Identity == selected);
        }
        else _appItems = merged;
        if (settings) _settingsApps = ApplicationAudio.MergeExternal(_settingsApps);
        if (automation) _autoApps = ApplicationAudio.MergeExternal(_autoApps);
        bool appsSuppressed = _suppressAppCombo, autoSuppressed = _suppressAutoUi;
        _suppressAppCombo = _suppressAutoUi = true;
        try
        {
            IEnumerable<ComboBox?> controls = automation ? _autoStepAppCombos.Prepend(AutoTriggerAppCombo) : Array.Empty<ComboBox>();
            if (settings) controls = controls.Prepend(FixedAppCombo);
            foreach (var combo in controls)
            {
                if (combo?.ItemsSource is not IEnumerable<AppItem> previous) continue;
                if (!candidates.TryGetValue(previous, out var items))
                    candidates[previous] = items = MergeExternalItems(previous);
                if (previous.SequenceEqual(items)) continue;
                var old = combo.SelectedItem as AppItem;
                string? targetName = old?.ProcessName ?? (combo.Tag as AutoRuleStep)?.TargetApp;
                combo.ItemsSource = items;
                combo.SelectedItem = items.FirstOrDefault(item => item.Identity == old?.Identity)
                    ?? items.FirstOrDefault(item => targetName != null && item.ProcessName == targetName);
            }
        }
        finally { _suppressAppCombo = appsSuppressed; _suppressAutoUi = autoSuppressed; }
        if (_appsSelected != null && AppsPage.IsVisible)
        {
            UpdateAppsDetailLabels();
            _ = RefreshAppsSelectionAsync();
        }
    }
#else
    private bool _localAudioSubscribed;
    private int _localAudioRefreshQueued;
    private void UpdateExternalAudioSubscription()
    {
        bool active = IsVisible && WindowState != WindowState.Minimized && !_isClosed;
        if (active == _localAudioSubscribed) return;
        _localAudioSubscribed = active;
        if (active)
        {
            ApplicationAudio.Changed += OnLocalAudioChanged;
            if (_appsSelected != null && AppsPage.IsVisible) _ = RefreshAppsSelectionAsync();
        }
        else
        {
            ApplicationAudio.Changed -= OnLocalAudioChanged;
        }
    }
    private void OnLocalAudioChanged()
    {
        if (_isClosed || Interlocked.Exchange(ref _localAudioRefreshQueued, 1) != 0) return;
        _uiLifetime.Post(Dispatcher, () =>
        {
            Interlocked.Exchange(ref _localAudioRefreshQueued, 0);
            if (!_localAudioSubscribed || !AppsPage.IsVisible || _appsSelected == null) return;
            _ = RefreshAppsSelectionAsync();
        });
    }
#endif

}
