using System.Diagnostics;
using System.Runtime.InteropServices;
using SonicRoute.Core;
using SonicRoute.Core.Interop;

internal static class NativeProgram
{
    private static int _passed;
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); _passed++; Console.WriteLine("PASS " + name); }
    public static int Main()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            int pid = process.Id;
            var outputs = AudioService.GetDevices(EDataFlow.eRender);
            Check(outputs.Count > 0, "read real output endpoints");
            var system = SystemVolumeService.ReadState();
            Check(system.Volume >= 0 && system.Volume <= 100, "read real default endpoint without writing");
            var mic = GlobalMicMuteService.ReadState();
            Console.WriteLine($"READ microphoneCount={mic.Count} unavailable={mic.UnavailableCount} (no microphone writes)");
            using var stream = new SilentSession();
            (int Volume, bool Muted) before = (-1, false);
            for (int i = 0; i < 20; i++)
            {
                before = SessionVolumeService.ReadState(pid);
                if (before.Volume >= 0) break;
                Thread.Sleep(50); // Bounded test-stream initialization, never product polling.
            }
            Check(before.Volume >= 0, "discover only test process silent session");
            bool restored = false;
            try
            {
                Check(AudioService.GetApps(true).Any(app => app.ProcessId == pid), "real app enumeration retains test session");
                Check(SessionVolumeService.SetVolumePercent(pid, 37), "set only own session volume");
                Check(SessionVolumeService.ReadState(pid).Volume == 37, "combined real own-session readback");
                Check(SessionVolumeService.SetMute(pid, true) && SessionVolumeService.ReadState(pid).Muted, "mute and read only own silent session");
                Check(SessionVolumeService.SetMute(pid, false) && !SessionVolumeService.ReadState(pid).Muted, "unmute and read only own silent session");
            }
            finally
            {
                bool volumeRestored = SessionVolumeService.SetVolumePercent(pid, before.Volume);
                bool muteRestored = SessionVolumeService.SetMute(pid, before.Muted);
                restored = volumeRestored && muteRestored;
            }
            Check(restored, "restore own session before disposing silent stream");
            Console.WriteLine($"RESULT passed={_passed} runtime={Environment.Version} writes=OWN_SILENT_SESSION_ONLY defaultDeviceWrites=0 globalVolumeWrites=0 microphoneWrites=0 routeWrites=0");
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
    }

    // Same silent waveOut session technique used by the existing Stage 6 harness.
    private sealed class SilentSession : IDisposable
    {
        private IntPtr _handle, _header, _data;
        private bool _prepared;
        private readonly uint _size = (uint)Marshal.SizeOf(typeof(WaveHeader));
        internal SilentSession()
        {
            try
            {
                var format = new WaveFormat { Tag = 1, Channels = 2, Samples = 44100, Average = 176400, Align = 4, Bits = 16 };
                if (waveOutOpen(out _handle, uint.MaxValue, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0) throw new Exception("waveOutOpen failed");
                _data = Marshal.AllocHGlobal(352800); Marshal.Copy(new byte[352800], 0, _data, 352800);
                _header = Marshal.AllocHGlobal((int)_size);
                Marshal.StructureToPtr(new WaveHeader { Data = _data, Length = 352800, Flags = 12, Loops = 100 }, _header, false);
                if (waveOutPrepareHeader(_handle, _header, _size) != 0) throw new Exception("waveOutPrepareHeader failed");
                _prepared = true;
                if (waveOutWrite(_handle, _header, _size) != 0) throw new Exception("waveOutWrite failed");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                waveOutReset(_handle);
                if (_prepared) waveOutUnprepareHeader(_handle, _header, _size);
                waveOutClose(_handle); _handle = IntPtr.Zero;
            }
            if (_header != IntPtr.Zero) { Marshal.FreeHGlobal(_header); _header = IntPtr.Zero; }
            if (_data != IntPtr.Zero) { Marshal.FreeHGlobal(_data); _data = IntPtr.Zero; }
        }
    }
    [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct WaveFormat
    { internal ushort Tag, Channels; internal uint Samples, Average; internal ushort Align, Bits, Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct WaveHeader
    { internal IntPtr Data; internal uint Length, Recorded; internal IntPtr User; internal uint Flags, Loops; internal IntPtr Next, Reserved; }
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr handle);
}
