using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SonicRoute.Core.Models;

namespace SonicRoute.Core;

// One enumeration, across both endpoint flows. This is the same list consumed by
// foreground selection, automation and the panel in either host.
public sealed class AudioAppCatalog
{
    private readonly Dictionary<uint, AudioAppInfo> _apps = new();
    private readonly Dictionary<uint, (string? Name, bool Alive)> _processes = new();

    public AudioAppInfo? Add(uint pid, bool active, string? displayName)
    {
        if (pid == 0 || AudioService.IsInternalApplication(pid, null)) return null;
        if (!_processes.TryGetValue(pid, out var process))
        {
            string? name = ForegroundAppService.GetProcessNameSafe((int)pid);
            process = (name, name != null || IsAlive(pid));
            _processes.Add(pid, process);
        }
        if (!process.Alive || AudioService.IsInternalApplication(pid, process.Name)) return null;
        if (!_apps.TryGetValue(pid, out var app))
            _apps.Add(pid, app = new AudioAppInfo { ProcessId = pid, ProcessName = process.Name });
        if (app.DisplayName == null && !string.IsNullOrWhiteSpace(displayName)) app.DisplayName = displayName;
        app.HasActiveSession |= active;
        return app;
    }

    public List<AudioAppInfo> Sorted() => _apps.Values.OrderByDescending(app => app.HasActiveSession)
        .ThenBy(app => app.Label, StringComparer.CurrentCultureIgnoreCase).ToList();

    private static bool IsAlive(uint pid)
    {
        try { using var process = Process.GetProcessById((int)pid); return true; }
        catch (ArgumentException) { return false; }
        catch { return true; }
    }
}
