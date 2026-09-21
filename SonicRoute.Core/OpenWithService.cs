using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SonicRoute.Core.Interop;

namespace SonicRoute.Core
{
    /// <summary>「打开方式」候选项：完全由系统文件关联决定，不写死任何程序名。</summary>
    public sealed class OpenWithApp
    {
        /// <summary>实际启动的 EXE 完整路径（自动化配置保存的就是它）。</summary>
        public string ExePath { get; set; } = "";

        /// <summary>显示名（来自系统 UIName，仅用于界面展示）。</summary>
        public string DisplayName { get; set; } = "";
    }

    /// <summary>
    /// 「打开方式」候选应用枚举（v1.18 二次改造）。
    /// - 数据源 = Windows Shell 的 SHAssocEnumHandlers，与资源管理器「打开方式」列表同源，
    ///   因此候选应用由系统实际安装与文件关联决定，不写死 QQ音乐 / VLC 等名称。
    /// - 顺序：先「推荐应用」，再补「全部已注册应用」，按 EXE 路径去重。
    /// - 只保留能解析出真实 EXE 路径的桌面应用：打包（UWP/MSIX）处理程序没有 EXE 路径，
    ///   无法用「EXE + 目标文件参数」的方式启动，故不作为可选打开方式（此类场景用「默认程序」）。
    /// - 按扩展名缓存（上限 64，FIFO 淘汰）；任何异常都返回空列表，不影响自动化编辑界面。
    /// </summary>
    public static class OpenWithService
    {
        private const int MaxCacheSize = 64;

        private static readonly ConcurrentDictionary<string, List<OpenWithApp>> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 取某文件（按扩展名）可用的「打开方式」应用列表。
        /// 传入文件路径或扩展名均可；无扩展名（如 .exe 或空路径）时返回空列表。
        /// </summary>
        public static List<OpenWithApp> GetHandlers(string? filePathOrExtension)
        {
            var ext = NormalizeExtension(filePathOrExtension);
            if (ext.Length == 0) return new List<OpenWithApp>();
            if (!Cache.TryGetValue(ext, out var cached))
            {
                cached = Enumerate(ext);
                Cache[ext] = cached;
                TrimIfNeeded();
            }
            return new List<OpenWithApp>(cached);
        }

        /// <summary>规范化扩展名（.mp3）；传入 EXE 等无关联扩展名或空值时返回空串。</summary>
        public static string NormalizeExtension(string? filePathOrExtension)
        {
            var s = (filePathOrExtension ?? "").Trim();
            if (s.Length == 0) return "";
            string ext;
            if (s[0] == '.' && s.IndexOf('\\') < 0 && s.IndexOf('/') < 0) ext = s;
            else
            {
                try { ext = Path.GetExtension(s); }
                catch { return ""; }
            }
            return ext.Length <= 1 ? "" : ext.ToLowerInvariant();
        }

        private static List<OpenWithApp> Enumerate(string ext)
        {
            var list = new List<OpenWithApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Collect(ext, AssocHandlerNative.AssocFilterRecommended, list, seen);
            Collect(ext, AssocHandlerNative.AssocFilterNone, list, seen);
            return list;
        }

        private static void Collect(string ext, int filter, List<OpenWithApp> list, HashSet<string> seen)
        {
            IEnumAssocHandlers? enumerator = null;
            try
            {
                AssocHandlerNative.SHAssocEnumHandlers(ext, filter, out enumerator);
                if (enumerator == null) return;

                var buffer = new IAssocHandler[8];
                while (true)
                {
                    int hr = enumerator.Next((uint)buffer.Length, buffer, out uint fetched);
                    if (fetched == 0) break;
                    for (uint i = 0; i < fetched; i++)
                    {
                        var handler = buffer[i];
                        buffer[i] = null!;
                        if (handler == null) continue;
                        try { AddHandler(handler, list, seen); }
                        finally { Marshal.ReleaseComObject(handler); }
                    }
                    if (hr != 0) break;
                }
            }
            catch
            {
                // 枚举失败（系统不支持 / 权限等）静默降级：界面仍可用「默认程序」与「选择其他应用」
            }
            finally
            {
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

        private static void AddHandler(IAssocHandler handler, List<OpenWithApp> list, HashSet<string> seen)
        {
            var name = TakeString(handler.GetName(out var pName) == 0 ? pName : IntPtr.Zero);
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            if (!File.Exists(name)) return;   // 打包应用返回的是应用名而非路径，在此被过滤

            var ui = TakeString(handler.GetUIName(out var pUi) == 0 ? pUi : IntPtr.Zero);
            if (string.IsNullOrWhiteSpace(ui)) ui = Path.GetFileNameWithoutExtension(name);
            if (!seen.Add(name)) return;

            list.Add(new OpenWithApp { ExePath = name, DisplayName = ui.Trim() });
        }

        /// <summary>读取 CoTaskMem 分配的 LPWSTR 并释放（Shell 出参约定）。</summary>
        private static string TakeString(IntPtr p)
        {
            if (p == IntPtr.Zero) return "";
            try { return Marshal.PtrToStringUni(p) ?? ""; }
            finally { Marshal.FreeCoTaskMem(p); }
        }

        /// <summary>缓存超上限时按枚举顺序淘汰，控制常驻内存（与 AppIconService 同策略）。</summary>
        private static void TrimIfNeeded()
        {
            int over = Cache.Count - MaxCacheSize;
            if (over <= 0) return;
            foreach (var kv in Cache)
            {
                if (over <= 0) break;
                if (Cache.TryRemove(kv.Key, out _)) over--;
            }
        }
    }
}
