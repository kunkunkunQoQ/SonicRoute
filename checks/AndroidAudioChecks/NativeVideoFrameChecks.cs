using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static async Task CheckNativeVideoFragmentsAsync()
    {
        Guid id = Guid.NewGuid();
        byte[] large = new byte[LinkConnection.LegacyMaxVideoFrame + 1];
        foreach (bool native in new[] { false, true })
        {
            using var tls = new SslStream(new MemoryStream());
            using var socket = new TcpClient();
            var hello = new LinkHello("Test peer", 0, LinkCapabilities.WindowReceive, true, [], native);
            var connection = new LinkConnection(socket, new LinkSecure(tls, "fixture", hello, true), false,
                IPAddress.Loopback, CancellationToken.None);
            try
            {
                connection.SendVideo(id, large, 0, true);
                connection.SendVideo(id, [1], 1, false);
                Check(connection.VideoBacklogged == native, native
                    ? "a native-capable receiver accepts keyframes larger than the legacy 1 MiB limit"
                    : "a legacy receiver skips the oversized frame and its dependent delta frames");
                if (!native)
                {
                    connection.SendVideo(id, [1], 2, true);
                    connection.SendVideo(id, [2], 3, false);
                    Check(connection.VideoBacklogged, "a legacy receiver resumes at the next valid keyframe");
                }
            }
            finally { connection.Abort(); }
        }

        var media = new LinkMedia(id, "fixture", LinkMediaKind.Window, "Fragment test", true, 1, false, null, false);
        await using var viewer = new LinkVideoViewer(media, null, _ => { }, frameLimit: LinkConnection.MaxVideoFrame);
        var field = typeof(LinkVideoViewer).GetField("_assembly", BindingFlags.NonPublic | BindingFlags.Instance)!;
        byte[]? Buffer() => (byte[]?)field.GetValue(viewer);
        var piece = new byte[9 + LinkConnection.VideoChunk];
        BinaryPrimitives.WriteInt64LittleEndian(piece.AsSpan(1), 1);
        piece[0] = 1;
        piece.AsSpan(9).Fill(0xa3);
        viewer.Push(piece.AsSpan(0, 73));
        Check(Buffer() is { Length: < 1024 }, "a small frame starts with a small pooled buffer");
        piece[0] = 0;
        piece.AsSpan(9).Fill(0x59);
        int received = 64;
        while (received <= LinkConnection.LegacyMaxVideoFrame)
        {
            viewer.Push(piece);
            received += LinkConnection.VideoChunk;
        }
        var bytes = Buffer()!;
        Check(bytes.Length >= received && bytes[0] == 0xa3 && bytes[63] == 0xa3
            && bytes[64] == 0x59 && bytes[received - 1] == 0x59,
            "high-resolution video fragments keep their bytes when the pooled buffer grows past 1 MiB");
        piece[0] = 2;
        viewer.Push(piece);
        Check(Buffer() == null, "a discarded pre-keyframe releases its assembly buffer");
        piece[0] = 1;
        viewer.Push(piece);
        piece[0] = 0;
        for (int total = LinkConnection.VideoChunk; total + LinkConnection.VideoChunk <= LinkConnection.MaxVideoFrame;
            total += LinkConnection.VideoChunk) viewer.Push(piece);
        bool rejected = false;
        try { viewer.Push(piece); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "an oversized fragmented frame is rejected before unbounded buffering");
    }
}
