using System;
using System.Collections.Generic;
using System.Linq;
using SonicRoute.Core.Models;

namespace SonicRoute.Core
{
    /// <summary>The same current-app state and resolution order is used by both product hosts.</summary>
    public sealed class CurrentAppSelection
    {
        private readonly object _gate = new();
        private AudioAppInfo? _current, _lastForeground;
        public event Action? Changed;

        public AudioAppInfo? Current
        {
            get { lock (_gate) return _current; }
            set
            {
                bool changed;
                lock (_gate) { changed = _current?.Identity != value?.Identity; _current = value; }
                if (changed) Changed?.Invoke();
            }
        }

        public AudioAppInfo? LastForeground
        {
            get { lock (_gate) return _lastForeground; }
            set { lock (_gate) _lastForeground = value; }
        }

        private static bool Disabled(AudioAppInfo app, AppConfig config) =>
            !string.IsNullOrWhiteSpace(app.ProcessName) && config.DisabledAutoSwitchApps.Contains(app.ProcessName!);

        public static AudioAppInfo? MatchForeground(List<AudioAppInfo> apps, AppConfig config, int pid,
            bool allowExactPid, Func<string?> readForegroundName)
        {
            if (allowExactPid)
            {
                var exact = apps.Find(a => !a.IsExternal && a.ProcessId == (uint)pid);
                if (exact != null && !Disabled(exact, config)) return exact;
            }
            string? name = readForegroundName();
            if (string.IsNullOrWhiteSpace(name)) return null;
            return apps.Find(a => string.Equals(a.ProcessName, name, StringComparison.OrdinalIgnoreCase) && !Disabled(a, config));
        }

        public AudioAppInfo? Resolve(List<AudioAppInfo> apps, AppConfig config, Func<AudioAppInfo?> matchForeground)
        {
            if (apps == null || apps.Count == 0) return null;
            AudioAppInfo? ByName(string? name) => string.IsNullOrWhiteSpace(name) ? null :
                apps.Find(a => string.Equals(a.ProcessName, name, StringComparison.OrdinalIgnoreCase));

            var explicitChoice = config.DefaultAppMode switch
            {
                "last" => ByName(config.LastUsedAppName), "fixed" => ByName(config.FixedAppName), _ => null
            };
            if (explicitChoice != null && !Disabled(explicitChoice, config)) return explicitChoice;
            if (matchForeground() is { } foreground) return foreground;
            var previous = LastForeground;
            if (previous != null && apps.Any(a => a.Identity == previous.Identity) && !Disabled(previous, config)) return previous;
            var last = ByName(config.LastUsedAppName);
            if (last != null && !Disabled(last, config)) return last;
            return apps.Find(a => !Disabled(a, config)) ?? apps[0];
        }

        public void Observe(AudioAppInfo app, AppConfig config)
        {
            if (LastForeground?.Identity == app.Identity) return;
            LastForeground = app;
            if (config.DefaultAppMode == "recent") Current = app;
        }
    }
}
