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
        bool selectAll = true);
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
    void OnCaptureLost() { }
    bool NeedsAnimationTick => false;
    void TickAnimation() { }
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
