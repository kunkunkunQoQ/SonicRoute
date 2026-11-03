using SonicRoute.Core.Interop;

internal sealed class FakeAppVolumeSession : ISimpleAudioVolume
{
    internal bool Muted;
    internal int MuteReads, MuteWrites;

    public int GetMasterVolume(out float value) { value = .42f; return 0; }
    public int GetMute(out int value) { MuteReads++; value = Muted ? 1 : 0; return 0; }
    public int SetMasterVolume(float value, ref Guid context) => throw new InvalidOperationException("Unexpected volume write");
    public int SetMute(int value, ref Guid context) { MuteWrites++; Muted = value != 0; return 0; }
}
