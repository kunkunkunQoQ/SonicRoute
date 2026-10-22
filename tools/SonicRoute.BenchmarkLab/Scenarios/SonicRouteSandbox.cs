using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SonicRoute.BenchmarkLab.Scenarios
{
    public sealed class SonicRouteSandbox : IDisposable
    {
        public string SandboxPath { get; }
        public string InstanceId { get; }
        public Dictionary<string, string> EnvironmentVariables { get; } = new();

        private bool _disposed;

        public SonicRouteSandbox(string? customBasePath = null)
        {
            InstanceId = "Bench_" + Guid.NewGuid().ToString("N")[..8];
            string root = customBasePath ?? Path.Combine(Path.GetTempPath(), "SonicRouteBenchmark");
            Directory.CreateDirectory(root);

            SandboxPath = Path.Combine(root, InstanceId);
            Directory.CreateDirectory(SandboxPath);
            Directory.CreateDirectory(Path.Combine(SandboxPath, "Automation"));

            EnvironmentVariables["SONICROUTE_DATA_DIRECTORY"] = SandboxPath;
            EnvironmentVariables["SONICROUTE_INSTANCE_ID"] = InstanceId;

            // Safe baseline configuration:
            // Disables autostart, persistent OSD, update checks, etc.
            WriteConfig(new
            {
                Language = "en-US",
                AutoStart = false,
                AutoStartStore = false,
                StartMinimized = true,
                StartPanelOnStart = false,
                AutoCheckUpdates = false,
                ThemeMode = "light",
                Accent = "blue",
                ExperimentalUnlocked = true,
                ExperimentalMode = false,
                BackgroundPollingFallbackEnabled = false,
                Hotkeys = new Dictionary<string, string>()
            });
        }

        public void WriteConfig(object configObject)
        {
            string configPath = Path.Combine(SandboxPath, "config.json");
            string json = JsonSerializer.Serialize(configObject, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
        }

        public void PopulateRules(
            int count,
            string triggerAppName = "mock_target_app.exe",
            int triggerType = 1 /* AppStart */,
            int delayMs = 0,
            string? hotkey = null)
        {
            string automationDir = Path.Combine(SandboxPath, "Automation");
            Directory.CreateDirectory(automationDir);

            // Clean previous rules
            foreach (var file in Directory.GetFiles(automationDir, "*.json"))
            {
                try { File.Delete(file); } catch { }
            }

            for (int i = 1; i <= count; i++)
            {
                string ruleHotkey = hotkey ?? (triggerType == 0 ? $"Ctrl+Alt+Shift+F{((i % 12) + 1)}" : "");
                var rule = new
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = $"Benchmark Rule {i:D4}",
                    Enabled = true,
                    Trigger = triggerType, // 0: Hotkey, 1: AppStart, 2: AppSwitch, 3: AppExit
                    TriggerApp = triggerAppName,
                    Hotkey = ruleHotkey,
                    // Safe action: SetAppVolume on a mock isolated app name.
                    // This exercises the rule parser, step execution engine, and session lookup,
                    // but NEVER modifies system master volume or physical audio endpoints!
                    Action = 4, // SetAppVolume
                    TargetApp = "mock_sandbox_app",
                    Volume = 50,
                    DelayMs = delayMs,
                    Actions = new[]
                    {
                        new {
                            Action = 4, // SetAppVolume
                            TargetApp = "mock_sandbox_app",
                            Volume = 50,
                            DelayMs = delayMs
                        }
                    }
                };

                string fileName = $"Benchmark_Rule_{i:D4}.json";
                File.WriteAllText(Path.Combine(automationDir, fileName),
                    JsonSerializer.Serialize(rule, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                try
                {
                    if (Directory.Exists(SandboxPath))
                    {
                        Directory.Delete(SandboxPath, recursive: true);
                    }
                }
                catch
                {
                    // Ignore sandbox cleanup errors (temp files)
                }
            }
        }
    }
}
