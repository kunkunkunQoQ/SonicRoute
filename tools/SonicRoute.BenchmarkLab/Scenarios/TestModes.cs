namespace SonicRoute.BenchmarkLab.Scenarios;

public static class TestModes
{
    public static readonly IReadOnlyDictionary<string, string[]> Features = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["idle"] = new[] { "idle_empty" },
        ["rules"] = new[] { "rules_scaling_1", "rules_scaling_10", "rules_scaling_100", "target_absent" },
        ["process"] = new[] { "app_lifecycle_churn" },
        ["foreground"] = new[] { "fast_foreground_switch" },
        ["delay"] = new[] { "long_delay_step" },
        ["hotkeys"] = new[] { "rule_save_hotkeys" },
        ["schedule"] = new[] { "schedule_rule_churn" },
        ["window"] = new[] { "window_tray_transitions" },
        ["endurance"] = new[] { "endurance_leak_trend" }
    };

    public static void Apply(Dictionary<string, string> options)
    {
        if (!options.TryGetValue("mode", out var mode))
        {
            if (options.ContainsKey("feature")) throw new ArgumentException("--feature requires --mode targeted (or use verify --feature for tool checks).");
            return;
        }
        mode = mode.ToLowerInvariant();
        if (mode is not ("targeted" or "light" or "full")) throw new ArgumentException("--mode must be targeted, light or full.");
        if (options.ContainsKey("suite") || options.ContainsKey("module")) throw new ArgumentException("Use either --mode or the legacy --suite/--module selectors, not both.");
        if (mode != "targeted" && (options.ContainsKey("feature") || options.ContainsKey("scenario"))) throw new ArgumentException("Custom --feature/--scenario selection belongs to --mode targeted.");
        if (mode == "targeted")
        {
            if (options.TryGetValue("feature", out var feature))
            {
                if (options.ContainsKey("scenario")) throw new ArgumentException("Choose --feature or --scenario, not both.");
                var ids = new List<string>();
                foreach (var item in feature.Split(',', StringSplitOptions.TrimEntries))
                {
                    if (!Features.TryGetValue(item, out var mapped)) throw new ArgumentException("Unknown performance feature: " + item + ". Supported: " + string.Join(", ", Features.Keys));
                    ids.AddRange(mapped);
                }
                options["scenario"] = string.Join(',', ids.Distinct());
            }
            else if (!options.ContainsKey("scenario")) throw new ArgumentException("Targeted mode requires --feature <names> or --scenario <ids>.");
        }
        else if (mode == "light") options["scenario"] = "idle_empty,rules_scaling_10,target_absent";
        else options["scenario"] = string.Join(',', ScenarioRegistry.GetAll().Where(s => s.Id != "generic_process").Select(s => s.Id));
        options["suite"] = mode == "light" ? "quick" : "standard";
        options.TryAdd("rounds", mode == "light" ? "1" : mode == "full" ? "7" : "5");
        options.TryAdd("duration", mode == "light" ? "3" : mode == "full" ? "60" : "30");
        options.TryAdd("warmup", mode == "light" ? "2" : "5");
        if (mode == "full" && int.TryParse(options["rounds"], out int rounds) && rounds < 3) throw new ArgumentException("Full mode requires at least three rounds; use light for smoke testing.");
    }
}
