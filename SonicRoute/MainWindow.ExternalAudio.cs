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
    private int _externalAudioQueued;
    private void InitializeExternalAudio()
    {
        IsVisibleChanged += ExternalAudioVisibilityChanged;
        UpdateExternalAudioSubscription();
    }
    private void ExternalAudioVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateExternalAudioSubscription();
    private void UpdateExternalAudioSubscription()
    {
        bool active = IsVisible && !_isClosed;
        if (active == _externalAudioSubscribed) return;
        _externalAudioSubscribed = active;
        if (active) { ApplicationAudio.Changed += ExternalAudioChanged; RefreshExternalAudioLists(); }
        else ApplicationAudio.Changed -= ExternalAudioChanged;
    }
    private void ReleaseExternalAudio()
    {
        IsVisibleChanged -= ExternalAudioVisibilityChanged;
        if (_externalAudioSubscribed) ApplicationAudio.Changed -= ExternalAudioChanged;
        _externalAudioSubscribed = false;
    }
    private void ExternalAudioChanged()
    {
        if (_isClosed || Interlocked.Exchange(ref _externalAudioQueued, 1) != 0) return;
        _uiLifetime.Post(Dispatcher, () =>
        {
            Interlocked.Exchange(ref _externalAudioQueued, 0);
            if (!_isClosed && IsVisible) RefreshExternalAudioLists();
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
            ? item : AppItem.From(info)).ToList();
        foreach (var item in items.Where(item => item.Info.IsExternal))
        { item.RefreshName(); item.RefreshAutoSwitchState(); item.IsMuted = ApplicationAudio.IsMuted(item.Info); }
        AppItem.LoadIconsAsync(items.Where(item => item.Info.IsExternal).ToList());
        return items;
    }
    private void RefreshExternalAudioLists()
    {
        // Merge into existing Windows snapshots. A phone event never re-enumerates Windows sessions.
        if (_isClosed) return;
        SyncExternalOutputDevices();
        var selected = (AppsListBox.SelectedItem as AppItem)?.Identity;
        var merged = MergeExternalItems(_appItems);
        if (!_appItems.SequenceEqual(merged))
        {
            _appItems = merged; AppsListBox.ItemsSource = merged;
            AppsListBox.SelectedItem = merged.FirstOrDefault(item => item.Identity == selected);
        }
        else _appItems = merged;
        _settingsApps = ApplicationAudio.MergeExternal(_settingsApps);
        _autoApps = ApplicationAudio.MergeExternal(_autoApps);
        bool appsSuppressed = _suppressAppCombo, autoSuppressed = _suppressAutoUi;
        _suppressAppCombo = _suppressAutoUi = true;
        try
        {
            foreach (var combo in new[] { FixedAppCombo, AutoTriggerAppCombo }.Concat(_autoStepAppCombos))
            {
                if (combo?.ItemsSource is not IEnumerable<AppItem> previous) continue;
                var current = previous.ToList(); var items = MergeExternalItems(current);
                if (current.SequenceEqual(items)) continue;
                var old = combo.SelectedItem as AppItem;
                string? targetName = old?.ProcessName ?? (combo.Tag as AutoRuleStep)?.TargetApp;
                combo.ItemsSource = items;
                combo.SelectedItem = items.FirstOrDefault(item => item.Identity == old?.Identity)
                    ?? items.FirstOrDefault(item => targetName != null && item.ProcessName == targetName);
            }
        }
        finally { _suppressAppCombo = appsSuppressed; _suppressAutoUi = autoSuppressed; }
        if (_appsSelected != null && AppsPage.IsVisible) _ = RefreshAppsSelectionAsync();
    }
#else
    private void InitializeExternalAudio() { }
    private void ReleaseExternalAudio() { }
#endif

}
