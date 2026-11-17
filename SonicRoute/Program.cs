using System;
using System.Windows;
using SonicRoute.Core;
using SonicRoute.Updates;

namespace SonicRoute
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            ExceptionDiagnostics.InstallProcessHandlers();
            try
            {
#if SONICROUTE_PHONE
                AndroidLabRuntime.Initialize(ref args);
#endif
                try
                {
                    var identity = AppRuntimeIdentity.Current;
                    if (!UpdateBootstrap.BeforeStartup(args, AppDataRoot.DirectoryPath, identity.CommandEdition,
                        identity.IsPackaged, identity.MutexName, Alert)) return;
                }
                catch (Exception ex)
                {
                    ExceptionDiagnostics.Record("Startup.UpdateRecovery", ex);
                    Alert("Upd.RecoveryRequired");
                    return;
                }
                var app = new App();
                app.InitializeComponent();
                if (RestartCoordinator.CompleteHandoff(ref args))
                {
                    app.StartupArguments = args;
                    app.Run();
                }
                else Environment.ExitCode = 1;
            }
            catch (Exception ex)
            {
                ExceptionDiagnostics.Record("Startup", ex);
                Environment.ExitCode = 1;
                try
                {
                    System.Windows.MessageBox.Show(string.Format(L10n.T("Diagnostics.StartupFailed"),
                        ExceptionDiagnostics.LastWrittenPath ?? ExceptionDiagnostics.LogPath), "SonicRoute", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
            }
        }

        private static void Alert(string key)
        {
            try { L10n.Instance.SetLanguage(ConfigService.Load().Language); } catch { }
            System.Windows.MessageBox.Show(L10n.T(key), "SonicRoute", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
