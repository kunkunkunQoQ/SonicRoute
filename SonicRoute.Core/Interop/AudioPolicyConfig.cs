using System;
using System.Runtime.InteropServices;

namespace SonicRoute.Core.Interop
{
    // =====================================================================
    // 按应用持久化音频设备的策略接口（Per-App Audio Routing）
    // 移植自 EarTrumpet（已验证可行的实现），不要自行猜测 API：
    //   EarTrumpet/Interop/MMDeviceAPI/IAudioPolicyConfigFactory*.cs
    //   EarTrumpet/Interop/Helpers/AudioPolicyConfigFactory*.cs
    //   EarTrumpet/DataModel/WindowsAudio/Internal/AudioPolicyConfigService.cs
    //
    // 激活方式：RoGetActivationFactory("Windows.Media.Internal.AudioPolicyConfig")
    //   接口 vtable：IUnknown(3) + IInspectable(3) + 19 个内部方法 = 25 个槽位，
    //   SetPersistedDefaultAudioEndpoint 位于槽位 25，Get 位于槽位 26。
    //   Win11(21H2+) 接口 IID：{ab3d4648-e242-459f-b02f-541c70306324}
    //   Win10          接口 IID：{2a59116d-6c4f-45e0-a74f-707e3fef9258}
    //
    // 实现说明（实测结论，避免踩坑）：
    //   1. .NET 8 的 DllImport 不支持 [MarshalAs(UnmanagedType.HString)] string，
    //      HSTRING 必须手动 WindowsCreateString 创建后以 IntPtr 传入。
    //   2. 该 WinRT 激活工厂对象的 RCW 在 .NET 8 上无法通过 CLR 封送调用
    //      自定义 ComImport 接口，因此这里用手动 vtable + 函数指针直接调用，
    //      全程只使用 blittable 类型（uint/int/enum/IntPtr），稳定可靠。
    //   3. 设备 ID 传完整设备接口路径（IMMDevice.GetId() 返回的
    //      \\?\SWD#MMDEVAPI#... 形式），且同时设置 eMultimedia 与 eConsole。
    // =====================================================================

    public sealed class AudioPolicyConfig : IDisposable
    {
        private const string ACTIVATABLE_CLASS_ID = "Windows.Media.Internal.AudioPolicyConfig";
        private const int BUILD_21H2 = 22000;

        private const string MMDEVAPI_TOKEN = @"\\?\SWD#MMDEVAPI#";
        private const string DEVINTERFACE_AUDIO_RENDER = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
        private const string DEVINTERFACE_AUDIO_CAPTURE = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";

        // vtable 槽位：3 IUnknown + 3 IInspectable + 19 内部方法
        private const int VTBL_SLOT_SET = 25;
        private const int VTBL_SLOT_GET = 26;

        private static readonly Guid IID_21H2 = new Guid("ab3d4648-e242-459f-b02f-541c70306324");
        private static readonly Guid IID_DOWNLEVEL = new Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258");

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetPersistedDefaultAudioEndpointDelegate(
            IntPtr self, uint processId, EDataFlow flow, ERole role, IntPtr deviceId);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetPersistedDefaultAudioEndpointDelegate(
            IntPtr self, uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);

        private readonly EDataFlow _flow;
        private readonly object _sync = new();
        private IntPtr _factory = IntPtr.Zero;

        public AudioPolicyConfig(EDataFlow flow)
        {
            _flow = flow;
        }

        private bool IsWin11 => Environment.OSVersion.Version.Build >= BUILD_21H2;

        /// <summary>取得策略接口指针（QI 后的目标接口，含正确 vtable）。进程内复用，不释放。</summary>
        private IntPtr EnsureFactory()
        {
            if (_factory != IntPtr.Zero) return _factory;
            lock (_sync)
            {
                if (_factory != IntPtr.Zero) return _factory;

                var iid = IsWin11 ? IID_21H2 : IID_DOWNLEVEL;

                int hrCreate = NativeMethods.WindowsCreateString(
                    ACTIVATABLE_CLASS_ID, (uint)ACTIVATABLE_CLASS_ID.Length, out IntPtr hClass);
                if (hrCreate < 0)
                    throw new InvalidOperationException($"WindowsCreateString 失败 HRESULT=0x{hrCreate:X8} ({hrCreate})");

                try
                {
                    int hr = NativeMethods.RoGetActivationFactory(hClass, ref iid, out IntPtr factoryUnk);
                    if (hr < 0)
                        throw new InvalidOperationException($"RoGetActivationFactory 失败 HRESULT=0x{hr:X8} ({hr})");

                    hr = Marshal.QueryInterface(factoryUnk, ref iid, out _factory);
                    Marshal.Release(factoryUnk);
                    if (hr < 0)
                        throw new InvalidOperationException($"QueryInterface 失败 HRESULT=0x{hr:X8} ({hr})");

                    return _factory;
                }
                finally
                {
                    NativeMethods.WindowsDeleteString(hClass);
                }
            }
        }

