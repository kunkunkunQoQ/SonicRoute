using System;
using System.Collections.Generic;
using System.Linq;
using SonicRoute.Core;
using SonicRoute.Core.Models;
#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;
#endif

namespace SonicRoute.Native.Product.Views;

internal static class PlaybackSelection
{
    private static readonly StringComparer IdCmp = StringComparer.OrdinalIgnoreCase;

    internal static string[] SelectRange(string[] order, HashSet<string> selected, string clicked, string? anchor, bool ctrl, bool shift)
    {
        if (!shift && !ctrl) return [clicked];
        var result = new HashSet<string>(selected, IdCmp);
        if (shift && ctrl)
        {
            result.Add(clicked);
        }
        else if (shift)
        {
            anchor ??= order.FirstOrDefault(result.Contains);
            int start = Array.FindIndex(order, id => IdCmp.Equals(id, anchor));
            int end = Array.FindIndex(order, id => IdCmp.Equals(id, clicked));
            if (start < 0 || end < 0) result.Add(clicked);
            else for (int i = Math.Min(start, end); i <= Math.Max(start, end); i++) result.Add(order[i]);
        }
        else if (!result.Remove(clicked))
        {
            result.Add(clicked);
        }
        var visible = order.Where(result.Contains);
        return visible.Concat(selected.Where(id => result.Contains(id) && !order.Contains(id, IdCmp))).ToArray();
    }

    internal static string SelectPrimary(IReadOnlyList<string> outputs, string? currentOutputId, bool preferPhysical, string? lowPriorityId)
    {
        if (outputs.Count == 0) return AudioService.SystemDefaultDeviceId;

        string? Pick(bool filtered)
        {
            string? first = null, matchedCurrent = null, systemDefault = null;
            for (int i = 0; i < outputs.Count; i++)
            {
                string id = outputs[i];
                if (filtered)
                {
                    if (preferPhysical && PcRoutingApi.IsPhoneOutputDevice(id)) continue;
                    if (lowPriorityId != null && IdCmp.Equals(id, lowPriorityId)) continue;
                }
                if (!preferPhysical && AudioService.IsSystemDefault(id)) return id;
                first ??= id;
                if (AudioService.IsSystemDefault(id)) systemDefault ??= id;
                if (currentOutputId != null && IdCmp.Equals(id, currentOutputId)) matchedCurrent = id;
            }
            return matchedCurrent ?? systemDefault ?? first;
        }

        return Pick(true) ?? Pick(false) ?? AudioService.SystemDefaultDeviceId;
    }

    internal static bool IsMultiOutputExternal(ProductAudio audio, AudioAppInfo app)
    {
#if SONICROUTE_PHONE
        return app.IsExternal && audio.ExternalAudio?.FindSession(app.ExternalSourceId) is IMultiOutputAudioSession;
#else
        return false;
#endif
    }

    internal static string? LowPriorityOutputId(ProductAudio audio, AudioAppInfo app)
    {
#if SONICROUTE_PHONE
        return app.IsExternal && audio.ExternalAudio?.FindSession(app.ExternalSourceId) is AndroidAudioSession
            ? AndroidAudioSession.SourcePhoneOutputId
            : null;
#else
        return null;
#endif
    }
}
