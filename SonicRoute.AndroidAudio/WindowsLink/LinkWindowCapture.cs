using System.Runtime.InteropServices;
using System.Text;
using SonicRoute.AndroidAudio;

namespace SonicRoute.WindowsLink;

internal static class LinkWindowCapture
{
    private const uint Bgra8 = 87;
    private static readonly Guid ItemInterop = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
    private static readonly Guid Item = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
    private static readonly Guid PoolStatics = new("589b103f-6bbc-5df5-a991-02e28b3b66d5");
    private static readonly Guid DxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid DxgiAccess = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
    private static readonly Guid Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid Closable = new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");

    internal static LinkWindow[] GetWindows()
    {
        var entries = new List<LinkWindow>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId || !IsWindowVisible(window) || window == GetShellWindow()) return true;
            if (DwmGetWindowAttribute(window, 14, out uint cloaked, sizeof(uint)) == 0 && cloaked != 0) return true;
            var title = new StringBuilder(160);
            if (GetWindowText(window, title, title.Capacity) != 0)
                entries.Add(new LinkWindow(window, title.ToString(), (int)pid));
            return true;
        }, 0);
        return entries.OrderBy(window => window.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static Task CaptureAsync(nint window, LinkBgraFrame deliver, int maxFps, CancellationToken token)
        => Task.Factory.StartNew(() => Capture(window, deliver, maxFps, token), token,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

    // An active capture owns a single MTA worker. A frame is requested at the output
    // cadence, rather than waking on every desktop refresh and copying discarded frames.
    private static void Capture(nint window, LinkBgraFrame deliver, int maxFps, CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) throw new PlatformNotSupportedException("Windows Graphics Capture is unavailable.");
        GetWindowThreadProcessId(window, out uint expectedPid);
        if (expectedPid == 0 || expectedPid == Environment.ProcessId) throw new InvalidOperationException("Select another visible window.");
        Check(RoInitialize(1));
        try
        {
            var factory = Activate("Windows.Graphics.Capture.GraphicsCaptureItem", ItemInterop);
            using (factory)
            {
                int result = ComVtbl.Get<GetInterface>(factory.Handle, 3)(factory.Handle, window, in Item, out nint captureItem);
                Check(result);
                using var item = new ComPtr(captureItem);
                Check(ComVtbl.Get<GetSize>(item.Handle, 7)(item.Handle, out Size size));
                Validate(size);
                Run(item.Handle, size, window, expectedPid, maxFps, deliver, token);
            }
        }
        finally { RoUninitialize(); }
    }

    private static unsafe void Run(nint item, Size size, nint window, uint expectedPid, int fps, LinkBgraFrame deliver, CancellationToken token)
    {
        int hr = D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out nint rawDevice, out _, out nint rawContext);
        if (hr < 0) hr = D3D11CreateDevice(0, 5, 0, 0x20, 0, 0, 7, out rawDevice, out _, out rawContext);
        Check(hr);
        using var device = new ComPtr(rawDevice);
        using var context = new ComPtr(rawContext);
        using var dxgi = device.QueryInterface(DxgiDevice);
        Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi.Handle, out nint inspectable));
        using var projectedDevice = new ComPtr(inspectable);
        using var statics = Activate("Windows.Graphics.Capture.Direct3D11CaptureFramePool", PoolStatics);
        Check(ComVtbl.Get<CreatePool>(statics.Handle, 6)(statics.Handle, projectedDevice.Handle, Bgra8, 2, size, out nint poolPtr));
        using var pool = new CaptureResource(poolPtr);
        Check(ComVtbl.Get<CreateSession>(poolPtr, 10)(poolPtr, item, out nint sessionPtr));
        using var session = new CaptureResource(sessionPtr);
        Check(ComVtbl.Get<NoArgs>(sessionPtr, 6)(sessionPtr));
        nint staging = 0;
        uint stagingWidth = 0, stagingHeight = 0;
        var cadence = TimeSpan.FromMilliseconds(1000d / Math.Clamp(fps, 1, 60));
        try
        {
        do
        {
            if (GetWindowThreadProcessId(window, out uint pid) == 0 || pid != expectedPid) return;
            Check(ComVtbl.Get<GetPtr>(poolPtr, 7)(poolPtr, out nint framePtr));
            if (framePtr == 0) continue;
            Size content;
            using (var frame = new CaptureResource(framePtr))
            {
                Check(ComVtbl.Get<GetSize>(framePtr, 8)(framePtr, out content));
                if (content.Width <= 0 || content.Height <= 0) continue;
                Validate(content);
                if (content == size)
                {
                    Check(ComVtbl.Get<GetPtr>(framePtr, 6)(framePtr, out nint surfacePtr));
                    using var surface = new ComPtr(surfacePtr);
                    using var access = surface.QueryInterface(DxgiAccess);
                    // IDirect3DDxgiInterfaceAccess derives from IUnknown.
                    Check(ComVtbl.Get<GetDxgiInterface>(access.Handle, 3)(access.Handle, in Texture2D, out nint texturePtr));
                    using var texture = new ComPtr(texturePtr);
                    ComVtbl.Get<DescribeTexture>(texturePtr, 10)(texturePtr, out TextureDesc desc);
                    if (desc.Width < content.Width || desc.Height < content.Height || desc.Format != Bgra8) continue;
                    if (staging == 0 || desc.Width != stagingWidth || desc.Height != stagingHeight)
                    {
                        stagingWidth = desc.Width; stagingHeight = desc.Height;
                        desc.Usage = 3; desc.Bind = 0; desc.CpuAccess = 0x20000; desc.Misc = 0;
                        Check(ComVtbl.Get<CreateTexture>(device.Handle, 5)(device.Handle, in desc, 0, out nint target));
                        ComVtbl.SafeRelease(ref staging);
                        staging = target;
                    }
                    ComVtbl.Get<CopyTexture>(context.Handle, 47)(context.Handle, staging, texturePtr);
                    Check(ComVtbl.Get<MapTexture>(context.Handle, 14)(context.Handle, staging, 0, 1, 0, out Mapping map));
                    try
                    {
                        int stride = checked((int)map.Pitch);
                        int bytes = checked(stride * (content.Height - 1) + content.Width * 4);
                        if (map.Data != 0 && stride >= content.Width * 4)
                            deliver(new ReadOnlySpan<byte>((void*)map.Data, bytes), content.Width, content.Height, stride);
                    }
                    finally { ComVtbl.Get<UnmapTexture>(context.Handle, 15)(context.Handle, staging, 0); }
                }
            }
            if (content != size)
            {
                Check(ComVtbl.Get<RecreatePool>(poolPtr, 6)(poolPtr, projectedDevice.Handle, Bgra8, 2, content));
                size = content;
            }
        }
        while (!token.WaitHandle.WaitOne(cadence));
        }
        finally { ComVtbl.SafeRelease(ref staging); }
    }

    private static void Validate(Size size)
    {
        if (size.Width < 1 || size.Height < 1 || (long)size.Width * size.Height > 16_777_216L)
            throw new NotSupportedException("The selected window exceeds the Windows Link capture size limit.");
    }

    private static ComPtr Activate(string name, Guid iid)
    {
        Check(WindowsCreateString(name, (uint)name.Length, out nint text));
        try { Check(RoGetActivationFactory(text, in iid, out nint factory)); return new ComPtr(factory); }
        finally { WindowsDeleteString(text); }
    }

    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    private sealed class CaptureResource(nint handle) : IDisposable
    {
        private readonly ComPtr _instance = new(handle);
        public void Dispose()
        {
            try
            {
                if (_instance.TryQueryInterface(Closable, out var closing))
                    using (closing) ComVtbl.Get<NoArgs>(closing!.Handle, 6)(closing.Handle);
            }
            finally { _instance.Dispose(); }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Size(int Width, int Height);
    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDesc { internal uint Width, Height, Mips, Array, Format, Samples, Quality, Usage, Bind, CpuAccess, Misc; }
    [StructLayout(LayoutKind.Sequential)] private struct Mapping { internal nint Data; internal uint Pitch, DepthPitch; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInterface(nint self, nint window, in Guid iid, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDxgiInterface(nint self, in Guid iid, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPtr(nint self, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetSize(nint self, out Size value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NoArgs(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreatePool(nint self, nint device, uint format, int capacity, Size size, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RecreatePool(nint self, nint device, uint format, int capacity, Size size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateSession(nint self, nint item, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DescribeTexture(nint self, out TextureDesc desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture(nint self, in TextureDesc desc, nint data, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyTexture(nint self, nint target, nint source);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapTexture(nint self, nint resource, uint index, int type, uint flags, out Mapping mapping);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapTexture(nint self, nint resource, uint index);
    private delegate bool EnumerateWindow(nint hwnd, nint state);

    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string text, uint length, out nint result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint text);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, in Guid iid, out nint result);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint result);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, uint driver, nint software, uint flags,
        nint levels, uint count, uint sdk, out nint device, out uint level, out nint context);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumerateWindow callback, nint state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder buffer, int length);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out uint value, uint size);
}
