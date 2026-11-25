#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Native.Graphics;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

/// <summary>
/// Reusable Direct2D AirPlay receiver sub-view embedded in PhoneDevicePageView and PhoneAudioWindowView.
/// </summary>
internal sealed class AirPlayReceiverViewControl
{
    private readonly ApplicationHost _host;
    private readonly AppConfig _config;
    private readonly Action _invalidate;
    private readonly Action<UiRect, IReadOnlyList<ComboChoice>, string, Action<ComboChoice>> _showCombo;
    private readonly Action<string, UiRect, string, Action<string>?, Action<string, bool>> _startInlineEdit;
    private readonly Func<string?> _getActiveInlineTag;
    private readonly Action<bool> _closeInlineEdit;
    private readonly UiHitMap _ownHit = new();

    private bool _attached;
    private bool _hevcChecked;
    private bool _hevcAvailable;
    private byte[]? _lastArtworkPng;
    private byte[]? _artworkBgra;
    private int _artW, _artH, _artVer;
    private UiHitMap? _lastHit;
    private string _idPrefix = "airplay";

    internal AirPlayReceiverViewControl(IViewHostActions actions, string idPrefix = "airplay")
        : this(
            actions.Host,
            actions.Config,
            actions.Invalidate,
            actions.ShowCombo,
            (tag, rect, text, onChanged, onCommit) => actions.StartInlineEdit(tag, rect, text, onChanged, onCommit, mono: false, password: false, selectAll: true),
            () => actions.ActiveInlineEditTag,
            actions.CloseInlineEdit,
            idPrefix)
    {
    }

    internal AirPlayReceiverViewControl(
        ApplicationHost host,
        AppConfig config,
        Action invalidate,
        Action<UiRect, IReadOnlyList<ComboChoice>, string, Action<ComboChoice>> showCombo,
        Action<string, UiRect, string, Action<string>?, Action<string, bool>> startInlineEdit,
        Func<string?> getActiveInlineTag,
        Action<bool> closeInlineEdit,
        string idPrefix = "airplay")
    {
        _host = host;
        _config = config;
        _invalidate = invalidate;
        _showCombo = showCombo;
        _startInlineEdit = startInlineEdit;
        _getActiveInlineTag = getActiveInlineTag;
        _closeInlineEdit = closeInlineEdit;
        _idPrefix = idPrefix;
    }

    internal void Attach()
    {
        if (_attached) return;
        _attached = true;
        _host.AirPlay.Changed += OnAirPlayChanged;
        if (!_hevcChecked)
        {
            _hevcChecked = true;
            _ = Task.Run(() =>
            {
                bool hevc = MediaFoundationH264Decoder.SupportsHevc;
                _host.TryPost(() =>
                {
                    _hevcAvailable = hevc;
                    if (_attached) _invalidate();
                });
            });
        }
    }

