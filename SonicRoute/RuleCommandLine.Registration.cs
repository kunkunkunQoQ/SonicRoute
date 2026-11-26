using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace SonicRoute
{
    internal static partial class RuleCommandLine
    {
        /// <summary>正常启动时登记一次用户级短命令，后台写入；不覆盖整个 PATH，也不启动额外常驻进程。</summary>
        internal static void EnsureShortCommand(string help)
        {
            // Explicitly isolated tooling must not register production short commands.
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY"))) return;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                using var setupMutex = new Mutex(false, @"Local\SonicRoute_ShortCommand_" + identity.User!.Value);
                bool acquired;
                try { acquired = setupMutex.WaitOne(3000); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return;
                try
                {
                    string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "SonicRoute", "Command");
                    Directory.CreateDirectory(directory);
                    using var resource = typeof(RuleCommandLine).Assembly.GetManifestResourceStream("SonicRoute.Resources.RuleCommand.ps1");
                    if (resource == null) return;
                    using var reader = new StreamReader(resource, Encoding.UTF8);
                    WriteIfChanged(Path.Combine(directory, "sr-client.ps1"), reader.ReadToEnd(), bom: true);
                    WriteIfChanged(Path.Combine(directory, "sr-help.txt"), help, bom: false);
                    WriteIfChanged(Path.Combine(directory, "sr.cmd"),
                        "@echo off\r\n\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" " +
                        "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0sr-client.ps1\" %*\r\n" +
                        "exit /b %errorlevel%\r\n", bom: false);

                    string processPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                    if (!ContainsCommandPath(processPath, directory))
                        Environment.SetEnvironmentVariable("PATH", processPath.TrimEnd(';')
                            + (processPath.Length > 0 ? ";" : "") + directory);

                    using var environment = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Environment");
                    if (environment == null) return;
                    string path = environment.GetValue("Path", "", Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                    bool contains = ContainsCommandPath(path, directory);
                    if (contains) return;
                    var kind = environment.GetValue("Path") == null ? Microsoft.Win32.RegistryValueKind.ExpandString
                        : environment.GetValueKind("Path");
                    if (kind != Microsoft.Win32.RegistryValueKind.String && kind != Microsoft.Win32.RegistryValueKind.ExpandString)
                        kind = Microsoft.Win32.RegistryValueKind.ExpandString;
                    environment.SetValue("Path", path.TrimEnd(';') + (path.Length > 0 ? ";" : "") + directory, kind);
                    // 通知 Explorer，使新终端继承更新后的用户 PATH；旧终端需重新打开。
                    SendMessageTimeout(new IntPtr(0xffff), 0x001a, IntPtr.Zero, "Environment", 2, 2000, out _);
                }
                finally { setupMutex.ReleaseMutex(); }
            }
            catch (Exception ex) { AutoRuleScheduler.Log("短命令登记失败: " + ex.Message); }
        }

        private static bool ContainsCommandPath(string path, string directory) => path.Split(';').Any(part =>
            string.Equals(Environment.ExpandEnvironmentVariables(part.Trim().Trim('"')).TrimEnd('\\'),
                directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

        private static void WriteIfChanged(string path, string text, bool bom)
        {
            if (File.Exists(path) && File.ReadAllText(path) == text) return;
            File.WriteAllText(path, text, new UTF8Encoding(bom));
        }

        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam,
            string lParam, uint flags, uint timeout, out IntPtr result);
    }
}
