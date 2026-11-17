using System;
using System.IO;
using System.Threading;

namespace SonicRoute
{
    /// <summary>
    /// Read-only input for eager WPF decoding. BitmapImage retains StreamSource even
    /// after OnLoad; detach the backing stream on Dispose so it cannot retain the PNG.
    /// </summary>
    internal sealed class ImageDataStream : Stream
    {
        private MemoryStream? _source;

        internal ImageDataStream(byte[] bytes) => _source = new MemoryStream(bytes, writable: false);
        private MemoryStream Source => Volatile.Read(ref _source)
            ?? throw new ObjectDisposedException(nameof(ImageDataStream));

        public override bool CanRead => Volatile.Read(ref _source)?.CanRead == true;
        public override bool CanSeek => Volatile.Read(ref _source)?.CanSeek == true;
        public override bool CanWrite => false;
        public override long Length => Source.Length;
        public override long Position { get => Source.Position; set => Source.Position = value; }
        public override void Flush() => Source.Flush();
        public override int Read(byte[] buffer, int offset, int count) => Source.Read(buffer, offset, count);
        public override int ReadByte() => Source.ReadByte();
        public override long Seek(long offset, SeekOrigin origin) => Source.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            var source = Interlocked.Exchange(ref _source, null);
            if (disposing) source?.Dispose();
            base.Dispose(disposing);
        }
    }
}
