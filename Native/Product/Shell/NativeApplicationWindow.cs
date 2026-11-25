using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Win = SonicRoute.Native.Product.ShellNative;

namespace SonicRoute.Native.Product;

[Flags]
internal enum ShellInterest { None = 0, Foreground = 1, Clipboard = 2, Device = 4, Power = 8, Session = 16, Theme = 32, Time = 64 }
internal enum ShellSignalKind { Activated, QuickPanel, TrayMenu, MainWindow, Hotkey, Foreground, Clipboard, Device, Power, Session, Theme, Time, TaskbarRestored, TrayWheel }
internal readonly record struct ShellSignal(ShellSignalKind Kind, long Value = 0, nint Window = 0, uint Modifiers = 0);

/// <summary>One invisible top-level HWND per owning STA. Posts may originate on other threads.</summary>
internal sealed unsafe partial class NativeApplicationWindow : IDisposable
{
    private const uint DispatchMessage = 0x8001, ExitMessage = 0x8002, TrayMessage = 0x8003;
    private const int QueueLimit = 256;
    [ThreadStatic] private static NativeApplicationWindow? current;
    private readonly uint ownerThread = Win.GetCurrentThreadId();
    private readonly nint module = Win.GetModuleHandleW(null);
    private readonly string className = $"SonicRoute.Native.Background.{Environment.ProcessId}";
    private readonly Queue<Action> posted = new();
    private readonly object postLock = new();
    private readonly HashSet<int> hotkeys = new();
    private readonly Dictionary<nuint, Action> deadlines = new();
    private readonly string tooltip;
    private readonly bool trayEnabled;
    private uint activateMessage, taskbarMessage;
    private bool comEntered, classRegistered, closing, disposed;
    private nuint nextTimer;
    private nint deviceRegistration;
    private global::SonicRoute.ForegroundChangeSource? foreground;
    private IDisposable? pendingClick;
    private bool doubleClick;
    private int exitCode;
    internal nint Handle { get; private set; }
    internal bool TrayRegistered { get; private set; }
    internal bool ForegroundRegistered => foreground?.IsInstalled == true;
    internal bool MouseRegistered => mouseHook != 0;
    internal int HotkeyCount => hotkeys.Count;
    internal int DeadlineCount => deadlines.Count;
    internal ShellInterest Interest { get; private set; }
    internal long MessageCount { get; private set; }
    internal long DispatchCount { get; private set; }
    internal long TimerCount { get; private set; }
    internal long TrayRegistrationCount { get; private set; }
    internal uint TaskbarCreatedMessage => taskbarMessage;
    internal event Action<ShellSignal>? Signal;
    internal event Action<Exception>? CallbackError;

    internal NativeApplicationWindow(string sinkTitle, string activationMessage, string tooltip, bool trayEnabled = true)
    {
        if (current is not null) throw new InvalidOperationException("This STA already owns a host.");
        this.tooltip = tooltip;
        this.trayEnabled = trayEnabled;
        try
        {
            Marshal.ThrowExceptionForHR(Win.CoInitializeEx(0, 2));
            comEntered = true;
            current = this;
            activateMessage = Win.RegisterWindowMessageW(activationMessage);
            taskbarMessage = Win.RegisterWindowMessageW("TaskbarCreated");
            if (activateMessage == 0 || taskbarMessage == 0) throw LastError("RegisterWindowMessageW");
            fixed (char* name = className)
            {
                var wc = new Win.WindowClass { Size = (uint)sizeof(Win.WindowClass), Instance = module, Name = (nint)name,
                    Proc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProc };
                if (Win.RegisterClassExW(in wc) == 0) throw LastError("RegisterClassExW");
                classRegistered = true;
            }
            // A message-only window would miss Explorer and system broadcasts. Never show or activate this HWND.
            Handle = Win.CreateWindowExW(0x08000080, className, sinkTitle, 0x80000000, 0, 0, 0, 0, 0, 0, module, 0);
            if (Handle == 0) throw LastError("CreateWindowExW");
            if (trayEnabled) RestoreTray();
        }
        catch { Dispose(); throw; }
    }

    internal static bool ActivateExisting(string sinkTitle, string activationMessage)
    {
        nint hwnd = Win.FindWindowW(null, sinkTitle);
        uint msg = Win.RegisterWindowMessageW(activationMessage);
        return hwnd != 0 && msg != 0 && Win.PostMessageW(hwnd, msg, 0, 0) != 0;
    }

    internal bool TryPost(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (postLock)
        {
            if (closing || posted.Count >= QueueLimit) return false;
            if (posted.Count == 0 && Win.PostMessageW(Handle, DispatchMessage, 0, 0) == 0) return false;
            posted.Enqueue(action);
            return true;
        }
    }

