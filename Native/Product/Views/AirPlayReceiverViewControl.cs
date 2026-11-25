#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Threading;
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

    private bool _attached;
    private bool _hevcChecked;
    private bool _hevcAvailable;
    private byte[]? _lastArtworkPng;
    private byte[]? _artworkBgra;
    private int _artW, _artH, _artVer, _artGen;
    private readonly string _idPrefix;

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
        Interlocked.Increment(ref _artGen);
        _lastArtworkPng = null;
        _artworkBgra = null;
        _artW = 0;
        _artH = 0;
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

        float h = 38f;
        if (!running && !changing)
        {
            h += 14f;
            bool twoCols = width >= 330f;
            h += twoCols ? 52f : 105f;
            h += 34f;
        }

        string? statusMsg = GetStatusMessage(status);
        if (!string.IsNullOrWhiteSpace(statusMsg))
        {
            float lineCapacity = MathF.Max(14f, width / 6.5f);
            int lines = Math.Max(1, (int)MathF.Ceiling(statusMsg.Length / lineCapacity));
            h += MathF.Max(24f, lines * 16f + 8f);
        }
        if (status.Code is { Length: > 0 }) h += 46f;
        if (status.Stage == AirPlayStage.Waiting)
        {
            string steps = L10n.T("Phone.AirPlaySteps");
            float lineCapacity = MathF.Max(16f, width / 6f);
            int lines = Math.Max(2, (int)MathF.Ceiling(steps.Length / lineCapacity));
            h += MathF.Max(36f, lines * 15f + 6f);
        }

        var track = status.Stage == AirPlayStage.Connected ? status.Metadata : null;
        if (track != null && (track.Title.Length > 0 || track.Artist.Length > 0 || track.Artwork != null))
            h += 48f;

        if (status.Mirroring) h += 70f;
        if (_host.AirPlay.Source != null) h += 36f;

        return h + 4f;
    }

    internal void Render(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect bounds, bool showHeaderStatus = true)
    {
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

            string currentName = string.IsNullOrWhiteSpace(_config.AirPlayReceiverName) ? AirPlayPreview.DefaultName : _config.AirPlayReceiverName;
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
                UiPrimitives.DrawTextBox(g, t, hit, nameEditTag, nameRect, currentName, AirPlayPreview.DefaultName, focused: nameFocused, fontSize: 12f);

                var decRect = new UiRect(x + colW + 10f, y, colW, 30f);
                UiPrimitives.DrawComboBox(g, t, hit, $"{_idPrefix}.decoder", decRect, decoderLabel, fontSize: 12f);
                y += 34f;
            }
            else
            {
                g.DrawText(L10n.T("Phone.AirPlayName"), new UiRect(x, y, w, 16f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
                y += 18f;
                UiPrimitives.DrawTextBox(g, t, hit, nameEditTag, new UiRect(x, y, w, 30f), currentName, AirPlayPreview.DefaultName, focused: nameFocused, fontSize: 12f);
                y += 35f;

                g.DrawText(L10n.T("Phone.StreamDecoder"), new UiRect(x, y, w, 16f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
                y += 18f;
                UiPrimitives.DrawComboBox(g, t, hit, $"{_idPrefix}.decoder", new UiRect(x, y, w, 30f), decoderLabel, fontSize: 12f);
                y += 34f;
            }

            g.DrawText(L10n.T("Phone.AirPlayRequireCode"), new UiRect(x, y, MathF.Max(20f, w - 42f), 28f),
                t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center, wrap: true, ellipsis: false);
            UiPrimitives.DrawSwitch(g, t, hit, $"{_idPrefix}.pin", new UiRect(x + w - 36f, y + 2f, 36f, 24f), _config.AirPlayRequireCode);
            y += 34f;
        }

        string? statusMsg = GetStatusMessage(status);
        if (!string.IsNullOrWhiteSpace(statusMsg))
        {
            var sz = D2dSharedContext.Instance.MeasureText(statusMsg, UiMetrics.FontFamily, 12f, 400, w, 120f, wrap: true);
            float msgH = MathF.Max(20f, sz.Height + 2f);
            var statusRect = new UiRect(x, y, w, msgH);
            if (status.NetworkError is int netErr)
                hit.Add($"{_idPrefix}.status", statusRect, UiCursorKind.Arrow, $"Winsock {netErr}");
            g.DrawText(statusMsg, statusRect, status.ErrorKey != null ? t.Fail : t.TextSecondary,
                12f, 400, UiTextAlign.Leading, UiVertAlign.Center, wrap: true, ellipsis: false);
            y += msgH + 4f;
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
            string steps = L10n.T("Phone.AirPlaySteps");
            var sz = D2dSharedContext.Instance.MeasureText(steps, UiMetrics.FontFamily, 10.5f, 400, w, 100f, wrap: true);
            float stepsH = MathF.Max(32f, sz.Height + 2f);
            g.DrawText(steps, new UiRect(x, y, w, stepsH),
                t.TextSecondary, 10.5f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true, ellipsis: false);
            y += stepsH + 4f;
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
            if (stage is AirPlayStage.Starting or AirPlayStage.Stopping) return true;
            bool running = stage is AirPlayStage.Waiting or AirPlayStage.Connected;
            _closeInlineEdit(true);
            _ = _host.AirPlay.SetReceivingAsync(_config, !running);
            return true;
        }

        if (hitId == $"{_idPrefix}.name")
        {
            string current = string.IsNullOrWhiteSpace(_config.AirPlayReceiverName) ? AirPlayPreview.DefaultName : _config.AirPlayReceiverName;
            _startInlineEdit($"{_idPrefix}.name", hitBounds, current, null, (text, _) =>
            {
                string norm = AirPlayIdentity.NormalizeName(text);
                if (norm == AirPlayPreview.DefaultName) norm = "";
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
            _lastArtworkPng = null;
            _artworkBgra = null;
            bgra = Array.Empty<byte>();
            w = h = ver = 0;
            return false;
        }

        if (ReferenceEquals(png, _lastArtworkPng))
        {
            if (_artworkBgra != null)
            {
                bgra = _artworkBgra;
                w = _artW;
                h = _artH;
                ver = _artVer;
                return true;
            }
            bgra = Array.Empty<byte>();
            w = h = ver = 0;
            return false;
        }

        _lastArtworkPng = png;
        _artworkBgra = null;
        int gen = Interlocked.Increment(ref _artGen);
        _ = Task.Run(() =>
        {
            if (!SonicRoute.Native.Graphics.WicIconLoader.TryDecodePngToBgra32(png, 72, 72, out byte[] pixels, out int pw, out int ph))
                return;
            PremultiplyBgra32(pixels);
            _host.TryPost(() =>
            {
                if (_attached && Volatile.Read(ref _artGen) == gen && ReferenceEquals(_lastArtworkPng, png))
                {
                    _artworkBgra = pixels;
                    _artW = pw;
                    _artH = ph;
                    _artVer++;
                    _invalidate();
                }
            });
        });

        bgra = Array.Empty<byte>();
        w = h = ver = 0;
        return false;
    }

    private static void PremultiplyBgra32(byte[] pixels)
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
    }

    internal static string GetStageLabel(AirPlayStatus status) => L10n.T(status.Stage switch
    {
        AirPlayStage.Starting => "Phone.AirPlayStarting",
        AirPlayStage.Stopping => "Phone.AirPlayStopping",
        AirPlayStage.Failed => "Phone.AirPlayFailed",
        AirPlayStage.Waiting => "Phone.AirPlayWaitingShort",
        AirPlayStage.Connected => "Phone.ConnectedStatus",
        _ => "Phone.AirPlayOff",
    });

    internal static string? GetStatusMessage(AirPlayStatus status)
    {
        if (status.ErrorKey != null)
        {
            string msg = L10n.T(status.ErrorKey);
            return status.NetworkError is int code ? $"{msg} (Winsock {code})" : msg;
        }
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
}
#endif
