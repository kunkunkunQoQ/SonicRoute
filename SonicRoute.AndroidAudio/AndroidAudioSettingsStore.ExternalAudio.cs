using SonicRoute.AirPlay;

namespace SonicRoute.AndroidAudio;

internal sealed record ExternalAudioPreference(string Key, string Name, AndroidApp? App,
    int Volume, bool Muted, string?[] Outputs, bool Visible = true, bool Resume = true)
{
    internal const string AirPlayPrefix = "airplay-source:";
    internal static string? AndroidKey(AndroidDevice device, string package) =>
        AndroidDeviceIdentity.StableKey(device) is { } key ? key + "\n" + package : null;
    internal static string AirPlayKey(string? senderId, string name) =>
        string.IsNullOrWhiteSpace(senderId) ? AirPlayPrefix + "name:" + name : AirPlayPrefix + "id:" + senderId.ToUpperInvariant();
    internal string? DeviceKey => App == null ? null : Key[..Key.IndexOf('\n')];
}

internal sealed record AirPlayReceiverPreference(bool Enabled, string Id);

public sealed partial class AndroidAudioSettingsStore
{
    // The array order is the card order. An update replaces a slot and keeps its position.
    private ExternalAudioPreference[] _externalAudio = [];
    private AirPlayReceiverPreference? _airPlayReceiver;
    internal IReadOnlyList<ExternalAudioPreference> ExternalAudio => Volatile.Read(ref _externalAudio);
    internal bool AirPlayEnabled { get { lock (_gate) return _airPlayReceiver?.Enabled == true; } }
    internal string AirPlayId
    {
        get
        {
            lock (_gate)
            {
                if (_airPlayReceiver != null) return _airPlayReceiver.Id;
                _airPlayReceiver = new(false, AirPlayIdentity.CreateId());
                QueueExternalSave();
                return _airPlayReceiver.Id;
            }
        }
    }

    internal void SetAirPlayEnabled(bool enabled)
    {
        lock (_gate)
        {
            if ((_airPlayReceiver?.Enabled ?? false) == enabled) return;
            _airPlayReceiver = new(enabled, _airPlayReceiver?.Id ?? AirPlayIdentity.CreateId());
            QueueExternalSave();
        }
    }

    internal ExternalAudioPreference? FindExternalAudio(string? key) => key == null ? null
        : Array.Find(Volatile.Read(ref _externalAudio), p => p.Key == key);

    internal void RememberExternalAudio(ExternalAudioPreference choice)
    {
        lock (_gate)
        {
            int index = Array.FindIndex(_externalAudio, p => p.Key == choice.Key);
            if (index >= 0)
            {
                var previous = _externalAudio[index];
                if (previous == choice || previous with { Outputs = choice.Outputs } == choice
                    && previous.Outputs.SequenceEqual(choice.Outputs, StringComparer.OrdinalIgnoreCase)) return;
                var next = (ExternalAudioPreference[])_externalAudio.Clone();
                next[index] = choice;
                Volatile.Write(ref _externalAudio, next);
            }
            else Volatile.Write(ref _externalAudio, [.. _externalAudio, choice]);
            QueueExternalSave();
        }
    }

    internal void StopExternalAudio(string? key, bool keepCard = false)
        => UpdateExternalAudio(key, choice => choice with { Visible = keepCard, Resume = false });

    internal void UpdateExternalAudio(string? key, Func<ExternalAudioPreference, ExternalAudioPreference> update)
    {
        lock (_gate)
        {
            if (FindExternalAudio(key) is { } choice)
                RememberExternalAudio(update(choice));
        }
    }

    internal void RetireExternalPhone(AndroidDevice device, bool removed)
    {
        if (AndroidDeviceIdentity.StableKey(device) is not { } key) return;
        lock (_gate)
        {
            foreach (var choice in _externalAudio)
            {
                if (choice.DeviceKey == key)
                    RememberExternalAudio(choice with { Visible = choice.Visible && !removed, Resume = false });
                else if (removed)
                {
                    var remaining = choice.Outputs.Where(id => !AndroidOutputIdentity.TryParseDeviceId(id, out var phone, out _)
                        || phone != key).ToArray();
                    if (remaining.Length != choice.Outputs.Length)
                        RememberExternalAudio(choice with { Outputs = remaining.Length == 0 ? [null] : remaining });
                }
            }
        }
    }

    private void LoadExternalAudio(SettingsFile saved)
    {
        _airPlayReceiver = saved.AirPlayReceiver is { Id: { Length: 12 } id } && id.All(char.IsAsciiHexDigit)
            ? saved.AirPlayReceiver : null;
        _externalAudio = (saved.ExternalAudio ?? []).Where(choice => choice != null && choice.Name is { Length: <= 1024 }
            && choice.Outputs is { Length: > 0 and <= 128 } && choice.Outputs.All(id => id == null || id.Length <= 1024)
            && (choice.App is { } app
                ? choice.Key is { Length: <= 1024 } key && key.StartsWith("device:", StringComparison.Ordinal)
                    && key.IndexOf('\n') > "device:".Length && key[(key.IndexOf('\n') + 1)..] == app.Package
                    && (app.IconPng?.Length ?? 0) <= 131072
                : choice.Key is { Length: <= 128 } airPlay && airPlay.StartsWith(ExternalAudioPreference.AirPlayPrefix, StringComparison.Ordinal)))
            .DistinctBy(choice => choice.Key).Select(choice => choice with { Volume = Math.Clamp(choice.Volume, 0, 100) }).ToArray();
    }

    private void QueueExternalSave()
    {
        ++_revision;
        if (!_writing) { _writing = true; _save = Task.Run(SaveAsync); }
    }
}
