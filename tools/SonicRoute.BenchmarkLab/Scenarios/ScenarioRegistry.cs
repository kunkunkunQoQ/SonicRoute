using System;
using System.Collections.Generic;
using System.Linq;

namespace SonicRoute.BenchmarkLab.Scenarios
{
    public static class ScenarioRegistry
    {
        private static readonly Dictionary<string, IBenchmarkScenario> _scenarios = new(StringComparer.OrdinalIgnoreCase);

        static ScenarioRegistry()
        {
            Register(new IdleEmptyScenario());
            Register(new RulesScalingScenario(1));
            Register(new RulesScalingScenario(10));
            Register(new RulesScalingScenario(100));
            Register(new TargetAbsentScenario());
            Register(new AppLifecycleChurnScenario());
            Register(new FastForegroundSwitchScenario());
            Register(new LongDelayStepScenario());
            Register(new RuleSaveHotkeysScenario());
            Register(new ScheduleRuleChurnScenario());
            Register(new WindowTrayTransitionsScenario());
            Register(new EnduranceLeakTrendScenario());
            Register(new GenericProcessScenario());
        }

        public static void Register(IBenchmarkScenario scenario)
        {
            _scenarios[scenario.Id] = scenario;
        }

        public static IBenchmarkScenario? GetById(string id)
        {
            return _scenarios.TryGetValue(id, out var scenario) ? scenario : null;
        }

        public static IReadOnlyCollection<IBenchmarkScenario> GetAll()
        {
            return _scenarios.Values.ToList();
        }

        public static IReadOnlyList<IBenchmarkScenario> GetScenariosForSuite(string suiteName, BenchmarkModule moduleFilter = BenchmarkModule.All)
        {
            List<IBenchmarkScenario> scenarios;

            switch (suiteName.ToLowerInvariant())
            {
                case "automation" or "optimization":
                    // The scenarios directly exercising the automation optimizations 1, 2, 3, 6
                    scenarios = new List<IBenchmarkScenario>
                    {
                        GetById("idle_empty")!,
                        GetById("rules_scaling_10")!,
                        GetById("rules_scaling_100")!,
                        GetById("target_absent")!,
                        GetById("app_lifecycle_churn")!,
                        GetById("fast_foreground_switch")!,
                        GetById("long_delay_step")!,
                        GetById("rule_save_hotkeys")!,
                        GetById("schedule_rule_churn")!
                    };
                    break;

                case "quick":
                    scenarios = new List<IBenchmarkScenario>
                    {
                        GetById("idle_empty")!,
                        GetById("rules_scaling_10")!,
                        GetById("target_absent")!
                    };
                    break;

                case "endurance":
                    scenarios = new List<IBenchmarkScenario>
                    {
                        GetById("idle_empty")!,
                        GetById("rules_scaling_100")!,
                        GetById("endurance_leak_trend")!
                    };
                    break;

                case "standard":
                default:
                    scenarios = new List<IBenchmarkScenario>
                    {
                        GetById("idle_empty")!,
                        GetById("rules_scaling_1")!,
                        GetById("rules_scaling_10")!,
                        GetById("rules_scaling_100")!,
                        GetById("target_absent")!,
                        GetById("app_lifecycle_churn")!,
                        GetById("window_tray_transitions")!
                    };
                    break;
            }

            if (moduleFilter != BenchmarkModule.All)
            {
                scenarios = scenarios.Where(s => s.Module == moduleFilter || s.Module == BenchmarkModule.General).ToList();
            }

            return scenarios;
        }
    }
}
