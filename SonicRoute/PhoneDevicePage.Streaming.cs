#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SonicRoute.AndroidAudio;
using ComboBox = System.Windows.Controls.ComboBox;

namespace SonicRoute;

public partial class PhoneDevicePage
{
    private sealed record StreamOption(string Label, string Value, bool IsEnabled = true)
    {
        // RoundedComboBox presents SelectionBoxItem directly in its compact template.
        public override string ToString() => Label;
    }

    private AndroidAudioSettingsStore? _streamStore;
    private AndroidDevice? _streamDevice;
    private string? _streamKey;
    private string? _streamLanguage;
    private AndroidStreamSettings _streamSettings = new();
    private AndroidEncoderCatalog? _streamEncoders;
    private CancellationTokenSource? _streamOperation;
    private CancellationTokenSource? _encoderOperation;
    private Task? _encoderRead;
    private bool _encoderFailed;
    private Task? _windowsHevcRead;
    private bool? _windowsHevcAvailable;
    private bool _streamApplying;
    private bool UseHevc => _streamSettings.VideoDecoder == AndroidStreamSettings.WindowsHevcDecoder;

    private void RefreshStreamingSettings(AndroidDevice? device)
    {
        string? previousSerial = _streamDevice?.Serial;
        _streamDevice = device;
        string? key = device == null ? null : AndroidDeviceIdentity.Key(device);
        if (key == _streamKey && previousSerial == device?.Serial && AudioEncoderCombo.ItemsSource != null
            && (device == null || _streamOperation != null))
        {
            if (_streamLanguage != L10n.CurrentLanguage) ApplyStreamingSettings();
            UpdateStreamingVisibility();
            return;
        }
        CancelStreamingWork();
        _streamKey = key;
        _streamEncoders = null;
        _encoderFailed = false;
        _streamSettings = device == null ? new() : _streamStore?.GetStreaming(device) ?? new();
        ApplyStreamingSettings();
        StreamingCard.IsEnabled = device != null && _streamStore != null;
        if (device == null) return;
        _streamOperation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = LoadStreamingSettingsAsync(device, key!, _streamOperation.Token);
    }

