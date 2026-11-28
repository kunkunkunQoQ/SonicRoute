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
    private NearbyPairing? _nearbyPairing;
    private (AndroidDevice Device, bool Screen)? _requestedSharing;

    private sealed class NearbyPairing(AndroidMdnsService service)
    {
        public AndroidMdnsService Service { get; } = service;
        private readonly string attempt = Guid.NewGuid().ToString("N");
        public string MatchAction => "phone.app.pair.match:" + attempt;
        public string CancelAction => "phone.app.pair.cancel:" + attempt;
        public string? ComparisonCode { get; set; }
        public TaskCompletionSource<bool>? Confirmation { get; set; }
    }

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
        if (_nearbyPairing is { } pairing) return RenderNearbyPairing(g, t, x, y, w, pairing);
        const float pad = DeviceCardPadding;
        float inner = MathF.Max(1f, w - 2 * pad);
        string guide = L10n.T(_appSearchComplete && _appCandidates.Length == 0 ? "Phone.SrapNoneNearby" : "Phone.SrapPairGuide");
        string findLabel = L10n.T(_appDiscovery == null ? "Phone.SrapFindNearby" : "Phone.SrapFinding");
        float findW = Math.Min(inner, Math.Max(90f, PhoneTextWidth(findLabel, 12f, 600) + 24f));
        bool beside = inner - findW - 16f >= 240f;
        float textWidth = beside ? inner - findW - 16f : inner;
        float hint = PhoneTextHeight(guide, textWidth);
        float bodyHeight = 24f + hint + (beside ? 0f : 40f);
        var card = new UiRect(x, y, w, pad * 2 + bodyHeight + 40f * _appCandidates.Length);
        UiPrimitives.DrawCard(g, t, card, DeviceCardRadius);
        var heading = new UiRect(x + pad, y + pad, textWidth, 20f);
        string title = L10n.T("Phone.SrapPairTitle");
        g.DrawText(title, heading, t.TextPrimary, DeviceHeadingSize, 600);
        _hit.DescribeText("phone.app.pair.heading", heading, title);
        var description = new UiRect(heading.X, heading.Bottom + 4f, textWidth, hint);
        g.DrawText(guide, description, t.TextSecondary, 12f, wrap: true);
        _hit.DescribeText("phone.app.pair.guide", description, guide);
        var find = new UiRect(beside ? card.Right - pad - findW : heading.X,
            beside ? heading.Y + (bodyHeight - 32f) / 2 : description.Bottom + 8f, findW, 32f);
        UiPrimitives.DrawAccentButton(g, t, _hit, "phone.app.find", find, findLabel,
            enabled: !_busy && _appDiscovery == null, fontSize: 12f);
        float row = heading.Y + bodyHeight + 8f;
        foreach (var service in _appCandidates)
        {
            UiPrimitives.DrawSecondaryButton(g, t, _hit, CandidateAction(service), new UiRect(heading.X, row, inner, 32f),
                CandidateLabel(service), enabled: !_busy, fontSize: 12f, icon: UiIcon.Phone);
            row += 40f;
        }
        return card.Bottom + DeviceCardGap;
    }

    private float RenderNearbyPairing(D2dTarget g, UiThemePalette t, float x, float y, float w, NearbyPairing pairing)
    {
        const float pad = DeviceCardPadding;
        float inner = MathF.Max(1f, w - 2 * pad);
        string title = CandidateLabel(pairing.Service);
        float headingHeight = PhoneTextHeight(title, inner, DeviceHeadingSize);
        bool hasCode = pairing.ComparisonCode != null, needsDecision = pairing.Confirmation != null;
        string guide = L10n.T(!hasCode ? "Phone.SrapStartingNearby" :
            needsDecision ? "Phone.SrapCompareCode" : "Phone.SrapWaitForPhone");
        float guideHeight = PhoneTextHeight(guide, inner);
        string agree = L10n.T("Phone.SrapCodesMatch"), cancel = L10n.T("Auto.Cancel");
        float agreeW = Math.Min(inner, Math.Max(82f, PhoneTextWidth(agree, 12f, 600) + 24f));
        float cancelW = Math.Min(inner, Math.Max(72f, PhoneTextWidth(cancel, 12f) + 24f));
        bool stacked = needsDecision && agreeW + cancelW + 8f > inner;
        var card = new UiRect(x, y, w, pad * 2 + headingHeight + (hasCode ? 47f : 0f) + 6f + guideHeight + 10f + (stacked ? 72f : 32f));
        UiPrimitives.DrawCard(g, t, card, DeviceCardRadius);
        var heading = new UiRect(x + pad, y + pad, inner, headingHeight);
        g.DrawText(title, heading, t.TextPrimary, DeviceHeadingSize, 600, wrap: true);
        _hit.DescribeText("phone.app.pair.target", heading, title);
        float row = heading.Bottom;
        if (pairing.ComparisonCode is { } code)
        {
            var number = new UiRect(heading.X, row + 4f, inner, 43f);
            g.DrawText(code, number, t.Accent, 28f, 600, UiTextAlign.Leading, UiVertAlign.Center);
            _hit.DescribeText("phone.app.pair.comparison", number, code);
            row = number.Bottom;
        }
        var description = new UiRect(heading.X, row + 6f, inner, guideHeight);
        g.DrawText(guide, description, t.TextSecondary, 12f, wrap: true);
        _hit.DescribeText("phone.app.pair.instruction", description, guide);
        float actionsY = description.Bottom + 10f;
        if (needsDecision)
            UiPrimitives.DrawAccentButton(g, t, _hit, pairing.MatchAction, new UiRect(heading.X, actionsY, agreeW, 32f), agree);
        var dismiss = new UiRect(heading.X + (needsDecision && !stacked ? agreeW + 8f : 0f), actionsY + (stacked ? 40f : 0f), cancelW, 32f);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, pairing.CancelAction, dismiss, cancel);
        return card.Bottom + DeviceCardGap;
    }

    private static string CandidateLabel(AndroidMdnsService service) => service.Name + " · " + service.Address;
    private static string CandidateAction(AndroidMdnsService service) => $"phone.app.nearby:{service.App!.Instance}/{service.Address}";

    private float? RenderAppHeaderActions(D2dTarget g, UiThemePalette t, UiRect more, float nameX, AndroidDevice device)
    {
        var phone = _actions.Host.Phone;
        bool mayAsk = phone.CanRequestPhoneSharing(device);
        bool screen = mayAsk || phone.Operations.CanViewSharedScreen(device);
        var package = phone.Operations.AppCapture(device)?.LocalSourceKey;
        bool sound = mayAsk || package != null;
        if (!screen && !sound) return null;
        bool playing = package != null && HasPhoneAudio(device, package);
        string soundLabel = L10n.T(playing ? "Phone.SrapCaptureStop" : "Phone.PlayPhoneAudio");
        string screenLabel = L10n.T("Phone.MirrorPhone");
        float soundW = sound ? PhoneTextWidth(soundLabel, 12f) + 24f : 0f;
        float screenW = screen ? PhoneTextWidth(screenLabel, 12f) + 24f : 0f;
        bool icons = more.X - nameX - screenW - soundW < 140f;
        float right = more.X - 6f;
        if (sound) Draw("phone.app.sound", soundLabel, playing ? UiIcon.Pause : UiIcon.Headphones, soundW);
        if (screen) Draw("phone.app.screen", screenLabel, UiIcon.Phone, screenW);
        return right;

        void Draw(string action, string title, UiIcon icon, float buttonWidth)
        {
            float width = icons ? 30f : buttonWidth;
            right -= width;
            UiPrimitives.DrawSecondaryButton(g, t, _hit, action, new UiRect(right, more.Y, width, more.Height),
                icons ? "" : title, enabled: !_busy, fontSize: 12f, icon: icons ? icon : null, tooltip: title);
            right -= 6f;
        }
    }

    private bool HasPhoneAudio(AndroidDevice device, string package) => _actions.Host.Phone.Sources.Sources.Any(source =>
        AndroidDeviceIdentity.SameTransport(source.Device, device) && source.App.Package == package && source.Session.State.State != CaptureState.Failed);

    private void AddAppDeviceTools(List<UiMenuItem> items, AndroidPhoneProfile profile, UiRect anchor)
    {
        var phone = _actions.Host.Phone;
        if (_activeDevice is not { IsApp: true } device) return;
        bool current() => _active && !IsConnectionPage && !_busy && ReferenceEquals(_activeDevice, device) && phone.Store.Profile.Id == profile.Id;
        void Command(string id) { if (current()) HandleAppDeviceAction(id, anchor); }
        if (phone.PcListening.StoppableRequest(device) is { } playing)
            items.Add(new UiMenuItem { Text = L10n.T("Phone.StopPcAudioOnPhone"), Icon = UiIcon.Pause, Click = () =>
            {
                if (current()) RunBusy(_ => phone.PcListening.StopOnPhoneAsync(device, playing));
            } });
        else if (phone.PcListening.CanStartOnPhone(device))
            items.Add(new UiMenuItem { Text = L10n.T("Phone.PlayPcAudioOnPhone"), Icon = UiIcon.Speaker, Click = () =>
            {
                if (current()) RunBusy(ct => phone.PcListening.StartOnPhoneAsync(device, ct));
            } });
        if (phone.Operations.CanTestRoute(device))
            items.Add(new UiMenuItem { Text = L10n.T("Phone.SrapSelectOutput"), Click = () => Command("phone.app.test") });
        if (phone.Operations.CanControlVolume(device))
        {
            void Volume(int step)
            {
                if (!current()) return;
                int value = 0;
                RunBusy(async ct =>
                {
                    value = await phone.Operations.VolumeAsync(device, null, ct).ConfigureAwait(false);
                    if (step != 0) value = await phone.Operations.VolumeAsync(device, Math.Clamp(value + step, 0, 100), ct).ConfigureAwait(false);
                }, completedText: () => L10n.T("Phone.SrapVolume") + " · " + value + "%");
            }
            items.Add(new UiMenuItem { Text = L10n.T("Phone.SrapVolume"), SubItems =
            [
                new() { Text = "−", Click = () => Volume(-10) },
                new() { Text = "+", Click = () => Volume(10) },
                new() { Text = L10n.T("Apps.Refresh"), Click = () => Volume(0) }
            ] });
        }
        if (phone.Connections.VerifiedAdbForDisplay(device) is { } adb)
            items.Add(new UiMenuItem { Text = L10n.T("Phone.SrapInteractiveAdb"), Click = () =>
            {
                if (current()) RunBusy(_ => { phone.Connections.SelectVerifiedAdbForDisplay(device, adb); return Task.CompletedTask; }, "Phone.SrapSwitchedAdb");
            } });
        items.Add(new UiMenuItem { Text = L10n.T("Phone.Disconnect"), Click = () => { if (current()) RunDeviceCommand("phone.disconnect"); } });
    }

    private bool HandleAppDeviceAction(string id, UiRect anchor)
    {
        var host = _actions.Host;
        if (id == "phone.app.find")
        {
            SearchAppCandidates();
            return true;
        }
        if (id.StartsWith("phone.app.nearby:", StringComparison.Ordinal))
        {
            if (AndroidConnectionVisible && !_busy && _appCandidates.FirstOrDefault(s => CandidateAction(s) == id) is { } service)
                BeginNearbyPairing(service);
            return true;
        }
        if (AndroidConnectionVisible && _nearbyPairing is { } pairing)
        {
            if (id == pairing.MatchAction && pairing.Confirmation is { } answer)
            {
                pairing.Confirmation = null;
                answer.TrySetResult(true);
                _actions.Invalidate();
                return true;
            }
            if (id == pairing.CancelAction)
            {
                CancelViewOperation();
                RefreshData();
                SetStatus(L10n.T("Phone.StatusCancelled"), false);
                return true;
            }
        }
        if (_activeDevice is not { IsApp: true } device) return false;
        if (id == "phone.app.sound")
        {
            if (host.Phone.Operations.AppCapture(device) is { LocalSourceKey: { } package } capture)
            {
                if (HasPhoneAudio(device, package))
                    RunBusy(ct => host.Phone.Sources.RemoveApplicationAsync(device, package).WaitAsync(ct));
                else
                {
                    var app = capture.IsDeviceMedia ? AndroidApp.DeviceAudio : new AndroidApp(package, package, 0, "");
                    RunBusy(ct => host.Phone.Sources.AddAsync(device, app, keepPhoneAudio: true).WaitAsync(ct));
                }
            }
            else if (host.Phone.CanRequestPhoneSharing(device))
                RequestSharing(device, screen: false);
            return true;
        }
        switch (id)
        {
            case "phone.app.screen":
                if (host.Phone.Operations.CanViewSharedScreen(device))
                    RunBusy(ct => host.Phone.Operations.ViewSharedScreenAsync(device, host.Phone.Store.GetDisplayName(device), ct), "Phone.SrapScreenPresented");
                else if (host.Phone.CanRequestPhoneSharing(device))
                    RequestSharing(device, screen: true);
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

    private void RequestSharing(AndroidDevice device, bool screen)
    {
        bool busy = false;
        RunBusy(async ct =>
        {
            try { await _actions.Host.Phone.RequestPhoneSharingAsync(device, screen, ct).ConfigureAwait(false); }
            catch (SrapException error) when (error.Code == "busy") { busy = true; }
        }, completedText: () =>
        {
            _requestedSharing = (device, screen);
            return L10n.T(busy ? "Phone.SharingBusy" : "Phone.WaitPhoneSharing");
        });
    }

    private void RefreshSharingStatus(AndroidDevice? device)
    {
        if (_requestedSharing is not { } request) return;
        var operations = _actions.Host.Phone.Operations;
        if (ReferenceEquals(request.Device, device) && !(request.Screen ? operations.CanViewSharedScreen(request.Device) :
            operations.AppCapture(request.Device)?.LocalSourceKey != null)) return;
        _requestedSharing = null;
        if (_statusMessage == L10n.T("Phone.WaitPhoneSharing") || _statusMessage == L10n.T("Phone.SharingBusy")) _statusMessage = null;
    }

    private void TestAppRoute(AndroidDevice device, AndroidOutputSink target, IReadOnlyList<AndroidOutputSink> outputs)
    {
        if (IsConnectionPage || !ReferenceEquals(_activeDevice, device)) return;
        string routed = "";
        RunBusy(async ct =>
        {
            string actual = await _actions.Host.Phone.Operations.TestRouteAsync(device, target.Key, ct).ConfigureAwait(false);
            var result = outputs.FirstOrDefault(o => o.Key == actual);
            routed = result != null ? PhoneAppPresentation.OutputLabel(result, _actions.Host.Phone.Store.GetDisplayName(device)) : actual;
        }, completedText: () => L10n.T("Phone.SrapRouteVerified") + " " + routed);
    }

    private void BeginNearbyPairing(AndroidMdnsService service)
    {
        var pairing = new NearbyPairing(service);
        _nearbyPairing = pairing;
        _scroll.Offset = 0f;
        RunBusy(ct => _actions.Host.Phone.PairNearbyAppAsync(service, ConfirmCode, ct),
            "Phone.ConnectedStatus", openDevice: true, failed: _ => ClearNearbyPairing(), timeoutSeconds: 130);

        Task<bool> ConfirmCode(string code, CancellationToken stop)
        {
            if (code.Length != 8 || !code.All(char.IsAsciiDigit)) return Task.FromResult(false);
            var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_actions.Host.TryPost(() =>
            {
                if (!AndroidConnectionVisible || !ReferenceEquals(_nearbyPairing, pairing) || stop.IsCancellationRequested || pairing.ComparisonCode != null)
                {
                    reply.TrySetResult(false);
                    return;
                }
                pairing.ComparisonCode = code.Insert(4, " ");
                pairing.Confirmation = reply;
                _actions.Invalidate();
            })) return Task.FromResult(false);
            return reply.Task.WaitAsync(stop);
        }
    }

    private void ClearNearbyPairing()
    {
        var pairing = _nearbyPairing;
        _nearbyPairing = null;
        pairing?.Confirmation?.TrySetResult(false);
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
                    .Where(s => s.App?.Pairing == true && IPEndPoint.TryParse(s.Address, out _))
                    .DistinctBy(s => (s.Name, s.App!.Instance)).Take(16).ToArray();
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
                if (failure != null) SetStatus(PhonePreview.TranslateError(failure), true);
                else _actions.Invalidate();
            })) searching.Dispose();
        }
    }
}
#endif
