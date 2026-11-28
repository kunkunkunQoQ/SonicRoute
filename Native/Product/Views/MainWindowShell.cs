using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SonicRoute.Core;
using SonicRoute.Native.Ui;
using SonicRoute.Updates;

namespace SonicRoute.Native.Product.Views;

internal sealed partial class MainWindowShell : LayeredWindow, IViewHostActions
{
    private static readonly string WindowTitle = $"{BuildIdentity.DisplayName} v{BuildIdentity.ProductVersion}";

    private const uint WmSysCommand = 0x0112;
    private const nuint ScMoveCaption = 0xF012;
    private const float SidebarDurationMs = 180f;

    private readonly ApplicationHost _host;
    private readonly AppConfig _config;
    private readonly Func<MainNavTab, IViewHostActions, IViewPage> _pageFactory;
    private readonly Dictionary<MainNavTab, IViewPage> _pages = new();
    private readonly UiHitMap _hit = new();
    private readonly InlineEditHost _inlineEdit = new();

    protected override UiHitMap InputMap => _hit;

    protected override bool ActivateFocused(UiHitItem item, UiPointerEventArgs e)
    {
        if (item.Id == "shell.titleToggle")
        {
            ToggleSidebar();
            return true;
        }
        if (ActivateShellControl(item.Id)) return true;
        if (_activePage?.ActivateFocused(e, item.Id) != true) return false;
        Invalidate();
        return true;
    }

    private UiThemePalette _theme;
    private MainNavTab _currentTab = MainNavTab.Apps;
    private IViewPage? _activePage;
    private IViewPage? _pressedPage;
    private UiRect _pageViewport;
    private nint _appIconHandle;

    // Sidebar collapse animation (196 <-> 64 DIP, 180ms CubicOut)
    private float _sidebarWidth;
    private float _sidebarStartWidth;
    private float _sidebarTargetWidth;
    private long _sidebarAnimStartTicks;
    private bool _sidebarAnimating;

    // Title drag tracking (click toggles sidebar, >=4px drag initiates SC_MOVE)
    private bool _titleTextPressed;
    private bool _titleBarPressed;
    private float _pressStartX;
    private float _pressStartY;

    // Popups & modals
    private ReadOnlyWindow? _historyViewer, _licenseViewer;
    private PopupMenuWindow? _activeMenu;
    private ComboPopupWindow? _activeCombo;
    private Action<string>? _inlineTextChanged;
    private Action<string, bool>? _inlineCommitted;
    private UiRect _inlineRectDip;
    private float _inlineFontSize = 12.5f;

    // Toast notice pill
    private string? _noticeText;
    private IDisposable? _noticeTimer;

    internal MainWindowShell(
        ApplicationHost host,
        AppConfig config,
        Func<MainNavTab, IViewHostActions, IViewPage> pageFactory)
        : base(UiMetrics.MainDefaultWidth, UiMetrics.MainDefaultHeight, UiMetrics.MainMinWidth, UiMetrics.MainMinHeight)
    {
        _host = host;
        _config = config;
        _pageFactory = pageFactory;
        _theme = UiThemePalette.Resolve(config);
        _sidebarWidth = _sidebarTargetWidth = config.MainSidebarCollapsed
            ? UiMetrics.NavCollapsedWidth
            : UiMetrics.NavExpandedWidth;

        Resizable = true;
        ShowInTaskbar = true;
        EnableFileDrop();

        _inlineEdit.TextChanged += OnInlineTextChanged;
        _inlineEdit.Committed += OnInlineCommitted;
        _host.Updates.Changed += OnUpdatesChanged;
    }

    public ApplicationHost Host => _host;
    public AppConfig Config => _config;
    public UiThemePalette Theme => _theme;
    public LayeredWindow OwnerWindow => this;
    public bool HasOpenModal =>
        _activeMenu is { IsVisible: true } ||
        _activeCombo is { IsVisible: true } ||
        _activePage is { HasOpenModal: true } ||
        _inlineEdit.IsOpen || _licenseViewer is { IsOpen: true };
    public string? ActiveInlineEditTag => _inlineEdit.ActiveTag;
    public UiRect ActiveInlineEditRect => _inlineEdit.FieldBounds.IsEmpty ? _inlineRectDip : _inlineEdit.FieldBounds;

