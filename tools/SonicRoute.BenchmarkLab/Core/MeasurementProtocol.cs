using SonicRoute.BenchmarkLab.Scenarios;

namespace SonicRoute.BenchmarkLab.Core;

// Version the measurement semantics independently from the executable version.
public sealed class MeasurementProtocol
{
    public const string CurrentRevision = "benchmarklab-1.2-timeweighted-v1";
    public string Revision { get; set; } = CurrentRevision;
    public string Tier { get; set; } = "";
    public int SampleIntervalMs { get; set; }
    public Dictionary<string, ScenarioProtocol> Scenarios { get; set; } = new();

    public static MeasurementProtocol Create(IReadOnlyList<IBenchmarkScenario> scenarios, BenchmarkTier tier, double? duration, int interval, double? warmup = null) => new()
    {
        Tier = tier.ToString(), SampleIntervalMs = interval,
        Scenarios = scenarios.ToDictionary(s => s.Id, s => new ScenarioProtocol
        {
            DurationSeconds = duration ?? s.GetDurationSeconds(tier),
            WarmupSeconds = warmup ?? s.GetWarmupSeconds(tier),
            Arguments = s is GenericProcessScenario generic ? generic.Arguments : "",
            Isolated = s.Id != "generic_process"
        })
    };

    // A selected subset can be compared with the corresponding historical subset.
    public bool IsCompatibleWith(MeasurementProtocol? historical) => historical != null
        && Revision == historical.Revision && Tier == historical.Tier && SampleIntervalMs == historical.SampleIntervalMs
        && Scenarios.All(entry => historical.Scenarios.TryGetValue(entry.Key, out var prior)
            && entry.Value.DurationSeconds == prior.DurationSeconds && entry.Value.WarmupSeconds == prior.WarmupSeconds
            && entry.Value.Arguments == prior.Arguments && entry.Value.Isolated == prior.Isolated);
}

public sealed class ScenarioProtocol
{
    public double DurationSeconds { get; set; }
    public double WarmupSeconds { get; set; }
    public string Arguments { get; set; } = "";
    public bool Isolated { get; set; }
}
