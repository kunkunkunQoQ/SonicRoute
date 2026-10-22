using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.BenchmarkLab.Core;
using SonicRoute.BenchmarkLab.Helpers;

namespace SonicRoute.BenchmarkLab.Scenarios
{
    public abstract class BaseScenario : IBenchmarkScenario
    {
        public abstract string Id { get; }
        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract BenchmarkModule Module { get; }

        public virtual double GetDurationSeconds(BenchmarkTier tier) => tier switch
        {
            BenchmarkTier.Quick => 3.0,
            BenchmarkTier.Standard => 10.0,
            BenchmarkTier.Endurance => 30.0,
            _ => 10.0
        };

        public abstract Task<ScenarioResult> RunAsync(BenchmarkContext context);

        protected double ResolveDuration(BenchmarkContext context)
        {
            return context.CustomDurationSeconds ?? GetDurationSeconds(context.Tier);
        }
    }

    /// <summary>
    /// Scenario: Background idle resource consumption with 0 automation rules.
    /// </summary>
    public sealed class IdleEmptyScenario : BaseScenario
    {
        public override string Id => "idle_empty";
        public override string Name => "Idle Background (0 Rules)";
        public override string Description => "Measures background idle resource footprint (CPU, Memory, Handles) with zero automation rules.";
        public override BenchmarkModule Module => BenchmarkModule.General;

        public override async Task<ScenarioResult> RunAsync(BenchmarkContext context)
        {
            using var sandbox = new SonicRouteSandbox();
            double duration = ResolveDuration(context);
            double warmup = context.Tier == BenchmarkTier.Quick ? 1.5 : 3.0;

            context.Log($"[idle_empty] Starting target with 0 rules in sandbox {sandbox.InstanceId}...");

            using var monitor = ProcessMonitor.Launch(
                context.TargetExecutable,
                arguments: "",
                environmentVariables: sandbox.EnvironmentVariables);

            // Warmup phase (let JIT, UI rendering and initialization settle)
            context.Log($"[idle_empty] Warming up for {warmup:F1}s...");
            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            // Active measurement phase
            context.Log($"[idle_empty] Measuring idle steady state for {duration:F1}s...");
            monitor.Start();
            await Task.Delay(TimeSpan.FromSeconds(duration), context.CancellationToken);

            var summary = await monitor.StopAsync();
            monitor.Kill();

            return new ScenarioResult
            {
                ScenarioId = Id,
                ScenarioName = Name,
                TargetPath = context.TargetExecutable,
                Tier = context.Tier,
                DurationSeconds = duration,
                WarmupSeconds = warmup,
                Metrics = summary,
                Timeseries = monitor.Samples,
                Observations = new Dictionary<string, object>
                {
                    ["RuleCount"] = 0,
                    ["Sandbox"] = sandbox.SandboxPath
                }
            };
        }
    }

    /// <summary>
    /// Scenario: Resource scaling with N automation rules (1, 10, or 100 rules).
    /// </summary>
    public sealed class RulesScalingScenario : BaseScenario
    {
        private readonly int _ruleCount;

        public RulesScalingScenario(int ruleCount)
        {
            _ruleCount = ruleCount;
        }

        public override string Id => $"rules_scaling_{_ruleCount}";
        public override string Name => $"Rules Scaling ({_ruleCount} Rules)";
        public override string Description => $"Measures resource changes and scheduler overhead when {_ruleCount} automation rules are loaded.";
        public override BenchmarkModule Module => BenchmarkModule.Automation;

