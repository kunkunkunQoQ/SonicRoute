using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SonicRoute.Native.Ui;

/// <summary>
/// Owned borderless Win32 Unicode EDIT popup layered cleanly over Direct2D ModernTextBox bounds.
/// Provides native IME composition, copy/paste, selection, and theme-matched colors.
/// </summary>
internal sealed unsafe class InlineEditHost : IDisposable
{
    private const string HostClassName = "SonicRoute.NativeUi.InlineEditHost";
    private const uint WsPopup = 0x80000000;
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint EsAutoHScroll = 0x0080;
    private const uint EsPassword = 0x0020;
    private const uint EsLeft = 0x0000;

    private const uint WmCommand = 0x0111;
    private const uint WmCtlColorEdit = 0x0133;
    private const uint WmKillFocus = 0x0008;
    private const uint WmKeyDown = 0x0100;
    private const uint WmDestroy = 0x0002;
    private const uint WmSetFont = 0x0030;
    private const uint EmSetSel = 0x00B1;
    private const uint EmSetMargins = 0x00D3;

    private static readonly Dictionary<nint, InlineEditHost> Hosts = new();
    private static readonly Dictionary<nint, InlineEditHost> Edits = new();
    private static bool _registered;
    private static readonly delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> HostProcPtr = &HostWndProc;
    private static readonly delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> EditSubProcPtr = &EditSubProc;

    private nint _hostHwnd;
    private nint _editHwnd;
    private nint _origEditProc;
    private nint _bgBrush;
    private nint _font;
    private uint _bgColorRef;
    private uint _fgColorRef;
    private bool _suppressChange;
    private bool _disposed;

    public string? ActiveTag { get; private set; }
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
        bool selectAll = false)
    {
        if (_disposed) return;
        EnsureClass();
        Close(commit: false, viaEnter: false);

        ActiveTag = tag;
        UpdateColors(bg, fg);

        _hostHwnd = CreateWindowExW(
            WsExToolWindow, HostClassName, "", WsPopup,
            screenXPx, screenYPx, Math.Max(20, widthPx), Math.Max(18, heightPx),
            ownerHwnd, 0, GetModuleHandleW(null), 0);
        if (_hostHwnd == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Hosts[_hostHwnd] = this;

        uint editStyle = WsChild | WsVisible | EsLeft | EsAutoHScroll | (password ? EsPassword : 0u);
        int editH = Math.Max(16, (int)MathF.Round(fontSizeDip * 1.45f * (dpi / 96f)));
        int editY = Math.Max(0, (heightPx - editH) / 2);
        _editHwnd = CreateWindowExW(
            0, "EDIT", initialText ?? "", editStyle,
            2, editY, Math.Max(12, widthPx - 4), editH,
            _hostHwnd, 1001, GetModuleHandleW(null), 0);
        if (_editHwnd == 0)
        {
            DestroyWindow(_hostHwnd);
            _hostHwnd = 0;
            return;
        }

        Edits[_editHwnd] = this;
        _origEditProc = SetWindowLongPtrW(_editHwnd, -4 /*GWLP_WNDPROC*/, (nint)EditSubProcPtr);

        int fontPx = -Math.Max(10, (int)MathF.Round(fontSizeDip * (dpi / 96f)));
        _font = CreateFontW(fontPx, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5 /*CLEARTYPE*/, 0,
            mono ? UiMetrics.MonoFontFamily : UiMetrics.FontFamily);
        if (_font != 0) SendMessageW(_editHwnd, WmSetFont, (nuint)_font, 1);
        SendMessageW(_editHwnd, EmSetMargins, 3 /*EC_LEFTMARGIN | EC_RIGHTMARGIN*/, (4 << 16) | 4);

        ShowWindow(_hostHwnd, 5 /*SW_SHOW*/);
        SetFocus(_editHwnd);
        int len = (initialText ?? "").Length;
        SendMessageW(_editHwnd, EmSetSel, selectAll ? 0u : (nuint)len, len);
    }

    public void Reposition(int screenXPx, int screenYPx, int widthPx, int heightPx, float fontSizeDip, uint dpi)
    {
        if (!IsOpen) return;
        SetWindowPos(_hostHwnd, 0, screenXPx, screenYPx, Math.Max(20, widthPx), Math.Max(18, heightPx), 0x0014 /*SWP_NOZORDER|SWP_NOACTIVATE*/);
        int editH = Math.Max(16, (int)MathF.Round(fontSizeDip * 1.45f * (dpi / 96f)));
        int editY = Math.Max(0, (heightPx - editH) / 2);
        SetWindowPos(_editHwnd, 0, 2, editY, Math.Max(12, widthPx - 4), editH, 0x0014);
    }

    public void SetTheme(UiColor bg, UiColor fg)
    {
        UpdateColors(bg, fg);
        if (_editHwnd != 0) InvalidateRect(_editHwnd, 0, true);
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
            Committed?.Invoke(tag, text, viaEnter);
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
            case WmCtlColorEdit:
                SetTextColor((nint)wParam, host._fgColorRef);
                SetBkColor((nint)wParam, host._bgColorRef);
                return host._bgBrush;

            case WmCommand:
                if (((uint)wParam >> 16) == 0x0300 /*EN_CHANGE*/ && !host._suppressChange && host.ActiveTag is string tag)
                {
                    host.TextChanged?.Invoke(tag, host.GetText());
                }
                else if (((uint)wParam >> 16) == 0x0200 /*EN_KILLFOCUS*/)
                {
                    host.Close(commit: true, viaEnter: false);
                }
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

        if (msg == WmKeyDown)
        {
            int vk = (int)wParam;
            if (vk == 0x0D /*VK_RETURN*/)
            {
                host.Close(commit: true, viaEnter: true);
                return 0;
            }
            if (vk == 0x1B /*VK_ESCAPE*/)
            {
                host.Close(commit: false, viaEnter: false);
                return 0;
            }
        }
        else if (msg == WmKillFocus)
        {
            nint nextFocus = (nint)wParam;
            if (nextFocus != host._hostHwnd)
                host.Close(commit: true, viaEnter: false);
        }

        return CallWindowProcW(host._origEditProc, hwnd, msg, wParam, lParam);
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
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint CallWindowProcW(nint prev, nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint hwnd, int idx, nint val);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextW(nint hwnd, nint buf, int maxCount);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFontW(int h, int w, int esc, int ori, int weight, uint italic, uint under, uint strike, uint cs, uint outp, uint clip, uint qual, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint colorRef);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint hdc, uint colorRef);
    [DllImport("gdi32.dll")] private static extern uint SetBkColor(nint hdc, uint colorRef);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
}
