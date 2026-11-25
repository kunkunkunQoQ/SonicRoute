using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SonicRoute.Core
{
    /// <summary>Error-triggered, bounded diagnostics. No open stream, worker or timer at idle.</summary>
    public static class ExceptionDiagnostics
    {
        private const long MaxLogBytes = 1024 * 1024;
        private const int MaxEntryChars = 32768;
        private static readonly object Gate = new();
        private static int _installed;
        [ThreadStatic] private static bool _writing;
        public static string LogPath => Path.Combine(AppDataRoot.DirectoryPath, "Logs", "diagnostics.log");
        public static string? LastWrittenPath { get; private set; }

        public static void InstallProcessHandlers()
        {
            if (Interlocked.Exchange(ref _installed, 1) != 0) return;
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Record(e.IsTerminating ? "Unhandled.Terminating" : "Unhandled", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) => Record("Task.Unobserved", e.Exception);
        }

        public static void Record(string stage, Exception? error)
        {
            if (_writing) return;
            _writing = true;
            try
            {
                string detail;
                try { detail = error?.ToString() ?? "No exception details available."; }
                catch { detail = "Exception details unavailable."; }
                if (detail.Length > MaxEntryChars) detail = detail.Substring(0, MaxEntryChars) + "\n[truncated]";
                string entry = DateTime.UtcNow.ToString("o") + " [" + stage + "] " + detail + Environment.NewLine;
                string? preferred = null;
                try { preferred = LogPath; } catch { }
                if ((preferred == null || !TryWrite(preferred, entry))
                    && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")))
                    TryWrite(Path.Combine(Path.GetTempPath(), "SonicRoute", "diagnostics.log"), entry);
            }
            catch { /* Diagnostics must not replace the original failure. */ }
            finally { _writing = false; }
        }

        private static bool TryWrite(string path, string entry)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    if (File.Exists(path) && new FileInfo(path).Length >= MaxLogBytes)
                    {
                        string previous = path + ".1";
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(path, previous);
                    }
                    File.AppendAllText(path, entry, new UTF8Encoding(false));
                    LastWrittenPath = path;
                }
                return true;
            }
            catch { return false; }
        }
    }
}
