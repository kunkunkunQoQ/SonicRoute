#if SONICROUTE_PHONE
using SonicRoute.WindowsLink;

namespace SonicRoute;

internal static class WindowsLinkPeerStatus
{
    internal static string Text(LinkPeer peer) => peer.State switch
    {
        LinkPeerState.Connected => L10n.T("Phone.ConnectedStatus"),
        LinkPeerState.Connecting => L10n.T("Phone.WindowsLinkStateConnecting"),
        LinkPeerState.Online => L10n.T("Phone.WindowsLinkStateOnline"),
        LinkPeerState.Discovered => L10n.T("Phone.WindowsLinkStateDiscovered"),
        LinkPeerState.DisconnectedByUser => L10n.T("Phone.Disconnected"),
        LinkPeerState.Failed => L10n.T("Phone.WindowsLinkStateFailed"),
        _ => L10n.T("Phone.WindowsLinkStateOffline")
    };

    internal static string StateWithPairing(LinkPeer peer)
    {
        string state = Text(peer);
        return peer.Trusted ? $"{L10n.T("Phone.WindowsLinkStatePaired")} · {state}" : state;
    }

    internal static string Subtitle(LinkPeer peer)
    {
        string status = StateWithPairing(peer);
        return string.IsNullOrWhiteSpace(peer.Address) ? status : $"{status} · {peer.Address}";
    }

    internal static string FailureText(LinkConnectFailure failure) => failure switch
    {
        LinkConnectFailure.None => "",
        LinkConnectFailure.DiscoveryTimeout => L10n.T("Phone.WindowsLinkFailDiscoveryTimeout"),
        LinkConnectFailure.TcpUnreachable => L10n.T("Phone.WindowsLinkFailTcpUnreachable"),
        LinkConnectFailure.PairingRejected => L10n.T("Phone.WindowsLinkFailPairingRejected"),
        LinkConnectFailure.IdentityMismatch => L10n.T("Phone.WindowsLinkFailIdentityMismatch"),
        LinkConnectFailure.SavedAddressUnavailable => L10n.T("Phone.WindowsLinkFailSavedAddressUnavailable"),
        LinkConnectFailure.Incompatible => L10n.T("Phone.WindowsLinkFailIncompatible"),
        _ => L10n.T("Phone.WindowsLinkReconnectFailed")
    };
}
#endif