    public void ShowMain(nint ownerHint)
    {
        OwnerHandle = 0; // Main window is top-level in taskbar
        bool firstCreate = Handle == 0;
        EnsureCreated(GetWindowTitle());
        if (firstCreate)
        {
            CenterOnPrimaryWorkArea();
            if (_appIconHandle == 0) _appIconHandle = LoadAppIconHandle();
            if (_appIconHandle != 0) SetIcon(_appIconHandle);
        }

        _theme = UiThemePalette.Resolve(_config);
        EnsureValidTab();
        Show(activate: true);
        RequestFocus();
        ActivateCurrentPage();
    }

#if SONICROUTE_PHONE
    internal void OpenPhoneDevice(string? profileId, bool airPlay)
    {
        var devices = (PhoneDevicePageView)GetPage(MainNavTab.Devices);
        if (profileId == null)
            devices.ShowConnectionPage(airPlay);
        else
            devices.ShowConfigurationPage(profileId);
        NavigateTo(MainNavTab.Devices);
        ShowMain(0);
    }
#endif

    internal void ConfirmAppVolume(string identity, int actual)
    {
        if (!IsDisposed && IsVisible && _activePage is AppsPageView apps)
            apps.ConfirmAppVolume(identity, actual);
    }

    internal void ConfirmEndpointVolume(SonicRoute.Native.Audio.Flow flow, string endpointId, int actual)
    {
        if (!IsDisposed && IsVisible && _activePage is AppsPageView apps)
            apps.ConfirmEndpointVolume(flow, endpointId, actual);
    }

    public void ExternalRefresh()
    {
        if (IsDisposed) return;
        _theme = UiThemePalette.Resolve(_config);
        EnsureValidTab();
        if (IsVisible)
        {
            _activePage?.RefreshData();
            Invalidate();
        }
    }

    internal void ApplySettings()
    {
        if (IsVisible && _activePage is AppsPageView apps) apps.RefreshSettings();
        InvalidateTheme();
    }

    public void InvalidateTheme() => ApplyTheme(UiThemePalette.Resolve(_config));

    internal override void ApplyTheme(UiThemePalette theme)
    {
        _theme = theme;
        _historyViewer?.UpdateTheme();
        _licenseViewer?.UpdateTheme();
        _activePage?.OnThemeChanged(theme);
        if (_inlineEdit.IsOpen)
            _inlineEdit.SetTheme(_inlineEdit.ReadOnlyMultiline ? _theme.SurfaceAlt : _theme.SurfaceBg, _theme.TextPrimary);
        if (IsVisible) Invalidate();
    }

    public void RefreshAll() => ExternalRefresh();

    public void NotifyThemeChanged() => _host.RefreshViewThemes();

    public void NotifySettingsChanged()
    {
        if (!_config.ExperimentalMode || !_config.ExecutionHistoryEnabled) _historyViewer?.Close();
        EnsureValidTab();
        _host.ViewSettingsChanged();
        if (IsVisible) Invalidate();
    }

    public void OpenHistory()
    {
        if (!_config.ExperimentalMode || !_config.ExecutionHistoryEnabled) return;
        if (_historyViewer is { IsOpen: true })
        {
            _historyViewer.Activate();
            _historyViewer.Refresh();
        }
        else _historyViewer = new ReadOnlyWindow(this);
    }

    public void RefreshHistoryIfOpen() => _historyViewer?.Refresh();

    public void ShowProjectLicense(string text)
    {
        if (_licenseViewer is { IsOpen: true }) _licenseViewer.Activate();
        else _licenseViewer = new ReadOnlyWindow(this, text);
    }

    public void ShowNotice(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _noticeText = message;
        _noticeTimer?.Dispose();
        _noticeTimer = _host.ScheduleOnce(TimeSpan.FromSeconds(3.2), () =>
        {
            _noticeTimer = null;
            _noticeText = null;
            if (IsVisible) Invalidate();
        });
        if (IsVisible) Invalidate();
    }

    public void ShowMenu(UiRect anchorDip, List<UiMenuItem> items)
    {
        ClosePopups();
        if (Handle == 0 || items.Count == 0) return;
        _activeMenu = PopupMenuWindow.ShowAtRect(_theme, items, this, anchorDip);
    }

    public void ShowMenuAtCursor(List<UiMenuItem> items, bool alignBottomUp = false)
    {
        ClosePopups();
        if (Handle == 0 || items.Count == 0) return;
        if (GetCursorPos(out var pt))
            _activeMenu = PopupMenuWindow.ShowAtCursor(_theme, items, Handle, pt.X, pt.Y, alignBottomUp);
    }

    public void ShowCombo(UiRect anchorDip, IReadOnlyList<ComboChoice> choices, string selectedKey,
        Action<ComboChoice> onSelected, bool fitChoices)
    {
        ClosePopups();
        if (Handle == 0 || choices.Count == 0) return;
        _activeCombo = ComboPopupWindow.ShowFor(_theme, this, anchorDip, choices, selectedKey, choice =>
        {
            onSelected(choice);
            if (IsVisible) Invalidate();
        }, _host.TryPost, fitChoices);
    }

