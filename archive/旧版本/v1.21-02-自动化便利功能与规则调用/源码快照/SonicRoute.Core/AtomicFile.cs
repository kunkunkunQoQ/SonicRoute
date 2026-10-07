using System;
using System.IO;
using System.Text;
using System.Threading;

namespace SonicRoute.Core
{
    /// <summary>先在同目录完整写入临时文件，再原子替换；失败时保留原文件。</summary>
    internal static class AtomicFile
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        internal static void WriteAllText(string path, string text)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string rollback = temporary + ".rollback";
            bool committed = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // 小缓冲编码，避免大配置/长规则再分配一份完整 UTF8 字节数组。
                    using (var writer = new StreamWriter(stream, Utf8, 1024, true))
                    {
                        writer.Write(text);
                        writer.Flush();
                    }
                    // Writer/Stream Dispose 将数据提交到 OS 写缓存，沿用原来的写盘策略。
                    // 不逐次强制物理磁盘同步，避免 UI 保存出现几百毫秒的尾部延迟。
                }
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temporary, path, rollback);
                        else File.Move(temporary, path);
                        committed = true;
                        break;
                    }
                    catch (IOException error) when (attempt < 2 && IsSharingConflict(error)
                        && File.Exists(temporary) && File.Exists(path) && !File.Exists(rollback))
                    {
                        // 短暂读句柄可能阻挡 Windows 原子替换。最多额外等待20ms，
                        // 持续占用仍报错并保留旧文件，不退化为先删除目标。
                        Thread.Sleep(attempt == 0 ? 5 : 15);
                    }
                }
            }
            catch
            {
                // ReplaceFile 部分失败可能已把旧文件移到备份名。恢复原路径；
                // 若文件系统仍阻止恢复，保留 rollback 中的原数据供后续恢复。
                try { if (File.Exists(rollback) && !File.Exists(path)) File.Move(rollback, path); }
                catch { }
                throw;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                if (committed) { try { if (File.Exists(rollback)) File.Delete(rollback); } catch { } }
            }
        }

        private static bool IsSharingConflict(IOException error)
        {
            int code = error.HResult & 0xffff;
            return code == 32 || code == 33 || code == 1175;
        }
    }
}
