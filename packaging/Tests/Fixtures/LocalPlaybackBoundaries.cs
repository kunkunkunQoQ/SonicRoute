using SonicRoute.Core.Models;

namespace SonicRoute;

// The linked desktop facade exercises local sessions without touching saved routes or mirrors.
internal sealed class L10n
{
    internal static L10n Instance { get; } = new();
    public string this[string key] => key;
    internal static string T(string key) => key;
}

internal static class AppPlaybackRestore
{
    internal static bool HasSavedChoice(int pid) => false;
    internal static Task<bool> ClearAsync() => Task.FromResult(true);
}

internal static class LocalAppOutputMirrors
{
    internal static string[] GetExtraOutputs(int pid) => Array.Empty<string>();
    internal static Task ClearAllAsync() => Task.CompletedTask;
}

internal static class AppPlaybackOutputs
{
    internal static Task<(bool Success, int HResult, string Message)> SetAsync(
        AudioAppInfo app, string[] ids, string? primary, bool allowSpeakerMute = false)
        => throw new InvalidOperationException("Unexpected managed playback route in the fixture.");
}
