namespace SonicRoute.AndroidAudio;

internal interface IMultiOutputAudioSession
{
    string?[] GetOutputs();
    Task SetOutputsAsync(IReadOnlyList<string?> outputs);
    (int VolumePercent, bool Muted) GetMirrorGain(string id) => (100, false);
    (int VolumePercent, bool Muted) SetMirrorGain(string id, int? volumePercent = null, bool? muted = null, bool toggleMute = false) => (100, false);
}