        public override async Task<ScenarioResult> RunAsync(BenchmarkContext context)
        {
            using var sandbox = new SonicRouteSandbox();
            sandbox.PopulateRules(_ruleCount);
            double duration = ResolveDuration(context);
            double warmup = context.Tier == BenchmarkTier.Quick ? 1.5 : 3.0;

            context.Log($"[{Id}] Starting target with {_ruleCount} rules...");

            using var monitor = ProcessMonitor.Launch(
                context.TargetExecutable,
                arguments: "",
                environmentVariables: sandbox.EnvironmentVariables);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            context.Log($"[{Id}] Measuring steady state for {duration:F1}s...");
            monitor.Start();
            await Task.Delay(TimeSpan.FromSeconds(duration), context.CancellationToken);

            var summary = await monitor.StopAsync();
            monitor.Kill();

            return new ScenarioResult
            {
                ScenarioId = Id,
                ScenarioName = Name,
                TargetPath = context.TargetExecutable,
                Tier = context.Tier,
                DurationSeconds = duration,
                WarmupSeconds = warmup,
                Metrics = summary,
                Timeseries = monitor.Samples,
                Observations = new Dictionary<string, object>
                {
                    ["RuleCount"] = _ruleCount,
                    ["Sandbox"] = sandbox.SandboxPath
                }
            };
        }
    }

    /// <summary>
    /// Scenario: Process detection overhead when target application does not exist for long periods.
    /// </summary>
    public sealed class TargetAbsentScenario : BaseScenario
    {
        public override string Id => "target_absent";
        public override string Name => "Target Application Absent Polling Overhead";
        public override string Description => "Measures background detection/WMI overhead when automation rules watch non-existent apps.";
        public override BenchmarkModule Module => BenchmarkModule.Automation;

        public override async Task<ScenarioResult> RunAsync(BenchmarkContext context)
        {
            using var sandbox = new SonicRouteSandbox();
            sandbox.PopulateRules(10, triggerAppName: "nonexistent_app_xyz_404.exe");
            double duration = ResolveDuration(context);
            double warmup = 2.0;

            context.Log($"[target_absent] Starting target with rules watching nonexistent processes...");

            using var monitor = ProcessMonitor.Launch(
                context.TargetExecutable,
                arguments: "",
                environmentVariables: sandbox.EnvironmentVariables);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            context.Log($"[target_absent] Measuring background watcher overhead for {duration:F1}s...");
            monitor.Start();
            await Task.Delay(TimeSpan.FromSeconds(duration), context.CancellationToken);

            var summary = await monitor.StopAsync();
            monitor.Kill();

            return new ScenarioResult
            {
                ScenarioId = Id,
                ScenarioName = Name,
                TargetPath = context.TargetExecutable,
                Tier = context.Tier,
                DurationSeconds = duration,
                WarmupSeconds = warmup,
                Metrics = summary,
                Timeseries = monitor.Samples,
                Observations = new Dictionary<string, object>
                {
                    ["MonitoredTarget"] = "nonexistent_app_xyz_404.exe",
                    ["RuleCount"] = 10
                }
            };
        }
    }

    /// <summary>
    /// Scenario: Process detection overhead under continuous app launch and exit churn.
    /// </summary>
    public sealed class AppLifecycleChurnScenario : BaseScenario
    {
        public override string Id => "app_lifecycle_churn";
        public override string Name => "App Lifecycle Churn (Process Start/Exit)";
        public override string Description => "Measures detection and event handling overhead when applications continuously launch and terminate.";
        public override BenchmarkModule Module => BenchmarkModule.Automation;

        public override async Task<ScenarioResult> RunAsync(BenchmarkContext context)
        {
            using var sandbox = new SonicRouteSandbox();
            sandbox.PopulateRules(5, triggerAppName: "cmd.exe", triggerType: 1 /* AppStart */);
            double duration = ResolveDuration(context);
            double warmup = 2.0;

            context.Log($"[app_lifecycle_churn] Starting target with process event rules...");

            using var monitor = ProcessMonitor.Launch(
                context.TargetExecutable,
                arguments: "",
                environmentVariables: sandbox.EnvironmentVariables);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            context.Log($"[app_lifecycle_churn] Spawning dummy churn processes during {duration:F1}s window...");
            monitor.Start();

            using var churnCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var churnTask = DummyProcessHelper.SimulateProcessChurnAsync(
                "cmd.exe",
                count: (int)(duration * 3),
                delayMs: 300,
                churnCts.Token);

            await Task.Delay(TimeSpan.FromSeconds(duration), context.CancellationToken);
            churnCts.Cancel();
            int actualChurnCount = 0;
            try { actualChurnCount = await churnTask; } catch { }

            var summary = await monitor.StopAsync();
            monitor.Kill();

            return new ScenarioResult
            {
                ScenarioId = Id,
                ScenarioName = Name,
                TargetPath = context.TargetExecutable,
                Tier = context.Tier,
                DurationSeconds = duration,
                WarmupSeconds = warmup,
                Metrics = summary,
                Timeseries = monitor.Samples,
                Observations = new Dictionary<string, object>
                {
                    ["ActualChurnCount"] = actualChurnCount,
                    ["ChurnRatePerSec"] = duration > 0 ? actualChurnCount / duration : 0,
                    ["RuleCount"] = 5
                }
            };
        }
    }

