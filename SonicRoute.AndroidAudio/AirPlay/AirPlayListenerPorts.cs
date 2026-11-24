using System.Net;
using System.Net.Sockets;

namespace SonicRoute.AirPlay;

internal readonly struct AirPlayListenerPorts(bool useStorePorts)
{
    // Keep this range in sync with packaging/Store/AppxManifest.xml.
    private const int FirstStorePort = 47120;
    private const int LastStorePort = 47183;

    internal TcpListener StartTcp(IPAddress address, int backlog) => BindAvailable(port =>
    {
        var listener = new TcpListener(address, port);
        try
        {
            listener.Start(backlog);
            return listener;
        }
        catch { listener.Stop(); throw; }
    });

    internal Socket BindUdp(IPAddress address, int fixedPort = 0, int receiveBufferSize = 0)
    {
        Socket Bind(int port)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                if (receiveBufferSize != 0) socket.ReceiveBufferSize = receiveBufferSize;
                socket.Bind(new IPEndPoint(address, port));
                return socket;
            }
            catch { socket.Dispose(); throw; }
        }

        return fixedPort == 0 ? BindAvailable(Bind) : Bind(fixedPort);
    }

    private T BindAvailable<T>(Func<int, T> bind)
    {
        if (!useStorePorts) return bind(0);

        int count = LastStorePort - FirstStorePort + 1;
        int offset = Random.Shared.Next(count);
        for (int attempt = 0; ; attempt++)
        {
            int port = FirstStorePort + (offset + attempt) % count;
            try { return bind(port); }
            catch (SocketException ex) when (attempt + 1 < count
                && ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied) { }
        }
    }
}
