#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

namespace SonicRoute.Native.Product;

internal sealed partial class PhonePreview
{
    private readonly Dictionary<string, (SrapSession Session, string Grant)> screenRequests = new(StringComparer.Ordinal);

    internal bool CanRequestPhoneSharing(AndroidDevice device) => device.IsApp && Connections.IsCurrent(device)
        && appTransport.Session(device)?.Capabilities.Entries.GetValueOrDefault("sharing.prompt") is
            { State: "available", Scope: "local_confirmation" };

    internal async Task RequestPhoneSharingAsync(AndroidDevice device, bool screen, CancellationToken cancel)
    {
        if (!CanRequestPhoneSharing(device) || appTransport.Session(device) is not { } session)
            throw new SrapException("permission_required");
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancel, session.Token, Connections.OperationToken(device));
        string kind = screen ? "screen" : "audio";
        var result = await session.RequestAsync("sharing.request", "sharing.prompt", new() { ["kind"] = kind }, request.Token)
            .ConfigureAwait(false);
        if (SrapClient.Text(result, "kind") != kind || SrapClient.Text(result, "state") != "awaiting_local_consent")
            throw new SrapException("invalid_argument");
    }

    // Called by the serialized source reconciler. Each local screen grant opens one viewer.
    private void StartRequestedScreenViewers()
    {
        var profiles = Store.Profiles;
        foreach (var id in screenRequests.Keys.ToArray())
            if (!profiles.Any(p => p.Id == id && appTransport.Resolve(p) is { } device &&
                ReferenceEquals(appTransport.Session(device), screenRequests[id].Session))) screenRequests.Remove(id);
        foreach (var profile in profiles)
        {
            if (appTransport.Resolve(profile) is not { } device || !Connections.IsCurrent(device) ||
                appTransport.Session(device) is not { ScreenGrant: { Length: > 0 and <= 20 } grant } session ||
                session.Capabilities.Entries.GetValueOrDefault("display.mirror")?.Limits?.GetValueOrDefault("autoview") != "pc_viewer_v1")
                continue;
            if (!grant.All(char.IsAsciiDigit) || screenRequests.TryGetValue(profile.Id, out var previous)
                && ReferenceEquals(previous.Session, session) && previous.Grant == grant) continue;
            screenRequests[profile.Id] = (session, grant);
            _ = PresentSharedScreenAsync();

            async Task PresentSharedScreenAsync()
            {
                try { await session.ViewScreenAsync(Store.GetDisplayName(device), session.Token, device).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception error) { ExceptionDiagnostics.Record("NativePhone.OpenSharedScreen", error); }
            }
        }
    }
}
#endif