    /// <summary>
    /// Scenario: Rapid foreground application switching overhead.
    /// </summary>
    public sealed class FastForegroundSwitchScenario : BaseScenario
    {
        public override string Id => "fast_foreground_switch";
        public override string Name => "Fast Foreground App Switch";
        public override string Description => "Measures CPU and memory stability during rapid foreground window changes.";
        public override BenchmarkModule Module => BenchmarkModule.Automation;

        public override async Task<ScenarioResult> RunAsync(BenchmarkContext context)
        {
            using var sandbox = new SonicRouteSandbox();
            sandbox.PopulateRules(5, triggerAppName: "cmd.exe", triggerType: 2 /* AppSwitch */);
            double duration = ResolveDuration(context);
            double warmup = 2.0;

            context.Log($"[fast_foreground_switch] Starting target with AppSwitch rules...");

            using var monitor = ProcessMonitor.Launch(
                context.TargetExecutable,
                arguments: "",
                environmentVariables: sandbox.EnvironmentVariables);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            monitor.Start();

            using var switchCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var switchTask = DummyProcessHelper.SimulateForegroundSwitchesAsync(
                targetSwitches: (int)(duration * 2.5),
                switchDelayMs: 400,
                switchCts.Token);

            await Task.Delay(TimeSpan.FromSeconds(duration), context.CancellationToken);
            switchCts.Cancel();
            var stats = new ForegroundSwitchStats();
            try { stats = await switchTask; } catch { }

            var summary = await monitor.StopAsync();
            monitor.Kill();

            bool loadValid = stats.Requested > 0 && stats.Successful > 0;

            return new ScenarioResult
            {
                ScenarioId = Id,
                ScenarioName = Name,
                TargetPath = context.TargetExecutable,
                Tier = context.Tier,
                DurationSeconds = duration,
                WarmupSeconds = warmup,
                Metrics = summary,
                Timeseries = monitor.Samples,
                Success = loadValid,
                ErrorMessage = loadValid ? null : $"Foreground switches blocked by Windows session policy (Requested={stats.Requested}, Successful={stats.Successful}).",
                Observations = new Dictionary<string, object>
                {
                    ["RequestedSwitches"] = stats.Requested,
                    ["SuccessfulSwitches"] = stats.Successful,
                    ["FailedSwitches"] = stats.Failed,
                    ["SuccessRate"] = stats.SuccessRate,
                    ["TriggerType"] = "AppSwitch",
                    ["RuleCount"] = 5
                }
            };
        }
    }

    /// <summary>
    /// Scenario: Rules with long delay steps (e.g. 2500ms delay timer).
    /// </summary>
    public sealed class LongDelayStepScenario : BaseScenario
    {
        public override string Id => "long_delay_step";
        public override string Name => "Long Delay Step Execution";
        public override string Description => "Measures scheduler timer overhead and resource stability with active delayed execution steps.";
        public override BenchmarkModule Module => BenchmarkModule.Automation;