    // Async rule continuations have already been accepted. When producers saturate the
    // bounded STA queue, let the consumer make space instead of losing a running rule.
    internal bool PostContinuation(Action continuation)
    {
        lock (postLock)
        {
            while (!closing && posted.Count >= QueueLimit)
            {
                if (Win.GetCurrentThreadId() == ownerThread) break;
                Monitor.Wait(postLock);
            }
            if (closing) return false;
            if (posted.Count < QueueLimit) return TryPost(continuation);
        }
        continuation(); // Only the owner reaches here. Never wait for its own pump.
        return true;
    }

    internal int Run()
    {
        VerifyOwner();
        while (true)
        {
            int result = Win.GetMessageW(out var message, 0, 0, 0);
            if (result < 0) throw LastError("GetMessageW");
            if (result == 0) return unchecked((int)message.W);
            Win.TranslateMessage(in message);
            Win.DispatchMessageW(in message);
        }
    }

    internal void RequestExit(int code = 0)
    {
        lock (postLock)
        {
            if (closing) return;
            if (Win.PostMessageW(Handle, ExitMessage, 0, 0) == 0) throw LastError("PostMessageW(exit)");
            closing = true;
            Monitor.PulseAll(postLock);
            exitCode = code;
        }
    }

    internal IDisposable ScheduleOnce(TimeSpan delay, Action action)
    {
        VerifyOwner();
        ArgumentNullException.ThrowIfNull(action);
        if (deadlines.Count >= 256) throw new InvalidOperationException("Too many outstanding deadlines.");
        uint ms = checked((uint)Math.Clamp(Math.Ceiling(delay.TotalMilliseconds), 1, 0x7fffffff));
        nuint id = checked(++nextTimer);
        if (Win.SetTimer(Handle, id, ms, 0) == 0) throw LastError("SetTimer");
        deadlines.Add(id, action);
        return new Deadline(this, id);
    }

    private sealed class Deadline(NativeApplicationWindow window, nuint id) : IDisposable
    {
        public void Dispose() => window.CancelDeadline(id);
    }

    private void CancelDeadline(nuint id)
    {
        if (disposed) return;
        VerifyOwner();
        if (deadlines.Remove(id)) Win.KillTimer(Handle, id);
    }

    internal bool RegisterHotkey(int id, uint modifiers, uint vk)
    {
        VerifyOwner();
        if (id is < 1 or > 0xbfff || hotkeys.Contains(id)) return false;
        if (Win.RegisterHotKey(Handle, id, modifiers, vk) == 0) return false;
        hotkeys.Add(id);
        return true;
    }

    internal void UnregisterHotkey(int id)
    {
        VerifyOwner();
        if (hotkeys.Remove(id)) Win.UnregisterHotKey(Handle, id);
    }

    internal void SetInterest(ShellInterest flags)
    {
        VerifyOwner();
        foreach (var kind in new[] { ShellInterest.Foreground, ShellInterest.Clipboard, ShellInterest.Device, ShellInterest.Session })
        {
            bool want = flags.HasFlag(kind);
            if (want == Interest.HasFlag(kind)) continue;
            if (want)
            {
                bool installed;
                switch (kind)
                {
                    case ShellInterest.Foreground:
                        foreground = new global::SonicRoute.ForegroundChangeSource(ForegroundChanged);
                        installed = foreground.IsInstalled;
                        if (!installed) { foreground.Dispose(); foreground = null; }
                        break;
                    case ShellInterest.Clipboard: installed = Win.AddClipboardFormatListener(Handle) != 0; break;
                    case ShellInterest.Device:
                        var filter = new Win.DeviceFilter { Size = (uint)sizeof(Win.DeviceFilter), Type = 5 };
                        deviceRegistration = Win.RegisterDeviceNotificationW(Handle, in filter, 4);
                        installed = deviceRegistration != 0;
                        break;
                    default: installed = Win.WTSRegisterSessionNotification(Handle, 0) != 0; break;
                }
                if (!installed) { Report(LastError($"Register {kind}")); continue; }
                Interest |= kind;
            }
            else
            {
                switch (kind)
                {
                    case ShellInterest.Foreground: foreground?.Dispose(); foreground = null; break;
                    case ShellInterest.Clipboard: Win.RemoveClipboardFormatListener(Handle); break;
                    case ShellInterest.Device: Win.UnregisterDeviceNotification(deviceRegistration); deviceRegistration = 0; break;
                    case ShellInterest.Session: Win.WTSUnRegisterSessionNotification(Handle); break;
                }
                Interest &= ~kind;
            }
        }
        const ShellInterest broadcast = ShellInterest.Theme | ShellInterest.Power | ShellInterest.Time;
        Interest = (Interest & ~broadcast) | (flags & broadcast);
    }

