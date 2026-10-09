using System;
using System.IO;
using System.Threading.Tasks;

namespace SonicRoute
{
    // Added only to the isolated release copy. No collection, audio writes, hooks, or real user storage.
    public partial class App
    {
        private static string ProbePhasePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "probe-phase.txt");
        private static void ProbePhase(string phase) => File.WriteAllText(ProbePhasePath, phase);
        internal async Task RunReleaseProbeAsync()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ProbePhasePath)!);
                ProbePhase("startup");
                await Task.Delay(8000);
                for (int round = 0; round < 2; round++)
                {
                    ProbePhase("main-open-" + round);
                    ShowMainWindow();
                    _mainWindow!.ShowActivated = false;
                    await Task.Delay(800);
                    _mainWindow.Close();
                    ProbePhase("main-idle-" + round);
                    await Task.Delay(9000);
                    foreach (string style in new[] { "modern", "classic" })
                    {
                        SonicRoute.Core.ConfigService.Load().QuickPanelStyle = style;
                        ProbePhase(style + "-open-" + round);
                        ToggleQuickPanel();
                        await Task.Delay(800);
                        _quickPanel?.Close();
                        ProbePhase(style + "-idle-" + round);
                        await Task.Delay(9000);
                    }
                }
                ProbePhase("steady-idle");
                await Task.Delay(30000);
                ProbePhase("done");
                Shutdown();
            }
            catch (Exception ex)
            {
                ProbePhase("error: " + ex);
                Shutdown(1);
            }
        }
    }
}
