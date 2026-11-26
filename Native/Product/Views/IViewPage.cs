using System;
using System.Collections.Generic;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal enum MainNavTab
{
    Apps,
    Hotkeys,
    Theme,
    Settings,
    Automation,
    Experimental,
}

internal interface IViewHostActions
{
    ApplicationHost Host { get; }
    SonicRoute.Core.AppConfig Config { get; }
    UiThemePalette Theme { get; }
    LayeredWindow OwnerWindow { get; }
    bool HasOpenModal { get; }
    string? ActiveInlineEditTag { get; }
    UiRect ActiveInlineEditRect { get; }

    void Invalidate();
    void RefreshAll();
    void NotifyThemeChanged();
    void NotifySettingsChanged();
    void ShowNotice(string message);
    void OpenHistory();
    void RefreshHistoryIfOpen();
    void ShowProjectLicense(string text);
    void ShowMenu(UiRect anchorDip, List<UiMenuItem> items);
    void ShowMenuAtCursor(List<UiMenuItem> items, bool alignBottomUp = false);
    void ShowCombo(UiRect anchorDip, IReadOnlyList<ComboChoice> choices, string selectedKey, Action<ComboChoice> onSelected);
    void StartInlineEdit(
        string tag,
        UiRect rectDip,
        string initialText,
        Action<string>? onTextChanged,
        Action<string, bool> onCommitted,
        bool mono = false,
        bool password = false,
        bool selectAll = false,
        bool readOnlyMultiline = false,
        int maxLength = 0,
        bool acceptFileDrop = false);
    void SetInlineEditText(string tag, string text);
    void CloseInlineEdit(bool commit);
    void NavigateTo(MainNavTab tab);
    void OpenPhoneDevice(string? profileId = null) { }
}

internal interface IViewPage : IDisposable
{
    void OnActivated();
    void OnDeactivated();
    void RefreshData();
    void Render(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect contentRect);
    bool OnPointerMove(float x, float y, bool leftDown);
    bool OnPointerDown(UiPointerEventArgs e, string? targetId);
    bool OnPointerUp(UiPointerEventArgs e, string? clickId);
    bool ActivateFocused(UiPointerEventArgs e, string id) => OnPointerUp(e, id);
    bool OnFocusedKey(string id, int vk) => false;
    bool OnMouseWheel(float x, float y, float delta, bool horizontal);
    bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt);
    void OnFilesDropped(string[] paths) { }
    void OnFilesDropped(string[] paths, string? targetId) => OnFilesDropped(paths);
    void OnCaptureLost() { }
    void OnFocusLost() { }
    bool NeedsAnimationTick => false;
    void TickAnimation() { }
}

// ConfigService owns the existing atomic file and last committed snapshot.
internal static class ViewPersistence
{
    internal static bool CommitSettings(this IViewHostActions actions)
    {
        if (SonicRoute.Core.ConfigService.TryCommit(actions.Config)) return true;
        actions.ShowNotice(L10n.T("Tray.SaveFailed"));
        actions.Invalidate();
        return false;
    }

    internal static bool WriteRule(this IViewHostActions actions, Action write)
    {
        try
        {
            write();
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException or System.Text.Json.JsonException)
        {
            SonicRoute.Core.ExceptionDiagnostics.Record("Native.RuleWrite", ex);
            actions.ShowNotice(L10n.T("Tray.SaveFailed"));
            return false;
        }
    }
}

// The frozen name editors save after 300 ms idle or immediately on focus loss.
// Keep uncommitted text in EDIT so another setting cannot serialize a canceled edit.
internal sealed class AliasEditor(IViewHostActions actions) : IDisposable
{
    private IDisposable? _timer;
    private int _version;

    public void Open(string tag, UiRect rect, string key, bool device, string? displayName = null)
    {
        Dispose();
        int version = _version;
        Dictionary<string, string> Names() => device ? actions.Config.DeviceNames : actions.Config.AppNames;
        void Apply(string text)
        {
            var names = Names();
            string value = text.Trim();
            string previous = names.GetValueOrDefault(key, "");
            if (value == previous) return;
            if (value.Length == 0) names.Remove(key);
            else names[key] = value;
            if (actions.CommitSettings()) actions.NotifySettingsChanged();
        }

        string initial = displayName ?? Names().GetValueOrDefault(key, "");
        bool edited = false;
        actions.StartInlineEdit(tag, rect, initial, text =>
        {
            edited = true;
            _timer?.Dispose();
            _timer = actions.Host.ScheduleOnce(TimeSpan.FromMilliseconds(300), () =>
            {
                _timer = null;
                if (version == _version && actions.ActiveInlineEditTag == tag) Apply(text);
            });
        }, (text, _) =>
        {
            Dispose();
            if (edited || text != initial) Apply(text);
        }, selectAll: !device);
    }

    public void Dispose()
    {
        _version++;
        _timer?.Dispose();
        _timer = null;
    }
}

internal static class ViewSliderKeys
{
    public static bool TryChange(int key, double current, double minimum, double maximum, double small, double large, out double value)
    {
        value = key switch
        {
            0x25 or 0x28 => current - small, // Left, Down
            0x26 or 0x27 => current + small, // Up, Right
            0x21 => current + large,         // Page Up
            0x22 => current - large,         // Page Down
            0x24 => minimum,                 // Home
            0x23 => maximum,                 // End
            _ => current
        };
        value = Math.Clamp(value, minimum, maximum);
        return key is >= 0x21 and <= 0x28;
    }
}