    public void StartInlineEdit(
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
        bool acceptFileDrop = false,
        bool centered = false)
    {
        if (Handle == 0 || !IsVisible) return;
        ClosePopups();
        CloseInlineEdit(commit: true);
        if (Handle == 0 || !IsVisible || IsDisposed) return;

        _inlineTextChanged = onTextChanged;
        _inlineCommitted = onCommitted;
        _inlineRectDip = rectDip;
        _inlineFontSize = rectDip.Height >= 34f ? 13f : 12f;

        _inlineEdit.Open(
            this,
            tag,
            initialText,
            rectDip,
            _inlineFontSize,
            readOnlyMultiline ? _theme.SurfaceAlt : _theme.SurfaceBg,
            _theme.TextPrimary,
            mono,
            password,
            selectAll,
            readOnlyMultiline,
            maxLength,
            acceptFileDrop,
            centered,
            minWidthPx: 24,
            minHeightPx: 20);
        Invalidate();
    }

    public void SetInlineEditText(string tag, string text) => _inlineEdit.SetText(tag, text);

    public void CloseInlineEdit(bool commit)
    {
        if (_inlineEdit.IsOpen)
        {
            _inlineEdit.Close(commit, viaEnter: false);
            Invalidate();
        }
    }

    internal void OpenUpdateSettings()
    {
        NavigateTo(MainNavTab.Settings);
        if (_activePage is SettingsPageView settings) settings.ScrollToUpdates();
    }

    public void NavigateTo(MainNavTab tab)
    {
        if (tab == MainNavTab.Experimental && !(_config.ExperimentalUnlocked && _config.ExperimentalMode))
            tab = MainNavTab.Settings;
        if (_currentTab == tab && _activePage != null) return;

        CloseInlineEdit(commit: true);
        ClosePopups();
        if (_pressedPage != null) OnCaptureLost();
        _pageViewport = default;
        _activePage?.OnDeactivated();
        _currentTab = tab;
        ActivateCurrentPage();
        Invalidate();
    }

    protected override InlineEditHost ActiveEditor => _inlineEdit;
    protected override bool ShowOwnedWindowTooltips => _currentTab != MainNavTab.Settings;

    protected override string GetWindowTitle() => WindowTitle;

    protected override void OnShown()
    {
        _theme = UiThemePalette.Resolve(_config);
        _activePage?.OnActivated();
    }

    protected override void OnHidden()
    {
        _historyViewer?.Close();
        _licenseViewer?.Close();
        CloseInlineEdit(commit: true);
        ClosePopups();
        _activePage?.OnDeactivated();
        _sidebarAnimating = false;
        _sidebarWidth = _sidebarTargetWidth;
        _modeAnimating = false;
        ReleaseRenderResources();
    }

    protected override bool OnClosing()
    {
        // Hide window on close button instead of destroying process; OnHidden releases D2D/DIB resources.
        return true;
    }

    protected override void OnWindowStateChanged(bool minimized, bool maximized)
    {
        if (minimized)
        {
            CloseInlineEdit(commit: true);
            ClosePopups();
            _activePage?.OnDeactivated();
            StopAnimationTimer();
            _modeAnimating = false;
            _sidebarAnimating = false;
            _sidebarWidth = _sidebarTargetWidth;
        }
        else
        {
            InvalidateTheme();
            _activePage?.OnActivated();
            _activePage?.RefreshData();
            if (_activePage is { NeedsAnimationTick: true })
                StartAnimationTimer(16);
            Invalidate();
        }
    }

    protected override void OnFilesDropped(string[] paths, float xDip, float yDip)
    {
        if (paths.Length == 0 || !IsVisible) return;
        _activePage?.OnFilesDropped(paths, _hit.HitTest(xDip, yDip)?.Id);
        Invalidate();
    }

    protected override void OnCaptureLost()
    {
        _titleTextPressed = false;
        _titleBarPressed = false;
        _hit.ActiveId = null;
        var page = _pressedPage;
        _pressedPage = null;
        page?.OnCaptureLost();
        SyncPageAnimationTimer();
        Invalidate();
    }

    protected override void OnDpiChanged(uint newDpi) => Invalidate();

    protected override void OnFocusLost() => _activePage?.OnFocusLost();

    protected override void OnActivated(bool active)
    {
        if (!active)
        {
            _activePage?.OnFocusLost();
            _titleTextPressed = false;
            _titleBarPressed = false;
        }
    }