        public override async Task<ScenarioResult> RunAsync(BenchmarkContext context)
        {
            using var sandbox = new SonicRouteSandbox();
            sandbox.WriteConfig(new
            {
                Language = "en-US",
                AutoStart = false,
                AutoStartStore = false,
                StartMinimized = true,
                ExperimentalMode = true,
                ExecutionHistoryEnabled = true,
                ThemeMode = "light",
                Accent = "blue",
                Hotkeys = new Dictionary<string, string>()
            });

            const int delayMs = 2500;
            sandbox.PopulateRules(1, triggerAppName: "cmd.exe", triggerType: 1 /* AppStart */, delayMs: delayMs);
            double duration = Math.Max(ResolveDuration(context), 6.0);
            double warmup = 2.0;

            context.Log($"[long_delay_step] Starting target with delayed step rules ({delayMs}ms delay)...");

            using var monitor = ProcessMonitor.Launch(
                context.TargetExecutable,
                arguments: "",
                environmentVariables: sandbox.EnvironmentVariables);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            monitor.Start();

            // Trigger the rule via process start (keep process alive so snapshot watcher catches AppStart)
            bool triggered = false;
            Process? holder = null;
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 >nul")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                holder = Process.Start(psi);
                triggered = holder != null;
            }
            catch { }

