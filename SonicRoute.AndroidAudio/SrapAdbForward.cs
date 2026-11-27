using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace SonicRoute.AndroidAudio;

// Only a captured in-process ADB connection can open this one-use forward. The client
// socket is allocated by its owner too, so another local process cannot take its place.
internal sealed class SrapAdbForward : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly TcpClient accepted;
    private readonly Stream remote;
    private readonly CancellationTokenSource stop = new();
    private readonly Task bridge;
    internal Socket Client { get; }
    internal IPEndPoint Endpoint { get; }

    private SrapAdbForward(TcpListener listener, Socket client, TcpClient accepted, Stream remote)
    {
        this.listener = listener;
        this.accepted = accepted;
        this.remote = remote;
        Client = client;
        Endpoint = (IPEndPoint)listener.LocalEndpoint;
        bridge = BridgeAsync();
    }

    internal static async Task<SrapAdbForward> OpenAsync(Stream remote, CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        TcpClient? accepted = null;
        try
        {
            listener.Start(1);
            await client.ConnectAsync(listener.LocalEndpoint, token).ConfigureAwait(false);
            do
            {
                accepted?.Dispose();
                accepted = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            } while (!Equals(accepted.Client.RemoteEndPoint, client.LocalEndPoint));
            accepted.NoDelay = true;
            return new(listener, client, accepted, remote);
        }
        catch
        {
            accepted?.Dispose();
            listener.Stop();
            client.Dispose();
            await remote.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task BridgeAsync()
    {
        using var direction = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var socket = accepted.Client;
        var local = accepted.GetStream();
        var send = local.CopyToAsync(remote, direction.Token);
        var receive = remote.CopyToAsync(local, direction.Token);
        try
        {
            await Task.WhenAny(send, receive).ConfigureAwait(false);
            // Propagate EOF to TLS while the caller still waits on the main session.
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException) { }
        finally
        {
            direction.Cancel();
            try { await Task.WhenAll(send, receive).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // These are owned handles, never a port lookup or a global ADB rule.
        try
        {
            listener.Stop();
            Client.Dispose();
            stop.Cancel();
            accepted.Dispose();
            await remote.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try { await bridge.ConfigureAwait(false); }
            finally { stop.Dispose(); }
        }
    }
}

internal sealed class SrapAdbAttachment(AdbMuxConnection connection)
{
    internal bool IsConnected => connection.IsConnected;
    internal event Action Disconnected { add => connection.Disconnected += value; remove => connection.Disconnected -= value; }

    internal async Task<SrapAdbForward> OpenForwardAsync(int port, CancellationToken token)
    {
        if (port is < 1 or > 65535 || !IsConnected) throw new SrapException("stale_session");
        var remote = await connection.OpenStreamAsync("tcp:" + port.ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false);
        return await SrapAdbForward.OpenAsync(remote, token).ConfigureAwait(false);
    }
}
