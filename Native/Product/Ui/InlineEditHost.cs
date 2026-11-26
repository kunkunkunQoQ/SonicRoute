using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SonicRoute.Native.Ui;

/// <summary>
/// Owned borderless Win32 Unicode EDIT popup layered cleanly over Direct2D ModernTextBox bounds.
/// Provides native IME composition, copy/paste, selection, theme colors, and safe deferred focus teardown.
/// </summary>
internal sealed unsafe class InlineEditHost : IDisposable
{
    private const string HostClassName = "SonicRoute.NativeUi.InlineEditHost";
    private const uint WsPopup = 0x80000000;
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExAcceptFiles = 0x00000010;
    private const uint EsAutoHScroll = 0x0080;
    private const uint EsPassword = 0x0020;
    private const uint EsLeft = 0x0000;

    private const uint WmCommand = 0x0111;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmCtlColorEdit = 0x0133;
    private const uint WmKillFocus = 0x0008;
    private const uint WmKeyDown = 0x0100;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmDestroy = 0x0002;
    private const uint WmSetFont = 0x0030;
    private const uint WmUserCommitClose = 0x0402;
    private const uint EmSetSel = 0x00B1;
    private const uint EmSetMargins = 0x00D3;

    private static readonly Dictionary<nint, InlineEditHost> Hosts = new();
    private static readonly Dictionary<nint, InlineEditHost> Edits = new();
    private static bool _registered;
    private static readonly delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> HostProcPtr = &HostWndProc;
    private static readonly delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> EditSubProcPtr = &EditSubProc;

    private nint _hostHwnd;
    private nint _ownerHwnd;
    private nint _editHwnd;
    private nint _origEditProc;
    private nint _bgBrush;
    private nint _font;
    private uint _bgColorRef;
    private uint _fgColorRef;
    private uint _fontDpi;
    private float _fontSizeDip;
    private bool _mono;
    private bool _readOnlyMultiline;
    private bool _suppressChange;
    private bool _closingPending;
    private bool _composing;
    private string _lastChanged = "";
    private bool _layoutSeen;
    private UiRect _textBounds, _visibleBounds;
    internal UiRect FieldBounds { get; private set; }
    private (int X, int Y, int W, int H, int ChildX, int ChildY, int ChildW, int ChildH)? _placement;
    private string _placeholder = "";
    private bool _disposed;

    public string? ActiveTag { get; private set; }
    internal bool ReadOnlyMultiline => _readOnlyMultiline;
    public bool IsOpen => _hostHwnd != 0 && _editHwnd != 0;

    public event Action<string, string>? TextChanged;
    public event Action<string, string, bool>? Committed; // tag, text, viaEnter

