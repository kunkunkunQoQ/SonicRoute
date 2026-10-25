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
                environmentVariables: sandbox.EnvironmentVariables,
                sampleIntervalMs: context.SampleIntervalMs);

            // Warmup phase (let JIT, UI rendering and initialization settle)
            context.Log($"[idle_empty] Warming up for {warmup:F1}s...");
            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            // Active measurement phase
            context.Log($"[idle_empty] Measuring idle steady state for {duration:F1}s...");
            await sandbox.VerifyReadyAsync(monitor, context.CancellationToken);
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
                    ["SandboxEvidence"] = sandbox.CaptureEvidence(),
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
                environmentVariables: sandbox.EnvironmentVariables,
                sampleIntervalMs: context.SampleIntervalMs);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            context.Log($"[{Id}] Measuring steady state for {duration:F1}s...");
            await sandbox.VerifyReadyAsync(monitor, context.CancellationToken);
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
                    ["SandboxEvidence"] = sandbox.CaptureEvidence(),
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
                environmentVariables: sandbox.EnvironmentVariables,
                sampleIntervalMs: context.SampleIntervalMs);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            context.Log($"[target_absent] Measuring background watcher overhead for {duration:F1}s...");
            await sandbox.VerifyReadyAsync(monitor, context.CancellationToken);
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
                    ["SandboxEvidence"] = sandbox.CaptureEvidence(),
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
            string probe = sandbox.CreateProbeExecutable();
            sandbox.PopulateRules(5, triggerAppName: Path.GetFileNameWithoutExtension(probe), triggerType: 1 /* AppStart */);
            double duration = ResolveDuration(context);
            double warmup = 2.0;

            context.Log($"[app_lifecycle_churn] Starting target with process event rules...");

            using var monitor = ProcessMonitor.Launch(
                context.TargetExecutable,
                arguments: "",
                environmentVariables: sandbox.EnvironmentVariables,
                sampleIntervalMs: context.SampleIntervalMs);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            context.Log($"[app_lifecycle_churn] Spawning dummy churn processes during {duration:F1}s window...");
            await sandbox.VerifyReadyAsync(monitor, context.CancellationToken);
            monitor.Start();

            using var churnCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var churnTask = DummyProcessHelper.SimulateProcessChurnAsync(
                probe,
                count: (int)(duration * 3),
                delayMs: 300,
                churnCts.Token);

            await Task.Delay(TimeSpan.FromSeconds(duration), context.CancellationToken);
            churnCts.Cancel();
            int actualChurnCount = 0;
            try { actualChurnCount = await churnTask; } catch (OperationCanceledException) { }
            int executedRules = sandbox.ReadHistory().Length;

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
                Success = actualChurnCount > 0 && executedRules > 0,
                ErrorMessage = actualChurnCount > 0 && executedRules > 0 ? null : "Owned churn or actual rule execution was not confirmed.",
                Observations = new Dictionary<string, object>
                {
                    ["SandboxEvidence"] = sandbox.CaptureEvidence(),
                    ["ActualChurnCount"] = actualChurnCount,
                    ["ConfirmedRuleExecutions"] = executedRules,
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
                environmentVariables: sandbox.EnvironmentVariables,
                sampleIntervalMs: context.SampleIntervalMs);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            await sandbox.VerifyReadyAsync(monitor, context.CancellationToken);
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

            bool loadValid = stats.Requested > 0 && stats.Successful > 0 && sandbox.ReadHistory().Length > 0;

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
                    ["SandboxEvidence"] = sandbox.CaptureEvidence(),
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
                TrayWheelPaused = true,
                ThemeMode = "light",
                Accent = "blue",
                Hotkeys = new Dictionary<string, string>()
            });

            const int delayMs = 2500;
            string probe = sandbox.CreateProbeExecutable();
            sandbox.PopulateRules(1, triggerAppName: Path.GetFileNameWithoutExtension(probe), triggerType: 1 /* AppStart */, delayMs: delayMs);
            double duration = Math.Max(ResolveDuration(context), 6.0);
            double warmup = 2.0;

            context.Log($"[long_delay_step] Starting target with delayed step rules ({delayMs}ms delay)...");

            using var monitor = ProcessMonitor.Launch(
                context.TargetExecutable,
                arguments: "",
                environmentVariables: sandbox.EnvironmentVariables,
                sampleIntervalMs: context.SampleIntervalMs);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            await sandbox.VerifyReadyAsync(monitor, context.CancellationToken);
            monitor.Start();

            using var holder = ProcessMonitor.Launch(probe, "__worker idle 2000", measureStartupLatency: false);
            bool triggered = !holder.Process.HasExited;
            await Task.Delay(TimeSpan.FromSeconds(duration), context.CancellationToken);
            holder.Kill();

            var summary = await monitor.StopAsync();
            monitor.Kill();

            var history = sandbox.ReadHistory();
            bool historyFound = history.Length > 0;
            long observedDelay = history.Select(e => e.TryGetProperty("ElapsedMs", out var elapsed) ? elapsed.GetInt64() : 0).DefaultIfEmpty().Max();

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
                Success = historyFound && observedDelay >= delayMs - 200,
                ErrorMessage = historyFound && observedDelay >= delayMs - 200 ? null : "Delayed execution was not confirmed in actual JSONL history.",
                Observations = new Dictionary<string, object>
                {
                    ["SandboxEvidence"] = sandbox.CaptureEvidence(),
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
            // Distinct controlled bindings avoid self-conflicts; other applications may still own these keys.
            sandbox.PopulateRules(4, triggerType: 0 /* Hotkey */);
            double duration = ResolveDuration(context);
            double warmup = 2.0;

            context.Log($"[rule_save_hotkeys] Starting target...");

            using var monitor = ProcessMonitor.Launch(
                context.TargetExecutable,
                arguments: "",
                environmentVariables: sandbox.EnvironmentVariables,
                sampleIntervalMs: context.SampleIntervalMs);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            await sandbox.VerifyReadyAsync(monitor, context.CancellationToken);
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
            bool reloadVerified = await sandbox.VerifyReloadAsync(monitor, context.CancellationToken);
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
                Success = reloadVerified,
                ErrorMessage = reloadVerified ? null : "Latest rule reload was not confirmed after measurement.",
                Observations = new Dictionary<string, object>
                {
                    ["SandboxEvidence"] = sandbox.CaptureEvidence(),
                    ["RuleReloadVerified"] = reloadVerified,
                    ["PhysicalHotkeyOrTimerFiringVerified"] = false,
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
                environmentVariables: sandbox.EnvironmentVariables,
                sampleIntervalMs: context.SampleIntervalMs);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            await sandbox.VerifyReadyAsync(monitor, context.CancellationToken);
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
            bool reloadVerified = await sandbox.VerifyReloadAsync(monitor, context.CancellationToken);
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
                Success = reloadVerified,
                ErrorMessage = reloadVerified ? null : "Latest rule reload was not confirmed after measurement.",
                Observations = new Dictionary<string, object>
                {
                    ["SandboxEvidence"] = sandbox.CaptureEvidence(),
                    ["RuleReloadVerified"] = reloadVerified,
                    ["PhysicalHotkeyOrTimerFiringVerified"] = false,
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
                environmentVariables: sandbox.EnvironmentVariables,
                sampleIntervalMs: context.SampleIntervalMs);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            await sandbox.VerifyReadyAsync(monitor, context.CancellationToken);
            monitor.Start();
            await Task.Delay(TimeSpan.FromSeconds(duration / 2.0), context.CancellationToken);

            double trayEnd = monitor.Samples.Last().ElapsedMs;
            var response = Stopwatch.StartNew();
            bool visible = false;
            Process? trigger = null;
            using var triggerJob = new ProcessJobObject();
            // Trigger the same isolated instance, then verify its actual window.
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
                trigger = Process.Start(psi);
                if (trigger != null && !triggerJob.AssignProcess(trigger) && !trigger.HasExited)
                    throw new InvalidOperationException("UI trigger could not be assigned to its cleanup Job.");
                visible = await WindowProbe.WaitForMainWindowAsync(monitor.Process, context.CancellationToken);
            }
            finally
            {
                if (trigger != null)
                {
                    try { if (!trigger.HasExited) { trigger.Kill(true); trigger.WaitForExit(1000); } } finally { trigger.Dispose(); }
                }
            }
            response.Stop();
            if (visible) monitor.ResponseLatencyMs = response.Elapsed.TotalMilliseconds;
            double mainStart = monitor.Samples.Last().ElapsedMs;

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
                Success = visible,
                ErrorMessage = visible ? null : "Main window was not observed; launching a --main trigger is not proof that UI opened.",
                Observations = new Dictionary<string, object>
                {
                    ["SandboxEvidence"] = sandbox.CaptureEvidence(),
                    ["TransitionAction"] = "TrayToMain",
                    ["VisibleNativeWindowConfirmed"] = visible,
                    ["RenderedFrameOrDataReadyMeasured"] = false,
                    ["WindowClosedAndReleasedMeasured"] = false,
                    ["TrayPhase"] = Phase(monitor.Samples.Where(s => s.ElapsedMs <= trayEnd).ToArray()),
                    ["MainPhase"] = Phase(monitor.Samples.Where(s => s.ElapsedMs >= mainStart).ToArray())
                }
            };
        }

        private static ProcessMetricSummary Phase(IReadOnlyList<ProcessSample> samples) => ProcessMetricSummary.Aggregate(samples,
            samples.Count > 1 ? (samples[^1].ElapsedMs - samples[0].ElapsedMs) / 1000 : 0);
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
                environmentVariables: sandbox.EnvironmentVariables,
                sampleIntervalMs: context.SampleIntervalMs);

            await Task.Delay(TimeSpan.FromSeconds(warmup), context.CancellationToken);

            await sandbox.VerifyReadyAsync(monitor, context.CancellationToken);
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
                    ["SandboxEvidence"] = sandbox.CaptureEvidence(),
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
                environmentVariables: context.EnvironmentVariables,
                sampleIntervalMs: context.SampleIntervalMs);

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