    protected override void OnTickAnimation()
    {
        bool fullFrame = _modeAnimating || _sidebarAnimating;
        bool anyAnim = TickThemeSwitch();
        if (_sidebarAnimating)
        {
            float elapsedMs = (float)(Stopwatch.GetTimestamp() - _sidebarAnimStartTicks) * 1000f / Stopwatch.Frequency;
            float p = Math.Clamp(elapsedMs / SidebarDurationMs, 0f, 1f);
            float eased = 1f - MathF.Pow(1f - p, 3f);
            _sidebarWidth = _sidebarStartWidth + (_sidebarTargetWidth - _sidebarStartWidth) * eased;
            if (p >= 1f)
            {
                _sidebarWidth = _sidebarTargetWidth;
                _sidebarAnimating = false;
            }
            else
            {
                anyAnim = true;
            }
        }

        if (_activePage is { NeedsAnimationTick: true } page)
        {
            bool overlayOnly = page is IViewDockPage { NeedsContentAnimationTick: false };
            page.TickAnimation();
            anyAnim |= page.NeedsAnimationTick;
            if (!fullFrame) fullFrame = !overlayOnly || !RefreshRenderOverlay();
        }

        if (!anyAnim) StopAnimationTimer();
        if (fullFrame) Invalidate();
    }

    private void SyncPageAnimationTimer()
    {
        if (_activePage is { NeedsAnimationTick: true }) StartAnimationTimer(16);
        else if (!_sidebarAnimating && !_modeAnimating) StopAnimationTimer();
    }

    protected override void OnRender(D2dTarget g, float widthDip, float heightDip)
    {
        _hit.Clear();
        bool maximized = IsMaximized;
        float radius = maximized ? 0f : UiMetrics.MainCornerRadius;
        var windowRect = new UiRect(0f, 0f, widthDip, heightDip);

        nint layer = 0;
        if (radius > 0f) layer = g.PushRoundedLayer(windowRect, radius);

        g.FillRoundedRect(windowRect, radius, _theme.WindowBgAlpha);

        var titleRect = new UiRect(0f, 0f, widthDip, UiMetrics.TitleBarHeight);
        RenderTitleBar(g, titleRect);

        float bodyY = UiMetrics.TitleBarHeight;
        float bodyH = MathF.Max(0f, heightDip - bodyY);

        var navClip = new UiRect(0f, bodyY, _sidebarWidth, bodyH);
        _pageViewport = new UiRect(navClip.Right, bodyY, MathF.Max(0f, widthDip - navClip.Right), bodyH);
        bool isApps = _currentTab == MainNavTab.Apps;
        var dockPage = _activePage as IViewDockPage;
        if (dockPage == null) DrawNavigation(g, navClip);
        const float padLeft = 9f;
        float padRight = isApps ? 14f : 26f;
        float padTop = isApps ? 10f : 20f;
        float padBottom = isApps ? 10f : 20f;
        var contentRect = new UiRect(
            _pageViewport.X + padLeft,
            bodyY + padTop,
            MathF.Max(40f, _pageViewport.Width - padLeft - padRight),
            MathF.Max(40f, bodyH - padTop - padBottom)).Deflate(1f);
        // The frozen RootBorder reserves 1 DIP on every side of the content presenter.

        if (_activePage != null)
        {
            g.PushClip(_pageViewport);
            _hit.PushClip(_pageViewport);
            _activePage.Render(g, _theme, _hit, contentRect);
            _hit.PopClip();
            g.PopClip();
        }

        if (dockPage != null)
        {
            dockPage.RenderDock(g, _theme, _hit, navClip.Right);
            DrawNavigation(g, navClip);
        }

        if (!string.IsNullOrEmpty(_noticeText))
            RenderNoticeToast(g, widthDip, heightDip, _noticeText);

        if (layer != 0) g.PopRoundedLayer(layer);

        g.DrawRoundedRect(new UiRect(0.5f, 0.5f, widthDip - 1f, heightDip - 1f), radius, _theme.Border, 1f);
    }

    private void DrawNavigation(D2dTarget g, UiRect bounds)
    {
        if (bounds.Width <= 0.5f) return;
        g.PushClip(bounds);
        _hit.PushClip(bounds);
        RenderNavRail(g, bounds);
        _hit.PopClip();
        g.PopClip();
        float edge = bounds.Right - 0.5f;
        g.DrawLine(edge, bounds.Y, edge, bounds.Bottom, _theme.Border, 1f, roundCaps: false);
    }

    private UiDockRail? ActiveDock => (_activePage as IViewDockPage)?.Dock;

    protected override UiRect RenderOverlayBounds => ActiveDock?.OverlayBounds ?? default;

    protected override string? RenderOverlayFocusId => ActiveDock?.OverlayFocusId;

