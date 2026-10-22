using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SonicRoute.BenchmarkLab.Helpers
{
    public sealed class ForegroundSwitchStats
    {
        public int Requested { get; set; }
        public int Successful { get; set; }
        public int Failed { get; set; }
        public double SuccessRate => Requested > 0 ? (double)Successful / Requested : 0;
    }

    internal static class DummyProcessHelper
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// Periodically spawns and terminates short-lived dummy processes to simulate system process churn.
        /// Returns the actual count of processes successfully spawned.
        /// </summary>
        public static async Task<int> SimulateProcessChurnAsync(string processName, int count, int delayMs, CancellationToken token)
        {
            int spawned = 0;
            for (int i = 0; i < count && !token.IsCancellationRequested; i++)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c exit",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };

                    using var p = Process.Start(psi);
                    if (p != null)
                    {
                        spawned++;
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore transient errors
                }
            }
            return spawned;
        }

        /// <summary>
        /// Simulates rapid foreground focus switches between processes and verifies using GetForegroundWindow.
        /// Returns detailed statistics (Requested, Successful, Failed).
        /// </summary>
        public static async Task<ForegroundSwitchStats> SimulateForegroundSwitchesAsync(int targetSwitches, int switchDelayMs, CancellationToken token)
        {
            var stats = new ForegroundSwitchStats();
            Process? p1 = null;
            Process? p2 = null;

            try
            {
                var psi1 = new ProcessStartInfo("cmd.exe", "/k title BenchmarkFocus1") { WindowStyle = ProcessWindowStyle.Normal };
                var psi2 = new ProcessStartInfo("cmd.exe", "/k title BenchmarkFocus2") { WindowStyle = ProcessWindowStyle.Normal };

                p1 = Process.Start(psi1);
                p2 = Process.Start(psi2);

                await Task.Delay(400, token);

                if (p1 != null && p2 != null)
                {
                    for (int i = 0; i < targetSwitches && !token.IsCancellationRequested; i++)
                    {
                        var target = (i % 2 == 0) ? p1 : p2;
                        stats.Requested++;

                        try
                        {
                            target.Refresh();
                            if (!target.HasExited && target.MainWindowHandle != IntPtr.Zero)
                            {
                                SetForegroundWindow(target.MainWindowHandle);
                                await Task.Delay(80, token).ConfigureAwait(false);

                                IntPtr actualFg = GetForegroundWindow();
                                if (actualFg == target.MainWindowHandle)
                                {
                                    stats.Successful++;
                                }
                                else
                                {
                                    stats.Failed++;
                                }
                            }
                            else
                            {
                                stats.Failed++;
                            }
                        }
                        catch
                        {
                            stats.Failed++;
                        }

                        int remainingDelay = Math.Max(50, switchDelayMs - 80);
                        await Task.Delay(remainingDelay, token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                try { if (p1 != null && !p1.HasExited) p1.Kill(true); } catch { }
                try { if (p2 != null && !p2.HasExited) p2.Kill(true); } catch { }
                p1?.Dispose();
                p2?.Dispose();
            }

            return stats;
        }
    }
}
