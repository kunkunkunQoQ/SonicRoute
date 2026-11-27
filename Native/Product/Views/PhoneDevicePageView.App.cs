#if SONICROUTE_PHONE
using System.Net;
using SonicRoute.AndroidAudio;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed partial class PhoneDevicePageView
{
    private bool _appConnectionChoice;
    private AndroidMdnsService[] _appCandidates = [];
    private IPEndPoint? _selectedAppCandidate;
    private AndroidDevice? _controlAttachment;
    private int? _appVolume;
    private string? _appRoute;

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
        float hint = PhoneTextHeight(guide, inner);
        var card = new UiRect(x, y, w, pad * 2 + hint + 76f);
        UiPrimitives.DrawCard(g, t, card, DeviceCardRadius);
        g.DrawText(guide, new UiRect(x + pad, y + pad, inner, hint), t.TextSecondary, 12f, wrap: true);
        float half = MathF.Max(1f, (inner - 8f) / 2);
        var paste = new UiRect(x + pad, y + pad + hint + 8f, half, 28f);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.app.paste", paste, L10n.T("Phone.SrapPaste"), enabled: !_busy, fontSize: 12f);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.app.find", new UiRect(paste.Right + 8f, paste.Y, half, 28f),
            L10n.T("Phone.SearchSavedDevices"), enabled: !_busy, fontSize: 12f);
        string candidate = _selectedAppCandidate?.ToString() ?? L10n.T("Phone.SrapCandidate");
        UiPrimitives.DrawComboBox(g, t, _hit, "phone.app.candidates", new UiRect(paste.X, paste.Bottom + 6f, inner, 28f),
            candidate, enabled: !_busy && _appCandidates.Length != 0);
        return card.Bottom + DeviceCardGap;
    }

    private float RenderAppControls(D2dTarget g, UiThemePalette t, float x, float y, float w, AndroidDevice? online)
    {
        if (!ReferenceEquals(online, _controlAttachment))
        {
            _controlAttachment = online;
            _appVolume = null;
            _appRoute = null;
        }
        if (online == null) return y;
        var controls = _actions.Host.Phone.Operations;
        bool volume = controls.CanControlVolume(online);
        bool route = controls.CanTestRoute(online);
        if (!volume && !route) return y;
        const float pad = DeviceCardPadding;
        float left = x + pad, inner = MathF.Max(1f, w - 2 * pad);
        var card = new UiRect(x, y, w, 2 * pad + 34f * ((volume ? 1 : 0) + (route ? 1 : 0)));
        UiPrimitives.DrawCard(g, t, card, DeviceCardRadius);
        float row = y + pad;
        if (volume)
        {
            string label = L10n.T("Phone.SrapVolume") + (_appVolume is { } value ? $" · {value}%" : "");
            g.DrawText(label, new UiRect(left, row, MathF.Max(1f, inner - 134f), 28f), t.TextPrimary, 12f, 600);
            float right = card.Right - pad;
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.app.volume.read", new UiRect(right - 76f, row, 76f, 28f),
                L10n.T("Apps.Refresh"), enabled: !_busy, fontSize: 11f);
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.app.volume.down", new UiRect(right - 136f, row, 28f, 28f),
                "−", enabled: !_busy && _appVolume > 0);
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.app.volume.up", new UiRect(right - 106f, row, 28f, 28f),
                "+", enabled: !_busy && _appVolume is < 100);
            row += 34f;
        }
        if (route)
        {
            string label = L10n.T("Phone.SrapTestRoute");
            float button = Math.Min(inner * 0.62f, PhoneTextWidth(label, 11f) + 30f);
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.app.test", new UiRect(left, row, button, 28f),
                label, enabled: !_busy, fontSize: 11f);
            if (_appRoute != null)
                g.DrawText(_appRoute, new UiRect(left + button + 8f, row, MathF.Max(1, inner - button - 8f), 28f), t.TextSecondary, 11f);
        }
        return card.Bottom + DeviceCardGap;
    }

    private bool HandleAppDeviceAction(string id, UiRect anchor)
    {
        var host = _actions.Host;
        switch (id)
        {
            case "phone.connchoice.app":
                if (_busy) return true;
                _actions.CloseInlineEdit(commit: false);
                _appConnectionChoice = true;
                _airPlayConnectionChoice = false;
                UpdateQrPairing();
                _actions.Invalidate();
                return true;
            case "phone.app.find":
                SearchAppCandidates();
                return true;
            case "phone.app.candidates":
                var choices = _appCandidates.Select(service => new UiMenuItem
                {
                    Text = service.Name + " · " + service.Address,
                    Click = () =>
                    {
                        if (!_active || !IsConnectionPage || !_appConnectionChoice || _busy) return;
                        _selectedAppCandidate = IPEndPoint.Parse(service.Address);
                        _actions.Invalidate();
                    }
                }).ToList();
                if (choices.Count != 0) _actions.ShowMenu(anchor, choices);
                return true;
            case "phone.app.paste":
                string? invite = NativeTextClipboard.ReadBoundedText(1024);
                if (string.IsNullOrWhiteSpace(invite))
                {
                    SetStatus(L10n.T("Phone.SrapInvalidInvite"), true);
                    return true;
                }
                var selected = _selectedAppCandidate;
                RunBusy(ct => host.Phone.PairAppAsync(invite, selected, () => host.TryPost(() =>
                {
                    if (_active && _appConnectionChoice && !ct.IsCancellationRequested) SetStatus(L10n.T("Phone.SrapConfirmOnPhone"), false);
                }), ct), "Phone.ConnectedStatus", openDevice: true, timeoutSeconds: 130);
                return true;
        }
        if (_activeDevice is not { IsApp: true } device) return false;
        switch (id)
        {
            case "phone.app.volume.read":
            case "phone.app.volume.up":
            case "phone.app.volume.down":
                int? value = id == "phone.app.volume.read" ? null : _appVolume + (id == "phone.app.volume.up" ? 10 : -10);
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

    private void SearchAppCandidates()
    {
        _selectedAppCandidate = null;
        _appCandidates = [];
        RunBusy(async ct =>
        {
            var found = (await PhonePreview.DiscoverAppConnectionsAsync(ct).ConfigureAwait(false))
                .Where(s => s.App?.Pairing == true && IPEndPoint.TryParse(s.Address, out _)).Take(16).ToArray();
            if (found.Length == 0) throw new SrapException("unavailable");
            _actions.Host.TryPost(() =>
            {
                if (!_active || !_appConnectionChoice) return;
                _appCandidates = found;
                if (found.Select(s => s.App!.Instance).Distinct().Count() == 1)
                    _selectedAppCandidate = IPEndPoint.Parse(found[0].Address);
            });
        });
    }
}
#endif
