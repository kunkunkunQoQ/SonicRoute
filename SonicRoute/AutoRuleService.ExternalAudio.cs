using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SonicRoute.Core.Models;

namespace SonicRoute;

public static partial class AutoRuleService
{
    private static volatile bool _externalWatching;
    private static HashSet<string> _externalNames = new(StringComparer.OrdinalIgnoreCase);
    private static int _externalNotification;
#if SONICROUTE_NATIVE
    private static HashSet<string> ExternalNames() => new((Product?.ExternalApps() ?? [])
        .Where(app => app.HasActiveSession && app.ProcessName != null).Select(app => app.ProcessName!), StringComparer.OrdinalIgnoreCase);
#else
    private static HashSet<string> ExternalNames() => new(ApplicationAudio.MergeExternal(Array.Empty<AudioAppInfo>())
        .Where(app => app.HasActiveSession && app.ProcessName != null).Select(app => app.ProcessName!), StringComparer.OrdinalIgnoreCase);
#endif

    private static void RefreshExternalWatcher(bool active)
    {
        if (_externalWatching == active) return;
        _externalWatching = active;
        if (active)
        {
            _externalNames = ExternalNames();
#if !SONICROUTE_NATIVE
            ApplicationAudio.Changed += ExternalSessionsChanged;
#endif
        }
        else
        {
#if !SONICROUTE_NATIVE
            ApplicationAudio.Changed -= ExternalSessionsChanged;
#endif
            _externalNames.Clear();
        }
    }
#if SONICROUTE_NATIVE
    internal static void ExternalSessionsChanged()
    {
        var dispatcher = Product;
        if (!_externalWatching || _shuttingDown || dispatcher == null
            || Interlocked.Exchange(ref _externalNotification, 1) != 0) return;
        if (!dispatcher.TryPost(ReadExternalChanges)) Interlocked.Exchange(ref _externalNotification, 0);
    }
#else
    private static void ExternalSessionsChanged()
    {
        var dispatcher = _watchDispatcher;
        if (!_externalWatching || _shuttingDown || dispatcher == null || dispatcher.HasShutdownStarted
            || Interlocked.Exchange(ref _externalNotification, 1) != 0) return;
        dispatcher.BeginInvoke(new Action(ReadExternalChanges));
    }
#endif

    private static void ReadExternalChanges()
    {
        Interlocked.Exchange(ref _externalNotification, 0);
        if (!_externalWatching || _shuttingDown) return;
        var now = ExternalNames(); var previous = _externalNames; _externalNames = now;
        var added = now.Except(previous, StringComparer.OrdinalIgnoreCase).ToArray();
        var removed = previous.Except(now, StringComparer.OrdinalIgnoreCase).ToArray();
        if (added.Length == 0 && removed.Length == 0) return;
        var rules = GetWatchRules();
        var requests = new List<WatchRequest>();
        foreach (var rule in rules)
        {
            bool trigger = rule.Trigger == AutoRuleTrigger.AppStart
                ? added.Any(name => MatchApp(rule.TriggerApp, name))
                : rule.Trigger == AutoRuleTrigger.AppExit && removed.Any(name => MatchApp(rule.TriggerApp, name));
            if (trigger) requests.Add(new WatchRequest { Rule = rule, Revision = _watchRulesRevision });
        }
        if (requests.Count > 0) QueueWatchRules(requests);
    }
}
