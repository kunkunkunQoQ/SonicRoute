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
    private sealed record StreamOption(string Label, string Value)
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
    private Task? _encoderRead;
    private bool _streamApplying;

    private void RefreshStreamingSettings(AndroidDevice? device)
    {
        _streamDevice = device;
        string? key = device == null ? null : AndroidDeviceIdentity.Key(device);
        if (key == _streamKey && AudioEncoderCombo.ItemsSource != null && (device == null || _streamOperation != null))
        {
            if (_streamLanguage != L10n.CurrentLanguage) ApplyStreamingSettings();
            return;
        }
        CancelStreamingWork();
        _streamKey = key;
        _streamEncoders = null;
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
        return vendorKey != null && effectiveKind != AndroidEncoderKind.Unknown
            ? string.Format(L10n.T("Phone.StreamEncoderVendorFormat"), L10n.T(vendorKey), kindText, name)
            : string.Format(L10n.T("Phone.StreamEncoderFormat"), kindText, name);
    }

    private static void EncoderOptions(ComboBox combo, IReadOnlyList<string>? names,
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
        combo.SelectedValuePath = nameof(StreamOption.Value);
        combo.ItemsSource = choices.ToArray();
        combo.SelectedValue = selected ?? "";
        UpdateEncoderToolTip(combo);
    }

    private static void UpdateEncoderToolTip(ComboBox combo) => combo.ToolTip =
        combo.SelectedItem is StreamOption option && !string.IsNullOrEmpty(option.Value)
            ? option.Label
            : L10n.T("Phone.StreamEncoder");

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
            EncoderOptions(VideoEncoderCombo, _streamEncoders?.Video, _streamEncoders?.Kinds, _streamSettings.VideoEncoder);
            NumericOptions(AudioBitRateCombo, [64000, 96000, 128000, 192000, 256000, 320000], _streamSettings.AudioBitRate, 1000, " Kbps");
            NumericOptions(VideoBitRateCombo, [1000000, 4000000, 6000000, 8000000, 12000000, 16000000, 24000000, 32000000, 50000000],
                _streamSettings.VideoBitRate, 1000000, " Mbps");
            NumericOptions(VideoFpsCombo, [15, 24, 30, 60, 120], _streamSettings.MaxFps, 1, " fps");
        }
        finally { _streamApplying = false; }
    }

    private async void StreamEncoder_DropDownOpened(object? sender, EventArgs e)
    {
        if (_disposed || !_active || _streamOperation == null || _device == null || _streamEncoders != null) return;
        if (_encoderRead == null || _encoderRead.IsCompleted)
            _encoderRead = LoadStreamEncodersAsync(_device, _streamOperation.Token);
        await _encoderRead;
    }

    private async Task LoadStreamEncodersAsync(AndroidDevice device, CancellationToken token)
    {
        try
        {
            var encoders = await AndroidAudioHost.Adb.EncodersAsync(device, token);
            if (_disposed || token.IsCancellationRequested || _streamKey != AndroidDeviceIdentity.Key(device)) return;
            _streamEncoders = encoders;
            _streamApplying = true;
            try
            {
                EncoderOptions(AudioEncoderCombo, encoders.Audio, encoders.Kinds, _streamSettings.AudioEncoder);
                EncoderOptions(VideoEncoderCombo, encoders.Video, encoders.Kinds, _streamSettings.VideoEncoder);
            }
            finally { _streamApplying = false; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed && !token.IsCancellationRequested) SetOperationStatusError(ex); }
    }

    private void StreamSettings_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_streamApplying || _disposed || _streamStore == null || _streamDevice == null) return;
        UpdateEncoderToolTip(AudioEncoderCombo);
        UpdateEncoderToolTip(VideoEncoderCombo);
        if (AudioEncoderCombo.SelectedValue is not string audioEncoder || VideoEncoderCombo.SelectedValue is not string videoEncoder
            || AudioBitRateCombo.SelectedValue is not string audioRate || VideoBitRateCombo.SelectedValue is not string videoRate
            || VideoFpsCombo.SelectedValue is not string fps) return;
        _streamSettings = new AndroidStreamSettings(string.IsNullOrEmpty(audioEncoder) ? null : audioEncoder,
            int.Parse(audioRate, CultureInfo.InvariantCulture), string.IsNullOrEmpty(videoEncoder) ? null : videoEncoder,
            int.Parse(videoRate, CultureInfo.InvariantCulture), int.Parse(fps, CultureInfo.InvariantCulture)).Normalize();
        _streamStore.RememberStreaming(_streamDevice, _streamSettings);
    }

    private void CancelStreamingWork()
    {
        _streamOperation?.Cancel();
        _streamOperation?.Dispose();
        _streamOperation = null;
        _encoderRead = null;
    }
}
#endif
