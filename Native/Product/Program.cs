using System.Runtime.InteropServices;
using System.Text.Json;
using SonicRoute.Core;

namespace SonicRoute.Native.Product;

internal static partial class Program
{
    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();

    [STAThread]
    private static int Main(string[] args)
    {
        CandidateEnvironment? environment = null;
        try
        {
            environment = CandidateEnvironment.Open();
            ExceptionDiagnostics.InstallProcessHandlers();
            int hr = CoInitializeEx(0, 2);
            Marshal.ThrowExceptionForHR(hr);
            try { return Start(environment, args); }
            finally { CoUninitialize(); }
        }
        catch (Exception error)
        {
            if (environment != null) ExceptionDiagnostics.Record("NativeHost.Main", error);
            CommandOutput.Write(error.Message);
            return 8;
        }
    }

    private static int Start(CandidateEnvironment env, string[] args)
    {
        if (!ProductProtocol.BeforeStartup(env, ref args)) return 0;
        var mode = ProductProtocol.ParseRule(args, out var selector);
        if (mode == RuleMode.Help)
        {
            string language = ConfigService.Load().Language;
            L10n.Instance.SetLanguage(string.IsNullOrWhiteSpace(language) ? HostStartup.DetectSystemLanguage() : language);
            CommandOutput.Write(L10n.T("Auto.CliHelp"));
            return 0;
        }
        if (mode == RuleMode.Invalid) return WriteRule(RuleCommandLine.EncodeResult(new() { ExitCode = 2, Code = "invalid_arguments" }));

        using var instance = new ProductInstance(env);
        if (args is ["--control", var verb, .. var parameters])
        {
            if (instance.IsPrimary) { CommandOutput.Write("host_unavailable"); return 6; }
            var request = JsonSerializer.Serialize(new ControlRequest(verb, parameters), HostJson.Default.ControlRequest);
            string response = instance.SendControlAsync(request).GetAwaiter().GetResult();
            CommandOutput.Write(response);
            return response.StartsWith("error:", StringComparison.Ordinal) ? 1 : 0;
        }
        if (!instance.IsPrimary)
        {
            if (mode == RuleMode.Run) return WriteRule(instance.SendRuleAsync(selector).GetAwaiter().GetResult());
            // The hidden HWND is created after the mutex. The control pipe also accepts
            // activation during that startup window.
            if (!NativeApplicationWindow.ActivateExisting(env.ActivationTitle, env.ActivationMessage))
            {
                string activate = JsonSerializer.Serialize(new ControlRequest("activate", []), HostJson.Default.ControlRequest);
                return instance.SendControlAsync(activate).GetAwaiter().GetResult() == "ok" ? 0 : 6;
            }
            return 0;
        }

        bool temporary = mode == RuleMode.Run;
        using var window = new NativeApplicationWindow(env.ActivationTitle, env.ActivationMessage,
            BuildIdentity.DisplayName + " · Native Candidate", trayEnabled: !temporary);
        using var host = new ApplicationHost(env, window, instance);
        return host.Run(args, temporary ? selector : null);
    }

    internal static int WriteRule(string json)
    {
        CommandOutput.Write(json);
        return RuleCommandLine.ReadResult(json)?.ExitCode ?? 6;
    }
}