            // Concurrently test subsequent background detection while delay step is in flight
            await Task.Delay(800, context.CancellationToken);
            try
            {
                var p2 = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false });
                p2?.WaitForExit(1500);
            }
            catch { }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1.0, duration - 1.8)), context.CancellationToken);

            try { holder?.Kill(); holder?.Dispose(); } catch { }

            var summary = await monitor.StopAsync();
            monitor.Kill();

            // Verify execution history recorded in sandbox
            long observedDelay = 0;
            bool historyFound = false;
            try
            {
                string histDir = Path.Combine(sandbox.SandboxPath, "ExecutionHistory");
                if (Directory.Exists(histDir))
                {
                    var files = Directory.GetFiles(histDir, "*.json", SearchOption.AllDirectories);
                    if (files.Length > 0)
                    {
                        historyFound = true;
                        foreach (var f in files)
                        {
                            string text = File.ReadAllText(f);
                            if (text.Contains("Benchmark Rule"))
                            {
                                // Parse ElapsedMs
                                using var doc = System.Text.Json.JsonDocument.Parse(text);
                                if (doc.RootElement.TryGetProperty("ElapsedMs", out var el))
                                {
                                    observedDelay = el.GetInt64();
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return new ScenarioResult
            {
                ScenarioId = Id,
                ScenarioName = Name,
                TargetPath = context.TargetExecutable,
                Tier = context.Tier,
                DurationSeconds = duration,
                WarmupSeconds = warmup,
                Metrics = summary,
                Timeseries = monitor.Samples,
                Observations = new Dictionary<string, object>
                {
                    ["DelayStepMs"] = delayMs,
                    ["DelayedStepTriggered"] = triggered,
                    ["HistoryRecorded"] = historyFound,
                    ["ObservedDelayMs"] = observedDelay,
                    ["DelayRealismVerified"] = (observedDelay >= delayMs - 200)
                }
            };
        }
    }

    /// <summary>
    /// Scenario: Rapid rule save, modify, and hotkey reload performance.
    /// </summary>
    public sealed class RuleSaveHotkeysScenario : BaseScenario
    {
        public override string Id => "rule_save_hotkeys";
        public override string Name => "Rule Save & Hotkey Re-registration";
        public override string Description => "Measures disk I/O, FileSystemWatcher reaction, and hotkey unregister/register CPU time.";
        public override BenchmarkModule Module => BenchmarkModule.Hotkey;

        public override async Task<ScenarioResult> RunAsync(BenchmarkContext context)
        {
            using var sandbox = new SonicRouteSandbox();
            // Use controlled non-standard hotkeys (Ctrl+Alt+Shift+F9..F12) to never collide with user shortcuts
            sandbox.PopulateRules(4, triggerType: 0 /* Hotkey */, hotkey: "Ctrl+Alt+Shift+F9");
            double duration = ResolveDuration(context);
            double warmup = 2.0;

            context.Log($"[rule_save_hotkeys] Starting target...");

            using var monitor = ProcessMonitor.Launch(
                context.TargetExecutable,
                arguments: "",
                environmentVariables: sandbox.EnvironmentVariables);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            monitor.Start();

            // Perform repeated rule modifications in background
            int modifications = 0;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < duration && !context.CancellationToken.IsCancellationRequested)
            {
                int keyIdx = 9 + (modifications % 4);
                sandbox.PopulateRules(4, triggerType: 0 /* Hotkey */, hotkey: $"Ctrl+Alt+Shift+F{keyIdx}");
                modifications++;
                await Task.Delay(500, context.CancellationToken);
            }

            var summary = await monitor.StopAsync();
            monitor.Kill();

            return new ScenarioResult
            {
                ScenarioId = Id,
                ScenarioName = Name,
                TargetPath = context.TargetExecutable,
                Tier = context.Tier,
                DurationSeconds = duration,
                WarmupSeconds = warmup,
                Metrics = summary,
                Timeseries = monitor.Samples,
                Observations = new Dictionary<string, object>
                {
                    ["RuleModificationCycles"] = modifications,
                    ["TriggerType"] = "Hotkey",
                    ["ControlledKeys"] = "Ctrl+Alt+Shift+F9..F12"
                }
            };
        }
    }

    /// <summary>
    /// Scenario: Repeated adjustment, cancellation, and rescheduling of timed schedule rules.
    /// Exercises AutoRuleScheduler queue efficiency.
    /// </summary>
    public sealed class ScheduleRuleChurnScenario : BaseScenario
    {
        public override string Id => "schedule_rule_churn";
        public override string Name => "Schedule Rules Churn & Rescheduling";
        public override string Description => "Measures AutoRuleScheduler timer queue management, cancellation, and rescheduling efficiency.";
        public override BenchmarkModule Module => BenchmarkModule.Automation;

        public override async Task<ScenarioResult> RunAsync(BenchmarkContext context)
        {
            using var sandbox = new SonicRouteSandbox();
            sandbox.PopulateRules(5, triggerType: 4 /* Schedule */);
            double duration = ResolveDuration(context);
            double warmup = 2.0;

            context.Log($"[schedule_rule_churn] Starting target with schedule rules...");

            using var monitor = ProcessMonitor.Launch(
                context.TargetExecutable,
                arguments: "",
                environmentVariables: sandbox.EnvironmentVariables);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            monitor.Start();

            int updates = 0;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < duration && !context.CancellationToken.IsCancellationRequested)
            {
                // Update schedule rules with new times
                sandbox.PopulateRules(5, triggerType: 4 /* Schedule */);
                updates++;
                await Task.Delay(600, context.CancellationToken);
            }

            var summary = await monitor.StopAsync();
            monitor.Kill();

            return new ScenarioResult
            {
                ScenarioId = Id,
                ScenarioName = Name,
                TargetPath = context.TargetExecutable,
                Tier = context.Tier,
                DurationSeconds = duration,
                WarmupSeconds = warmup,
                Metrics = summary,
                Timeseries = monitor.Samples,
                Observations = new Dictionary<string, object>
                {
                    ["ScheduleUpdatesCount"] = updates,
                    ["TriggerType"] = "Schedule"
                }
            };
        }
    }

    /// <summary>
    /// Scenario: Program window show, hide, and tray resident transitions.
    /// </summary>
    public sealed class WindowTrayTransitionsScenario : BaseScenario
    {
        public override string Id => "window_tray_transitions";
        public override string Name => "Window & Tray Transitions";
        public override string Description => "Measures resource delta between minimized tray state and opened UI windows.";
        public override BenchmarkModule Module => BenchmarkModule.Ui;

        public override async Task<ScenarioResult> RunAsync(BenchmarkContext context)
        {
            using var sandbox = new SonicRouteSandbox();
            double duration = ResolveDuration(context);
            double warmup = 2.0;

            context.Log($"[window_tray_transitions] Starting target in tray...");

            using var monitor = ProcessMonitor.Launch(
                context.TargetExecutable,
                arguments: "",
                environmentVariables: sandbox.EnvironmentVariables);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            monitor.Start();
            await Task.Delay(TimeSpan.FromSeconds(duration / 2.0), context.CancellationToken);

            // Trigger main window
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = context.TargetExecutable,
                    Arguments = "--main",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var kvp in sandbox.EnvironmentVariables) psi.Environment[kvp.Key] = kvp.Value;
                using var trigger = Process.Start(psi);
                trigger?.WaitForExit(3000);
            }
            catch { }

            await Task.Delay(TimeSpan.FromSeconds(duration / 2.0), context.CancellationToken);

            var summary = await monitor.StopAsync();
            monitor.Kill();

            return new ScenarioResult
            {
                ScenarioId = Id,
                ScenarioName = Name,
                TargetPath = context.TargetExecutable,
                Tier = context.Tier,
                DurationSeconds = duration,
                WarmupSeconds = warmup,
                Metrics = summary,
                Timeseries = monitor.Samples,
                Observations = new Dictionary<string, object>
                {
                    ["TransitionAction"] = "TrayToMain"
                }
            };
        }
    }

    /// <summary>
    /// Scenario: Endurance / long-running stability to measure memory, handle, and thread trends.
    /// </summary>
    public sealed class EnduranceLeakTrendScenario : BaseScenario
    {
        public override string Id => "endurance_leak_trend";
        public override string Name => "Endurance & Leak Trend Analysis";
        public override string Description => "Extended run analyzing rate-of-change (slope) in WorkingSet, PrivateBytes, and Handles to detect leaks.";
        public override BenchmarkModule Module => BenchmarkModule.General;

        public override double GetDurationSeconds(BenchmarkTier tier) => tier switch
        {
            BenchmarkTier.Quick => 8.0,
            BenchmarkTier.Standard => 25.0,
            BenchmarkTier.Endurance => 60.0,
            _ => 25.0
        };

        public override async Task<ScenarioResult> RunAsync(BenchmarkContext context)
        {
            using var sandbox = new SonicRouteSandbox();
            sandbox.PopulateRules(10);
            double duration = ResolveDuration(context);
            double warmup = 3.0;

            context.Log($"[endurance_leak_trend] Starting endurance test ({duration:F0}s)...");

            using var monitor = ProcessMonitor.Launch(
                context.TargetExecutable,
                arguments: "",
                environmentVariables: sandbox.EnvironmentVariables);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            monitor.Start();
            await Task.Delay(TimeSpan.FromSeconds(duration), context.CancellationToken);

            var summary = await monitor.StopAsync();
            monitor.Kill();

            return new ScenarioResult
            {
                ScenarioId = Id,
                ScenarioName = Name,
                TargetPath = context.TargetExecutable,
                Tier = context.Tier,
                DurationSeconds = duration,
                WarmupSeconds = warmup,
                Metrics = summary,
                Timeseries = monitor.Samples,
                Observations = new Dictionary<string, object>
                {
                    ["PrivateBytesTrendPerSec"] = summary.PrivateBytes.TrendPerSecond,
                    ["HandleTrendPerSec"] = summary.HandleCount.TrendPerSecond,
                    ["ThreadTrendPerSec"] = summary.ThreadCount.TrendPerSecond
                }
            };
        }
    }

    /// <summary>
    /// Generic Process Scenario: Can monitor ANY arbitrary Windows application.
    /// </summary>
    public sealed class GenericProcessScenario : BaseScenario
    {
        private readonly string _args;

        public GenericProcessScenario(string args = "")
        {
            _args = args;
        }

        public override string Id => "generic_process";
        public override string Name => "Generic Process Benchmark";
        public override string Description => "Measures CPU, Memory, Threads, Handles, and I/O for any generic Windows executable.";
        public override BenchmarkModule Module => BenchmarkModule.General;

        public override async Task<ScenarioResult> RunAsync(BenchmarkContext context)
        {
            double duration = ResolveDuration(context);
            double warmup = 2.0;

            context.Log($"[generic_process] Starting {context.TargetExecutable} {_args}...");

            using var monitor = ProcessMonitor.Launch(
                context.TargetExecutable,
                arguments: _args,
                environmentVariables: context.EnvironmentVariables);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            monitor.Start();
            await Task.Delay(TimeSpan.FromSeconds(duration), context.CancellationToken);

            var summary = await monitor.StopAsync();
            monitor.Kill();

            return new ScenarioResult
            {
                ScenarioId = Id,
                ScenarioName = Name,
                TargetPath = context.TargetExecutable,
                Tier = context.Tier,
                DurationSeconds = duration,
                WarmupSeconds = warmup,
                Metrics = summary,
                Timeseries = monitor.Samples,
                Observations = new Dictionary<string, object>()
            };
        }
    }
}
