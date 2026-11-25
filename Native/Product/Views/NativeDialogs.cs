using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Win = SonicRoute.Native.Product.ViewNative;

namespace SonicRoute.Native.Product;

internal static unsafe class NativeDialogs
{
    private static readonly string DialogClass = $"SonicRoute.Preview.Dialog.{Environment.ProcessId}";
    private const int DialogStateIndex = -21;

    internal static string? Open(nint owner, string filter) => FileDialog(owner, filter, false, "");
    internal static string? Save(nint owner, string filter, string defaultExtension) => FileDialog(owner, filter, true, defaultExtension);

    private static string? FileDialog(nint owner, string filter, bool save, string extension)
    {
        var buffer = new char[32768];
        string spec = filter.Replace('|', '\0') + "\0\0";
        fixed (char* name = buffer, filters = spec, suffix = extension)
        {
            var request = new OpenFileName
            {
                Size = (uint)sizeof(OpenFileName), Owner = owner, Filter = filters, File = name,
                MaxFile = (uint)buffer.Length, DefaultExtension = suffix,
                Flags = 0x00080000 | 0x00001000 | (save ? 0x00000006u : 0x00000800u)
            };
            int ok = save ? GetSaveFileNameW(ref request) : GetOpenFileNameW(ref request);
            return ok == 0 ? null : new string(name);
        }
    }

    internal static string? Input(nint owner, string title, string initial, bool readOnly = false, bool multiline = false)
        => Run(owner, title, initial, readOnly, multiline || initial.Contains('\n'), false);

    internal static string? RecordHotkey(nint owner, string title, string initial)
        => Run(owner, title, initial, true, false, true);

    internal static bool Confirm(nint owner, string title, string message)
        => MessageBoxW(owner, message, title, 0x00000124) == 6;

    private sealed class DialogState(string initial, bool recording)
    {
        internal string Value = initial;
        internal readonly bool Recording = recording;
        internal bool Done, Accepted;
        internal nint Edit;
    }