    internal void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _host.AirPlay.Changed -= OnAirPlayChanged;
    }

    private void OnAirPlayChanged()
    {
        _host.TryPost(() =>
        {
            if (_attached) _invalidate();
        });
    }

    internal float MeasureHeight(float width, bool showHeaderStatus = true)
    {
        var status = _host.AirPlay.Status;
        bool running = status.Stage is AirPlayStage.Waiting or AirPlayStage.Connected;
        bool changing = status.Stage is AirPlayStage.Starting or AirPlayStage.Stopping;

        float h = 38f; // Header row
        if (!running && !changing)
        {
            h += 12f; // border/pad
            bool twoCols = width >= 330f;
            h += twoCols ? 50f : 104f; // Name + Decoder
            h += 38f; // Require PIN row
        }

        string? statusMsg = GetStatusMessage(status);
        if (!string.IsNullOrWhiteSpace(statusMsg)) h += 24f;
        if (status.Code is { Length: > 0 }) h += 46f;
        if (status.Stage == AirPlayStage.Waiting) h += 36f;

        var track = status.Stage == AirPlayStage.Connected ? status.Metadata : null;
        if (track != null && (track.Title.Length > 0 || track.Artist.Length > 0 || track.Artwork != null))
            h += 50f;

        if (status.Mirroring) h += 72f;
        if (_host.AirPlay.Source != null) h += 38f;

        return h + 4f;
    }

    internal void Render(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect bounds, bool showHeaderStatus = true)
    {
        _lastHit = hit;
        var status = _host.AirPlay.Status;
        bool running = status.Stage is AirPlayStage.Waiting or AirPlayStage.Connected;
        bool changing = status.Stage is AirPlayStage.Starting or AirPlayStage.Stopping;

        float y = bounds.Y;
        float x = bounds.X;
        float w = bounds.Width;

        // Header row
        var iconBox = new UiRect(x, y + 2f, 32f, 32f);
        g.FillRoundedRect(iconBox, 9f, t.NavSelected);
        g.DrawIcon(UiIcon.AirPlay, new UiRect(iconBox.X + 7f, iconBox.Y + 7f, 18f, 18f), t.TextPrimary, 1.8f);

        var swRect = new UiRect(x + w - 38f, y + 4f, 38f, 28f);
        UiPrimitives.DrawSwitch(g, t, hit, $"{_idPrefix}.toggle", swRect, running || status.Stage == AirPlayStage.Starting, enabled: !changing);

        float textX = iconBox.Right + 10f;
        float textW = MathF.Max(40f, swRect.X - 8f - textX);
        if (showHeaderStatus)
        {
            g.DrawText(L10n.T("Phone.AirPlayReceive"), new UiRect(textX, y, textW, 20f),
                t.TextPrimary, 13.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);

            UiColor dotCol = status.ErrorKey != null ? t.Fail : status.Stage switch
            {
                AirPlayStage.Connected => t.Success,
                AirPlayStage.Waiting or AirPlayStage.Starting => t.Accent,
                _ => t.TextSecondary,
            };
            float dotY = y + 27f;
            g.FillEllipse(textX + 3f, dotY, 3f, 3f, dotCol);
            string stageText = GetStageLabel(status);
            if (_host.AirPlay.Port > 0 && status.Stage is AirPlayStage.Waiting or AirPlayStage.Connected)
                stageText += $" · TCP {_host.AirPlay.Port}";
            g.DrawText(stageText, new UiRect(textX + 10f, y + 19f, MathF.Max(10f, textW - 10f), 16f),
                t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        }
        else
        {
            g.DrawText(L10n.T("Phone.AirPlayReceive"), new UiRect(textX, y, textW, 36f),
                t.TextPrimary, 13.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        }
        y += 38f;

        // Preferences (shown when off or failed)
        if (!running && !changing)
        {
            y += 6f;
            g.DrawLine(x, y, x + w, y, t.Border, 1f, roundCaps: false);
            y += 8f;

            string currentName = string.IsNullOrWhiteSpace(_config.AirPlayReceiverName) ? DefaultReceiverName() : _config.AirPlayReceiverName;
            string nameEditTag = $"{_idPrefix}.name";
            bool nameFocused = _getActiveInlineTag() == nameEditTag;
            string decoderLabel = GetDecoderLabel(_config.AirPlayVideoDecoder);

            if (w >= 330f)
            {
                float colW = (w - 10f) * 0.5f;
                g.DrawText(L10n.T("Phone.AirPlayName"), new UiRect(x, y, colW, 16f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
                g.DrawText(L10n.T("Phone.StreamDecoder"), new UiRect(x + colW + 10f, y, colW, 16f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
                y += 18f;

                var nameRect = new UiRect(x, y, colW, 30f);
                UiPrimitives.DrawTextBox(g, t, hit, nameEditTag, nameRect, currentName, DefaultReceiverName(), focused: nameFocused, fontSize: 12f);

                var decRect = new UiRect(x + colW + 10f, y, colW, 30f);
                UiPrimitives.DrawComboBox(g, t, hit, $"{_idPrefix}.decoder", decRect, decoderLabel, fontSize: 12f);
                y += 34f;
            }
            else
            {
                g.DrawText(L10n.T("Phone.AirPlayName"), new UiRect(x, y, w, 16f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
                y += 18f;
                UiPrimitives.DrawTextBox(g, t, hit, nameEditTag, new UiRect(x, y, w, 30f), currentName, DefaultReceiverName(), focused: nameFocused, fontSize: 12f);
                y += 35f;

                g.DrawText(L10n.T("Phone.StreamDecoder"), new UiRect(x, y, w, 16f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
                y += 18f;
                UiPrimitives.DrawComboBox(g, t, hit, $"{_idPrefix}.decoder", new UiRect(x, y, w, 30f), decoderLabel, fontSize: 12f);
                y += 34f;
            }

            var pinRow = new UiRect(x, y, w, 28f);
            g.DrawText(L10n.T("Phone.AirPlayRequireCode"), new UiRect(x, y, MathF.Max(20f, w - 42f), 28f),
                t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center, wrap: true, ellipsis: false);
            UiPrimitives.DrawSwitch(g, t, hit, $"{_idPrefix}.pin", new UiRect(x + w - 36f, y + 2f, 36f, 24f), _config.AirPlayRequireCode);
            y += 34f;
        }

        string? statusMsg = GetStatusMessage(status);
        if (!string.IsNullOrWhiteSpace(statusMsg))
        {
            g.DrawText(statusMsg, new UiRect(x, y, w, 20f), status.ErrorKey != null ? t.Fail : t.TextSecondary,
                12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            y += 24f;
        }

        if (status.Code is { Length: > 0 } code)
        {
            var codeBox = new UiRect(x, y, w, 40f);
            g.FillRoundedRect(codeBox, 8f, t.NavSelected);
            g.DrawText(L10n.T("Phone.AirPlayEnterCode"), new UiRect(codeBox.X + 10f, codeBox.Y, MathF.Max(20f, codeBox.Width - 110f), codeBox.Height),
                t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center, wrap: true);
            g.DrawText(code, new UiRect(codeBox.Right - 96f, codeBox.Y, 86f, codeBox.Height),
                t.TextPrimary, 19f, 600, UiTextAlign.Trailing, UiVertAlign.Center, fontFamily: UiMetrics.MonoFontFamily);
            y += 46f;
        }

        if (status.Stage == AirPlayStage.Waiting)
        {
            g.DrawText(L10n.T("Phone.AirPlaySteps"), new UiRect(x, y, w, 32f),
                t.TextSecondary, 10.5f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true, ellipsis: false);
            y += 36f;
        }

        var track = status.Stage == AirPlayStage.Connected ? status.Metadata : null;
        if (track != null && (track.Title.Length > 0 || track.Artist.Length > 0 || track.Artwork != null))
        {
            var artBox = new UiRect(x, y + 4f, 36f, 36f);
            g.FillRoundedRect(artBox, 6f, t.SurfaceBg);
            g.DrawRoundedRect(artBox, 6f, t.Border, 1f);
            if (TryGetArtwork(track.Artwork, out var bgra, out int aw, out int ah, out int ver))
            {
                g.DrawBgraBitmap($"{_idPrefix}.art", bgra, aw, ah, artBox.Deflate(1f), version: ver);
            }
            else
            {
                g.DrawText("♫", artBox, t.TextSecondary, 16f, 400, UiTextAlign.Center, UiVertAlign.Center);
            }
            string title = track.Title.Length > 0 ? track.Title : (status.Sender ?? "");
            g.DrawText(title, new UiRect(artBox.Right + 8f, y + 4f, MathF.Max(20f, w - 44f), 18f),
                t.TextPrimary, 12f, 600, UiTextAlign.Leading, UiVertAlign.Center);
            g.DrawText(track.Artist, new UiRect(artBox.Right + 8f, y + 22f, MathF.Max(20f, w - 44f), 16f),
                t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            y += 48f;
        }

        if (status.Mirroring)
        {
            UiPrimitives.DrawSecondaryButton(g, t, hit, $"{_idPrefix}.showmirror", new UiRect(x, y, w, 30f),
                L10n.T("Phone.AirPlayShowScreen"), fontSize: 11f);
            y += 34f;
            float halfW = (w - 4f) * 0.5f;
            UiPrimitives.DrawSecondaryButton(g, t, hit, $"{_idPrefix}.topmost", new UiRect(x, y, halfW, 30f),
                L10n.T(status.Topmost ? "Phone.AirPlayUnpinWindow" : "Phone.AirPlayPinWindow"), fontSize: 11f, active: status.Topmost);
            UiPrimitives.DrawSecondaryButton(g, t, hit, $"{_idPrefix}.fullscreen", new UiRect(x + halfW + 4f, y, halfW, 30f),
                L10n.T(status.Fullscreen ? "Phone.AirPlayExitFullscreen" : "Phone.AirPlayFullscreen"), fontSize: 11f, active: status.Fullscreen);
            y += 36f;
        }

        if (_host.AirPlay.Source != null)
        {
            UiPrimitives.DrawSecondaryButton(g, t, hit, $"{_idPrefix}.disconnect", new UiRect(x, y, w, 30f),
                L10n.T("Phone.Disconnect"), fontSize: 11f, danger: true);
        }
    }

    internal bool HandleClick(string hitId, UiRect hitBounds)
    {
        if (hitId == $"{_idPrefix}.toggle")
        {
            var stage = _host.AirPlay.Status.Stage;
            bool running = stage is AirPlayStage.Waiting or AirPlayStage.Connected or AirPlayStage.Starting;
            _closeInlineEdit(true);
            _ = _host.AirPlay.SetReceivingAsync(_config, !running);
            return true;
        }

        if (hitId == $"{_idPrefix}.name")
        {
            string current = string.IsNullOrWhiteSpace(_config.AirPlayReceiverName) ? DefaultReceiverName() : _config.AirPlayReceiverName;
            _startInlineEdit($"{_idPrefix}.name", hitBounds, current, null, (text, commit) =>
            {
                if (!commit) return;
                string norm = AirPlayIdentity.NormalizeName(text);
                if (norm == DefaultReceiverName()) norm = "";
                _config.AirPlayReceiverName = norm;
                ConfigService.TrySave(_config);
                _invalidate();
            });
            return true;
        }

        if (hitId == $"{_idPrefix}.decoder")
        {
            var choices = new List<ComboChoice>
            {
                new("0", L10n.T("Phone.StreamDecoderAutomatic")),
                new("1", "H.264 (AVC)"),
            };
            if (_hevcAvailable)
            {
                choices.Add(new("2", L10n.T("Phone.AirPlayHevcOption")));
            }
            _showCombo(hitBounds, choices, _config.AirPlayVideoDecoder.ToString(), picked =>
            {
                if (int.TryParse(picked.Key, out int val))
                {
                    _config.AirPlayVideoDecoder = val;
                    ConfigService.TrySave(_config);
                    _invalidate();
                }
            });
            return true;
        }

        if (hitId == $"{_idPrefix}.pin" || hitId == $"{_idPrefix}.pin:sw")
        {
            _config.AirPlayRequireCode = !_config.AirPlayRequireCode;
            ConfigService.TrySave(_config);
            _invalidate();
            return true;
        }

        if (hitId == $"{_idPrefix}.showmirror")
        {
            _host.AirPlay.ShowMirror();
            return true;
        }

        if (hitId == $"{_idPrefix}.topmost")
        {
            _host.AirPlay.SetMirrorTopmost(!_host.AirPlay.Status.Topmost);
            return true;
        }

        if (hitId == $"{_idPrefix}.fullscreen")
        {
            _host.AirPlay.SetMirrorFullscreen(!_host.AirPlay.Status.Fullscreen);
            return true;
        }

        if (hitId == $"{_idPrefix}.disconnect")
        {
            _host.AirPlay.Disconnect();
            return true;
        }

        return false;
    }

    private bool TryGetArtwork(byte[]? png, out byte[] bgra, out int w, out int h, out int ver)
    {
        if (png is null || png.Length == 0)
        {
            bgra = Array.Empty<byte>(); w = h = ver = 0;
            return false;
        }
        if (ReferenceEquals(png, _lastArtworkPng) && _artworkBgra != null)
        {
            bgra = _artworkBgra; w = _artW; h = _artH; ver = _artVer;
            return true;
        }
        _lastArtworkPng = png;
        if (SonicRoute.Native.Graphics.WicIconLoader.TryDecodePngToBgra32(png, 72, 72, out byte[] pixels, out int pw, out int ph))
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte a = pixels[i + 3];
                if (a == 255) continue;
                if (a == 0) { pixels[i] = pixels[i + 1] = pixels[i + 2] = 0; continue; }
                pixels[i] = (byte)(pixels[i] * a / 255);
                pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
                pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
            }
            _artworkBgra = pixels;
            _artW = pw;
            _artH = ph;
            _artVer++;
            bgra = pixels; w = pw; h = ph; ver = _artVer;
            return true;
        }
        _artworkBgra = null;
        bgra = Array.Empty<byte>(); w = h = ver = 0;
        return false;
    }

    private static string GetStageLabel(AirPlayStatus status) => L10n.T(status.Stage switch
    {
        AirPlayStage.Starting => "Phone.AirPlayStarting",
        AirPlayStage.Stopping => "Phone.AirPlayStopping",
        AirPlayStage.Failed => "Phone.AirPlayFailed",
        AirPlayStage.Waiting => "Phone.AirPlayWaitingShort",
        AirPlayStage.Connected => "Phone.ConnectedStatus",
        _ => "Phone.AirPlayOff",
    });

    private static string? GetStatusMessage(AirPlayStatus status)
    {
        if (status.ErrorKey != null) return L10n.T(status.ErrorKey);
        return status.Stage switch
        {
            AirPlayStage.Waiting => status.Name,
            AirPlayStage.Connected => status.Mirroring
                ? string.Format(L10n.T(status.MirrorAudioUnavailable ? "Phone.AirPlayMirrorPictureOnly" : "Phone.AirPlayMirrorActive"), status.Sender)
                : status.Sender,
            _ => null,
        };
    }

    private static string GetDecoderLabel(int decoder) => decoder switch
    {
        1 => "H.264 (AVC)",
        2 => L10n.T("Phone.AirPlayHevcOption"),
        _ => L10n.T("Phone.StreamDecoderAutomatic"),
    };

    private static string DefaultReceiverName()
    {
        string host = "";
        foreach (char c in Environment.MachineName)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '-')
            {
                host += c;
                if (host.Length >= 32) break;
            }
        }
        return host.Length == 0 ? "SonicRoute" : "SonicRoute - " + host;
    }
}
#endif
