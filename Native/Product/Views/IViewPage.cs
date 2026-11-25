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
    bool OnMouseWheel(float x, float y, float delta, bool horizontal);
    bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt);
    void OnFilesDropped(string[] paths) { }
    void OnCaptureLost() { }
    bool NeedsAnimationTick => false;
    void TickAnimation() { }
}