    public void Open(
        nint ownerHwnd,
        string tag,
        string initialText,
        int screenXPx,
        int screenYPx,
        int widthPx,
        int heightPx,
        float fontSizeDip,
        uint dpi,
        UiColor bg,
        UiColor fg,
        bool mono = false,
        bool password = false,
        bool selectAll = false,
        bool readOnlyMultiline = false,
        int maxLength = 0,
        bool acceptFileDrop = false,
        bool centered = false)
    {
        if (_disposed) return;
        EnsureClass();
        Close(commit: false, viaEnter: false);

        ActiveTag = tag;
        _ownerHwnd = ownerHwnd;
        _mono = mono;
        _readOnlyMultiline = readOnlyMultiline;
        FieldBounds = default;
        _fontSizeDip = fontSizeDip;
        _fontDpi = dpi;
        _closingPending = false;
        _composing = false;
        _lastChanged = initialText ?? "";
        _layoutSeen = false;
        _placement = null;
        _placeholder = "";
        UpdateColors(bg, fg);

        uint dropStyle = acceptFileDrop ? WsExAcceptFiles : 0;
        _hostHwnd = CreateWindowExW(
            WsExToolWindow | dropStyle, HostClassName, "", WsPopup,
            screenXPx + (int)MathF.Round(11 * dpi / 96f), screenYPx + (heightPx - Math.Max(1, (int)MathF.Round(fontSizeDip * 1.45f * dpi / 96f))) / 2,
            Math.Max(1, widthPx - (int)MathF.Round(22 * dpi / 96f)), Math.Max(1, (int)MathF.Round(fontSizeDip * 1.45f * dpi / 96f)),
            ownerHwnd, 0, GetModuleHandleW(null), 0);
        if (_hostHwnd == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Hosts[_hostHwnd] = this;

        uint editStyle = WsChild | WsVisible | (centered ? 1u /* ES_CENTER */ : EsLeft) | (readOnlyMultiline ? 0x00200844u /* WS_VSCROLL | ES_READONLY | ES_AUTOVSCROLL | ES_MULTILINE */ : EsAutoHScroll) | (password ? EsPassword : 0u);
        int editH = Math.Max(16, (int)MathF.Round(fontSizeDip * 1.45f * (dpi / 96f)));
        int editY = 0;
        _suppressChange = true;
        _editHwnd = CreateWindowExW(
            dropStyle, "EDIT", readOnlyMultiline ? initialText?.ReplaceLineEndings("\r\n") ?? "" : initialText ?? "", editStyle,
            0, editY, Math.Max(1, widthPx - (int)MathF.Round(22 * dpi / 96f)), editH,
            _hostHwnd, 1001, GetModuleHandleW(null), 0);
        _suppressChange = false;
        if (_editHwnd == 0)
        {
            Hosts.Remove(_hostHwnd);
            DestroyWindow(_hostHwnd);
            _hostHwnd = 0;
            return;
        }

        if (maxLength > 0) SendMessageW(_editHwnd, 0x00C5 /* EM_SETLIMITTEXT */, (nuint)maxLength, 0);
        Edits[_editHwnd] = this;
        _origEditProc = SetWindowLongPtrW(_editHwnd, -4 /*GWLP_WNDPROC*/, (nint)EditSubProcPtr);

        RebuildFont(fontSizeDip, dpi);
        SendMessageW(_editHwnd, EmSetMargins, 3 /*EC_LEFTMARGIN | EC_RIGHTMARGIN*/, 0);

        ShowWindow(_hostHwnd, 5 /*SW_SHOW*/);
        SetFocus(_editHwnd);
        int len = (initialText ?? "").Length;
        SendMessageW(_editHwnd, EmSetSel, selectAll ? 0u : (nuint)len, len);
    }

    internal void BeginLayout() => _layoutSeen = false;

    internal void SetLayout(UiRect field, UiRect text, UiRect visible, float fontSize, bool mono, string cue)
    {
        FieldBounds = field;
        _textBounds = text;
        _visibleBounds = visible;
        _layoutSeen = true;
        if (_mono != mono || _fontSizeDip != fontSize)
        {
            _mono = mono;
            RebuildFont(fontSize, _fontDpi);
        }
        if (_placeholder != cue)
        {
            _placeholder = cue;
            fixed (char* p = cue) SendMessageW(_editHwnd, 0x1501 /* EM_SETCUEBANNER */, 1, (nint)p);
        }
    }

    internal void FinishLayout(LayeredWindow owner)
    {
        if (!IsOpen || _closingPending) return;
        var text = _textBounds;
        float lineHeight = _readOnlyMultiline ? text.Height : MathF.Min(text.Height, _fontSizeDip * 1.45f);
        var line = new UiRect(text.X, text.CenterY - lineHeight / 2, text.Width, lineHeight);
        var view = UiHitMap.Intersect(line, _visibleBounds);
        if (!_layoutSeen || view.IsEmpty)
        {
            ShowWindow(_hostHwnd, 0);
            ScheduleCommitClose();
            return;
        }

        if (_fontDpi != owner.Dpi) RebuildFont(_fontSizeDip, owner.Dpi);
        var first = owner.DipToScreenPx(view.X, view.Y);
        var last = owner.DipToScreenPx(view.Right, view.Bottom);
        var start = owner.DipToScreenPx(line.X, line.Y);
        var end = owner.DipToScreenPx(line.Right, line.Bottom);
        var position = (first.X, first.Y, Math.Max(1, last.X - first.X), Math.Max(1, last.Y - first.Y),
            start.X - first.X, start.Y - first.Y, Math.Max(1, end.X - start.X), Math.Max(1, end.Y - start.Y));
        if (_placement == position) return;
        _placement = position;
        var (x, y, width, height, childX, childY, childWidth, childHeight) = position;
        SetWindowPos(_hostHwnd, 0, x, y, width, height, 0x0014);
        SetWindowPos(_editHwnd, 0, childX, childY, childWidth, childHeight, 0x0014);
    }

    private void RebuildFont(float fontSizeDip, uint dpi)
    {
        _fontSizeDip = fontSizeDip;
        _fontDpi = dpi;
        nint previous = _font;
        int fontPx = -Math.Max(10, (int)MathF.Round(fontSizeDip * (dpi / 96f)));
        _font = CreateFontW(fontPx, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5 /*CLEARTYPE*/, 0,
            _mono ? UiMetrics.MonoFontFamily : UiMetrics.FontFamily);
        if (_font == 0) { _font = previous; return; }
        if (_editHwnd != 0) SendMessageW(_editHwnd, WmSetFont, (nuint)_font, 1);
        if (previous != 0) DeleteObject(previous);
    }

    public void SetTheme(UiColor bg, UiColor fg)
    {
        UpdateColors(bg, fg);
        if (_hostHwnd != 0) InvalidateRect(_hostHwnd, 0, true);
        if (_editHwnd != 0) InvalidateRect(_editHwnd, 0, true);
    }

    internal void BeginPointerSelection(int screenX, int screenY, uint message, nuint keys)
    {
        if (!IsOpen) return;
        _closingPending = false;
        SetFocus(_editHwnd);
        var pt = new WinPoint { X = screenX, Y = screenY };
        ScreenToClient(_editHwnd, ref pt);
        // Pass the initiating physical press to EDIT. Its own selection drag now owns capture.
        SendMessageW(_editHwnd, message, keys, (nint)((pt.Y << 16) | (ushort)pt.X));
    }

    [StructLayout(LayoutKind.Sequential)] private struct WinPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref WinPoint point);

