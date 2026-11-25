using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SonicRoute.Core
{
    public static class RuleCommandFrame
    {
        public const int MaxFrameBytes = 16 * 1024;

        public static async Task WriteFrameAsync(Stream stream, string text, CancellationToken token)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            if (body.Length == 0 || body.Length > MaxFrameBytes) throw new InvalidDataException();
            byte[] header = BitConverter.GetBytes(body.Length);
            await stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        public static async Task<string> ReadFrameAsync(Stream stream, CancellationToken token)
        {
            byte[] header = new byte[4];
            await ReadFullAsync(stream, header, token).ConfigureAwait(false);
            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > MaxFrameBytes) throw new InvalidDataException();
            byte[] body = new byte[length];
            await ReadFullAsync(stream, body, token).ConfigureAwait(false);
            return new UTF8Encoding(false, true).GetString(body);
        }

        private static async Task ReadFullAsync(Stream stream, byte[] buffer, CancellationToken token)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
        }
    }
}