    private static string? Run(nint owner, string title, string initial, bool readOnly, bool multiline, bool hotkey)
    {
        nint module = ShellNative.GetModuleHandleW(null);
        fixed (char* name = DialogClass)
        {
            var windowClass = new Win.WindowClass
            {
                Size = (uint)sizeof(Win.WindowClass), Instance = module, Name = (nint)name,
                Proc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&DialogProc,
                Cursor = Win.LoadCursorW(0, 32512), Background = 6
            };
            if (Win.RegisterClassExW(windowClass) == 0 && Marshal.GetLastPInvokeError() != 1410)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        Win.GetCursorPos(out var cursor);
        var (work, dpi) = Win.WorkArea(cursor);
        int Scale(int length) => length * (int)dpi / 96;
        int height = multiline ? 350 : 150;
        nint window = Win.CreateWindowExW(0x00000101, DialogClass, title, 0x00C80000,
            work.Left + Math.Max(0, (work.Width - Scale(520)) / 2), work.Top + Math.Max(0, (work.Height - Scale(height)) / 2),
            Scale(520), Scale(height), owner, 0, module, 0);
        if (window == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());

        var state = new DialogState(initial, hotkey);
        GCHandle data = GCHandle.Alloc(state);
        bool disabled = false;
        try
        {
            SetWindowLongPtrW(window, DialogStateIndex, GCHandle.ToIntPtr(data));
            uint editStyle = 0x50010080u | (multiline ? 0x00201044u : 0u);
            string hint = L10n.CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "按键、滚轮或鼠标中键 / 侧键。Esc 清空。" : "Press a shortcut or a mouse gesture. Esc clears.";
            state.Edit = Win.CreateWindowExW(0x200, "EDIT", hotkey ? hint : initial, editStyle,
                Scale(16), Scale(15), Scale(480), Scale(multiline ? 250 : 29), window, 101, module, 0);
            SendMessageW(state.Edit, 0x0030, GetStockObject(17), 1);
            if (readOnly) SendMessageW(state.Edit, 0x00CF, 1, 0);
            if (!hotkey) SendMessageW(state.Edit, 0x00B1, 0, -1);
            string cancel = L10n.CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "取消" : "Cancel";
            foreach ((int id, string label, int x) in new[] { (1, "OK", 319), (2, cancel, 411) })
            {
                nint button = Win.CreateWindowExW(0, "BUTTON", label, id == 1 ? 0x50010001u : 0x50010000u, Scale(x),
                    Scale(multiline ? 273 : 60), Scale(84), Scale(29), window, id, module, 0);
                SendMessageW(button, 0x0030, GetStockObject(17), 1);
            }
            disabled = owner != 0 && IsWindowEnabled(owner) != 0;
            if (disabled) EnableWindow(owner, 0);
            Win.ShowWindow(window, 5);
            Win.SetForegroundWindow(window);
            SetFocus(hotkey ? window : state.Edit);
            while (!state.Done)
            {
                int result = GetMessageW(out var message, 0, 0, 0);
                if (result == 0) { PostQuitMessage(unchecked((int)message.WParam)); break; }
                if (result < 0) break;
                if (message.Window == window || IsChild(window, message.Window) != 0)
                {
                    if (hotkey && TryRecord(state, message)) continue;
                    if (!hotkey && message.Kind == 0x100 && message.WParam == 27) break;
                    if (!hotkey && !multiline && message.Kind == 0x100 && message.WParam == 13)
                    { Finish(state, true); break; }
                    if (IsDialogMessageW(window, ref message) != 0) continue;
                }
                TranslateMessage(in message);
                DispatchMessageW(in message);
            }
            return state.Accepted ? state.Value : null;
        }
        finally
        {
            SetWindowLongPtrW(window, DialogStateIndex, 0);
            if (disabled) EnableWindow(owner, 1);
            Win.DestroyWindow(window);
            data.Free();
            if (disabled) Win.SetForegroundWindow(owner);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint DialogProc(nint window, uint message, nuint w, nint l)
    {
        var handle = GetWindowLongPtrW(window, DialogStateIndex);
        if (handle != 0 && GCHandle.FromIntPtr(handle).Target is DialogState state)
        {
            if (message == 0x0111 && (ushort)w is 1 or 2) { Finish(state, (ushort)w == 1); return 0; }
            if (message == 0x0010) { Finish(state, false); return 0; }
        }
        return Win.DefWindowProcW(window, message, w, l);
    }

    private static bool TryRecord(DialogState state, Message message)
    {
        int vk = (int)message.WParam;
        if (message.Kind == 0x100 && vk == 27)
        {
            state.Value = "";
            Finish(state, true);
            return true;
        }
        string? key = message.Kind switch
        {
            0x100 or 0x104 => KeyName(vk),
            0x207 => "MButton",
            0x20B => (ushort)(message.WParam >> 16) == 1 ? "XButton1" : "XButton2",
            0x20A => (short)(message.WParam >> 16) > 0 ? "WheelUp" : "WheelDown",
            _ => null
        };
        if (key == null) return false;
        var parts = new List<string>(5);
        if (GetKeyState(0x11) < 0) parts.Add("Ctrl");
        if (GetKeyState(0x12) < 0) parts.Add("Alt");
        if (GetKeyState(0x10) < 0) parts.Add("Shift");
        if (GetKeyState(0x5B) < 0 || GetKeyState(0x5C) < 0) parts.Add("Win");
        parts.Add(key);
        state.Value = string.Join("+", parts);
        Finish(state, true);
        return true;
    }

    private static void Finish(DialogState state, bool accept)
    {
        if (state.Done) return;
        if (accept && !state.Recording)
        {
            int length = (int)SendMessageW(state.Edit, 0x000E, 0, 0);
            var text = new char[length + 1];
            fixed (char* buffer = text)
            {
                int written = GetWindowTextW(state.Edit, buffer, text.Length);
                state.Value = new string(buffer, 0, written);
            }
        }
        (state.Accepted, state.Done) = (accept, true);
    }

    private static string? KeyName(int vk)
    {
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5a) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6f);
        if (vk is >= 0x60 and <= 0x69) return "NumPad" + (vk - 0x60);
        return vk switch
        {
            0x20 => "Space", 0x09 => "Tab", 0x0d => "Enter", 0x26 => "Up", 0x28 => "Down", 0x25 => "Left", 0x27 => "Right",
            0x2d => "Insert", 0x2e => "Delete", 0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown", 0x08 => "Back", 0x90 => "NumLock",
            0xbb => "OemPlus", 0xbd => "OemMinus", 0xbc => "OemComma", 0xbe => "OemPeriod", 0xbf => "OemQuestion", 0xdb => "OemOpenBrackets",
            0xdd => "OemCloseBrackets", 0xdc => "OemPipes", 0xba => "OemSemicolon", 0xde => "OemQuotes", 0xc0 => "OemTilde", _ => null
        };
    }

    [StructLayout(LayoutKind.Sequential)] private struct OpenFileName
    {
        public uint Size;
        public nint Owner, Instance;
        public char* Filter, CustomFilter;
        public uint MaxCustomFilter, FilterIndex;
        public char* File;
        public uint MaxFile;
        public char* FileTitle;
        public uint MaxFileTitle;
        public char* InitialDirectory, Title;
        public uint Flags;
        public ushort FileOffset, ExtensionOffset;
        public char* DefaultExtension;
        public nint Data, Hook, TemplateName, Reserved;
        public uint Reserved2, ExtendedFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public nint Window;
        public uint Kind;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("comdlg32.dll")] private static extern int GetOpenFileNameW(ref OpenFileName request);
    [DllImport("comdlg32.dll")] private static extern int GetSaveFileNameW(ref OpenFileName request);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint window, uint message, nint w, nint l);
    [DllImport("user32.dll")] private static extern int GetWindowTextW(nint window, char* buffer, int capacity);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern int IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern int EnableWindow(nint window, int enabled);
    [DllImport("user32.dll")] private static extern int IsChild(nint parent, nint window);
    [DllImport("user32.dll")] private static extern int IsDialogMessageW(nint window, ref Message message);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern int TranslateMessage(in Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(in Message message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
}
