using SonicRoute.BenchmarkLab.Core;

namespace SonicRoute.BenchmarkLab.Scenarios;

public sealed class BenchmarkPlan
{
    public string ToolVersion { get; set; } = "1.2";
    public string Mode { get; set; } = "";
    public int Rounds { get; set; }
    public int TargetLaunchCount { get; set; }
    public double MeasurementSeconds { get; set; }
    public double WarmupSeconds { get; set; }
    public double CooldownSeconds { get; set; }
    public double PlannedSecondsExcludingStartupReadinessAndWorkloadOverruns => MeasurementSeconds + WarmupSeconds + CooldownSeconds;
    public bool DesktopInteractionRequired { get; set; }
    public bool DesktopInteractionAllowed { get; set; }
    public MeasurementProtocol Protocol { get; set; } = new();
    public List<PlannedRun> Runs { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    public static bool RequiresDesktop(IBenchmarkScenario scenario) => scenario.Module is BenchmarkModule.Ui or BenchmarkModule.Hotkey
        || scenario.Id == "fast_foreground_switch";

    public static BenchmarkPlan Create(IReadOnlyList<IBenchmarkScenario> scenarios, BenchmarkTier tier, int rounds,
        double? duration, int interval, bool compare, bool historical, bool desktopAllowed, double? warmup = null)
    {
        var protocol = MeasurementProtocol.Create(scenarios, tier, duration, interval, warmup);
        var result = new BenchmarkPlan { Mode = !compare ? "run" : historical ? "historical" : "alternating",
            Rounds = rounds, Protocol = protocol, DesktopInteractionAllowed = desktopAllowed,
            DesktopInteractionRequired = scenarios.Any(RequiresDesktop) };
        for (int round = 1; round <= rounds; round++)
        {
            foreach (var scenario in scenarios)
            {
                string[] sides = !compare || historical ? new[] { "Candidate" }
                    : round % 2 == 1 ? new[] { "Baseline", "Candidate" } : new[] { "Candidate", "Baseline" };
                foreach (string side in sides)
                    result.Runs.Add(new PlannedRun { Order = result.Runs.Count + 1, Round = round, Side = side,
                        ScenarioId = scenario.Id, DurationSeconds = protocol.Scenarios[scenario.Id].DurationSeconds,
                        WarmupSeconds = protocol.Scenarios[scenario.Id].WarmupSeconds });
            }
        }
        result.TargetLaunchCount = result.Runs.Count;
        result.MeasurementSeconds = result.Runs.Sum(r => r.DurationSeconds);
        result.WarmupSeconds = result.Runs.Sum(r => r.WarmupSeconds);
        result.CooldownSeconds = !compare || historical ? result.Runs.Count
            : result.Runs.Count * 0.8 + scenarios.Count * rounds * 1.5 + (rounds - 1) * 2;
        if (rounds < 3) result.Warnings.Add("Fewer than three rounds: smoke evidence only; repeatability is not established.");
        else if (rounds < 5) result.Warnings.Add("Three/four rounds are exploratory; five or more predeclared rounds are recommended for a final decision.");
        if (protocol.Scenarios.Values.Any(s => s.WarmupSeconds < 2)) result.Warnings.Add("Warmup below two seconds may retain startup/JIT work; inspect readiness and do not describe it as cold-start latency.");
        if (protocol.Scenarios.Values.Any(s => s.DurationSeconds < 10)) result.Warnings.Add("Short measurement windows cannot establish low idle CPU overhead; use longer windows and A/A controls.");
        if (protocol.Scenarios.Values.Any(s => s.DurationSeconds * 1000 < interval * 2)) result.Warnings.Add("Requested duration may yield fewer than three samples; reduce interval or lengthen the window.");
        if (result.DesktopInteractionRequired && !desktopAllowed) result.Warnings.Add("Execution requires --allow-desktop-interaction for the selected scenarios; preview starts no processes.");
        if (scenarios.Any(s => s.Id == "generic_process")) result.Warnings.Add("Generic targets follow their own startup behavior and are not isolated from user data or audio.");
        result.Warnings.Add("Time estimate excludes process startup/readiness, cleanup and workload overruns. UI scenarios may launch an additional activation helper. No target is launched by plan.");
        return result;
    }
}

public sealed class PlannedRun
{
    public int Order { get; set; }
    public int Round { get; set; }
    public string Side { get; set; } = "";
    public string ScenarioId { get; set; } = "";
    public double DurationSeconds { get; set; }
    public double WarmupSeconds { get; set; }
}
