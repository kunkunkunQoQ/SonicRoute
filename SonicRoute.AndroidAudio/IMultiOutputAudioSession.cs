namespace SonicRoute.AndroidAudio;

internal interface IMultiOutputAudioSession
{
    string?[] GetOutputs();
    Task SetOutputsAsync(IReadOnlyList<string?> outputs);
}
