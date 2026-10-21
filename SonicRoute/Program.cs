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
            try
            {
                var identity = AppRuntimeIdentity.Current;
                if (!UpdateBootstrap.BeforeStartup(args, AppDataRoot.DirectoryPath, identity.CommandEdition,
                    identity.IsPackaged, identity.MutexName, Alert)) return;
            }
            catch { Alert("Upd.RecoveryRequired"); return; }
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }

        private static void Alert(string key)
        {
            try { L10n.Instance.SetLanguage(ConfigService.Load().Language); } catch { }
            System.Windows.MessageBox.Show(L10n.T(key), "SonicRoute", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