        private static T GetMethod<T>(IntPtr factory, int slot) where T : Delegate
        {
            IntPtr vtbl = Marshal.ReadIntPtr(factory);
            IntPtr fnPtr = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(fnPtr);
        }

        /// <summary>
        /// 将某进程的输出/输入设备持久化为指定设备。
        /// fullDeviceId 必须是 IMMDevice.GetId() 的完整设备接口路径。
        /// 与 EarTrumpet 一致：同时设置 eMultimedia 与 eConsole 两个 role。
        /// 返回 HRESULT（&gt;=0 成功）。
        /// </summary>
        public int SetDefaultEndPoint(string fullDeviceId, int processId)
        {
            IntPtr factory = EnsureFactory();

            IntPtr hstring = IntPtr.Zero;
            if (!string.IsNullOrWhiteSpace(fullDeviceId))
            {
                int hrCreate = NativeMethods.WindowsCreateString(fullDeviceId, (uint)fullDeviceId.Length, out hstring);
                if (hrCreate < 0)
                    throw new InvalidOperationException($"WindowsCreateString 失败 HRESULT=0x{hrCreate:X8} ({hrCreate})");
            }

            try
            {
                var fn = GetMethod<SetPersistedDefaultAudioEndpointDelegate>(factory, VTBL_SLOT_SET);
                int hr1 = fn(factory, (uint)processId, _flow, ERole.eMultimedia, hstring);
                int hr2 = fn(factory, (uint)processId, _flow, ERole.eConsole, hstring);
                return hr1 < 0 ? hr1 : hr2;
            }
            finally
            {
                if (hstring != IntPtr.Zero)
                    NativeMethods.WindowsDeleteString(hstring);
            }
        }

        /// <summary>
        /// 读取某进程当前持久化的设备完整 ID；未设置时返回 null。
        /// </summary>
        public string? GetDefaultEndPoint(int processId)
        {
            IntPtr factory = EnsureFactory();
            var fn = GetMethod<GetPersistedDefaultAudioEndpointDelegate>(factory, VTBL_SLOT_GET);

            int hr = fn(factory, (uint)processId, _flow, ERole.eMultimedia, out IntPtr hstring);
            if (hr < 0 || hstring == IntPtr.Zero) return null;

            try
            {
                IntPtr raw = NativeMethods.WindowsGetStringRawBuffer(hstring, out uint len);
                if (raw == IntPtr.Zero) return null;
                return Marshal.PtrToStringUni(raw, (int)len);
            }
            finally
            {
                NativeMethods.WindowsDeleteString(hstring);
            }
        }

        /// <summary>释放持有的 COM 工厂接口引用（内存优化①）：每次切换设备 new 一个实例，
        /// 若只增不减会持续累积 COM 引用导致无法 GC 回收。调用方用 using 包裹。</summary>
        public void Dispose()
        {
            lock (_sync)
            {
                if (_factory != IntPtr.Zero)
                {
                    Marshal.Release(_factory);
                    _factory = IntPtr.Zero;
                }
            }
            GC.SuppressFinalize(this);
        }        /// <summary>生成完整设备接口路径（把 IMMDevice.GetId() 的短 ID 包装为策略 API 需要的完整路径）。</summary>
        public static string GenerateDeviceId(string shortDeviceId, EDataFlow flow)
        {
            var suffix = flow == EDataFlow.eRender ? DEVINTERFACE_AUDIO_RENDER : DEVINTERFACE_AUDIO_CAPTURE;
            return $"{MMDEVAPI_TOKEN}{shortDeviceId}{suffix}";
        }

        /// <summary>确保设备 ID 为完整接口路径（已是完整路径则原样返回，否则包装）。</summary>
        public static string EnsureFullDeviceId(string deviceId, EDataFlow flow)
        {
            if (deviceId.StartsWith(MMDEVAPI_TOKEN, StringComparison.OrdinalIgnoreCase))
                return deviceId;
            return GenerateDeviceId(deviceId, flow);
        }

        /// <summary>从完整接口路径还原短 ID（去掉前缀与方向后缀），用于与设备列表比对。</summary>
        public static string UnpackDeviceId(string fullDeviceId)
        {
            var id = fullDeviceId;
            if (id.StartsWith(MMDEVAPI_TOKEN, StringComparison.OrdinalIgnoreCase))
                id = id.Substring(MMDEVAPI_TOKEN.Length);
            if (id.EndsWith(DEVINTERFACE_AUDIO_RENDER, StringComparison.OrdinalIgnoreCase))
                id = id.Substring(0, id.Length - DEVINTERFACE_AUDIO_RENDER.Length);
            if (id.EndsWith(DEVINTERFACE_AUDIO_CAPTURE, StringComparison.OrdinalIgnoreCase))
                id = id.Substring(0, id.Length - DEVINTERFACE_AUDIO_CAPTURE.Length);
            return id;
        }
    }
}