    internal void SetText(string tag, string text)
    {
        if (!IsOpen || ActiveTag != tag || text == _lastChanged) return;
        int caret = 0;
        SendMessageW(_editHwnd, 0x00B0 /* EM_GETSEL */, 0, (nint)(&caret));
        _suppressChange = true;
        try
        {
            fixed (char* value = text)
                SendMessageW(_editHwnd, 0x000C /* WM_SETTEXT */, 0, (nint)value);
            _lastChanged = text;
            caret = Math.Clamp(caret, 0, text.Length);
            SendMessageW(_editHwnd, EmSetSel, (nuint)caret, caret);
        }
        finally { _suppressChange = false; }
    }

    public string GetText()
    {
        if (_editHwnd == 0) return "";
        int len = GetWindowTextLengthW(_editHwnd);
        if (len <= 0) return "";
        char[] buf = new char[len + 1];
        fixed (char* p = buf)
        {
            int actual = GetWindowTextW(_editHwnd, (nint)p, buf.Length);
            return new string(p, 0, actual);
        }
    }

    public void Close(bool commit, bool viaEnter)
    {
        _closingPending = false;
        string? tag = ActiveTag;
        string text = commit ? GetText() : "";
        ActiveTag = null;
        if (_editHwnd != 0)
        {
            Edits.Remove(_editHwnd);
            if (_origEditProc != 0) SetWindowLongPtrW(_editHwnd, -4, _origEditProc);
            _origEditProc = 0;
            _editHwnd = 0;
        }
        if (_hostHwnd != 0)
        {
            nint h = _hostHwnd;
            _hostHwnd = 0;
            Hosts.Remove(h);
            DestroyWindow(h);
        }
        if (_font != 0) { DeleteObject(_font); _font = 0; }
        if (commit && tag is not null)
        {
            try { Committed?.Invoke(tag, text, viaEnter); }
            catch (Exception error) { SonicRoute.Core.ExceptionDiagnostics.Record("Native.EditCommit", error); }
        }
    }

    private void NotifyTextChanged()
    {
        if (_composing || _suppressChange || TextChanged == null || ActiveTag is not string tag) return;
        string value = GetText();
        if (value == _lastChanged) return;
        _lastChanged = value;
        try { TextChanged.Invoke(tag, value); }
        catch (Exception error) { SonicRoute.Core.ExceptionDiagnostics.Record("Native.EditChange", error); }
    }

    private void ScheduleCommitClose()
    {
        if (_closingPending || _hostHwnd == 0) return;
        _closingPending = true;
        PostMessageW(_hostHwnd, WmUserCommitClose, 0, 0);
    }

    private static bool IsImeComposing(nint hwnd)
    {
        nint himc = ImmGetContext(hwnd);
        if (himc == 0) return false;
        try
        {
            int bytes = ImmGetCompositionStringW(himc, 0x0008 /*GCS_COMPSTR*/, 0, 0);
            return bytes > 0;
        }
        finally
        {
            ImmReleaseContext(hwnd, himc);
        }
    }

    private void UpdateColors(UiColor bg, UiColor fg)
    {
        _bgColorRef = ToColorRef(bg);
        _fgColorRef = ToColorRef(fg);
        if (_bgBrush != 0) { DeleteObject(_bgBrush); _bgBrush = 0; }
        _bgBrush = CreateSolidBrush(_bgColorRef);
    }

