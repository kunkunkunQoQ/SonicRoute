using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task CheckSocketGenerationAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        AdbClient? adb = null;
        try
        {
            int port = 0;
            AdbSocketClient socket = null!;
            AndroidDevice previous = null!;
            await WithSocketGenerationServerAsync(0, "hardware-A", async assignedPort =>
            {
                port = assignedPort;
                socket = new AdbSocketClient(port);
                adb = new AdbClient(AppContext.BaseDirectory, socket);
                previous = (await adb.DevicesAsync(token)).Single();
                Check(previous.ConnectionTarget != null && previous.HardwareId == "hardware-A" && previous.IdentityFingerprint != null,
                    "the first daemon supplies a pinned transport and verified hardware identity");
            }, token);

            string requests = await WithSocketGenerationServerAsync(port, "hardware-B", async _ =>
            {
                // Exercise old work before another discovery could invalidate anything.
                async Task<bool> Rejected(Func<Task> command)
                {
                    try { await command(); return false; }
                    catch (IOException) { return true; }
                }
                Check(await Rejected(() => socket.RunShellAsync(previous.CommandTarget, "old-session", token)),
                    "a stale shell is rejected before rediscovery on the replacement daemon");
                Check(await Rejected(() => adb!.CaptureAliasAttachmentAsync(previous, token)),
                    "an alias proof cannot select a recycled transport id from a replacement daemon");
                Check(await Rejected(() => socket.RemoveForwardAsync(previous.CommandTarget, "tcp:34001", token)),
                    "old forward cleanup is rejected before rediscovery on the replacement daemon");
                var current = (await adb!.DevicesAsync(token)).Single();
                Check(current.Serial == previous.Serial && current.TransportId == previous.TransportId
                    && current.HardwareId == "hardware-B" && current.IdentityFingerprint != null
                    && current.IdentityFingerprint != previous.IdentityFingerprint
                    && current.ConnectionTarget != null && current.ConnectionTarget != previous.ConnectionTarget,
                    "a replacement daemon reusing both the serial and transport ID receives a new selector and fresh hardware identity");
                var shell = await socket.RunShellAsync(current.CommandTarget, "new-session", token);
                Check(shell.ExitCode == 0 && shell.Output == "hardware-B", "the replacement selector can run its own shell");
                await socket.RemoveForwardAsync(current.CommandTarget, "tcp:34002", token);
            }, token);
            Check(!requests.Contains("old-session") && !requests.Contains("killforward:tcp:34001")
                && requests.Contains("new-session") && requests.Contains("killforward:tcp:34002"),
                "the replacement server received only the new shell and forward cleanup commands");
        }
        finally { adb?.Dispose(); }
    }

    private static async Task<string> WithSocketGenerationServerAsync(int port, string hardware,
        Func<int, Task> checks, CancellationToken token)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--socket-generation-server");
        start.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(hardware);
        using var child = Process.Start(start) ?? throw new IOException("Unable to start the private fixture daemon.");
        try
        {
            var ready = await child.StandardOutput.ReadLineAsync(token);
            if (!int.TryParse(ready, out int assigned) || assigned < 1024 || assigned == 5037 || port != 0 && port != assigned)
                throw new IOException("The private fixture daemon did not report its requested port.");
            var transcript = child.StandardOutput.ReadToEndAsync(token);
            await checks(assigned);
            child.StandardInput.Close();
            await child.WaitForExitAsync(token);
            if (child.ExitCode != 0) throw new IOException("The private fixture daemon failed.");
            return await transcript;
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                await child.WaitForExitAsync();
            }
        }
    }

    private static async Task RunSocketGenerationServerAsync(int port, string hardware)
    {
        if (!Console.IsInputRedirected || port is not (0 or >= 1024 and <= 65535) || port == 5037
            || hardware is not ("hardware-A" or "hardware-B")) throw new ArgumentException("Invalid private fixture arguments.");
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Console.WriteLine(((IPEndPoint)listener.LocalEndpoint).Port);
        var serve = ServeSocketGenerationAsync(listener, hardware, stop.Token);
        // The parent closes its pipe on completion or exit.
        await Task.WhenAny(serve, Task.Run(Console.In.ReadLine));
        stop.Cancel();
        try { await serve; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private static async Task ServeSocketGenerationAsync(TcpListener listener, string hardware, CancellationToken token)
    {
        while (true)
        {
            using var connection = await listener.AcceptTcpClientAsync(token);
            var stream = connection.GetStream();
            async Task<string> RequestAsync()
            {
                var header = new byte[4];
                await stream.ReadExactlyAsync(header, token);
                int size = int.Parse(Encoding.ASCII.GetString(header), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                var payload = new byte[size];
                await stream.ReadExactlyAsync(payload, token);
                string request = Encoding.UTF8.GetString(payload);
                Console.WriteLine(request);
                return request;
            }
            Task WriteTextAsync(string text) => stream.WriteAsync(Encoding.UTF8.GetBytes(text), token).AsTask();
            try
            {
                switch (await RequestAsync())
                {
                    case "host:devices-l":
                        const string list = "same-serial\tdevice model:Fake_Phone transport_id:7\n";
                        await WriteTextAsync($"OKAY{list.Length:X4}{list}");
                        break;
                    case "host:transport-id:7":
                        await WriteTextAsync("OKAY");
                        string shell = await RequestAsync();
                        string output = shell switch
                        {
                            "shell,v2,raw:getprop ro.serialno" or "shell,v2,raw:new-session" or "shell,v2,raw:old-session" => hardware,
                            "shell,v2,raw:settings get secure android_id" => hardware == "hardware-A" ? "aaaaaaaaaaaaaaaa" : "bbbbbbbbbbbbbbbb",
                            _ => throw new InvalidDataException("Unexpected fixture shell: " + shell)
                        };
                        await WriteTextAsync("OKAY");
                        byte[] frame = new byte[5 + output.Length];
                        frame[0] = 1; // shell v2 stdout
                        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), output.Length);
                        Encoding.ASCII.GetBytes(output, frame.AsSpan(5));
                        await stream.WriteAsync(frame, token);
                        await stream.WriteAsync(new byte[] { 3, 1, 0, 0, 0, 0 }, token); // exit 0
                        break;
                    case "host-transport-id:7:killforward:tcp:34001" or "host-transport-id:7:killforward:tcp:34002":
                        await WriteTextAsync("OKAYOKAY");
                        break;
                    default:
                        throw new InvalidDataException("Unexpected command to fixture daemon.");
                }
            }
            catch (EndOfStreamException) { } // A rejected selector closes its connection without sending any request.
        }
    }
}