    private async Task LoadStreamingSettingsAsync(AndroidDevice device, string key, CancellationToken token)
    {
        try
        {
            var store = await AndroidAudioHost.PrepareStreamingAsync(token);
            if (_disposed || token.IsCancellationRequested || key != _streamKey) return;
            _streamStore = store;
            _streamSettings = store.GetStreaming(device);
            ApplyStreamingSettings();
            StreamingCard.IsEnabled = true;
            UpdateStreamingVisibility();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed && !token.IsCancellationRequested) SetOperationStatusError(ex); }
    }

    private static (string? VendorKey, AndroidEncoderKind FallbackKind) ClassifyEncoder(string name)
    {
        if (Starts(name, "c2.qti.", "c2.qcom.", "omx.qcom.")) return ("Phone.StreamVendorQualcomm", AndroidEncoderKind.Hardware);
        if (Starts(name, "c2.mtk.", "omx.mtk.")) return ("Phone.StreamVendorMediaTek", AndroidEncoderKind.Hardware);
        if (Starts(name, "c2.exynos.", "omx.exynos.", "c2.samsung.", "omx.sec.")) return ("Phone.StreamVendorSamsung", AndroidEncoderKind.Hardware);
        if (Starts(name, "c2.hisi.", "omx.hisi.", "c2.kirin.")) return ("Phone.StreamVendorHiSilicon", AndroidEncoderKind.Hardware);
        if (Starts(name, "c2.unisoc.", "c2.sprd.", "omx.sprd.")) return ("Phone.StreamVendorUnisoc", AndroidEncoderKind.Hardware);
        if (Starts(name, "c2.rk.", "omx.rk.")) return ("Phone.StreamVendorRockchip", AndroidEncoderKind.Hardware);
        if (Starts(name, "c2.amlogic.", "omx.amlogic.")) return ("Phone.StreamVendorAmlogic", AndroidEncoderKind.Hardware);
        if (Starts(name, "c2.google.", "omx.google.")) return ("Phone.StreamVendorGoogle", AndroidEncoderKind.Software);
        if (Starts(name, "c2.android.", "omx.android.")) return ("Phone.StreamVendorAndroid", AndroidEncoderKind.Software);
        return (null, AndroidEncoderKind.Unknown);

        static bool Starts(string value, params string[] prefixes) =>
            prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    internal static string FormatEncoderLabel(string name, AndroidEncoderKind reportedKind = AndroidEncoderKind.Unknown)
    {
        var (vendorKey, fallbackKind) = ClassifyEncoder(name);
        var effectiveKind = reportedKind != AndroidEncoderKind.Unknown ? reportedKind : fallbackKind;
        string kindText = effectiveKind switch
        {
            AndroidEncoderKind.Hardware => L10n.T("Phone.StreamEncoderHardware"),
            AndroidEncoderKind.Software => L10n.T("Phone.StreamEncoderSoftware"),
            _ => L10n.T("Phone.StreamEncoderCustom")
        };
        if (name.Contains(".hevc", StringComparison.OrdinalIgnoreCase) || name.Contains(".h265", StringComparison.OrdinalIgnoreCase))
        {
            string format = name.EndsWith(".cq", StringComparison.OrdinalIgnoreCase) ? L10n.T("Phone.StreamEncoderHevcCq")
                : name.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase) ? L10n.T("Phone.StreamEncoderHevcHdr") : "H.265";
            kindText = format + " · " + kindText;
        }
        return vendorKey != null && effectiveKind != AndroidEncoderKind.Unknown
            ? string.Format(L10n.T("Phone.StreamEncoderVendorFormat"), L10n.T(vendorKey), kindText, name)
            : string.Format(L10n.T("Phone.StreamEncoderFormat"), kindText, name);
    }

    private void EncoderOptions(ComboBox combo, IReadOnlyList<string>? names,
        IReadOnlyDictionary<string, AndroidEncoderKind>? kinds, string? selected)
    {
        var choices = new List<StreamOption> { new(L10n.T("Phone.StreamAutomatic"), "") };
        var entries = (names ?? Array.Empty<string>())
            .Concat(selected == null ? Array.Empty<string>() : new[] { selected })
            .Distinct(StringComparer.Ordinal)
            .Select(name =>
            {
                var reported = kinds != null && kinds.TryGetValue(name, out var kind) ? kind : AndroidEncoderKind.Unknown;
                var (_, fallbackKind) = ClassifyEncoder(name);
                var effective = reported != AndroidEncoderKind.Unknown ? reported : fallbackKind;
                int rank = effective switch { AndroidEncoderKind.Hardware => 0, AndroidEncoderKind.Software => 1, _ => 2 };
                return (Name: name, Label: FormatEncoderLabel(name, reported), Rank: rank);
            })
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Name, StringComparer.Ordinal);
        foreach (var item in entries) choices.Add(new(item.Label, item.Name));
        if (_streamEncoders == null && (_encoderOperation != null || _encoderFailed))
            choices.Add(new(L10n.T(_encoderFailed ? "Phone.StreamEncoderRetry" : "Phone.StreamEncoderLoading"), "#status", false));
        combo.SelectedValuePath = nameof(StreamOption.Value);
        combo.ItemsSource = choices.ToArray();
        combo.SelectedValue = selected ?? "";
        UpdateCodecToolTip(combo, "Phone.StreamEncoder");
    }

    private static void UpdateCodecToolTip(ComboBox combo, string key) => combo.ToolTip =
        combo.SelectedItem is StreamOption option && !string.IsNullOrEmpty(option.Value)
            ? option.Label
            : L10n.T(key);

    private void DecoderOptions(ComboBox combo, bool video, string? selected)
    {
        const string windowsName = "Microsoft H.264 Video Decoder MFT";
        var choices = new List<StreamOption> { new(L10n.T("Phone.StreamDecoderAutomatic"), "") };
        if (video)
        {
            choices.Add(new(string.Format(L10n.T("Phone.StreamDecoderAccelerated"), windowsName + " / D3D11"), AndroidStreamSettings.WindowsVideoDecoder));
            choices.Add(new(string.Format(L10n.T("Phone.StreamDecoderSoftware"), windowsName + " / CPU"), AndroidStreamSettings.SoftwareVideoDecoder));
            bool deviceLacksHevc = _streamEncoders?.Hevc is { Count: 0 };
            if (_windowsHevcAvailable.HasValue || selected == AndroidStreamSettings.WindowsHevcDecoder)
            {
                string label = _windowsHevcAvailable == false ? L10n.T("Phone.StreamDecoderHevcWindowsUnavailable")
                    : deviceLacksHevc ? L10n.T("Phone.StreamDecoderHevcAndroidUnavailable")
                    : string.Format(L10n.T("Phone.StreamDecoderHevc"), "Microsoft HEVC Video Decoder MFT");
                choices.Add(new(label, AndroidStreamSettings.WindowsHevcDecoder, _windowsHevcAvailable == true && !deviceLacksHevc));
            }
        }
        else choices.Add(new(string.Format(L10n.T("Phone.StreamDecoderManaged"), "Concentus Opus"), AndroidStreamSettings.ConcentusDecoder));
        combo.SelectedValuePath = nameof(StreamOption.Value);
        combo.ItemsSource = choices.ToArray();
        combo.SelectedValue = selected != null && choices.Any(option => option.Value == selected) ? selected : "";
        UpdateCodecToolTip(combo, "Phone.StreamDecoder");
    }

    private static void NumericOptions(ComboBox combo, int[] presets, int selected, int divisor, string unit)
    {
        combo.DisplayMemberPath = nameof(StreamOption.Label);
        combo.SelectedValuePath = nameof(StreamOption.Value);
        combo.ItemsSource = presets.Append(selected).Distinct().Order().Select(value => new StreamOption(
            (value / (double)divisor).ToString("0.##", CultureInfo.InvariantCulture) + unit,
            value.ToString(CultureInfo.InvariantCulture))).ToArray();
        combo.SelectedValue = selected.ToString(CultureInfo.InvariantCulture);
    }

    private void ApplyStreamingSettings()
    {
        _streamApplying = true;
        try
        {
            _streamLanguage = L10n.CurrentLanguage;
            EncoderOptions(AudioEncoderCombo, _streamEncoders?.Audio, _streamEncoders?.Kinds, _streamSettings.AudioEncoder);
            ApplyVideoEncoderOptions();
            DecoderOptions(AudioDecoderCombo, false, _streamSettings.AudioDecoder);
            DecoderOptions(VideoDecoderCombo, true, _streamSettings.VideoDecoder);
            NumericOptions(AudioBitRateCombo, [64000, 96000, 128000, 192000, 256000, 320000], _streamSettings.AudioBitRate, 1000, " Kbps");
            NumericOptions(VideoBitRateCombo, [1000000, 4000000, 6000000, 8000000, 12000000, 16000000, 24000000, 32000000, 50000000],
                _streamSettings.VideoBitRate, 1000000, " Mbps");
            NumericOptions(VideoFpsCombo, [15, 24, 30, 60, 120], _streamSettings.MaxFps, 1, " fps");
        }
        finally { _streamApplying = false; }
    }

    private void StreamEncoder_DropDownOpened(object? sender, EventArgs e)
    {
        _encoderFailed = false;
        UpdateStreamingVisibility();
    }

    private void StreamingVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateStreamingVisibility();

    private void UpdateStreamingVisibility()
    {
        if (StreamingCard == null) return; // IsVisibleChanged can run inside InitializeComponent.
        if (_disposed || !_active || !IsLoaded || !IsVisible || !StreamingCard.IsVisible
            || _streamOperation == null)
        { CancelEncoderRead(); return; }
        if (_streamStore == null) return;
        _windowsHevcRead ??= LoadWindowsHevcAsync();
        if (_device == null) { CancelEncoderRead(); return; }
        if (_streamEncoders != null || _encoderOperation != null || _encoderFailed) return;
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_streamOperation.Token);
        _encoderOperation = operation;
        RefreshEncoderOptions();
        _encoderRead = LoadStreamEncodersAsync(_device, operation);
    }

    private async Task LoadWindowsHevcAsync()
    {
        _windowsHevcAvailable = await Task.Run(() => MediaFoundationH264Decoder.SupportsHevc);
        if (!_disposed && _streamOperation != null) RefreshEncoderOptions();
    }

    private void RefreshEncoderOptions()
    {
        _streamApplying = true;
        try
        {
            EncoderOptions(AudioEncoderCombo, _streamEncoders?.Audio, _streamEncoders?.Kinds, _streamSettings.AudioEncoder);
            ApplyVideoEncoderOptions();
            DecoderOptions(VideoDecoderCombo, true, _streamSettings.VideoDecoder);
        }
        finally { _streamApplying = false; }
    }

    private void ApplyVideoEncoderOptions()
        => EncoderOptions(VideoEncoderCombo, UseHevc ? _streamEncoders?.Hevc : _streamEncoders?.Video,
            _streamEncoders?.Kinds, UseHevc ? _streamSettings.HevcEncoder : _streamSettings.VideoEncoder);

    private async Task LoadStreamEncodersAsync(AndroidDevice device, CancellationTokenSource operation)
    {
        CancellationToken token = operation.Token;
        await Task.Yield();
        try
        {
            // Runtime extraction, verification and initial transport work never block the UI.
            var encoders = await Task.Run(() => _readStreamEncoders(device, token), token);
            if (_disposed || token.IsCancellationRequested || !ReferenceEquals(_encoderOperation, operation)
                || _streamKey != AndroidDeviceIdentity.Key(device)) return;
            _streamEncoders = encoders;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed && !token.IsCancellationRequested && ReferenceEquals(_encoderOperation, operation))
            { _encoderFailed = true; SetOperationStatusError(ex); }
        }
        finally
        {
            if (ReferenceEquals(_encoderOperation, operation))
            {
                _encoderOperation = null;
                _encoderRead = null;
                if (!_disposed) RefreshEncoderOptions();
            }
            operation.Dispose();
        }
    }

    private void StreamSettings_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_streamApplying || _disposed || _streamStore == null || _streamDevice == null) return;
        if (VideoDecoderCombo.SelectedItem is StreamOption { IsEnabled: false } unavailable
            && unavailable.Value != _streamSettings.VideoDecoder)
        {
            ApplyStreamingSettings();
            return;
        }
        UpdateCodecToolTip(AudioEncoderCombo, "Phone.StreamEncoder");
        UpdateCodecToolTip(VideoEncoderCombo, "Phone.StreamEncoder");
        UpdateCodecToolTip(AudioDecoderCombo, "Phone.StreamDecoder");
        UpdateCodecToolTip(VideoDecoderCombo, "Phone.StreamDecoder");
        if (AudioEncoderCombo.SelectedValue is not string audioEncoder || VideoEncoderCombo.SelectedValue is not string videoEncoder
            || AudioBitRateCombo.SelectedValue is not string audioRate || VideoBitRateCombo.SelectedValue is not string videoRate
            || VideoFpsCombo.SelectedValue is not string fps || AudioDecoderCombo.SelectedValue is not string audioDecoder
            || VideoDecoderCombo.SelectedValue is not string videoDecoder) return;
        bool wasHevc = UseHevc;
        _streamSettings = (_streamSettings with
        {
            AudioEncoder = NullIfAutomatic(audioEncoder), AudioDecoder = NullIfAutomatic(audioDecoder),
            AudioBitRate = int.Parse(audioRate, CultureInfo.InvariantCulture),
            VideoBitRate = int.Parse(videoRate, CultureInfo.InvariantCulture), MaxFps = int.Parse(fps, CultureInfo.InvariantCulture),
            VideoEncoder = wasHevc ? _streamSettings.VideoEncoder : NullIfAutomatic(videoEncoder),
            HevcEncoder = wasHevc ? NullIfAutomatic(videoEncoder) : _streamSettings.HevcEncoder,
            VideoDecoder = NullIfAutomatic(videoDecoder)
        }).Normalize();
        _streamStore.RememberStreaming(_streamDevice, _streamSettings);
        if (wasHevc != UseHevc)
        {
            _streamApplying = true;
            try { ApplyVideoEncoderOptions(); }
            finally { _streamApplying = false; }
        }
    }

    private static string? NullIfAutomatic(string value) => value.Length == 0 ? null : value;

    private void CancelStreamingWork()
    {
        CancelEncoderRead();
        _streamOperation?.Cancel();
        _streamOperation?.Dispose();
        _streamOperation = null;
    }

    private void CancelEncoderRead()
    {
        var operation = _encoderOperation;
        _encoderOperation = null;
        _encoderRead = null;
        operation?.Cancel(); // The owning async operation disposes its token after cleanup.
    }
}
#endif
