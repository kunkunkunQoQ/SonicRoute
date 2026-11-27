#if SONICROUTE_PHONE
using System.Net;
using SonicRoute.AndroidAudio;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed partial class PhoneDevicePageView
{
    private CancellationTokenSource? _appDiscovery;
    private AndroidMdnsService[] _appCandidates = [];
    private bool _appSearchComplete;
    private IPEndPoint? _selectedAppCandidate;
    private AndroidDevice? _controlAttachment;
    private int? _appVolume;
    private string? _appRoute;
    private AndroidDevice? _interactiveAdb;
    private IReadOnlyList<AndroidDevice>? _interactiveAdbScan;
    private long _interactiveRevision = -1;

    private void AddAppBindingMenu(List<UiMenuItem> items, AndroidPhoneProfile profile)
    {
        if (profile.LastDevice is not { IsApp: false }) return;
        var phone = _actions.Host.Phone;
        bool Selected() => _active && !IsConnectionPage && !_busy && phone.Store.Profile.Id == profile.Id;
        if (profile.AppPublicKeySha256 is { } pin)
        {
            void Select(bool? preferApp)
            {
                if (!Selected()) return;
                RunBusy(_ =>
                {
                    phone.Connections.SelectAppTransport(profile.Id, pin, preferApp);
                    return Task.CompletedTask;
                }, preferApp == null ? "Phone.SrapUnpairedLocally" : null);
            }
            items.Add(new UiMenuItem { Text = L10n.T("Phone.SrapAliasPreferAdb"), Checked = !profile.PreferAppTransport, Click = () => Select(false) });
            items.Add(new UiMenuItem { Text = L10n.T("Phone.SrapAliasPreferApp"), Checked = profile.PreferAppTransport,
                Enabled = !profile.AppTrustRevoked, Click = () => Select(true) });
            items.Add(new UiMenuItem { Text = L10n.T("Phone.SrapAliasForget"), Click = () => Select(null) });
            return;
        }
        var choices = new List<UiMenuItem>();
        if (_activeDevice is { IsApp: false } adb && AdbClient.SupportsAliasForward(adb))
        {
            foreach (var candidate in phone.Store.Profiles.Where(p => p.LastDevice?.IsApp == true))
            {
                if (phone.Connections.Resolve(candidate.LastDevice) is not { } app || !phone.Connections.CanBindApp(app)) continue;
                void Bind(bool keepAdb)
                {
                    if (!Selected()) return;
                    RunBusy(ct => phone.Connections.BindAppAsync(profile.Id, adb, candidate.Id, app, keepAdb, ct),
                        "Phone.SrapAliasBound", timeoutSeconds: 100);
                    SetStatus(L10n.T("Phone.SrapAliasConfirm"), false);
                }
                choices.Add(new UiMenuItem { Text = phone.Store.GetProfileDisplayName(candidate) + " · " + app.Serial[..12],
                    SubItems = [new() { Text = L10n.T("Phone.SrapAliasKeepAdb"), Click = () => Bind(true) },
                        new() { Text = L10n.T("Phone.SrapAliasKeepApp"), Click = () => Bind(false) }] });
            }
        }
        items.Add(new UiMenuItem { Text = L10n.T("Phone.SrapAliasBind"), Enabled = choices.Count != 0,
            Tooltip = choices.Count == 0 ? L10n.T("Phone.SrapAliasAvailability") : null, SubItems = choices });
    }

    private float RenderAppConnection(D2dTarget g, UiThemePalette t, float x, float y, float w)
    {
        const float pad = DeviceCardPadding;
        float inner = MathF.Max(1f, w - 2 * pad);
        string guide = L10n.T("Phone.SrapPairGuide");
        string pasteLabel = L10n.T("Phone.SrapPaste"), findLabel = L10n.T("Phone.SrapFindNearby");
        float half = Math.Max(1f, (inner - 8f) / 2);
        float pasteW = Math.Min(half, Math.Max(90f, PhoneTextWidth(pasteLabel, 12f, 600) + 24f));
        float findW = Math.Min(half, Math.Max(90f, PhoneTextWidth(findLabel, 12f) + 24f));
        float actionWidth = pasteW + 8f + findW;
        bool beside = inner - actionWidth - 20f >= 280f;
        float textWidth = beside ? inner - actionWidth - 20f : inner;
        float hint = PhoneTextHeight(guide, textWidth);
        bool hasCandidates = _appCandidates.Length != 0;
        string noResults = _appDiscovery != null ? L10n.T("Phone.ProcessingStatus") : _appSearchComplete && !hasCandidates ? L10n.T("Phone.SrapNoneNearby") : "";
        float resultsHeight = hasCandidates ? 40f : noResults.Length == 0 ? 0f : 8f + PhoneTextHeight(noResults, inner);
        float bodyHeight = 24f + hint + (beside ? 0f : 40f);
        var card = new UiRect(x, y, w, pad * 2 + bodyHeight + resultsHeight);
        UiPrimitives.DrawCard(g, t, card, DeviceCardRadius);
        var heading = new UiRect(x + pad, y + pad, textWidth, 20f);
        string title = L10n.T("Phone.SrapPairTitle");
        g.DrawText(title, heading, t.TextPrimary, DeviceHeadingSize, 600);
        _hit.DescribeText("phone.app.pair.heading", heading, title);
        var description = new UiRect(heading.X, heading.Bottom + 4f, textWidth, hint);
        g.DrawText(guide, description, t.TextSecondary, 12f, wrap: true);
        _hit.DescribeText("phone.app.pair.guide", description, guide);
        var paste = new UiRect(beside ? card.Right - pad - actionWidth : heading.X,
            beside ? heading.Y + (bodyHeight - 32f) / 2 : description.Bottom + 8f, pasteW, 32f);
        UiPrimitives.DrawAccentButton(g, t, _hit, "phone.app.paste", paste, pasteLabel, enabled: !_busy, fontSize: 12f);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.app.find", new UiRect(paste.Right + 8f, paste.Y,
            findW, 32f), findLabel, enabled: !_busy && _appDiscovery == null, fontSize: 12f);
        if (hasCandidates)
        {
            string candidate = _appCandidates.FirstOrDefault(a => a.Address == _selectedAppCandidate?.ToString())?.Name ?? L10n.T("Phone.SrapCandidate");
            UiPrimitives.DrawComboBox(g, t, _hit, "phone.app.candidates", new UiRect(heading.X, heading.Y + bodyHeight + 8f, inner, 32f), candidate, enabled: !_busy);
        }
        else if (noResults.Length != 0)
        {
            var message = new UiRect(heading.X, heading.Y + bodyHeight + 8f, inner, resultsHeight - 8f);
            g.DrawText(noResults, message, t.TextSecondary, 12f, wrap: true);
            _hit.DescribeText("phone.app.discovery.status", message, noResults);
        }
        return card.Bottom + DeviceCardGap;
    }

    private float RenderAppControls(D2dTarget g, UiThemePalette t, float x, float y, float w, AndroidDevice? online)
    {
        if (!ReferenceEquals(online, _controlAttachment))
        {
            _controlAttachment = online;
            _appVolume = null;
            _appRoute = null;
            _interactiveRevision = -1;
        }
        if (online == null) return y;
        var controls = _actions.Host.Phone.Operations;
        bool volume = controls.CanControlVolume(online);
        bool route = controls.CanTestRoute(online);
        bool screen = controls.CanViewSharedScreen(online);
        bool screenRow = screen || controls.NeedsScreenConsent(online);
        var connections = _actions.Host.Phone.Connections;
        if (_interactiveRevision != connections.Revision || !ReferenceEquals(_interactiveAdbScan, connections.CurrentRaw))
        {
            _interactiveRevision = connections.Revision;
            _interactiveAdbScan = connections.CurrentRaw;
            _interactiveAdb = connections.VerifiedAdbForDisplay(online);
        }
        var capture = controls.AppCapture(online);
        if (!volume && !route && !screenRow && capture == null && _interactiveAdb == null) return y;
        const float pad = DeviceCardPadding;
        float left = x + pad, inner = MathF.Max(1f, w - 2 * pad);
        string screenGuide = screenRow && !screen ? L10n.T("Phone.SrapScreenGrant") : "";
        string captureGuide = capture is { Package: null } ? L10n.T("Phone.SrapCaptureGrant") : "";
        float PermissionHeight(string guide) => 31f + PhoneTextHeight(guide, inner, 12f);
        float screenHeight = !screenRow ? 0f : screen ? 38f : PermissionHeight(screenGuide);
        float captureHeight = capture == null ? 0f : capture.Package != null ? 38f : PermissionHeight(captureGuide);
        var card = new UiRect(x, y, w, 2 * pad + 38f * ((volume ? 1 : 0) + (route ? 1 : 0) + (_interactiveAdb != null ? 1 : 0)) + screenHeight + captureHeight);
        UiPrimitives.DrawCard(g, t, card, DeviceCardRadius);
        float row = y + pad;
        if (volume)
        {
            string label = L10n.T("Phone.SrapVolume") + (_appVolume is { } value ? $" · {value}%" : "");
            g.DrawText(label, new UiRect(left, row, MathF.Max(1f, inner - 134f), 28f), t.TextPrimary, 12f, 600);
            _hit.DescribeText("phone.app.volume.value", new UiRect(left, row, MathF.Max(1f, inner - 150f), 28f), label);
            float right = card.Right - pad;
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.app.volume.down", new UiRect(right - 136f, row, 28f, 28f),
                "−", enabled: !_busy && _appVolume > 0);
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.app.volume.up", new UiRect(right - 106f, row, 28f, 28f),
                "+", enabled: !_busy && _appVolume is < 100);
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.app.volume.read", new UiRect(right - 76f, row, 76f, 28f),
                L10n.T("Apps.Refresh"), enabled: !_busy, fontSize: 11f);
            row += 38f;
        }
        if (route)
        {
            string buttonLabel = L10n.T("Phone.SrapSelectOutput");
            string label = L10n.T("Phone.SrapOutput") + (_appRoute == null ? "" : " · " + _appRoute);
            float button = Math.Min(inner * 0.55f, PhoneTextWidth(buttonLabel, 12f) + 28f);
            var outputLabel = new UiRect(left, row, MathF.Max(1f, inner - button - 10f), 30f);
            g.DrawText(label, outputLabel, t.TextPrimary, 12f, 600);
            _hit.DescribeText("phone.app.output.value", outputLabel, label);
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.app.test", new UiRect(card.Right - pad - button, row, button, 30f),
                buttonLabel, enabled: !_busy, fontSize: 12f, tooltip: L10n.T("Phone.SrapTestRoute"));
            row += 38f;
        }
        if (screen)
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.app.screen", new UiRect(left, row + 4f, MathF.Min(inner, 230f), 30f),
                L10n.T("Phone.SrapOpenScreen"), enabled: !_busy, fontSize: 12f);
        else if (screenRow) DrawPermission("phone.app.permission.screen", L10n.T("Phone.SrapScreenSharing"), screenGuide);
        row += screenHeight;
        if (_interactiveAdb != null)
        {
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.app.interactiveAdb", new UiRect(left, row, MathF.Min(inner, 260f), 28f),
                L10n.T("Phone.SrapInteractiveAdb"), enabled: !_busy, fontSize: 11f, tooltip: L10n.T("Phone.SrapInteractiveAdbHint"));
            row += 38f;
        }
        if (capture?.Package is { } package)
        {
            var phone = _actions.Host.Phone;
            string name = phone.Store.FindProfile(online)?.RetainedApps.FirstOrDefault(a => a.Package == package)?.Name ?? package;
            bool listening = phone.Sources.Sources.Any(s => AndroidDeviceIdentity.SameTransport(s.Device, online) && s.App.Package == package);
            string label = L10n.T(listening ? "Phone.SrapCaptureStop" : "Phone.SrapCaptureListen");
            float button = Math.Min(inner * 0.58f, PhoneTextWidth(label, 11f) + 30f);
            g.DrawText(name, new UiRect(left, row, MathF.Max(1f, inner - button - 8f), 40f), t.TextPrimary, 12f, 600);
            UiPrimitives.DrawSecondaryButton(g, t, _hit, (listening ? "phone.app.capture.stop." : "phone.app.capture.listen.") + package,
                new UiRect(card.Right - pad - button, row + 5f, button, 30f), label, enabled: !_busy, fontSize: 11f);
        }
        else if (capture != null) DrawPermission("phone.app.permission.capture", L10n.T("Phone.SrapAudioSharing"), captureGuide);
        return card.Bottom + DeviceCardGap;

        void DrawPermission(string id, string title, string guide)
        {
            g.DrawLine(left, row, card.Right - pad, row, t.Border, 1f);
            var heading = new UiRect(left, row + 6f, inner, 18f);
            g.DrawText(title, heading, t.TextPrimary, 12f, 600);
            var description = new UiRect(left, heading.Bottom + 1f, inner, PhoneTextHeight(guide, inner, 12f));
            g.DrawText(guide, description, t.TextSecondary, 12f, wrap: true);
            _hit.DescribeText(id, new UiRect(left, row + 4f, inner, description.Bottom - row - 4f), title + " · " + guide);
        }
    }

    private bool HandleAppDeviceAction(string id, UiRect anchor)
    {
        var host = _actions.Host;
        switch (id)
        {
            case "phone.app.find":
                SearchAppCandidates();
                return true;
            case "phone.app.candidates":
                if (!AndroidConnectionVisible || _busy) return true;
                var choices = _appCandidates.Select(service => new UiMenuItem
                {
                    Text = service.Name + " · " + service.Address,
                    Click = () =>
                    {
                        if (!AndroidConnectionVisible || _busy) return;
                        _selectedAppCandidate = IPEndPoint.Parse(service.Address);
                        _actions.Invalidate();
                    }
                }).ToList();
                if (choices.Count != 0) _actions.ShowMenu(anchor, choices);
                return true;
            case "phone.app.paste":
                if (!AndroidConnectionVisible || _busy) return true;
                string? invite = NativeTextClipboard.ReadBoundedText(1024);
                if (string.IsNullOrWhiteSpace(invite))
                {
                    SetStatus(L10n.T("Phone.SrapInvalidInvite"), true);
                    return true;
                }
                var selected = _selectedAppCandidate;
                RunBusy(ct => host.Phone.PairAppAsync(invite, selected, () => host.TryPost(() =>
                {
                    if (AndroidConnectionVisible && !ct.IsCancellationRequested) SetStatus(L10n.T("Phone.SrapConfirmOnPhone"), false);
                }), ct), "Phone.ConnectedStatus", openDevice: true, timeoutSeconds: 130);
                return true;
        }
        if (_activeDevice is not { IsApp: true } device) return false;
        if (host.Phone.Operations.AppCapture(device)?.Package is { } package)
        {
            if (id == "phone.app.capture.listen." + package)
            {
                var app = host.Phone.Store.FindProfile(device)?.RetainedApps.FirstOrDefault(a => a.Package == package)
                    ?? new AndroidApp(package, package, 0, "");
                RunBusy(ct => host.Phone.Sources.AddAsync(device, app, keepPhoneAudio: true).WaitAsync(ct));
                return true;
            }
            if (id == "phone.app.capture.stop." + package)
            {
                RunBusy(ct => host.Phone.Sources.RemoveApplicationAsync(device, package).WaitAsync(ct));
                return true;
            }
        }
        switch (id)
        {
            case "phone.app.screen":
                string title = host.Phone.Store.GetDisplayName(device);
                RunBusy(ct => host.Phone.Operations.ViewSharedScreenAsync(device, title, ct), "Phone.SrapScreenPresented");
                return true;
            case "phone.app.interactiveAdb":
                if (_interactiveAdb is { } shown)
                    RunBusy(_ => { host.Phone.Connections.SelectVerifiedAdbForDisplay(device, shown); return Task.CompletedTask; }, "Phone.SrapSwitchedAdb");
                return true;
            case "phone.app.volume.read":
            case "phone.app.volume.up":
            case "phone.app.volume.down":
                int? value = id == "phone.app.volume.read" || _appVolume == null ? null : Math.Clamp(_appVolume.Value + (id == "phone.app.volume.up" ? 10 : -10), 0, 100);
                RunBusy(async ct =>
                {
                    int volume = await host.Phone.Operations.VolumeAsync(device, value, ct).ConfigureAwait(false);
                    host.TryPost(() =>
                    {
                        if (ReferenceEquals(_activeDevice, device)) _appVolume = volume;
                    });
                });
                return true;
            case "phone.app.test":
                RunBusy(async ct =>
                {
                    var outputs = await host.Phone.Operations.OutputsAsync(device, ct).ConfigureAwait(false);
                    host.TryPost(() =>
                    {
                        if (!_active || IsConnectionPage || !ReferenceEquals(_activeDevice, device)) return;
                        var items = outputs.Select(output => new UiMenuItem
                        {
                            Text = PhoneAppPresentation.OutputLabel(output, host.Phone.Store.GetDisplayName(device)),
                            Click = () => TestAppRoute(device, output, outputs)
                        }).ToList();
                        if (items.Count != 0) _actions.ShowMenu(anchor, items);
                    });
                });
                return true;
            default:
                return false;
        }
    }

    private void TestAppRoute(AndroidDevice device, AndroidOutputSink target, IReadOnlyList<AndroidOutputSink> outputs)
    {
        if (IsConnectionPage || !ReferenceEquals(_activeDevice, device)) return;
        _appRoute = null;
        RunBusy(async ct =>
        {
            string actual = await _actions.Host.Phone.Operations.TestRouteAsync(device, target.Key, ct).ConfigureAwait(false);
            var result = outputs.FirstOrDefault(o => o.Key == actual);
            _actions.Host.TryPost(() =>
            {
                if (ReferenceEquals(_activeDevice, device))
                    _appRoute = result != null ? PhoneAppPresentation.OutputLabel(result, _actions.Host.Phone.Store.GetDisplayName(device)) : actual;
            });
        }, "Phone.SrapRouteVerified");
    }

    private void CancelAppDiscovery()
    {
        var searching = _appDiscovery;
        _appDiscovery = null;
        searching?.Cancel();
    }

    private void SearchAppCandidates()
    {
        if (!AndroidConnectionVisible || _busy || _appDiscovery != null) return;
        _selectedAppCandidate = null;
        _appCandidates = [];
        _appSearchComplete = false;
        var searching = CancellationTokenSource.CreateLinkedTokenSource(_actions.Host.Phone.Lifetime);
        searching.CancelAfter(TimeSpan.FromSeconds(10));
        _appDiscovery = searching;
        _actions.Invalidate();
        _ = ReadAsync();

        async Task ReadAsync()
        {
            AndroidMdnsService[] found = [];
            Exception? failure = null;
            try
            {
                found = (await PhonePreview.DiscoverAppConnectionsAsync(searching.Token).ConfigureAwait(false))
                    .Where(s => s.App?.Pairing == true && IPEndPoint.TryParse(s.Address, out _)).Take(16).ToArray();
            }
            catch (OperationCanceledException) when (searching.IsCancellationRequested) { }
            catch (Exception error) { failure = error; }
            if (!_actions.Host.TryPost(() =>
            {
                searching.Dispose();
                if (!ReferenceEquals(_appDiscovery, searching)) return;
                _appDiscovery = null;
                if (!AndroidConnectionVisible) return;
                _appCandidates = found;
                _appSearchComplete = failure == null;
                if (found.Select(s => s.App!.Instance).Distinct().Count() == 1)
                    _selectedAppCandidate = IPEndPoint.Parse(found[0].Address);
                if (failure != null) SetStatus(PhonePreview.TranslateError(failure), true);
                else _actions.Invalidate();
            })) searching.Dispose();
        }
    }
}
#endif
