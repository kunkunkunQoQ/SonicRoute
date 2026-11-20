namespace SonicRoute.AndroidAudio;

internal static class LocalProcessAudioCapture
{
    internal static async Task CaptureAsync(int processId, Action<ReadOnlyMemory<byte>> onPcm, Action onStarted, CancellationToken token)
    {
        using var destination = new PcmWriter(onPcm);
        await PcProcessLoopbackCapture.RunMtaCaptureLoopAsync(processId, destination, token,
            static () => { }, onStarted: onStarted).ConfigureAwait(false);
    }

    private sealed class PcmWriter(Action<ReadOnlyMemory<byte>> output) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => output(buffer.AsMemory(offset, count));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
