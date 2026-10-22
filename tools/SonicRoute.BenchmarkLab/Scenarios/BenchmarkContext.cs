using System;
using System.Collections.Generic;
using System.Threading;
using SonicRoute.BenchmarkLab.Core;

namespace SonicRoute.BenchmarkLab.Scenarios
{
    public enum BenchmarkTier
    {
        Quick,
        Standard,
        Endurance
    }

    public enum BenchmarkModule
    {
        All,
        General,
        Automation,
        Hotkey,
        Ui,
        Audio
    }

    public sealed class BenchmarkContext
    {
        public string TargetExecutable { get; set; } = string.Empty;
        public BenchmarkTier Tier { get; set; } = BenchmarkTier.Standard;
        public double? CustomDurationSeconds { get; set; }
        public CancellationToken CancellationToken { get; set; } = CancellationToken.None;
        public string SandboxDirectory { get; set; } = string.Empty;
        public string InstanceId { get; set; } = string.Empty;
        public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
        public Action<string>? LogMessage { get; set; }

        public void Log(string message) => LogMessage?.Invoke(message);
    }

    public sealed class ScenarioResult
    {
        public string ScenarioId { get; set; } = string.Empty;
        public string ScenarioName { get; set; } = string.Empty;
        public string TargetPath { get; set; } = string.Empty;
        public string TargetVersion { get; set; } = string.Empty;
        public BenchmarkTier Tier { get; set; }
        public double DurationSeconds { get; set; }
        public double WarmupSeconds { get; set; }
        public ProcessMetricSummary Metrics { get; set; } = new();
        public IReadOnlyList<ProcessSample> Timeseries { get; set; } = Array.Empty<ProcessSample>();
        public Dictionary<string, object> Observations { get; set; } = new();
        public bool Success { get; set; } = true;
        public string? ErrorMessage { get; set; }
    }

    public interface IBenchmarkScenario
    {
        string Id { get; }
        string Name { get; }
        string Description { get; }
        BenchmarkModule Module { get; }

        double GetDurationSeconds(BenchmarkTier tier);

        System.Threading.Tasks.Task<ScenarioResult> RunAsync(BenchmarkContext context);
    }
}
