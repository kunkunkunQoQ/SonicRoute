using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SonicRoute.Core.Models;

namespace SonicRoute;

public static partial class AutoRuleService
{
    private static bool _externalWatching;
    private static HashSet<string> _externalNames = new(StringComparer.OrdinalIgnoreCase);
    private static int _externalNotification;
    private static HashSet<string> ExternalNames() => new(ApplicationAudio.MergeExternal(Array.Empty<AudioAppInfo>())
        .Where(app => app.HasActiveSession && app.ProcessName != null).Select(app => app.ProcessName!), StringComparer.OrdinalIgnoreCase);

    private static void RefreshExternalWatcher(bool active)
    {
        if (_externalWatching == active) return;
        _externalWatching = active;
        if (active)
        {
            _externalNames = ExternalNames();
            ApplicationAudio.Changed += ExternalSessionsChanged;
        }
        else { ApplicationAudio.Changed -= ExternalSessionsChanged; _externalNames.Clear(); }
    }
    private static void ExternalSessionsChanged()
    {
        var dispatcher = _watchDispatcher;
        if (!_externalWatching || _shuttingDown || dispatcher == null || dispatcher.HasShutdownStarted
            || Interlocked.Exchange(ref _externalNotification, 1) != 0) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref _externalNotification, 0);
            if (!_externalWatching || _shuttingDown) return;
            var now = ExternalNames(); var previous = _externalNames; _externalNames = now;
            var rules = GetWatchRules();
            var requests = new List<WatchRequest>();
            foreach (var rule in rules)
            {
                bool trigger = rule.Trigger == AutoRuleTrigger.AppStart
                    ? now.Except(previous).Any(name => MatchApp(rule.TriggerApp, name))
                    : rule.Trigger == AutoRuleTrigger.AppExit && previous.Except(now).Any(name => MatchApp(rule.TriggerApp, name));
                if (trigger) requests.Add(new WatchRequest { Rule = rule, Revision = _watchRulesRevision });
            }
            if (requests.Count > 0) QueueWatchRules(requests);
        }));
    }
}