    protected override void OnRenderOverlay(D2dTarget g)
    {
        if (ActiveDock is { } dock)
        {
            dock.RenderOverlay(g, _theme);
            if (_noticeText is { Length: > 0 } notice)
                RenderNoticeToast(g, WidthDip, HeightDip, notice);
        }
    }
    private void RenderTitleBar(D2dTarget g, UiRect rect)
    {
        float textW = D2dSharedContext.Instance.MeasureText(WindowTitle, UiMetrics.FontFamily, 13.5f, 600, 200f, 32f, false).Width;
        var pillRect = new UiRect(8f, (rect.Height - 28f) * 0.5f, textW + 16f, 28f);
        string toggleTip = L10n.T(_config.MainSidebarCollapsed ? "Window.ExpandNavigation" : "Window.CollapseNavigation");
        _hit.Add("shell.titleToggle", pillRect, UiCursorKind.Hand, toggleTip + "\n" + L10n.T("Window.DragToMove"));

        bool pillHot = _hit.IsHover("shell.titleToggle");
        if (pillHot) g.FillRoundedRect(pillRect, 7f, _theme.NavHover);
        UiColor titleFg = _titleTextPressed ? _theme.TextPrimary.WithAlphaFraction(0.7f) : _theme.TextPrimary;
        g.DrawText(WindowTitle, pillRect, titleFg, 13.5f, 600, UiTextAlign.Center, UiVertAlign.Center);

        float btnW = 46f, btnH = 30f;
        float btnY = (rect.Height - btnH) * 0.5f;
        float closeX = rect.Right - 10f - btnW;
        float maxX = closeX - btnW;
        float minX = maxX - btnW;

        float dragRight = minX;

        if (_host.Updates.ShouldNotify)
        {
            string updText = string.Format(L10n.T("Upd.Notice"), _host.Updates.Release?.Tag ?? "");
            float measuredUpd = D2dSharedContext.Instance.MeasureText(updText, UiMetrics.FontFamily, 12f, 600, 260f, 28f, false).Width;
            float updW = Math.Clamp(measuredUpd + 24f, 80f, 300f);
            float updX = minX - 12f - updW;
            if (updX > pillRect.Right + 12f)
            {
                var updRect = new UiRect(updX, (rect.Height - 28f) * 0.5f, updW, 28f);
                UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "shell.updateNotice", updRect, updText, fontSize: 12f, radius: 7f, active: true, tooltip: L10n.T("Upd.Available"));
                dragRight = updRect.X;
            }
        }

        float dragLeft = pillRect.Right + 4f;
        if (dragRight > dragLeft)
            _hit.Add("shell.titleDrag", new UiRect(dragLeft, 0f, dragRight - dragLeft, rect.Height), UiCursorKind.Arrow);

