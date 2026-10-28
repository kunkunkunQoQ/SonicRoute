namespace SonicRoute.BenchmarkLab.Core;

public static class BaselinePolicy
{
    // Small future skew is tolerated; an unset timestamp is never considered fresh.
    public static bool IsFresh(DateTime savedUtc, DateTime nowUtc, double maxAgeDays) => savedUtc != default
        && double.IsFinite(maxAgeDays) && maxAgeDays > 0
        && savedUtc <= nowUtc.AddMinutes(5) && nowUtc - savedUtc <= TimeSpan.FromDays(maxAgeDays);
}

public sealed class DecisionReadiness
{
    public bool RecommendedProtocolMet { get; set; }
    public int MinimumRounds { get; set; }
    public double ShortestWindowSeconds { get; set; }
    public List<string> Reasons { get; set; } = new();

    public static DecisionReadiness Assess(Comparison.SuiteComparisonResult suite)
    {
        var result = new DecisionReadiness();
        var rounds = suite.BaselineRounds.Values.Concat(suite.CandidateRounds.Values).ToArray();
        result.MinimumRounds = rounds.Length > 0 ? rounds.Min(r => r.Count) : 0;
        var summaries = rounds.SelectMany(r => r).ToArray();
        result.ShortestWindowSeconds = summaries.Length > 0 ? summaries.Min(s => s.DurationSeconds) : 0;
        if (suite.Protocol == null) result.Reasons.Add("Missing measurement protocol.");
        if (suite.IsAaControl) result.Reasons.Add("A/A describes repeatability, not a product version decision.");
        if (suite.Scenarios.Any(s => !suite.BaselineRounds.ContainsKey(s.ScenarioId) || !suite.CandidateRounds.ContainsKey(s.ScenarioId))) result.Reasons.Add("Missing scenario rounds on one side.");
        if (result.MinimumRounds < 5) result.Reasons.Add("Fewer than five predeclared rounds per side; exploratory or smoke evidence.");
        if (result.ShortestWindowSeconds < 29) result.Reasons.Add("Less than a 30-second requested window (one-second scheduling tolerance).");
        if (suite.Protocol?.Scenarios.Values.Any(s => s.WarmupSeconds < 5) != false) result.Reasons.Add("Warmup below five seconds or missing.");
        if (!suite.SourceFilesUnchanged || suite.Runs.Any(r => !r.Result.Success)) result.Reasons.Add("Source identity or workload failed.");
        if (summaries.Any(s => !s.MeasurementValid || s.SamplingQuality?.SuitableForComparison != true)) result.Reasons.Add("Missing/unsuitable per-round measurement quality.");
        if (suite.OverallVerdict == Comparison.OverallVerdict.Inconclusive) result.Reasons.Add("Statistical result or historical comparability is inconclusive.");
        result.RecommendedProtocolMet = result.Reasons.Count == 0;
        return result;
    }
}
