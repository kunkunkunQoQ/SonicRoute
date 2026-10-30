using System;
using System.Diagnostics;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Core.Models;

namespace SonicRoute
{
    internal sealed class ExecutionHistoryRun
    {
        private readonly ExecutionHistoryStore _store;
        private readonly long _started = Stopwatch.GetTimestamp();
        internal ExecutionHistoryEntry Entry { get; }
        internal ExecutionHistoryRun(ExecutionHistoryStore store, AutoRule rule, string source, string? parentRunId)
        {
            _store = store;
            Entry = new ExecutionHistoryEntry
            {
                RunId = Guid.NewGuid().ToString("N"), ParentRunId = Cut(parentRunId, 64),
                RuleId = Cut(rule.Id, 64), RuleName = Cut(rule.Name, 160), Source = Cut(source, 32),
                StartedUtc = DateTime.UtcNow
            };
        }
        internal static long Elapsed(long since) => (long)((Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency);
        private static string Cut(string? value, int maximum) => value == null ? "" : value.Length <= maximum ? value : value.Substring(0, maximum);
        internal ExecutionHistoryStep? Step(int index, AutoRuleStep step)
        {
            if (Entry.Steps.Count >= 100) return null;
            var detail = new ExecutionHistoryStep { Number = index + 1, Action = (int)step.Action, TargetApp = Cut(step.TargetApp, 80),
                TargetDeviceId = AutoRuleActions.NeedsDevice(step.Action) ? Cut(step.TargetDeviceId, 160) : null };
            Entry.Steps.Add(detail);
            return detail;
        }
        internal Task CompleteAsync(AutoRuleService.ExecutionResult result)
        {
            Entry.ElapsedMs = Elapsed(_started);
            Entry.Succeeded = result.Succeeded; Entry.Failed = result.Failed; Entry.Skipped = result.Skipped;
            Entry.Status = result.Busy ? "busy" : result.Canceled ? "canceled" : result.Stopped ? "stopped"
                : result.Failed > 0 || result.Succeeded == 0 ? "failed" : "success";
            return _store.AppendAsync(Entry);
        }
    }
}
