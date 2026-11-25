using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SonicRoute.AndroidAudio;

internal static class FileTransferProbe
{
    internal static async Task RunAsync(string root)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new AdbSocketClient((IPEndPoint)listener.LocalEndpoint);
        await using var adb = new AdbClient(root, client);
        var transfer = new AndroidFileTransfer(adb, "probe-phone");
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeData = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] payload = "SonicRoute file transfer / 跨窗口"u8.ToArray();
        var serving = ServeAsync(listener, requestReceived, writeData, payload, deadline.Token);
        var jobs = new AndroidFileTransfers();
        const string profile = "probe-persistent-profile";
        Program.Require(jobs.TryStart(profile, transfer, ["hello.txt"], root, "/sdcard/Download", () => true, deadline.Token, out Task job), "accepted file job");
        await requestReceived.Task.WaitAsync(deadline.Token);
        Program.Require(!jobs.TryStart(profile, transfer, ["same.txt"], root, "/sdcard/Download", () => true, deadline.Token, out _), "duplicate rejected");
        using var view = new CancellationTokenSource();
        var viewWait = job.WaitAsync(view.Token);
        view.Cancel();
        try { await viewWait; throw new InvalidOperationException("Observer continued after closure"); }
        catch (OperationCanceledException) when (view.IsCancellationRequested) { }
        Program.Require(!job.IsCompleted && jobs.Get(profile)?.Phase == AndroidFilePhase.Running, "reopened view sees same device-owned work");
        writeData.SetResult();
        await job.WaitAsync(deadline.Token);
        await serving;
        var saved = jobs.Get(profile);
        Program.Require(saved?.Phase == AndroidFilePhase.Saved, "terminal transfer state: " + saved?.Text);
        string path = saved!.Text;
        Program.Require(SHA256.HashData(await File.ReadAllBytesAsync(path)).SequenceEqual(SHA256.HashData(payload)), "ADB SYNC receive SHA256");
        foreach (var cause in new[] { "user", "device", "shutdown" })
        {
            var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var device = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            using var peerToken = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var peer = ServeAsync(listener, inFlight, release, payload, peerToken.Token);
            Program.Require(jobs.TryStart(profile, transfer, ["hello.txt"], root, "/sdcard/Download", () => true, device.Token, out var interrupted), "cancel fixture accepted");
            await inFlight.Task.WaitAsync(deadline.Token);
            if (cause == "shutdown") await jobs.CloseAsync().WaitAsync(deadline.Token);
            else if (cause == "device") device.Cancel();
            else jobs.Cancel(profile);
            await interrupted.WaitAsync(deadline.Token);
            Program.Require(jobs.Get(profile)?.Phase == AndroidFilePhase.Cancelled, cause + " cancellation");
            peerToken.Cancel();
            try { await peer; } catch (OperationCanceledException) when (peerToken.IsCancellationRequested) { }
        }
        Program.Require(!jobs.TryStart(profile, transfer, ["hello.txt"], root, "/sdcard/Download", () => true, deadline.Token, out _), "shutdown rejects new work");
        Program.Require(!Directory.EnumerateFiles(root, ".sonicroute-*.part").Any(), "Staging file released");
    }

    internal static async Task ServeAsync(TcpListener listener, TaskCompletionSource received, TaskCompletionSource release,
        byte[] payload, CancellationToken token)
    {
        using var peer = await listener.AcceptTcpClientAsync(token);
        using var stream = peer.GetStream();
        foreach (string request in new[] { "host:transport:probe-phone", "sync:" })
        {
            byte[] asciiLength = new byte[4];
            await stream.ReadExactlyAsync(asciiLength, token);
            int length = int.Parse(Encoding.ASCII.GetString(asciiLength), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte[] incoming = new byte[length];
            await stream.ReadExactlyAsync(incoming, token);
            Program.Require(Encoding.ASCII.GetString(incoming) == request, request);
            await stream.WriteAsync("OKAY"u8.ToArray(), token);
        }
        byte[] header = new byte[8];
        await stream.ReadExactlyAsync(header, token);
        Program.Require(header.AsSpan(0,4).SequenceEqual("RECV"u8), "RECV tag");
        byte[] remote = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4))];
        await stream.ReadExactlyAsync(remote, token);
        Program.Require(Encoding.UTF8.GetString(remote) == "/sdcard/Download/hello.txt", "RECV path");
        received.SetResult();
        await release.Task.WaitAsync(token);
        "DATA"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), payload.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(payload, token);
        "DONE"u8.CopyTo(header);
        Array.Clear(header, 4, 4);
        await stream.WriteAsync(header, token);
    }
}

