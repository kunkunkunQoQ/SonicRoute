using System;
using System.Runtime.InteropServices;

namespace SonicRoute
{
    /// <summary>必须在有消息循环的线程创建、释放；委托在整个 hook 生命周期内保持存活。</summary>
    internal sealed class ForegroundChangeSource : IDisposable
    {
        private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int objectId, int childId, uint thread, uint time);
        private readonly WinEventProc _callback;
        private IntPtr _hook;
        internal bool IsInstalled => _hook != IntPtr.Zero;

        internal ForegroundChangeSource(Action changed)
        {
            _callback = (_, _, _, _, _, _, _) => { try { changed(); } catch { } };
            // OUTOFCONTEXT: callback 送到注册线程；也接收自身窗口事件，使旧查询及时失效。
            TryRegister();
        }
        internal bool TryRegister()
        {
            if (IsInstalled) return true;
            try { _hook = SetWinEventHook(3, 3, IntPtr.Zero, _callback, 0, 0, 0); }
            catch { _hook = IntPtr.Zero; }
            return IsInstalled;
        }
        public void Dispose()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hook);
    }
}
