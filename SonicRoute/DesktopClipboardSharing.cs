#if SONICROUTE_PHONE
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SonicRoute;

internal sealed partial class DesktopClipboardLink
{
    private static readonly ConditionalWeakTable<HwndSource, ClipboardListeners> Listeners = new();

    private sealed class ClipboardListeners(HwndSource window)
    {
        private event Action? Changed;
        internal void Subscribe(Action changed)
        {
            if (Changed == null)
            {
                window.AddHook(OnMessage);
                if (!AddClipboardFormatListener(window.Handle)) { window.RemoveHook(OnMessage); throw new ExternalException(); }
            }
            Changed += changed;
        }
        internal void Unsubscribe(Action changed)
        {
            Changed -= changed;
            if (Changed == null && !window.IsDisposed)
            {
                RemoveClipboardFormatListener(window.Handle);
                window.RemoveHook(OnMessage);
            }
        }
        private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message == 0x031D) Changed?.Invoke();
            return 0;
        }
    }

    private static class Formats
    {
        internal static readonly uint Remote = RegisterClipboardFormatW("SonicRoute.RemoteClipboard.v1");
        internal static readonly uint IgnoreViewer = RegisterClipboardFormatW("Clipboard Viewer Ignore");
        internal static readonly uint Exclude = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
        internal static readonly uint Cloud = RegisterClipboardFormatW("CanUploadToCloudClipboard");
        internal static readonly uint History = RegisterClipboardFormatW("CanIncludeInClipboardHistory");
        internal static readonly uint Password = RegisterClipboardFormatW("application/x-kde-passwordManagerHint");
    }

    private static bool ExcludeFromSharing()
    {
        if (HasPrivateFormat(IsClipboardFormatAvailable, DeniesSharing)) return true;
        if (GetWindowThreadProcessId(GetClipboardOwner(), out int pid) == 0 || pid <= 0) return false;
        try
        {
            using var owner = Process.GetProcessById(pid);
            return IsPasswordManager(owner.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        { return true; }
    }

    internal static bool HasPrivateFormat(Func<uint, bool> present, Func<uint, bool> denies) =>
        present(15) || present(Formats.Remote) || present(Formats.Exclude) || present(Formats.IgnoreViewer)
        || present(Formats.Password) || denies(Formats.Cloud) || denies(Formats.History);

    internal static bool IsPasswordManager(string name) => name.ToLowerInvariant() is
        "keepass" or "keepassxc" or "1password" or "bitwarden" or "enpass" or "nordpass"
        or "proton pass" or "protonpass" or "keeperpasswordmanager" or "dashlane" or "lastpass" or "identities" or "robotaskbaricon";

    private static bool DeniesSharing(uint format)
    {
        if (!IsClipboardFormatAvailable(format)) return false;
        nint memory = GetClipboardData(format);
        if (memory == 0 || GlobalSize(memory) < 4) return true;
        nint value = GlobalLock(memory);
        if (value == 0) return true;
        try { return Marshal.ReadInt32(value) != 1; }
        finally { GlobalUnlock(memory); }
    }

    private static bool TransferClipboardData(uint format, ref nint memory)
    {
        if (SetClipboardData(format, memory) == 0) return false;
        memory = 0; // Windows owns the handle after a successful transfer.
        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint RegisterClipboardFormatW(string format);
    [DllImport("user32.dll")] private static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
}
#endif
