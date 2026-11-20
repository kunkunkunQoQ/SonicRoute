using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal sealed class FakeAdbServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly Queue<string> _trackUpdates = new();
    private readonly SemaphoreSlim _trackSignal = new(0);
    private readonly ConcurrentDictionary<string, (string Target, TaskCompletionSource Primed, NetworkStream Shell)> _audio = new();
    private readonly Dictionary<string, List<byte>> _bySink = new();
    public Func<string, Task>? BeforeRouteReport { get; set; }
    public bool RejectBluetooth { get; set; }
    internal byte[] ReceivedFor(string sink) { lock (_gate) return _bySink.GetValueOrDefault(sink)?.ToArray() ?? []; }
    internal void DropSink(string sink)
    {
        foreach (var helper in _audio.Values.Where(x => x.Target == sink)) helper.Shell.Dispose();
    }
    private readonly List<byte> _stdinBytes = new();
    private readonly List<byte> _localAbstractBytes = new();
    private readonly object _gate = new();
    private string? _lastRemoteJar;

    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;
    public ConcurrentQueue<string> Requests { get; } = new();
    public ConcurrentQueue<string> Commands { get; } = new();
    public ConcurrentDictionary<string, (int Mode, byte[] Data)> PushedFiles { get; } = new();
    public string? FailNextPair { get; set; }
    public bool ObservedStdinBom { get; private set; }

    public FakeAdbServer()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public void EnqueueTrackUpdate(string text)
    {
        lock (_trackUpdates) _trackUpdates.Enqueue(text);
        _trackSignal.Release();
    }

    public async Task WaitForShellInputAsync(string needle, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            string current;
            lock (_gate) current = Encoding.UTF8.GetString(_stdinBytes.ToArray());
            if (current.Contains(needle, StringComparison.Ordinal)) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Expected shell stdin substring not observed: " + needle);
    }

    public async Task<byte[]> WaitForLocalAbstractBytesAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_localAbstractBytes.Count >= count) return _localAbstractBytes.Take(count).ToArray();
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("Expected localabstract PCM bytes not observed.");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, _cts.Token));
            }
            catch (OperationCanceledException) { client?.Dispose(); break; }
            catch (ObjectDisposedException) { client?.Dispose(); break; }
            catch { client?.Dispose(); }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        try
        {
            string req = await ReadSmartRequestAsync(stream, token).ConfigureAwait(false);
            Requests.Enqueue(req);

            if (req == "host:version")
            {
                await SendOkayPayloadAsync(stream, "0029", token).ConfigureAwait(false);
                return;
            }
            if (req == "host:devices-l")
            {
                await SendOkayPayloadAsync(stream, "fake-usb-1\tdevice model:Fake_Phone transport_id:7\n", token).ConfigureAwait(false);
                return;
            }
            if (req == "host:track-devices-l")
            {
                await stream.WriteAsync("OKAY"u8.ToArray(), token).ConfigureAwait(false);
                await SendLengthPayloadAsync(stream, "fake-usb-1\tdevice model:Fake_Phone transport_id:7\n", token).ConfigureAwait(false);
                while (!token.IsCancellationRequested)
                {
                    await _trackSignal.WaitAsync(token).ConfigureAwait(false);
                    string? next = null;
                    lock (_trackUpdates) if (_trackUpdates.Count > 0) next = _trackUpdates.Dequeue();
                    if (next != null) await SendLengthPayloadAsync(stream, next, token).ConfigureAwait(false);
                }
                return;
            }
            if (req.StartsWith("host:pair:", StringComparison.Ordinal))
            {
                await stream.WriteAsync("OKAY"u8.ToArray(), token).ConfigureAwait(false);
                if (FailNextPair != null)
                {
                    string err = FailNextPair; FailNextPair = null;
                    await stream.WriteAsync("FAIL"u8.ToArray(), token).ConfigureAwait(false);
                    await SendLengthPayloadAsync(stream, err, token).ConfigureAwait(false);
                }
                else
                {
                    await SendLengthPayloadAsync(stream, "Successfully paired to 192.168.1.20:37000 [guid=adb-abc]", token).ConfigureAwait(false);
                }
                return;
            }
            if (req.StartsWith("host:connect:", StringComparison.Ordinal))
            {
                await SendOkayPayloadAsync(stream, "connected to " + req["host:connect:".Length..], token).ConfigureAwait(false);
                return;
            }
            if (req.StartsWith("host:disconnect:", StringComparison.Ordinal))
            {
                await SendOkayPayloadAsync(stream, "disconnected " + req["host:disconnect:".Length..], token).ConfigureAwait(false);
                return;
            }
            if (req == "host:mdns:services")
            {
                await SendOkayPayloadAsync(stream, "adb-abc-123\t_adb-tls-connect._tcp\t192.168.1.20:41234\n", token).ConfigureAwait(false);
                return;
            }
            if (req == "host-serial:fake-usb-1:get-serialno")
            {
                await SendOkayPayloadAsync(stream, "HW-FAKE-01\n", token).ConfigureAwait(false);
                return;
            }
            if (req.StartsWith("host-serial:fake-usb-1:forward:", StringComparison.Ordinal))
            {
                await stream.WriteAsync("OKAYOKAY"u8.ToArray(), token).ConfigureAwait(false);
                await SendLengthPayloadAsync(stream, "44123", token).ConfigureAwait(false);
                return;
            }
            if (req == "host:list-forward")
            {
                await SendOkayPayloadAsync(stream, "fake-usb-1 tcp:44123 localabstract:test_sock\n", token).ConfigureAwait(false);
                return;
            }
            if (req.StartsWith("host-serial:fake-usb-1:killforward:", StringComparison.Ordinal))
            {
                await stream.WriteAsync("OKAYOKAY"u8.ToArray(), token).ConfigureAwait(false);
                return;
            }
            if (req.StartsWith("host:transport:", StringComparison.Ordinal))
            {
                string serial = req["host:transport:".Length..];
                await stream.WriteAsync("OKAY"u8.ToArray(), token).ConfigureAwait(false);
                string service = await ReadSmartRequestAsync(stream, token).ConfigureAwait(false);
                Commands.Enqueue(service);

                if (service == "sync:")
                {
                    await HandleSyncAsync(stream, token).ConfigureAwait(false);
                    return;
                }
                if (service.StartsWith("localabstract:", StringComparison.Ordinal))
                {
                    await stream.WriteAsync("OKAY"u8.ToArray(), token).ConfigureAwait(false);
                    byte[] buf = new byte[4096];
                    int n;
                    while ((n = await stream.ReadAsync(buf, token).ConfigureAwait(false)) > 0)
                    {
                        byte[] packet = buf.AsSpan(0, n).ToArray();
                        lock (_gate)
                        {
                            _localAbstractBytes.AddRange(packet);
                            if (_audio.TryGetValue(service["localabstract:".Length..], out var helper))
                            {
                                if (!_bySink.TryGetValue(helper.Target, out var bytes)) _bySink[helper.Target] = bytes = [];
                                bytes.AddRange(packet);
                                helper.Primed.TrySetResult();
                            }
                        }
                    }
                    return;
                }
                if (serial == "legacy-v1" && service.StartsWith("shell,v2,", StringComparison.Ordinal))
                {
                    await SendFailAsync(stream, "unknown command shell,v2", token).ConfigureAwait(false);
                    return;
                }
                if (serial == "legacy-v1" && service.StartsWith("shell:", StringComparison.Ordinal))
                {
                    await stream.WriteAsync("OKAY"u8.ToArray(), token).ConfigureAwait(false);
                    await stream.WriteAsync("LEGACY-01\n"u8.ToArray(), token).ConfigureAwait(false);
                    return;
                }
                if (service.StartsWith("shell,v2,raw:", StringComparison.Ordinal))
                {
                    string cmd = service["shell,v2,raw:".Length..];
                    await HandleShellV2Async(stream, cmd, token).ConfigureAwait(false);
                    return;
                }
            }
            await SendFailAsync(stream, "unhandled fake request: " + req, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private async Task HandleSyncAsync(NetworkStream stream, CancellationToken token)
    {
        await stream.WriteAsync("OKAY"u8.ToArray(), token).ConfigureAwait(false);
        byte[] hdr = new byte[8];
        string? path = null;
        int mode = 0;
        using var ms = new MemoryStream();
        while (true)
        {
            await ReadExactAsync(stream, hdr, token).ConfigureAwait(false);
            string tag = Encoding.ASCII.GetString(hdr, 0, 4);
            int len = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(4, 4));
            if (tag == "SEND")
            {
                byte[] specBytes = new byte[len];
                await ReadExactAsync(stream, specBytes, token).ConfigureAwait(false);
                string spec = Encoding.UTF8.GetString(specBytes);
                int comma = spec.LastIndexOf(',');
                path = spec[..comma];
                mode = int.Parse(spec[(comma + 1)..], CultureInfo.InvariantCulture);
                _lastRemoteJar = path;
            }
            else if (tag == "DATA")
            {
                byte[] chunk = new byte[len];
                await ReadExactAsync(stream, chunk, token).ConfigureAwait(false);
                ms.Write(chunk);
            }
            else if (tag == "DONE")
            {
                PushedFiles[path!] = (mode, ms.ToArray());
                byte[] okay = new byte[8];
                "OKAY"u8.CopyTo(okay);
                await stream.WriteAsync(okay, token).ConfigureAwait(false);
            }
            else if (tag == "QUIT")
            {
                break;
            }
        }
    }

    private async Task HandleShellV2Async(NetworkStream stream, string cmd, CancellationToken token)
    {
        await stream.WriteAsync("OKAY"u8.ToArray(), token).ConfigureAwait(false);
        if (cmd == "getprop ro.serialno")
        {
            await SendV2PacketAsync(stream, 1, "HW-FAKE-01\n"u8.ToArray(), token).ConfigureAwait(false);
            await SendV2PacketAsync(stream, 3, [0], token).ConfigureAwait(false);
            return;
        }
        if (cmd == "echo_both_and_fail")
        {
            await SendV2PacketAsync(stream, 1, "out-line\n"u8.ToArray(), token).ConfigureAwait(false);
            await SendV2PacketAsync(stream, 2, "err-line\n"u8.ToArray(), token).ConfigureAwait(false);
            await SendV2PacketAsync(stream, 3, [42], token).ConfigureAwait(false);
            return;
        }
        if (cmd.Contains("org.sonicroute.android.Catalog", StringComparison.Ordinal))
        {
            await SendV2PacketAsync(stream, 1, "{\"package\":\"com.example.music\",\"name\":\"Music\",\"uid\":10123,\"category\":1}\n"u8.ToArray(), token).ConfigureAwait(false);
            await SendV2PacketAsync(stream, 3, [0], token).ConfigureAwait(false);
            return;
        }
        if (cmd.Contains("org.sonicroute.android.AudioOutputBridge list", StringComparison.Ordinal))
        {
            await SendV2PacketAsync(stream, 1, "{\"outputs\":[{\"key\":\"speaker\",\"kind\":\"speaker\",\"id\":1,\"type\":2,\"name\":\"Speaker\"},{\"key\":\"bt:aa\",\"kind\":\"bluetooth\",\"id\":5,\"type\":8,\"name\":\"Headphones\"}]}\n"u8.ToArray(), token).ConfigureAwait(false);
            await SendV2PacketAsync(stream, 3, [0], token).ConfigureAwait(false);
            return;
        }
        if (cmd.Contains("org.sonicroute.android.MediaSessionBridge", StringComparison.Ordinal))
        {
            await SendV2PacketAsync(stream, 1, "SR_MEDIA_PID:4567\n{\"package\":\"com.example.music\",\"title\":\"Song\",\"artist\":\"Artist\",\"playing\":true,\"actions\":512}\n"u8.ToArray(), token).ConfigureAwait(false);
            await PumpStdinPacketsAsync(stream, token).ConfigureAwait(false);
            await SendV2PacketAsync(stream, 3, [0], token).ConfigureAwait(false);
            return;
        }
        if (cmd.Contains("org.sonicroute.android.ClipboardBridge", StringComparison.Ordinal))
        {
            await SendV2PacketAsync(stream, 1, "SR_CLIPBOARD_PID:4568\n{\"ready\":true,\"revision\":5}\n{\"revision\":6,\"text\":\"hello-from-phone\"}\n"u8.ToArray(), token).ConfigureAwait(false);
            await PumpStdinPacketsAsync(stream, token).ConfigureAwait(false);
            await SendV2PacketAsync(stream, 3, [0], token).ConfigureAwait(false);
            return;
        }
        if (cmd.Contains("org.sonicroute.android.AudioOutputBridge play", StringComparison.Ordinal))
        {
            string[] arguments = cmd[(cmd.IndexOf("AudioOutputBridge play ", StringComparison.Ordinal) + "AudioOutputBridge play ".Length)..].Split(' ');
            string socket = arguments[0], target = arguments[1].Trim('\'');
            var primed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _audio[socket] = (target, primed, stream);
            try
            {
                await SendV2PacketAsync(stream, 1,
                    "SR_PCOUT_PID:4569\n{\"listening\":true,\"outputs\":[{\"key\":\"speaker\",\"kind\":\"speaker\",\"id\":3,\"type\":2,\"name\":\"Speaker\",\"address\":\"\"},{\"key\":\"bt:aa\",\"kind\":\"bluetooth\",\"id\":5,\"type\":8,\"name\":\"Headphones\",\"address\":\"aa\"}]}\n"u8.ToArray(),
                    token).ConfigureAwait(false);
                await primed.Task.WaitAsync(token).ConfigureAwait(false);
                await SendV2PacketAsync(stream, 1,
                    "{\"routed\":{\"target\":\"speaker\",\"preferredApplied\":false,\"bufferFrames\":4096,\"startThresholdFrames\":480,\"id\":9,\"type\":9,\"name\":\"Wrong\",\"key\":\"other\"}}\n"u8.ToArray(),
                    token).ConfigureAwait(false);
                if (cmd.EndsWith("'bt:unavailable'", StringComparison.Ordinal) || (target == "bt:aa" && RejectBluetooth))
                {
                    await SendV2PacketAsync(stream, 1, "{\"error\":\"Fake Bluetooth output rejected\"}\n"u8.ToArray(), token).ConfigureAwait(false);
                    await SendV2PacketAsync(stream, 3, [1], token).ConfigureAwait(false);
                    return;
                }
                if (BeforeRouteReport != null) await BeforeRouteReport(target).WaitAsync(token).ConfigureAwait(false);
                await SendV2PacketAsync(stream, 1, target == "bt:aa"
                    ? "{\"routed\":{\"target\":\"bt:aa\",\"preferredApplied\":true,\"id\":5,\"type\":8,\"name\":\"Headphones\",\"key\":\"bt:aa\"}}\n"u8.ToArray()
                    : "{\"routed\":{\"target\":\"speaker\",\"preferredApplied\":true,\"bufferFrames\":7688,\"startThresholdFrames\":960,\"id\":3,\"type\":2,\"name\":\"Speaker\",\"key\":\"speaker\"}}\n"u8.ToArray(),
                    token).ConfigureAwait(false);
                await PumpStdinPacketsAsync(stream, token, async line =>
                {
                    if (line == "target bt:aa")
                    {
                        await SendV2PacketAsync(stream, 1,
                            "{\"routed\":{\"target\":\"bt:aa\",\"preferredApplied\":true,\"bufferFrames\":3840,\"startThresholdFrames\":960,\"id\":5,\"type\":8,\"name\":\"Headphones\",\"key\":\"bt:aa\"}}\n"u8.ToArray(),
                            token).ConfigureAwait(false);
                    }
                    else if (line == "target speaker")
                    {
                        await SendV2PacketAsync(stream, 1,
                            "{\"routed\":{\"target\":\"speaker\",\"preferredApplied\":true,\"bufferFrames\":7688,\"startThresholdFrames\":960,\"id\":3,\"type\":2,\"name\":\"Speaker\",\"key\":\"speaker\"}}\n{\"switchSetup\":{\"policyMs\":12.3,\"preferredMs\":4.5,\"releaseMs\":1.2}}\n"u8.ToArray(),
                            token).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
                await SendV2PacketAsync(stream, 3, [0], token).ConfigureAwait(false);
            }
            finally { _audio.TryRemove(socket, out _); }
            return;
        }
        await SendV2PacketAsync(stream, 3, [0], token).ConfigureAwait(false);
    }

    private async Task PumpStdinPacketsAsync(NetworkStream stream, CancellationToken token, Func<string, Task>? onLine = null)
    {
        byte[] hdr = new byte[5];
        var lineBuffer = new StringBuilder();
        while (!token.IsCancellationRequested)
        {
            int r = await ReadUpToAsync(stream, hdr, token).ConfigureAwait(false);
            if (r < 5) break;
            byte id = hdr[0];
            int len = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(1, 4));
            byte[] data = new byte[len];
            if (len > 0) await ReadExactAsync(stream, data, token).ConfigureAwait(false);
            if (id == 0 && len > 0)
            {
                if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) ObservedStdinBom = true;
                lock (_gate) _stdinBytes.AddRange(data);
                if (onLine != null)
                {
                    lineBuffer.Append(Encoding.UTF8.GetString(data));
                    while (true)
                    {
                        string text = lineBuffer.ToString();
                        int nl = text.IndexOf('\n');
                        if (nl < 0) break;
                        string line = text[..nl].TrimEnd('\r');
                        lineBuffer.Remove(0, nl + 1);
                        if (line == "quit") return;
                        await onLine(line).ConfigureAwait(false);
                    }
                }
            }
            else if (id == 4)
            {
                break;
            }
        }
    }

    private static async Task SendV2PacketAsync(NetworkStream stream, byte id, byte[] payload, CancellationToken token)
    {
        byte[] buf = new byte[5 + payload.Length];
        buf[0] = id;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(1, 4), payload.Length);
        payload.CopyTo(buf, 5);
        await stream.WriteAsync(buf, token).ConfigureAwait(false);
    }

    private static async Task<string> ReadSmartRequestAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] lenBuf = new byte[4];
        await ReadExactAsync(stream, lenBuf, token).ConfigureAwait(false);
        int len = int.Parse(Encoding.ASCII.GetString(lenBuf), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte[] payload = new byte[len];
        await ReadExactAsync(stream, payload, token).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    private static async Task SendOkayPayloadAsync(NetworkStream stream, string payload, CancellationToken token)
    {
        await stream.WriteAsync("OKAY"u8.ToArray(), token).ConfigureAwait(false);
        await SendLengthPayloadAsync(stream, payload, token).ConfigureAwait(false);
    }

    private static async Task SendFailAsync(NetworkStream stream, string message, CancellationToken token)
    {
        await stream.WriteAsync("FAIL"u8.ToArray(), token).ConfigureAwait(false);
        await SendLengthPayloadAsync(stream, message, token).ConfigureAwait(false);
    }

    private static async Task SendLengthPayloadAsync(NetworkStream stream, string payload, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        byte[] pkt = new byte[4 + bytes.Length];
        Encoding.ASCII.GetBytes(bytes.Length.ToString("x4", CultureInfo.InvariantCulture), pkt.AsSpan(0, 4));
        bytes.CopyTo(pkt, 4);
        await stream.WriteAsync(pkt, token).ConfigureAwait(false);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buf, CancellationToken token)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await stream.ReadAsync(buf.AsMemory(off), token).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            off += n;
        }
    }

    private static async Task<int> ReadUpToAsync(Stream stream, byte[] buf, CancellationToken token)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await stream.ReadAsync(buf.AsMemory(off), token).ConfigureAwait(false);
            if (n == 0) break;
            off += n;
        }
        return off;
    }

    public void Disconnect()
    {
        _cts.Cancel();
        _listener.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        Disconnect();
        try { await _acceptLoop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
        _trackSignal.Dispose();
    }
}
