using System;
using System.Runtime.InteropServices;

namespace SonicRoute.Core.Interop
{
    /// <summary>
    /// 「打开方式」处理程序枚举接口（Windows Shell 官方数据源，声明取自 Windows SDK ShObjIdl_core.h）。
    /// 说明：接口只声明实际使用的方法（vtable 前缀），未使用的 IsRecommended / MakeDefault /
    /// Invoke / CreateInvoker 不声明，避免手写 vtable 出错（与项目既有手写互操作风格一致）。
    /// </summary>
    [ComImport, Guid("973810AE-9599-4B88-9E4D-6EE98C9552DA"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEnumAssocHandlers
    {
        [PreserveSig]
        int Next(uint celt,
            [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Interface, SizeParamIndex = 0)] IAssocHandler[] rgelt,
            out uint pceltFetched);
    }

    /// <summary>单个「打开方式」处理程序。</summary>
    [ComImport, Guid("F04061AC-1659-4A3F-A954-775AA57FC083"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAssocHandler
    {
        /// <summary>
        /// 处理程序名称：桌面（Win32）应用返回 EXE 完整路径；打包（UWP/MSIX）应用返回应用名而非路径。
        /// 返回值为 CoTaskMem 分配的 LPWSTR，调用方负责 FreeCoTaskMem。
        /// </summary>
        [PreserveSig]
        int GetName(out IntPtr ppszName);

        /// <summary>面向用户的显示名（CoTaskMem 分配的 LPWSTR，调用方负责 FreeCoTaskMem）。</summary>
        [PreserveSig]
        int GetUIName(out IntPtr ppszUIName);
    }

    /// <summary>shell32.dll 导出的文件关联枚举入口（Vista+）。</summary>
    public static class AssocHandlerNative
    {
        /// <summary>只返回系统推荐的处理程序（ASSOC_FILTER_RECOMMENDED）。</summary>
        public const int AssocFilterRecommended = 1;

        /// <summary>返回全部已注册的处理程序（ASSOC_FILTER_NONE）。</summary>
        public const int AssocFilterNone = 0;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        public static extern void SHAssocEnumHandlers(
            [MarshalAs(UnmanagedType.LPWStr)] string pszExtra,
            int afFilter,
            out IEnumAssocHandlers ppEnumHandler);
    }
}
