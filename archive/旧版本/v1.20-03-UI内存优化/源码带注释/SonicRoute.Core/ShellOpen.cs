using System;
using System.Diagnostics;
using System.IO;

namespace SonicRoute.Core
{
    /// <summary>
    /// Shell 交互小工具（打开目录等），供多处共用，避免同类实现分叉。
    /// </summary>
    public static class ShellOpen
    {
        /// <summary>
        /// 在文件资源管理器中打开目录（不存在则先创建）；返回是否成功。
        /// 语言文件夹（L10n.OpenExternalLangDir）与自动化脚本文件夹共用此实现。
        /// </summary>
        public static bool Folder(string? dirPath)
        {
            if (string.IsNullOrWhiteSpace(dirPath)) return false;
            try
            {
                Directory.CreateDirectory(dirPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = dirPath,
                    UseShellExecute = true,
                });
                return true;
            }
            catch { return false; }
        }
    }
}
