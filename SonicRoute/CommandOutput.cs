using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SonicRoute
{
    internal static class CommandOutput
    {
        internal static void Write(string output)
        {
            try
            {
                // GUI 子系统没有默认控制台；保留调用方已有重定向，否则附着父控制台。
                if (GetFileType(GetStdHandle(-11)) == 0) AttachConsole(uint.MaxValue);
                var handle = GetStdHandle(-11);
                if (GetFileType(handle) == 2)
                {
                    string line = output + Environment.NewLine;
                    if (WriteConsole(handle, line, (uint)line.Length, out _, IntPtr.Zero)) return;
                }
                using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                writer.WriteLine(output);
                writer.Flush();
            }
            catch { } // 由快捷方式启动时可无控制台；退出码仍可由调用者读取。
        }

        [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint processId);
        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
        [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);
        [DllImport("kernel32.dll", EntryPoint = "WriteConsoleW", CharSet = CharSet.Unicode)]
        private static extern bool WriteConsole(IntPtr handle, string text, uint length, out uint written, IntPtr reserved);
    }
}
