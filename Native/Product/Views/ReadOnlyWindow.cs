using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SonicRoute.Core;
using SonicRoute.Core.Models;
using SonicRoute.Native.Graphics;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

// Both viewers use real EDIT controls for selection, clipboard and native scrolling.
internal sealed class ReadOnlyWindow
{
    private const string ClassName = "SonicRoute.Native.ReadOnlyWindow";
    private static readonly Dictionary<nint, ReadOnlyWindow> Windows = new();
    private readonly IViewHostActions _actions;
    private readonly bool _license;
    private readonly nint _owner;
    private nint _window, _detail, _list, _notice, _refresh, _font, _surfaceBrush, _windowBrush;
    private int _textColor, _surfaceColor, _windowColor;
    private bool _disabledOwner;
    private IReadOnlyList<ExecutionHistoryEntry> _entries = [];
    private CancellationTokenSource? _read;

    public bool IsOpen => _window != 0;

    internal unsafe ReadOnlyWindow(IViewHostActions actions, string? licenseText = null)
    {
        _actions = actions;
        _owner = actions.OwnerWindow.Handle;
        _license = licenseText != null;
        nint module = ShellNative.GetModuleHandleW(null);
        fixed (char* name = ClassName)
        {
            var cls = new ViewNative.WindowClass
            {
                Size = (uint)sizeof(ViewNative.WindowClass), Instance = module, Name = (nint)name,
                Proc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProc,
                Cursor = ViewNative.LoadCursorW(0, 32512)
            };
            if (ViewNative.RegisterClassExW(cls) == 0 && Marshal.GetLastPInvokeError() != 1410)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        GetWindowRect(_owner, out var owner);
        var (work, dpi) = ViewNative.WorkArea(new ViewNative.Point(owner.Left + owner.Width / 2, owner.Top + owner.Height / 2));
        int width = Math.Min(work.Width, (int)((_license ? 800 : 900) * dpi / 96));
        int height = Math.Min(work.Height, (int)((_license ? 600 : 560) * dpi / 96));
        int left = Math.Clamp(owner.Left + (owner.Width - width) / 2, work.Left, work.Right - width);
        int top = Math.Clamp(owner.Top + (owner.Height - height) / 2, work.Top, work.Bottom - height);
        _window = ViewNative.CreateWindowExW(0, ClassName, _license ? "SonicRoute · GPL-3.0" : L10n.T("History.Title"),
            0x02CF0000, left, top, width, height, _owner, 0, module, 0);
        if (_window == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        Windows.Add(_window, this);
        try
        {
            _detail = Child("EDIT", "", 0x50210844, 101); // read-only multiline, vertical scroll, word wrap
            if (!_license)
            {
                _refresh = Child("BUTTON", L10n.T("History.Refresh"), 0x50010000, 102);
                _notice = Child("STATIC", "", 0x50000000, 103);
                _list = Child("LISTBOX", "", 0x50210101, 104); // notify, no integral height
            }
            else
            {
                SetWindowTextW(_detail, licenseText!.ReplaceLineEndings("\r\n"));
                if (IsWindowEnabled(_owner)) { EnableWindow(_owner, false); _disabledOwner = true; }
            }
            UpdateTheme();
            SetFont();
            Layout();
            Activate();
            SetFocus(_license ? _detail : _refresh);
            if (!_license) Refresh();
        }
        catch
        {
            Close();
            throw;
        }
    }

    internal void Activate()
    {
        ViewNative.ShowWindow(_window, IsIconic(_window) ? 9 : 5);
        ViewNative.SetForegroundWindow(_window);
    }

    private unsafe nint Child(string cls, string text, uint style, int id)
    {
        nint child = ViewNative.CreateWindowExW(0, cls, text, style, 0, 0, 1, 1, _window, id, ShellNative.GetModuleHandleW(null), 0);
        if (child == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (cls != "STATIC" && !SetWindowSubclass(child, &ChildProc, 1, (nuint)_window))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return child;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint ChildProc(nint hwnd, uint msg, nuint w, nint l, nuint id, nuint owner)
    {
        if (msg == 0x100 && Windows.TryGetValue((nint)owner, out var viewer))
        {
            if (w == 27) { viewer.Close(); return 0; }
            if (w == 65 && hwnd == viewer._detail && GetKeyState(17) < 0)
            {
                ViewNative.SendMessageW(hwnd, 0xB1, 0, -1);
                return 0;
            }
            if (w == 9 && !viewer._license)
            {
                bool reverse = GetKeyState(16) < 0;
                nint next = hwnd == viewer._list ? (reverse ? viewer._refresh : viewer._detail) :
                    hwnd == viewer._detail ? (reverse ? viewer._list : viewer._refresh) : (reverse ? viewer._detail : viewer._list);
                SetFocus(next);
                return 0;
            }
        }
        return DefSubclassProc(hwnd, msg, w, l);
    }

    private int Scale(int dip) => (int)Math.Round(dip * ViewNative.GetDpiForWindow(_window) / 96d);

    private void SetFont()
    {
        nint previous = _font;
        _font = CreateFontW(-Scale(13), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, UiMetrics.FontFamily);
        foreach (nint control in new[] { _detail, _refresh, _notice, _list })
            if (control != 0) ViewNative.SendMessageW(control, 0x30, (nuint)_font, 1);
        if (previous != 0) ViewNative.DeleteObject(previous);
    }

    internal void UpdateTheme()
    {
        if (!IsOpen) return;
        var theme = _actions.Theme;
        static int Rgb(UiColor c) => c.R | (c.G << 8) | (c.B << 16);
        _textColor = Rgb(theme.TextPrimary);
        _surfaceColor = Rgb(theme.SurfaceBg);
        _windowColor = Rgb(theme.WindowBg);
        if (_surfaceBrush != 0) ViewNative.DeleteObject(_surfaceBrush);
        if (_windowBrush != 0) ViewNative.DeleteObject(_windowBrush);
        _surfaceBrush = CreateSolidBrush(_surfaceColor);
        _windowBrush = CreateSolidBrush(_windowColor);
        int dark = theme.IsDark ? 1 : 0;
        ViewNative.DwmSetWindowAttribute(_window, 20, in dark, 4);
        RedrawWindow(_window, 0, 0, 0x85);
    }

    private void Layout()
    {
        if (_detail == 0 || ViewNative.GetClientRect(_window, out var client) == 0) return;
        int p = Scale(16), width = Math.Max(1, client.Width - 2 * p), height = Math.Max(1, client.Height - 2 * p);
        if (_license) { MoveWindow(_detail, p, p, width, height, true); return; }
        MoveWindow(_refresh, p, p, Scale(105), Scale(32), true);
        MoveWindow(_notice, p, p + Scale(36), width, Scale(24), true);
        int top = p + Scale(64), body = Math.Max(1, client.Height - p - top), split = (int)(width * .42);
        MoveWindow(_list, p, top, Math.Max(1, split - Scale(10)), body, true);
        MoveWindow(_detail, p + split, top, Math.Max(1, width - split), body, true);
    }

    internal async void Refresh()
    {
        if (!IsOpen || _license) return;
        var store = AutoRuleService.GetHistoryStore();
        if (store == null) { SetRows([], L10n.T("History.Empty")); return; }
        _read?.Cancel();
        var request = _read = new CancellationTokenSource();
        var token = request.Token;
        EnableWindow(_refresh, false);
        IReadOnlyList<ExecutionHistoryEntry> rows = [];
        string notice = "";
        try
        {
            rows = await Task.Run(async () =>
            {
                await store.WhenIdleAsync().WaitAsync(token).ConfigureAwait(false);
                return await store.ReadRecentAsync(token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            if (store.LastError != null) notice = "History.ReadError";
            else if (rows.Count == 0) notice = "History.Empty";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { notice = "History.ReadError"; }
        if (!_actions.Host.TryPost(() =>
        {
            try
            {
                if (_read != request || !IsOpen) return;
                _read = null;
                SetRows(rows, notice.Length == 0 ? "" : L10n.T(notice));
                EnableWindow(_refresh, true);
            }
            finally { request.Dispose(); }
        })) request.Dispose();
    }
    private unsafe void SetRows(IReadOnlyList<ExecutionHistoryEntry> rows, string notice)
    {
        _entries = rows;
        ViewNative.SendMessageW(_list, 0x184, 0, 0); // LB_RESETCONTENT
        foreach (var row in rows)
        {
            string text = $"{row.StartedUtc.ToLocalTime():MM-dd HH:mm:ss}  {row.RuleName}   {L10n.T("History.Status." + row.Status)} · {L10n.T("History.Source." + row.Source)}";
            fixed (char* p = text) ViewNative.SendMessageW(_list, 0x180, 0, (nint)p);
        }
        SetWindowTextW(_notice, notice);
        ViewNative.SendMessageW(_list, 0x186, 0, 0);
        SelectRow();
    }

    private void SelectRow()
    {
        int index = (int)ViewNative.SendMessageW(_list, 0x188, 0, 0);
        SetWindowTextW(_detail, (uint)index < (uint)_entries.Count ? FormatHistory(_entries[index]) : "");
    }

    private string FormatHistory(ExecutionHistoryEntry entry)
    {
        var text = new StringBuilder();
        text.AppendLine(entry.RuleName).AppendLine(entry.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        text.AppendLine(L10n.T("History.Source." + entry.Source)).AppendLine(L10n.T("History.Status." + entry.Status));
        text.AppendLine(string.Format(L10n.T("History.Summary"), entry.Succeeded, entry.Failed, entry.Skipped));
        text.AppendLine(string.Format(L10n.T("History.Elapsed"), entry.ElapsedMs)).AppendLine();
        foreach (var step in entry.Steps)
        {
            string action = Enum.IsDefined(typeof(AutoRuleAction), step.Action) ? L10n.T(AutomationPageView.AutoActionKey((AutoRuleAction)step.Action)) : step.Action.ToString(CultureInfo.InvariantCulture);
            text.AppendLine(string.Format(L10n.T("History.Step"), step.Number, action));
            if (step.TargetApp.Length > 0) text.AppendLine(FormatApp(step.TargetApp));
            if (step.TargetPid.HasValue) text.Append("PID ").AppendLine(step.TargetPid.Value.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(step.TargetDeviceId)) text.AppendLine(step.TargetDeviceName ?? step.TargetDeviceId);
            if (!string.IsNullOrEmpty(step.TargetRuleName)) text.AppendLine(step.TargetRuleName);
            if (step.TotalTargets.HasValue) text.AppendLine(string.Format(L10n.T("History.TargetCounts"), step.SucceededTargets ?? 0, step.TotalTargets));
            text.Append(L10n.T("History.Status." + step.Status)).Append(" · ").AppendLine(string.Format(L10n.T("History.Elapsed"), step.ElapsedMs));
            if (step.Reason.Length > 0) text.AppendLine(L10n.T("History.Reason." + step.Reason));
            text.AppendLine();
        }
        if (entry.TotalSteps > 100) text.AppendLine(string.Format(L10n.T("History.StepsTruncated"), entry.TotalSteps - 100));
        return text.ToString();
    }

    private string FormatApp(string identifier)
    {
        const string prefix = "android:";
        if (!identifier.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return identifier;
        if (_actions.Config.AppNames.TryGetValue(identifier, out var alias) && !string.IsNullOrWhiteSpace(alias)) return alias;
        string package = identifier[prefix.Length..].Trim();
        return package.Length == 0 || package.Equals("sonicroute:device-audio", StringComparison.OrdinalIgnoreCase) ? "Phone" : package;
    }

    internal void Close() { if (_window != 0) ViewNative.DestroyWindow(_window); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe nint WindowProc(nint hwnd, uint msg, nuint w, nint l)
    {
        if (!Windows.TryGetValue(hwnd, out var viewer)) return ViewNative.DefWindowProcW(hwnd, msg, w, l);
        switch (msg)
        {
            case 0x5: viewer.Layout(); break;
            case 0x24:
                var minMax = (ViewNative.Point*)l;
                minMax[3] = new ViewNative.Point(viewer.Scale(viewer._license ? 400 : 680), viewer.Scale(viewer._license ? 300 : 400));
                return 0;
            case 0x2E0:
                var rect = *(WinRect*)l;
                ViewNative.SetWindowPos(hwnd, 0, rect.Left, rect.Top, rect.Width, rect.Height, 0x14);
                viewer.SetFont();
                viewer.Layout();
                return 0;
            case 0x111:
                if ((ushort)w == 102 && (w >> 16) == 0) viewer.Refresh();
                if ((ushort)w == 104 && (w >> 16) == 1) viewer.SelectRow();
                break;
            case 0x14:
                ViewNative.GetClientRect(hwnd, out var bounds);
                FillRect((nint)w, in bounds, viewer._windowBrush);
                return 1;
            case 0x134:
            case 0x135:
            case 0x138:
                bool surface = l == viewer._list || l == viewer._detail;
                SetTextColor((nint)w, viewer._textColor);
                SetBkColor((nint)w, surface ? viewer._surfaceColor : viewer._windowColor);
                return surface ? viewer._surfaceBrush : viewer._windowBrush;
            case 0x82:
                Windows.Remove(hwnd);
                viewer._window = 0;
                viewer._read?.Cancel();
                viewer._read = null;
                viewer._entries = [];
                if (viewer._disabledOwner) { EnableWindow(viewer._owner, true); ViewNative.SetForegroundWindow(viewer._owner); }
                foreach (nint handle in new[] { viewer._font, viewer._surfaceBrush, viewer._windowBrush })
                    if (handle != 0) ViewNative.DeleteObject(handle);
                break;
        }
        return ViewNative.DefWindowProcW(hwnd, msg, w, l);
    }

    [DllImport("comctl32", SetLastError = true)] private static extern unsafe bool SetWindowSubclass(nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> proc, nuint id, nuint data);
    [DllImport("comctl32")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint w, nint l);
    [DllImport("user32")] private static extern short GetKeyState(int key);
    [DllImport("user32")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32")] private static extern bool EnableWindow(nint hwnd, bool enable);
    [DllImport("user32")] private static extern nint SetFocus(nint hwnd);
    [DllImport("user32")] private static extern bool GetWindowRect(nint hwnd, out WinRect rect);
    [DllImport("user32")] private static extern bool MoveWindow(nint hwnd, int x, int y, int w, int h, bool redraw);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(nint hwnd, string text);
    [DllImport("gdi32", CharSet = CharSet.Unicode)] private static extern nint CreateFontW(int h, int w, int escape, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint outp, uint clip, uint quality, uint pitch, string name);
    [DllImport("gdi32")] private static extern nint CreateSolidBrush(int color);
    [DllImport("gdi32")] private static extern int SetTextColor(nint dc, int color);
    [DllImport("gdi32")] private static extern int SetBkColor(nint dc, int color);
    [DllImport("user32")] private static extern int FillRect(nint dc, in WinRect rect, nint brush);
    [DllImport("user32")] private static extern bool RedrawWindow(nint hwnd, nint rect, nint region, uint flags);
}