    private void ForegroundChanged()
    {
        if (!closing && Interest.HasFlag(ShellInterest.Foreground))
            Raise(ShellSignalKind.Foreground, window: Win.GetForegroundWindow());
    }

    private void Drain(int limit)
    {
        for (int i = 0; i < limit; i++)
        {
            Action? action;
            lock (postLock)
            {
                if (!posted.TryDequeue(out action)) return;
                if (posted.Count == QueueLimit - 1) Monitor.PulseAll(postLock);
            }
            try { action(); } catch (Exception ex) { Report(ex); }
        }
        lock (postLock)
            if (posted.Count > 0 && !closing && Win.PostMessageW(Handle, DispatchMessage, 0, 0) == 0)
                throw LastError("PostMessageW(dispatch)");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint msg, nuint w, nint l)
    {
        var host = current;
        if (host != null && host.Handle == hwnd)
        {
            try { return host.OnMessage(msg, w, l); }
            catch (Exception ex) { host.Report(ex); }
        }
        return Win.DefWindowProcW(hwnd, msg, w, l);
    }

    private nint OnMessage(uint msg, nuint w, nint l)
    {
        MessageCount++;
        if (msg == activateMessage) { Raise(ShellSignalKind.Activated); return 0; }
        if (msg == taskbarMessage)
        {
            if (trayEnabled && !closing) { RestoreTray(); Raise(ShellSignalKind.TaskbarRestored, TrayRegistered ? 1 : 0); }
            return 0;
        }
        switch (msg)
        {
            case DispatchMessage: DispatchCount++; Drain(32); return 0;
            case ExitMessage: Drain(QueueLimit); Win.PostQuitMessage(exitCode); return 0;
            case 0x10: RequestExit(); return 0;                       // WM_CLOSE
            case 0x16 when w != 0: RequestExit(); break;              // WM_ENDSESSION
            case TrayMessage: OnTrayMessage(w, l); return 0;
            case 0x113:                                              // WM_TIMER
                if (deadlines.Remove(w, out var action))
                { Win.KillTimer(Handle, w); TimerCount++; action(); }
                return 0;
            case 0x312 when hotkeys.Contains((int)w): Raise(ShellSignalKind.Hotkey, (int)w); return 0;
            case 0x31d when Interest.HasFlag(ShellInterest.Clipboard): Raise(ShellSignalKind.Clipboard); return 0;
            case 0x219 when Interest.HasFlag(ShellInterest.Device): Raise(ShellSignalKind.Device, (long)w); break;
            case 0x218 when Interest.HasFlag(ShellInterest.Power): Raise(ShellSignalKind.Power, (long)w); break;
            case 0x2b1 when Interest.HasFlag(ShellInterest.Session): Raise(ShellSignalKind.Session, (long)w); break;
            case 0x1a: case 0x31a: case 0x320:
                if (trayEnabled && !closing) RefreshTrayTheme();
                if (Interest.HasFlag(ShellInterest.Theme)) Raise(ShellSignalKind.Theme);
                break;
            case 0x1e when Interest.HasFlag(ShellInterest.Time): Raise(ShellSignalKind.Time); break;
        }
        return Win.DefWindowProcW(Handle, msg, w, l);
    }

    private void Raise(ShellSignalKind kind, long value = 0, nint window = 0, uint modifiers = 0)
        => Signal?.Invoke(new(kind, value, window, modifiers));

    private void Report(Exception ex)
    {
        try { CallbackError?.Invoke(ex); } catch { /* Never cross the native ABI. */ }
    }

    private static Win32Exception LastError(string operation) => new(Marshal.GetLastPInvokeError(), operation);
    private void VerifyOwner()
    {
        if (ownerThread != Win.GetCurrentThreadId()) throw new InvalidOperationException("Call from the host STA.");
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public void Dispose()
    {
        if (disposed) return;
        VerifyOwner();
        lock (postLock) { closing = true; Monitor.PulseAll(postLock); }
        if (Handle != 0)
        {
            SetInterest(ShellInterest.None);
            StopMouse();
            foreach (int id in hotkeys) Win.UnregisterHotKey(Handle, id);
            hotkeys.Clear();
            foreach (var id in deadlines.Keys) Win.KillTimer(Handle, id);
            deadlines.Clear();
            if (trayEnabled) RemoveTray();
            Win.DestroyWindow(Handle);
            Handle = 0;
        }
        if (classRegistered) Win.UnregisterClassW(className, module);
        if (current == this) current = null;
        if (comEntered) Win.CoUninitialize();
        disposed = true;
    }
}