        DrawCaptionButton(g, "shell.min", new UiRect(minX, btnY, btnW, btnH), UiIcon.Minimize, false, L10n.T("Window.Minimize"));
        DrawCaptionButton(g, "shell.max", new UiRect(maxX, btnY, btnW, btnH), IsMaximized ? UiIcon.Restore : UiIcon.Maximize, false,
            L10n.T(IsMaximized ? "Window.Restore" : "Window.Maximize"));
        DrawCaptionButton(g, "shell.close", new UiRect(closeX, btnY, btnW, btnH), UiIcon.Close, true, L10n.T("Window.Close"));
    }

    private void DrawCaptionButton(D2dTarget g, string id, UiRect rect, UiIcon icon, bool isClose, string tooltip)
    {
        _hit.Add(id, rect, UiCursorKind.Hand, tooltip);
        bool hot = _hit.IsHover(id);
        bool down = _hit.IsActive(id);

        if (hot)
        {
            UiColor bg = isClose ? UiColor.FromRgb(0xE8, 0x11, 0x23) : _theme.NavHover;
            if (down) bg = bg.WithAlphaFraction(0.75f);
            g.FillRoundedRect(rect, 7f, bg);
        }

        UiColor fg = (hot && isClose)
            ? UiColor.White
            : hot ? _theme.TextPrimary : _theme.TextSecondary;
        if (down && !isClose) fg = fg.WithAlphaFraction(0.7f);

        var iconBox = new UiRect(rect.CenterX - 5.5f, rect.CenterY - 5.5f, 11f, 11f);
        g.DrawIcon(icon, iconBox, fg, 1.5f);
    }

    private void RenderNavRail(D2dTarget g, UiRect navRect)
    {
        float expanded = (navRect.Width - UiMetrics.NavCollapsedWidth) / (UiMetrics.NavExpandedWidth - UiMetrics.NavCollapsedWidth);
        float sidePadding = 12f + 2f * expanded;
        float x = navRect.X + sidePadding;
        float y = navRect.Y + 22f;
        float itemW = navRect.Width - 2f * sidePadding;

        DrawNavItem(g, MainNavTab.Apps, L10n.T("Nav.Apps"), UiIcon.Apps, new UiRect(x, y, itemW, UiMetrics.NavItemHeight));
        y += UiMetrics.NavItemHeight + 4f;
#if SONICROUTE_PHONE
        DrawNavItem(g, MainNavTab.Devices, L10n.T("Phone.Title"), UiIcon.Devices, new UiRect(x, y, itemW, UiMetrics.NavItemHeight));
        y += UiMetrics.NavItemHeight + 4f;
#endif
        DrawNavItem(g, MainNavTab.Hotkeys, L10n.T("Nav.Hotkeys"), UiIcon.Keyboard, new UiRect(x, y, itemW, UiMetrics.NavItemHeight));
        y += UiMetrics.NavItemHeight + 4f;
        DrawNavItem(g, MainNavTab.Theme, L10n.T("Nav.Theme"), UiIcon.Theme, new UiRect(x, y, itemW, UiMetrics.NavItemHeight));
        y += UiMetrics.NavItemHeight + 4f;
        DrawNavItem(g, MainNavTab.Settings, L10n.T("Nav.Settings"), UiIcon.Settings, new UiRect(x, y, itemW, UiMetrics.NavItemHeight));
        y += UiMetrics.NavItemHeight + 4f;
        DrawNavItem(g, MainNavTab.Automation, L10n.T("Nav.Automation"), UiIcon.Automation, new UiRect(x, y, itemW, UiMetrics.NavItemHeight));

        if (_config.ExperimentalUnlocked && _config.ExperimentalMode)
        {
            y += UiMetrics.NavItemHeight + 8f;
            DrawNavItem(g, MainNavTab.Experimental, L10n.T("Nav.Experimental"), UiIcon.Experiment, new UiRect(x, y, itemW, UiMetrics.NavItemHeight));
        }

        DrawThemeSwitch(g, navRect);
    }

    private void DrawNavItem(D2dTarget g, MainNavTab tab, string label, UiIcon icon, UiRect rect)
    {
        string id = "nav." + tab;
        _hit.Add(id, rect, UiCursorKind.Hand, _config.MainSidebarCollapsed || _sidebarAnimating ? label : null);
        bool selected = _currentTab == tab;
        _hit.Describe(id, label, UiAccessibleRole.PageTab, selected);
        bool hot = _hit.IsHover(id);

        if (selected) g.FillRoundedRect(rect, UiMetrics.NavItemRadius, _theme.NavSelected);
        else if (hot) g.FillRoundedRect(rect, UiMetrics.NavItemRadius, _theme.NavHover);

        float expanded = Math.Clamp((rect.Width - UiMetrics.NavItemHeight) / (UiMetrics.NavExpandedWidth - 28f - UiMetrics.NavItemHeight), 0f, 1f);
        float iconInset = (UiMetrics.NavItemHeight - 19f) * .5f;
        var iconRect = new UiRect(rect.X + iconInset + (14f - iconInset) * expanded, rect.CenterY - 9.5f, 19f, 19f);
        g.DrawIcon(icon, iconRect, selected ? _theme.Accent : _theme.TextSecondary, 1.8f);

        float textWidth = rect.Width - 52f;
        if (textWidth > 8f)
        {
            var textRect = new UiRect(rect.X + 44f, rect.Y, textWidth, rect.Height);
            g.DrawText(label, textRect, _theme.TextPrimary.WithAlphaFraction(expanded), UiMetrics.NavFontSize,
                selected ? 600 : 400, UiTextAlign.Leading, UiVertAlign.Center);
        }
    }

    private void RenderNoticeToast(D2dTarget g, float widthDip, float heightDip, string text)
    {
        float maxW = MathF.Min(460f, widthDip - 64f);
        var size = D2dSharedContext.Instance.MeasureText(text, UiMetrics.FontFamily, 12.5f, 600, maxW - 28f, 44f, wrap: true);
        float pillW = Math.Clamp(size.Width + 28f, 120f, maxW);
        float pillH = MathF.Max(32f, size.Height + 14f);
        var rect = new UiRect((widthDip - pillW) * 0.5f, heightDip - pillH - 18f, pillW, pillH);

        g.FillRoundedRect(rect, pillH * 0.5f, _theme.SurfaceBg);
        g.DrawRoundedRect(rect, pillH * 0.5f, _theme.Accent, 1.2f);
        g.DrawText(text, rect.Deflate(14f, 4f), _theme.TextPrimary, 12.5f, 600, UiTextAlign.Center, UiVertAlign.Center, wrap: true, ellipsis: false);
    }

    protected override void OnPointerMove(float xDip, float yDip, bool leftDown)
    {
        if (_titleTextPressed && leftDown)
        {
            if (MathF.Abs(xDip - _pressStartX) >= 4f || MathF.Abs(yDip - _pressStartY) >= 4f)
            {
                _titleTextPressed = false;
                ReleasePointerCapture();
                if (!IsMaximized) BeginWindowMoveDrag();
                return;
            }
        }
        else if (_titleBarPressed && leftDown)
        {
            if (MathF.Abs(xDip - _pressStartX) >= 4f || MathF.Abs(yDip - _pressStartY) >= 4f)
            {
                _titleBarPressed = false;
                ReleasePointerCapture();
                if (!IsMaximized) BeginWindowMoveDrag();
                return;
            }
        }

        string? previousHover = _hit.Hovered?.Id;
        bool hoverChanged = _hit.UpdateHover(xDip, yDip);
        if (_hit.Hovered is { } hit) SetCursorKind(hit.Cursor);
        else SetCursorKind(UiCursorKind.Arrow);

        bool pageChanged = _activePage?.OnPointerMove(xDip, yDip, leftDown) ?? false;
        if (ActiveDock is { } dock)
        {
            if (!leftDown) pageChanged |= dock.UpdateHover(HasOpenModal ? null : _hit.Hovered?.Id);
            if (hoverChanged && IsPlainDockHover(previousHover) && IsPlainDockHover(_hit.Hovered?.Id))
                hoverChanged = false;
            SyncPageAnimationTimer();
        }
        else if (_pressedPage != null) SyncPageAnimationTimer();
        if (hoverChanged || pageChanged) Invalidate();
    }

    private bool IsPlainDockHover(string? id) =>
        id is null or "canvas.bg" || ActiveDock?.OwnsItem(id) == true;

    protected override void OnPointerLeave()
    {
        bool redraw = !IsPlainDockHover(_hit.Hovered?.Id);
        _hit.ClearHover();
        SetCursorKind(UiCursorKind.Arrow);
        if (ActiveDock is { } dock)
        {
            redraw |= dock.UpdateHover(null);
            SyncPageAnimationTimer();
        }
        else redraw = true;
        if (redraw) Invalidate();
    }

    protected override void OnPointerDown(UiPointerEventArgs e)
    {
        if (_inlineEdit.IsOpen && !ActiveInlineEditRect.Contains(e.X, e.Y))
        {
            CloseInlineEdit(commit: true);
            if (!IsVisible || IsDisposed) return;
        }

        var hit = _hit.HitTest(e.X, e.Y);
        if (!_pageViewport.Contains(e.X, e.Y)) _activePage?.OnFocusLost();
        if (e.Button != UiMouseButton.Left)
        {
            if (_activePage?.OnPointerDown(e, hit?.Id) == true) Invalidate();
            return;
        }
        _hit.ActiveId = hit?.Id;
        if (hit != null) Invalidate();

        if (hit?.Id == "shell.titleToggle")
        {
            _titleTextPressed = true;
            _pressStartX = e.X;
            _pressStartY = e.Y;
            return;
        }
        if (hit?.Id == "shell.titleDrag")
        {
            if (e.ClickCount >= 2)
            {
                ReleasePointerCapture();
                ToggleMaximize();
                return;
            }
            _titleBarPressed = true;
            _pressStartX = e.X;
            _pressStartY = e.Y;
            return;
        }

        _pressedPage = _pageViewport.Contains(e.X, e.Y) ? _activePage : null;
        if (_pressedPage?.OnPointerDown(e, hit?.Id) == true)
            Invalidate();
    }

    protected override void OnPointerUp(UiPointerEventArgs e)
    {
        if (e.Button != UiMouseButton.Left) return;

        var page = _pressedPage;
        _pressedPage = null;
        var releasedId = _hit.HitTest(e.X, e.Y)?.Id;
        var clickId = releasedId == _hit.ActiveId ? releasedId : null;
        if (_hit.ActiveId != null) Invalidate();
        _hit.ActiveId = null;

        if (page != null)
        {
            if (page == _activePage && page.OnPointerUp(e, clickId)) Invalidate();
            SyncPageAnimationTimer();
            return;
        }

        if (_titleTextPressed)
        {
            _titleTextPressed = false;
            if (clickId == "shell.titleToggle" && MathF.Abs(e.X - _pressStartX) < 4f && MathF.Abs(e.Y - _pressStartY) < 4f)
                ToggleSidebar();
            Invalidate();
            return;
        }
        if (_titleBarPressed)
        {
            _titleBarPressed = false;
            Invalidate();
            return;
        }

        ActivateShellControl(clickId);
    }

    private bool ActivateShellControl(string? clickId)
    {
        if (clickId is "shell.mode.system" or "shell.mode.light" or "shell.mode.dark")
        {
            SelectThemeMode(clickId);
            return true;
        }
        switch (clickId)
        {
            case "shell.min": MinimizeWindow(); break;
            case "shell.max": ToggleMaximize(); break;
            case "shell.close": Hide(); break;
            case "shell.updateNotice": OpenUpdateSettings(); break;
            case "nav.Apps": NavigateTo(MainNavTab.Apps); break;
#if SONICROUTE_PHONE
            case "nav.Devices":
                ((PhoneDevicePageView)GetPage(MainNavTab.Devices)).ShowConfigurationPage();
                NavigateTo(MainNavTab.Devices);
                break;
#endif
            case "nav.Hotkeys": NavigateTo(MainNavTab.Hotkeys); break;
            case "nav.Theme": NavigateTo(MainNavTab.Theme); break;
            case "nav.Settings": NavigateTo(MainNavTab.Settings); break;
            case "nav.Automation": NavigateTo(MainNavTab.Automation); break;
            case "nav.Experimental": NavigateTo(MainNavTab.Experimental); break;
            default: return false;
        }
        return true;
    }

    protected override void OnMouseWheel(float xDip, float yDip, float delta, bool horizontal)
    {
        if (_activePage?.OnMouseWheel(xDip, yDip, delta, horizontal) == true)
        {
            if (_pressedPage != null) SyncPageAnimationTimer();
            Invalidate();
        }
    }

    protected override bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        if (_activePage?.OnKeyDown(vk, ctrl, shift, alt) == true ||
            (!ctrl && !alt && _hit.TryGetFocus(out var item, out _) && _activePage?.OnFocusedKey(item.Id, vk) == true))
        {
            if (_pressedPage != null) SyncPageAnimationTimer();
            Invalidate();
            return true;
        }
        return false;
    }

    private void OnUpdatesChanged(string? _)
    {
        _host.TryPost(() =>
        {
            if (!IsDisposed && IsVisible) Invalidate();
        });
    }

    private void ToggleSidebar()
    {
        _config.MainSidebarCollapsed = !_config.MainSidebarCollapsed;
        if (!ConfigService.TryCommit(_config))
        {
            ShowNotice(L10n.T("Tray.SaveFailed"));
            return;
        }

        _sidebarStartWidth = _sidebarWidth;
        _sidebarTargetWidth = _config.MainSidebarCollapsed
            ? UiMetrics.NavCollapsedWidth
            : UiMetrics.NavExpandedWidth;
        _sidebarAnimStartTicks = Stopwatch.GetTimestamp();
        _sidebarAnimating = true;
        StartAnimationTimer(16);
        Invalidate();
    }

    private void BeginWindowMoveDrag()
    {
        if (Handle != 0)
            SendMessageW(Handle, WmSysCommand, ScMoveCaption, 0);
    }

    private IViewPage GetPage(MainNavTab tab)
    {
        if (!_pages.TryGetValue(tab, out var page))
        {
            page = _pageFactory(tab, this);
            _pages[tab] = page;
        }
        return page;
    }

    private void ActivateCurrentPage()
    {
        var page = GetPage(_currentTab);
        _activePage = page;
        if (IsVisible) _activePage.OnActivated();
        if (_activePage.NeedsAnimationTick) StartAnimationTimer(16);
    }

    private void EnsureValidTab()
    {
        if (_currentTab == MainNavTab.Experimental && !(_config.ExperimentalUnlocked && _config.ExperimentalMode))
            NavigateTo(MainNavTab.Settings);
    }

    public void ClosePopups()
    {
        if (_activeMenu is { IsVisible: true }) _activeMenu.CloseWindow();
        _activeMenu = null;
        ComboPopupWindow.Close(ref _activeCombo);
        _activePage?.ClosePopups();
    }



    private void OnInlineTextChanged(string tag, string text) => _inlineTextChanged?.Invoke(text);

    private void OnInlineCommitted(string tag, string text, bool viaEnter)
    {
        var cb = _inlineCommitted;
        _inlineTextChanged = null;
        _inlineCommitted = null;
        cb?.Invoke(text, viaEnter);
        if (IsVisible) Invalidate();
    }

    private static nint LoadAppIconHandle()
    {
        try
        {
            var icon = NativeAppIconService.GetBrandIcon();
            if (icon is { } bmp)
                return SonicRoute.Native.Graphics.WicIconLoader.CreateIconFromBgra32(bmp.BgraPixels, bmp.Width, bmp.Height);
        }
        catch { }
        return 0;
    }

    public override void Dispose()
    {
        _historyViewer?.Close();
        _licenseViewer?.Close();
        _host.Updates.Changed -= OnUpdatesChanged;
        _noticeTimer?.Dispose();
        _noticeTimer = null;
        ClosePopups();
        CloseInlineEdit(commit: true);
        _inlineEdit.TextChanged -= OnInlineTextChanged;
        _inlineEdit.Committed -= OnInlineCommitted;
        _inlineEdit.Dispose();
        foreach (var page in _pages.Values)
            page.Dispose();
        _pages.Clear();
        _activePage = null;
        if (_appIconHandle != 0)
        {
            DestroyIcon(_appIconHandle);
            _appIconHandle = 0;
        }
        base.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint pt);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint hIcon);
}
