namespace SonicRoute.AndroidAudio;

internal interface IMultiOutputAudioSession
{
    string?[] GetOutputs();
    Task SetOutputsAsync(IReadOnlyList<string?> outputs);
}

internal interface IFailedPcmPlayer
{
    event Action<Exception>? Failed;
}
