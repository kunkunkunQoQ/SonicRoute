using System;
using System.Linq;
using System.Text;
using SonicRoute.Core;

namespace SonicRoute
{
    internal static partial class RuleCommandLine
    {
        internal static string BuildShortCommand(string id)
        {
            string selector = Guid.TryParse(id, out var guid) ? "\"" + guid.ToString("N") + "\"" : "'" + id.Replace("'", "''") + "'";
#if NET48
            return "sr --legacy " + selector;
#else
            return "sr " + selector;
#endif
        }

        internal enum Mode { None, Run, Help, Invalid }

        internal static Mode Parse(string[] args, out string selector)
        {
            selector = "";
            if (args.Length == 1 && (EqualsArg(args[0], "--help") || EqualsArg(args[0], "-h")))
                return Mode.Help;
            if (args.Length == 2 && (EqualsArg(args[0], "--run-rule") || EqualsArg(args[0], "-r")) && !string.IsNullOrWhiteSpace(args[1]))
            {
                selector = args[1].Trim();
                return Encoding.UTF8.GetByteCount(selector) <= RuleCommandFrame.MaxFrameBytes ? Mode.Run : Mode.Invalid;
            }
            // 保留原 --main/--panel 启动行为；混用规则调用和界面参数视为参数错误。
            return args.Any(a => EqualsArg(a, "--run-rule") || EqualsArg(a, "-r") || EqualsArg(a, "--help") || EqualsArg(a, "-h")
                || a.StartsWith("--run-rule=", StringComparison.OrdinalIgnoreCase)) ? Mode.Invalid : Mode.None;
        }

        private static bool EqualsArg(string value, string expected) =>
            string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }
}
