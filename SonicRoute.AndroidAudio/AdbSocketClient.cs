using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SonicRoute.AndroidAudio;

/// <summary>
/// Implements the ADB smart-socket wire protocol against an ADB server (default 127.0.0.1:5037)
/// without spawning adb.exe subprocesses per request.
/// </summary>
public sealed class AdbSocketClient : IAdbTransport
{
    private const int SyncDataMax = 65536;
    private const int MaxPayloadBytes = 8 * 1024 * 1024;
    private const int MaxShellPacketBytes = 1024 * 1024;
    private const int DefaultFileMode = 33188; // S_IFREG | 0644

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private readonly IPEndPoint _endpoint;

    public AdbSocketClient(int port = 5037) : this(new IPEndPoint(IPAddress.Loopback, port)) { }

    public AdbSocketClient(IPEndPoint endpoint) => _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    public IPEndPoint EndPoint => _endpoint;

    /// <summary>Non-invasively probes whether an ADB smart-socket daemon is responding on this endpoint.</summary>
    public static async Task<bool> IsServerReachableAsync(IPEndPoint? endpoint = null, CancellationToken token = default)
    {
        endpoint ??= new IPEndPoint(IPAddress.Loopback, 5037);
        token.ThrowIfCancellationRequested();
        // On Windows loopback, connecting to a closed port blocks ~2s waiting for SYN retransmission;
        // inspect the kernel TCP listener table first so an absent 5037 server fails in <1ms.
        if (IPAddress.IsLoopback(endpoint.Address) && !HasLoopbackTcpListener(endpoint))
            return false;

        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        probeTimeout.CancelAfter(TimeSpan.FromMilliseconds(350));
        try
        {
            using var client = await ConnectAsync(endpoint, probeTimeout.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            await SendRequestAsync(stream, "host:version", probeTimeout.Token).ConfigureAwait(false);
            await ReadStatusAsync(stream, probeTimeout.Token).ConfigureAwait(false);
            _ = await ReadLengthPrefixedUtf8Async(stream, 64, probeTimeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool HasLoopbackTcpListener(IPEndPoint endpoint)
    {
        try
        {
            foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (ep.Port != endpoint.Port) continue;
                if (ep.Address.Equals(endpoint.Address)
                    || ep.Address.Equals(IPAddress.Any)
                    || (endpoint.AddressFamily == AddressFamily.InterNetworkV6 && ep.Address.Equals(IPAddress.IPv6Any)))
                    return true;
            }
            return false;
        }
        catch (NetworkInformationException)
        {
            return true;
        }
    }

    public async Task<string> VersionAsync(CancellationToken token)
    {
        string hex = await QueryHostStringAsync("host:version", 64, token).ConfigureAwait(false);
        return int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int protocol)
            ? $"Android Debug Bridge protocol version {protocol} (socket)"
            : hex;
    }

    public async Task<IReadOnlyList<AndroidDevice>> DevicesAsync(CancellationToken token)
    {
        string payload = await QueryHostStringAsync("host:devices-l", 1024 * 1024, token).ConfigureAwait(false);
        return AdbClient.ParseDevices(payload);
    }

    public async Task TrackDevicesAsync(Action<IReadOnlyList<AndroidDevice>> onChanged, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        using var client = await ConnectAsync(_endpoint, token).ConfigureAwait(false);
        using var cancelReg = RegisterSocketAbort(client, token);
        var stream = client.GetStream();
        await SendRequestAsync(stream, "host:track-devices-l", token).ConfigureAwait(false);
        await ReadStatusAsync(stream, token).ConfigureAwait(false);

        byte[] lenBuf = new byte[4];
        while (!token.IsCancellationRequested)
        {
            int read = await ReadUpToAsync(stream, lenBuf, token).ConfigureAwait(false);
            if (read == 0)
            {
                token.ThrowIfCancellationRequested();
                return;
            }
            if (read < 4) throw new EndOfStreamException("Incomplete ADB track-devices frame header.");
            int length = ParseHexLength(lenBuf, 1024 * 1024);
            string text = await ReadExactUtf8Async(stream, length, token).ConfigureAwait(false);
            onChanged(AdbClient.ParseDevices(text));
        }
        token.ThrowIfCancellationRequested();
    }

    public async Task<IReadOnlyList<string>> FeaturesAsync(string serial, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        string raw = await QueryHostStringAsync($"host-serial:{serial}:features", 64 * 1024, token).ConfigureAwait(false);
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task<string> GetSerialNoAsync(string serial, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        return (await QueryHostStringAsync($"host-serial:{serial}:get-serialno", 4096, token).ConfigureAwait(false)).Trim();
    }

    public Task<AdbShellResult> RunShellAsync(string serial, string command, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        ArgumentNullException.ThrowIfNull(command);
        return RunShellWithOpenerAsync((cmd, redirectInput, ct) => OpenShellAsync(serial, cmd, redirectInput, ct), command, token);
    }

    internal static async Task<AdbShellResult> RunShellWithOpenerAsync(
        Func<string, bool, CancellationToken, Task<IAdbShellSession>> openShell,
        string command,
        CancellationToken token)
    {
        await using var session = await openShell(command, false, token).ConfigureAwait(false);
        var stdoutTask = ReadTextLimitedAsync(session.OutputStream, MaxPayloadBytes, token);
        var stderrTask = ReadTextLimitedAsync(session.ErrorStream, 512 * 1024, token);
        try
        {
            string[] texts = await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            int exitCode = await session.WaitForExitAsync(token).ConfigureAwait(false);
            return new AdbShellResult(texts[0], exitCode, texts[1]);
        }
        catch
        {
            session.Kill();
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    public async Task<IAdbShellSession> OpenShellAsync(string serial, string command, bool redirectInput, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        ArgumentNullException.ThrowIfNull(command);

        TcpClient? client = null;
        try
        {
            client = await OpenTransportAsync(serial, token).ConfigureAwait(false);
            var stream = client.GetStream();
            string v2Service = "shell,v2,raw:" + command;
            await SendRequestAsync(stream, v2Service, token).ConfigureAwait(false);
            string? v2Error = await TryReadStatusFailureAsync(stream, token).ConfigureAwait(false);
            if (v2Error == null)
            {
                var ownedClient = client;
                client = null;
                return CreateV2ShellSession(stream, () => { try { ownedClient.Dispose(); } catch { } },
                    async () => { try { await stream.DisposeAsync().ConfigureAwait(false); } finally { ownedClient.Dispose(); } });
            }

            if (!IsUnsupportedShellV2Error(v2Error))
                throw new IOException("ADB: " + v2Error);

            client.Dispose();
            client = await OpenTransportAsync(serial, token).ConfigureAwait(false);
            stream = client.GetStream();
            await SendRequestAsync(stream, "shell:" + command, token).ConfigureAwait(false);
            await ReadStatusAsync(stream, token).ConfigureAwait(false);
            var v1Client = client;
            client = null;
            return CreateV1ShellSession(stream, redirectInput,
                () => { try { v1Client.Client.Shutdown(SocketShutdown.Send); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { } },
                () => { try { v1Client.Dispose(); } catch { } },
                async () => { try { await stream.DisposeAsync().ConfigureAwait(false); } finally { v1Client.Dispose(); } });
        }
        finally
        {
            client?.Dispose();
        }
    }

    internal static IAdbShellSession CreateV2ShellSession(Stream stream, Action abortAction, Func<ValueTask> disposeOwner) =>
        new AdbV2ShellSession(stream, abortAction, disposeOwner);

    internal static IAdbShellSession CreateV1ShellSession(Stream stream, bool redirectInput, Action? closeInputAction, Action abortAction, Func<ValueTask> disposeOwner) =>
        new AdbV1ShellSession(stream, redirectInput, closeInputAction, abortAction, disposeOwner);

    public async Task PushAsync(string serial, string localPath, string remotePath, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        ArgumentException.ThrowIfNullOrEmpty(localPath);
        ArgumentException.ThrowIfNullOrEmpty(remotePath);
        if (!File.Exists(localPath)) throw new FileNotFoundException("Missing local file to push.", localPath);

        using var client = await OpenTransportAsync(serial, token).ConfigureAwait(false);
        using var cancelReg = RegisterSocketAbort(client, token);
        var stream = client.GetStream();
        await SendRequestAsync(stream, "sync:", token).ConfigureAwait(false);
        await ReadStatusAsync(stream, token).ConfigureAwait(false);
        await PushSyncOverStreamAsync(stream, localPath, remotePath, token).ConfigureAwait(false);
    }

    internal static async Task PushSyncOverStreamAsync(Stream stream, string localPath, string remotePath, CancellationToken token)
    {
        long unixMtime = Math.Max(0L, new DateTimeOffset(File.GetLastWriteTimeUtc(localPath)).ToUnixTimeSeconds());
        await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, SyncDataMax, FileOptions.Asynchronous | FileOptions.SequentialScan);

        string remoteSpec = $"{remotePath},{DefaultFileMode}";
        byte[] specBytes = Utf8NoBom.GetBytes(remoteSpec);
        byte[] sendPacket = new byte[8 + specBytes.Length];
        WriteSyncHeader(sendPacket, "SEND"u8, specBytes.Length);
        specBytes.CopyTo(sendPacket.AsSpan(8));
        await stream.WriteAsync(sendPacket, token).ConfigureAwait(false);

        byte[] header = new byte[8];
        byte[] packet = ArrayPool<byte>.Shared.Rent(8 + SyncDataMax);
        try
        {
            while (true)
            {
                int read = await file.ReadAsync(packet.AsMemory(8, SyncDataMax), token).ConfigureAwait(false);
                if (read == 0) break;
                WriteSyncHeader(packet.AsSpan(0, 8), "DATA"u8, read);
                await stream.WriteAsync(packet.AsMemory(0, 8 + read), token).ConfigureAwait(false);
            }

            WriteSyncHeader(header, "DONE"u8, unchecked((int)(uint)Math.Min(uint.MaxValue, (ulong)unixMtime)));
            await stream.WriteAsync(header, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }

        await ReadExactAsync(stream, header, token).ConfigureAwait(false);
        string status = Encoding.ASCII.GetString(header, 0, 4);
        int replyLen = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (status == "OKAY")
        {
            try
            {
                WriteSyncHeader(header, "QUIT"u8, 0);
                await stream.WriteAsync(header, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            catch { }
            return;
        }
        if (status == "FAIL")
        {
            int safeLen = Math.Clamp(replyLen, 0, 64 * 1024);
            string err = safeLen > 0 ? await ReadExactUtf8Async(stream, safeLen, token).ConfigureAwait(false) : "sync push failed";
            throw new IOException("ADB: " + err);
        }
        throw new InvalidDataException($"Invalid ADB sync response '{status}'.");
    }

    public async Task<int> ForwardTcpToLocalAbstractAsync(string serial, string localAbstractName, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        ArgumentException.ThrowIfNullOrEmpty(localAbstractName);
        using var client = await ConnectAsync(_endpoint, token).ConfigureAwait(false);
        using var cancelReg = RegisterSocketAbort(client, token);
        var stream = client.GetStream();
        await SendRequestAsync(stream, $"host-serial:{serial}:forward:tcp:0;localabstract:{localAbstractName}", token).ConfigureAwait(false);
        await ReadStatusAsync(stream, token).ConfigureAwait(false);
        string portText = await ReadTwoStageOrLengthStringAsync(stream, 64, token).ConfigureAwait(false);
        if (!int.TryParse(portText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
            throw new IOException("Invalid ADB forward port response: " + portText);
        return port;
    }

    public async Task RemoveForwardAsync(string serial, string localSpec, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        ArgumentException.ThrowIfNullOrEmpty(localSpec);
        using var client = await ConnectAsync(_endpoint, token).ConfigureAwait(false);
        using var cancelReg = RegisterSocketAbort(client, token);
        var stream = client.GetStream();
        await SendRequestAsync(stream, $"host-serial:{serial}:killforward:{localSpec}", token).ConfigureAwait(false);
        await ReadStatusAsync(stream, token).ConfigureAwait(false);
        // ADB sends a second OKAY/FAIL from the listener handler when dispatched.
        byte[] second = new byte[4];
        int n = await ReadUpToAsync(stream, second, token).ConfigureAwait(false);
        if (n == 4 && second.AsSpan().SequenceEqual("FAIL"u8))
        {
            string msg = await ReadLengthPrefixedUtf8Async(stream, 64 * 1024, token).ConfigureAwait(false);
            throw new IOException("ADB: " + msg);
        }
    }

    public async Task<IReadOnlyList<AdbForwardRule>> ListForwardsAsync(CancellationToken token)
    {
        string text = await QueryHostStringAsync("host:list-forward", 256 * 1024, token).ConfigureAwait(false);
        return ParseForwards(text);
    }

    internal static IReadOnlyList<AdbForwardRule> ParseForwards(string text)
    {
        var rules = new List<AdbForwardRule>();
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseTokenFromRight(line, out int remoteStart, out string remote)
                && TryParseTokenFromRight(line.AsSpan(0, remoteStart).TrimEnd(), out int localStart, out string local))
            {
                string serial = line[..localStart].Trim();
                if (serial.Length > 0)
                    rules.Add(new AdbForwardRule(serial, local, remote));
            }
        }
        return rules;
    }

    private static bool TryParseTokenFromRight(ReadOnlySpan<char> span, out int tokenStart, out string token)
    {
        span = span.TrimEnd();
        tokenStart = 0;
        token = "";
        if (span.IsEmpty) return false;
        int i = span.Length - 1;
        while (i >= 0 && !char.IsWhiteSpace(span[i])) i--;
        tokenStart = i + 1;
        token = span[tokenStart..].ToString();
        return token.Length > 0;
    }

    public async Task<Stream> OpenLocalAbstractAsync(string serial, string name, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        ArgumentException.ThrowIfNullOrEmpty(name);
        TcpClient? client = null;
        try
        {
            client = await OpenTransportAsync(serial, token).ConfigureAwait(false);
            var stream = client.GetStream();
            await SendRequestAsync(stream, "localabstract:" + name, token).ConfigureAwait(false);
            await ReadStatusAsync(stream, token).ConfigureAwait(false);
            var owned = new OwnedNetworkStream(client, stream);
            client = null;
            return owned;
        }
        finally
        {
            client?.Dispose();
        }
    }

    public async Task<string> PairAsync(string address, string password, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        ArgumentException.ThrowIfNullOrEmpty(password);
        using var client = await ConnectAsync(_endpoint, token).ConfigureAwait(false);
        using var cancelReg = RegisterSocketAbort(client, token);
        var stream = client.GetStream();
        // AOSP ADB wire order places password first because IP:port / [IPv6]:port contains colons.
        await SendRequestAsync(stream, $"host:pair:{password}:{address}", token).ConfigureAwait(false);
        await ReadStatusAsync(stream, token).ConfigureAwait(false);
        return await ReadTwoStageOrLengthStringAsync(stream, 64 * 1024, token).ConfigureAwait(false);
    }

    public Task<string> ConnectAsync(string address, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        return QueryHostStringAsync("host:connect:" + address, 64 * 1024, token);
    }

    public async Task<string> DisconnectAsync(string address, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        using var client = await ConnectAsync(_endpoint, token).ConfigureAwait(false);
        using var cancelReg = RegisterSocketAbort(client, token);
        var stream = client.GetStream();
        await SendRequestAsync(stream, "host:disconnect:" + address, token).ConfigureAwait(false);
        await ReadStatusAsync(stream, token).ConfigureAwait(false);
        return await ReadOptionalLengthPrefixedUtf8Async(stream, 64 * 1024, token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AndroidMdnsService>> MdnsServicesAsync(CancellationToken token)
    {
        string text = await QueryHostStringAsync("host:mdns:services", 512 * 1024, token).ConfigureAwait(false);
        return AndroidWirelessConnection.ParseServices(text);
    }

    private async Task<string> QueryHostStringAsync(string request, int maxBytes, CancellationToken token)
    {
        using var client = await ConnectAsync(_endpoint, token).ConfigureAwait(false);
        using var cancelReg = RegisterSocketAbort(client, token);
        var stream = client.GetStream();
        await SendRequestAsync(stream, request, token).ConfigureAwait(false);
        await ReadStatusAsync(stream, token).ConfigureAwait(false);
        return await ReadLengthPrefixedUtf8Async(stream, maxBytes, token).ConfigureAwait(false);
    }

    private async Task<TcpClient> OpenTransportAsync(string serial, CancellationToken token)
    {
        TcpClient? client = null;
        try
        {
            client = await ConnectAsync(_endpoint, token).ConfigureAwait(false);
            using var cancelReg = RegisterSocketAbort(client, token);
            var stream = client.GetStream();
            await SendRequestAsync(stream, "host:transport:" + serial, token).ConfigureAwait(false);
            await ReadStatusAsync(stream, token).ConfigureAwait(false);
            var result = client;
            client = null;
            return result;
        }
        finally
        {
            client?.Dispose();
        }
    }

    private static async Task<TcpClient> ConnectAsync(IPEndPoint endpoint, CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(endpoint, token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static CancellationTokenRegistration RegisterSocketAbort(TcpClient client, CancellationToken token) =>
        token.Register(static state => { try { ((TcpClient)state!).Dispose(); } catch { } }, client);

    private static async Task SendRequestAsync(NetworkStream stream, string request, CancellationToken token)
    {
        int byteCount = Utf8NoBom.GetByteCount(request);
        if (byteCount > 0xFFFF) throw new ArgumentException("ADB smart-socket request exceeds 64 KiB.", nameof(request));
        byte[] packet = ArrayPool<byte>.Shared.Rent(4 + byteCount);
        try
        {
            Encoding.ASCII.GetBytes(byteCount.ToString("x4", CultureInfo.InvariantCulture), packet.AsSpan(0, 4));
            Utf8NoBom.GetBytes(request, packet.AsSpan(4, byteCount));
            await stream.WriteAsync(packet.AsMemory(0, 4 + byteCount), token).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    private static async Task ReadStatusAsync(NetworkStream stream, CancellationToken token)
    {
        string? fail = await TryReadStatusFailureAsync(stream, token).ConfigureAwait(false);
        if (fail != null) throw new IOException("ADB: " + fail);
    }

    private static async Task<string?> TryReadStatusFailureAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] status = new byte[4];
        await ReadExactAsync(stream, status, token).ConfigureAwait(false);
        if (status.AsSpan().SequenceEqual("OKAY"u8)) return null;
        if (status.AsSpan().SequenceEqual("FAIL"u8))
            return await ReadLengthPrefixedUtf8Async(stream, 1024 * 1024, token).ConfigureAwait(false);
        throw new InvalidDataException($"Invalid ADB smart-socket status: {BitConverter.ToString(status)}");
    }

    private static bool IsUnsupportedShellV2Error(string error) =>
        error.Contains("unknown", StringComparison.OrdinalIgnoreCase)
        || error.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
        || error.Contains("closed", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadTwoStageOrLengthStringAsync(NetworkStream stream, int maxBytes, CancellationToken token)
    {
        byte[] tag = new byte[4];
        int read = await ReadUpToAsync(stream, tag, token).ConfigureAwait(false);
        if (read == 0) return "";
        if (read < 4) return Utf8NoBom.GetString(tag, 0, read);

        if (tag.AsSpan().SequenceEqual("FAIL"u8))
        {
            string msg = await ReadLengthPrefixedUtf8Async(stream, maxBytes, token).ConfigureAwait(false);
            throw new IOException("ADB: " + msg);
        }
        if (tag.AsSpan().SequenceEqual("OKAY"u8))
            return await ReadOptionalLengthPrefixedUtf8Async(stream, maxBytes, token).ConfigureAwait(false);

        if (TryParseHexLength(tag, maxBytes, out int hexLen))
            return await ReadExactUtf8Async(stream, hexLen, token).ConfigureAwait(false);

        return await ReadRemainingUtf8Async(stream, tag, maxBytes, token).ConfigureAwait(false);
    }

    private static async Task<string> ReadOptionalLengthPrefixedUtf8Async(NetworkStream stream, int maxBytes, CancellationToken token)
    {
        byte[] prefix = new byte[4];
        int read = await ReadUpToAsync(stream, prefix, token).ConfigureAwait(false);
        if (read == 0) return "";
        if (read < 4) return Utf8NoBom.GetString(prefix, 0, read);
        if (TryParseHexLength(prefix, maxBytes, out int length))
            return await ReadExactUtf8Async(stream, length, token).ConfigureAwait(false);
        return await ReadRemainingUtf8Async(stream, prefix, maxBytes, token).ConfigureAwait(false);
    }

    private static async Task<string> ReadRemainingUtf8Async(NetworkStream stream, byte[] alreadyRead, int maxBytes, CancellationToken token)
    {
        using var ms = new MemoryStream(alreadyRead.Length + 256);
        ms.Write(alreadyRead, 0, alreadyRead.Length);
        byte[] buf = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (ms.Length < maxBytes)
            {
                int n = await stream.ReadAsync(buf, token).ConfigureAwait(false);
                if (n == 0) break;
                ms.Write(buf, 0, Math.Min(n, maxBytes - (int)ms.Length));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        return Utf8NoBom.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static async Task<string> ReadLengthPrefixedUtf8Async(NetworkStream stream, int maxBytes, CancellationToken token)
    {
        byte[] lenBuf = new byte[4];
        await ReadExactAsync(stream, lenBuf, token).ConfigureAwait(false);
        int length = ParseHexLength(lenBuf, maxBytes);
        return await ReadExactUtf8Async(stream, length, token).ConfigureAwait(false);
    }

    private static int ParseHexLength(ReadOnlySpan<byte> lenBuf, int maxBytes)
    {
        if (!TryParseHexLength(lenBuf, maxBytes, out int length))
            throw new InvalidDataException("Invalid or oversized ADB payload length.");
        return length;
    }

    private static bool TryParseHexLength(ReadOnlySpan<byte> lenBuf, int maxBytes, out int length)
    {
        length = 0;
        if (lenBuf.Length != 4) return false;
        int parsed = 0;
        for (int i = 0; i < 4; i++)
        {
            int digit = lenBuf[i] switch
            {
                >= (byte)'0' and <= (byte)'9' => lenBuf[i] - '0',
                >= (byte)'a' and <= (byte)'f' => lenBuf[i] - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => lenBuf[i] - 'A' + 10,
                _ => -1
            };
            if (digit < 0) return false;
            parsed = (parsed << 4) | digit;
        }
        if (parsed > maxBytes) return false;
        length = parsed;
        return true;
    }

    private static async Task<string> ReadExactUtf8Async(Stream stream, int length, CancellationToken token)
    {
        if (length == 0) return "";
        byte[] data = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadExactAsync(stream, data.AsMemory(0, length), token).ConfigureAwait(false);
            return Utf8NoBom.GetString(data, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n;
            try { n = await stream.ReadAsync(buffer[offset..], token).ConfigureAwait(false); }
            catch (Exception ex) when (token.IsCancellationRequested && ex is IOException or ObjectDisposedException or SocketException)
            { throw new OperationCanceledException(token); }
            if (n == 0) throw new EndOfStreamException("ADB connection closed unexpectedly.");
            offset += n;
        }
    }

    private static async Task<int> ReadUpToAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n;
            try { n = await stream.ReadAsync(buffer[offset..], token).ConfigureAwait(false); }
            catch (Exception ex) when (token.IsCancellationRequested && ex is IOException or ObjectDisposedException or SocketException)
            { throw new OperationCanceledException(token); }
            if (n == 0) break;
            offset += n;
        }
        return offset;
    }

    private static void WriteSyncHeader(Span<byte> dest, ReadOnlySpan<byte> id, int length)
    {
        id.CopyTo(dest[..4]);
        BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(4, 4), length);
    }

    private static void SafeComplete(PipeWriter writer, Exception? error = null)
    {
        try { writer.Complete(error); } catch (InvalidOperationException) { }
    }

    private static async Task<string> ReadTextLimitedAsync(Stream stream, int maxCharacters, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        var sb = new StringBuilder();
        char[] buf = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buf.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                if (sb.Length + read > maxCharacters)
                    throw new InvalidDataException("ADB response exceeds catalogue limit.");
                sb.Append(buf, 0, read);
            }
            return sb.ToString();
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buf);
        }
    }

    /// <summary>Wraps a NetworkStream so disposing the stream owns and disposes the backing TcpClient.</summary>
    private sealed class OwnedNetworkStream(TcpClient client, NetworkStream inner) : Stream
    {
        private int _disposed;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && disposing)
            {
                try { inner.Dispose(); } finally { client.Dispose(); }
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try { await inner.DisposeAsync().ConfigureAwait(false); }
                finally { client.Dispose(); }
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class AdbV2ShellSession : IAdbShellSession
    {
        private const byte IdStdin = 0;
        private const byte IdStdout = 1;
        private const byte IdStderr = 2;
        private const byte IdExit = 3;
        private const byte IdCloseStdin = 4;

        private readonly Stream _stream;
        private readonly Action _abortAction;
        private readonly Func<ValueTask> _disposeOwner;
        private readonly Pipe _stdoutPipe = new(new PipeOptions(pauseWriterThreshold: 512 * 1024, resumeWriterThreshold: 256 * 1024, useSynchronizationContext: false));
        private readonly Pipe _stderrPipe = new(new PipeOptions(pauseWriterThreshold: 512 * 1024, resumeWriterThreshold: 256 * 1024, useSynchronizationContext: false));
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource<int> _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AdbV2StdinStream _stdin;
        private readonly Task _pumpTask;
        private int _disposed;
        private int? _exitCode;

        public AdbV2ShellSession(Stream stream, Action abortAction, Func<ValueTask> disposeOwner)
        {
            _stream = stream;
            _abortAction = abortAction;
            _disposeOwner = disposeOwner;
            OutputStream = _stdoutPipe.Reader.AsStream();
            ErrorStream = _stderrPipe.Reader.AsStream();
            _stdin = new AdbV2StdinStream(stream);
            _pumpTask = Task.Run(PumpAsync);
        }

        public Stream OutputStream { get; }
        public Stream ErrorStream { get; }
        public Stream InputStream => _stdin;
        public bool HasExited => _exitTcs.Task.IsCompleted;
        public int? ExitCode => _exitCode;

        public Task<int> WaitForExitAsync(CancellationToken token = default) => _exitTcs.Task.WaitAsync(token);

        public Task CloseInputAsync(CancellationToken token = default) => _stdin.CloseStdinAsync(token);

        public void Kill()
        {
            _lifetime.Cancel();
            try { _abortAction(); } catch { }
        }

        private async Task PumpAsync()
        {
            byte[] header = new byte[5];
            byte[] scratch = ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    int hdrRead = await ReadUpToAsync(_stream, header, _lifetime.Token).ConfigureAwait(false);
                    if (hdrRead == 0) break;
                    if (hdrRead < 5) throw new EndOfStreamException("Truncated ADB shell_v2 packet header.");

                    byte id = header[0];
                    uint payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1, 4));
                    if (payloadLen > MaxShellPacketBytes)
                        throw new InvalidDataException($"ADB shell_v2 packet size {payloadLen} exceeds limit.");

                    int remaining = (int)payloadLen;
                    if (id == IdStdout || id == IdStderr)
                    {
                        var writer = id == IdStdout ? _stdoutPipe.Writer : _stderrPipe.Writer;
                        while (remaining > 0)
                        {
                            Memory<byte> dest = writer.GetMemory(Math.Min(remaining, 16384));
                            int want = Math.Min(remaining, dest.Length);
                            int n = await _stream.ReadAsync(dest[..want], _lifetime.Token).ConfigureAwait(false);
                            if (n == 0) throw new EndOfStreamException("Unexpected EOF inside ADB shell_v2 payload.");
                            writer.Advance(n);
                            remaining -= n;
                            var flush = await writer.FlushAsync(_lifetime.Token).ConfigureAwait(false);
                            if (flush.IsCompleted) break;
                        }
                    }
                    else if (id == IdExit)
                    {
                        if (remaining > 0)
                        {
                            await ReadExactAsync(_stream, scratch.AsMemory(0, Math.Min(remaining, scratch.Length)), _lifetime.Token).ConfigureAwait(false);
                            int code = scratch[0];
                            _exitCode = code;
                            _exitTcs.TrySetResult(code);
                            remaining -= Math.Min(remaining, scratch.Length);
                        }
                        else
                        {
                            _exitCode = 0;
                            _exitTcs.TrySetResult(0);
                        }
                        while (remaining > 0)
                        {
                            int n = await _stream.ReadAsync(scratch.AsMemory(0, Math.Min(remaining, scratch.Length)), _lifetime.Token).ConfigureAwait(false);
                            if (n == 0) break;
                            remaining -= n;
                        }
                    }
                    else
                    {
                        while (remaining > 0)
                        {
                            int n = await _stream.ReadAsync(scratch.AsMemory(0, Math.Min(remaining, scratch.Length)), _lifetime.Token).ConfigureAwait(false);
                            if (n == 0) throw new EndOfStreamException("Unexpected EOF inside ADB shell_v2 payload.");
                            remaining -= n;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                if (!_exitTcs.Task.IsCompleted)
                {
                    SafeComplete(_stdoutPipe.Writer, ex);
                    SafeComplete(_stderrPipe.Writer, ex);
                    _exitTcs.TrySetException(ex);
                    return;
                }
            }
            catch (Exception ex)
            {
                SafeComplete(_stdoutPipe.Writer, ex);
                SafeComplete(_stderrPipe.Writer, ex);
                _exitTcs.TrySetException(ex);
                return;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
                SafeComplete(_stdoutPipe.Writer);
                SafeComplete(_stderrPipe.Writer);
                if (!_exitTcs.Task.IsCompleted)
                {
                    _exitCode ??= -1;
                    _exitTcs.TrySetResult(_exitCode.Value);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lifetime.Cancel();
            try { await OutputStream.DisposeAsync().ConfigureAwait(false); } catch { }
            try { await ErrorStream.DisposeAsync().ConfigureAwait(false); } catch { }
            try { await _stdin.DisposeAsync().ConfigureAwait(false); } catch { }
            try { await _disposeOwner().ConfigureAwait(false); } catch { }
            try { await _pumpTask.ConfigureAwait(false); } catch { }
            _lifetime.Dispose();
        }

        private sealed class AdbV2StdinStream(Stream stream) : Stream
        {
            private readonly SemaphoreSlim _writeLock = new(1, 1);
            private int _closed;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Flush() => stream.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => stream.FlushAsync(cancellationToken);

            public override void Write(byte[] buffer, int offset, int count) =>
                Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
                if (buffer.IsEmpty) return;
                _writeLock.Wait();
                try
                {
                    int pos = 0;
                    byte[] packet = ArrayPool<byte>.Shared.Rent(5 + Math.Min(buffer.Length, 32768));
                    try
                    {
                        while (pos < buffer.Length)
                        {
                            int chunk = Math.Min(buffer.Length - pos, 32768);
                            packet[0] = IdStdin;
                            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(1, 4), chunk);
                            buffer.Slice(pos, chunk).CopyTo(packet.AsSpan(5, chunk));
                            stream.Write(packet, 0, 5 + chunk);
                            pos += chunk;
                        }
                    }
                    finally { ArrayPool<byte>.Shared.Return(packet); }
                }
                finally { _writeLock.Release(); }
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
                if (buffer.IsEmpty) return;
                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    int pos = 0;
                    byte[] packet = ArrayPool<byte>.Shared.Rent(5 + Math.Min(buffer.Length, 32768));
                    try
                    {
                        while (pos < buffer.Length)
                        {
                            int chunk = Math.Min(buffer.Length - pos, 32768);
                            packet[0] = IdStdin;
                            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(1, 4), chunk);
                            buffer.Slice(pos, chunk).Span.CopyTo(packet.AsSpan(5, chunk));
                            await stream.WriteAsync(packet.AsMemory(0, 5 + chunk), cancellationToken).ConfigureAwait(false);
                            pos += chunk;
                        }
                    }
                    finally { ArrayPool<byte>.Shared.Return(packet); }
                }
                finally { _writeLock.Release(); }
            }

            public async Task CloseStdinAsync(CancellationToken token)
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0) return;
                await _writeLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    byte[] closePacket = [IdCloseStdin, 0, 0, 0, 0];
                    await stream.WriteAsync(closePacket, token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
                finally { _writeLock.Release(); }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
                {
                    if (_writeLock.Wait(250))
                    {
                        try { stream.Write([IdCloseStdin, 0, 0, 0, 0], 0, 5); }
                        catch { }
                        finally { _writeLock.Release(); }
                    }
                    _writeLock.Dispose();
                }
                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _closed, 1) == 0)
                {
                    try { await _writeLock.WaitAsync().ConfigureAwait(false); }
                    catch (ObjectDisposedException) { return; }
                    try { await stream.WriteAsync(new byte[] { IdCloseStdin, 0, 0, 0, 0 }).ConfigureAwait(false); }
                    catch { }
                    finally { _writeLock.Release(); _writeLock.Dispose(); }
                }
                await base.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class AdbV1ShellSession : IAdbShellSession
    {
        private readonly Stream _stream;
        private readonly Action? _closeInputAction;
        private readonly Action _abortAction;
        private readonly Func<ValueTask> _disposeOwner;
        private readonly Pipe _stdoutPipe = new(new PipeOptions(pauseWriterThreshold: 512 * 1024, resumeWriterThreshold: 256 * 1024, useSynchronizationContext: false));
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource<int> _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _pumpTask;
        private int _disposed;
        private int _inputClosed;

        public AdbV1ShellSession(Stream stream, bool redirectInput, Action? closeInputAction, Action abortAction, Func<ValueTask> disposeOwner)
        {
            _stream = stream;
            _closeInputAction = closeInputAction;
            _abortAction = abortAction;
            _disposeOwner = disposeOwner;
            OutputStream = _stdoutPipe.Reader.AsStream();
            ErrorStream = Stream.Null;
            InputStream = redirectInput ? new RawStdinStream(this) : Stream.Null;
            _pumpTask = Task.Run(PumpAsync);
        }

        public Stream OutputStream { get; }
        public Stream ErrorStream { get; }
        public Stream InputStream { get; }
        public bool HasExited => _exitTcs.Task.IsCompleted;
        public int? ExitCode => HasExited ? 0 : null;

        public Task<int> WaitForExitAsync(CancellationToken token = default) => _exitTcs.Task.WaitAsync(token);

        public Task CloseInputAsync(CancellationToken token = default)
        {
            if (Interlocked.Exchange(ref _inputClosed, 1) == 0)
            {
                try { _closeInputAction?.Invoke(); }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException) { }
            }
            return Task.CompletedTask;
        }

        public void Kill()
        {
            _lifetime.Cancel();
            try { _abortAction(); } catch { }
        }

        private async Task PumpAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    Memory<byte> dest = _stdoutPipe.Writer.GetMemory(8192);
                    int n = await _stream.ReadAsync(dest, _lifetime.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    _stdoutPipe.Writer.Advance(n);
                    var flush = await _stdoutPipe.Writer.FlushAsync(_lifetime.Token).ConfigureAwait(false);
                    if (flush.IsCompleted) break;
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
            finally
            {
                SafeComplete(_stdoutPipe.Writer);
                _exitTcs.TrySetResult(0);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lifetime.Cancel();
            try { await OutputStream.DisposeAsync().ConfigureAwait(false); } catch { }
            try { await _disposeOwner().ConfigureAwait(false); } catch { }
            try { await _pumpTask.ConfigureAwait(false); } catch { }
            _lifetime.Dispose();
        }

        private sealed class RawStdinStream(AdbV1ShellSession owner) : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => owner._stream.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => owner._stream.FlushAsync(cancellationToken);
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => owner._stream.Write(buffer, offset, count);
            public override void Write(ReadOnlySpan<byte> buffer) => owner._stream.Write(buffer);
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => owner._stream.WriteAsync(buffer, offset, count, cancellationToken);
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => owner._stream.WriteAsync(buffer, cancellationToken);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) owner.CloseInputAsync().GetAwaiter().GetResult(); base.Dispose(disposing); }
        }
    }
}