    private static uint ToColorRef(UiColor c) => (uint)(c.R | (c.G << 8) | (c.B << 16));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close(commit: false, viaEnter: false);
        if (_bgBrush != 0) { DeleteObject(_bgBrush); _bgBrush = 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint HostWndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (!Hosts.TryGetValue(hwnd, out var host))
            return DefWindowProcW(hwnd, msg, wParam, lParam);

        switch (msg)
        {
            case LayeredWindow.WmDropFiles:
                LayeredWindow.ReceiveFileDrop(host._ownerHwnd, hwnd, (nint)wParam);
                return 0;

            case WmEraseBkgnd:
                if (host._bgBrush != 0 && GetClientRect(hwnd, out var rc))
                {
                    FillRect((nint)wParam, ref rc, host._bgBrush);
                    return 1;
                }
                break;

            case 0x0138 /* WM_CTLCOLORSTATIC for read-only EDIT */:
            case WmCtlColorEdit:
                SetTextColor((nint)wParam, host._fgColorRef);
                SetBkColor((nint)wParam, host._bgColorRef);
                return host._bgBrush;

            case WmCommand:
                if (((uint)wParam >> 16) == 0x0300 /*EN_CHANGE*/)
                {
                    host.NotifyTextChanged();
                }
                else if (((uint)wParam >> 16) == 0x0200 /*EN_KILLFOCUS*/)
                {
                    host.ScheduleCommitClose();
                }
                return 0;

            case WmUserCommitClose:
                if (host.IsOpen && host._closingPending) host.Close(commit: true, viaEnter: false);
                return 0;

            case WmDestroy:
                Hosts.Remove(hwnd);
                return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint EditSubProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (!Edits.TryGetValue(hwnd, out var host))
            return DefWindowProcW(hwnd, msg, wParam, lParam);

        if (msg == LayeredWindow.WmDropFiles)
        {
            LayeredWindow.ReceiveFileDrop(host._ownerHwnd, hwnd, (nint)wParam);
            return 0;
        }
        nint origProc = host._origEditProc;
        if (msg == 0x0102 /* WM_CHAR */ && wParam == 1 /* Ctrl+A */)
        {
            SendMessageW(hwnd, EmSetSel, 0, -1);
            return 0;
        }
        if (msg == 0x010D /* WM_IME_STARTCOMPOSITION */) host._composing = true;
        if (msg == 0x010E /* WM_IME_ENDCOMPOSITION */)
        {
            nint result = CallWindowProcW(origProc, hwnd, msg, wParam, lParam);
            host._composing = false;
            host.NotifyTextChanged();
            return result;
        }
        if (msg == WmKeyDown && !host._composing && !IsImeComposing(hwnd))
        {
            int vk = (int)wParam;
            if (vk is 9 or 13 or 27)
            {
                bool reverse = GetKeyState(0x10) < 0;
                nint owner = host._ownerHwnd;
                host.Close(commit: vk != 27, viaEnter: vk == 13);
                LayeredWindow.ReturnFromEditor(owner, vk == 9, reverse);
                return 0;
            }
        }
        else if (msg == WmMouseWheel && !host._readOnlyMultiline)
        {
            LayeredWindow.ScrollFromEditor(host._ownerHwnd, wParam, lParam);
            return 0;
        }
        else if (msg == WmKillFocus)
        {
            nint result = origProc != 0 ? CallWindowProcW(origProc, hwnd, msg, wParam, lParam) : DefWindowProcW(hwnd, msg, wParam, lParam);
            nint nextFocus = (nint)wParam;
            if (nextFocus != host._hostHwnd)
                host.ScheduleCommitClose();
            return result;
        }

        return origProc != 0 ? CallWindowProcW(origProc, hwnd, msg, wParam, lParam) : DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void EnsureClass()
    {
        if (_registered) return;
        var wc = new WndClassExW
        {
            CbSize = (uint)Marshal.SizeOf<WndClassExW>(),
            LpfnWndProc = (nint)HostProcPtr,
            HInstance = GetModuleHandleW(null),
            LpszClassName = Marshal.StringToHGlobalUni(HostClassName),
        };
        try
        {
            if (RegisterClassExW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _registered = true;
        }
        finally
        {
            Marshal.FreeHGlobal(wc.LpszClassName);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct WinRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassExW
    {
        public uint CbSize, Style; public nint LpfnWndProc; public int CbClsExtra, CbWndExtra;
        public nint HInstance, HIcon, HCursor, HbrBackground, LpszMenuName, LpszClassName, HIconSm;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClassExW(ref WndClassExW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint CallWindowProcW(nint prev, nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint hwnd, int idx, nint val);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextW(nint hwnd, nint buf, int maxCount);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out WinRect rect);
    [DllImport("user32.dll")] private static extern int FillRect(nint hdc, ref WinRect rc, nint brush);
    [DllImport("imm32.dll")] private static extern nint ImmGetContext(nint hwnd);
    [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(nint hwnd, nint himc);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] private static extern int ImmGetCompositionStringW(nint himc, uint index, nint buf, uint len);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFontW(int h, int w, int esc, int ori, int weight, uint italic, uint under, uint strike, uint cs, uint outp, uint clip, uint qual, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint colorRef);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint hdc, uint colorRef);
    [DllImport("gdi32.dll")] private static extern uint SetBkColor(nint hdc, uint colorRef);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
}
