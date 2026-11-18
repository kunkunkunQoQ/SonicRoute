using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SonicRoute.AndroidAudio;

namespace SonicRoute.AirPlay;

internal enum AirPlayStage { Off, Starting, Waiting, Connected, Failed }
internal sealed record AirPlayStatus(AirPlayStage Stage, string? Name = null, string? Sender = null, string? ErrorKey = null,
    int? NetworkError = null, bool Mirroring = false, bool MirrorAudioUnavailable = false);
internal sealed class AirPlayNoNetworkException() : InvalidOperationException("No active IPv4 LAN interface.");

/// <summary>An on-demand AirPlay receiver for audio and screen mirroring.</summary>
internal sealed class AirPlayReceiver : IAsyncDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(2);
    private readonly TcpListener _listener;
    private readonly AirPlayDiscovery? _discovery;
    private readonly CancellationTokenSource _stop;
    private readonly Task _run;
    private readonly AirPlayIdentity _identity;
    private readonly AirPlaySigningKey _signingKey;
    private readonly bool _showWindow;
    private readonly bool _hevc;
    private readonly AirPlayDiscovery.Lan[] _lan;
    private readonly Func<AndroidPcmSource, string?, CancellationToken, IPcmPlayer> _player;
    private readonly Func<Action, IDisposable> _watchDefault;
    private readonly byte[] _info;
    private readonly object _sessionGate = new();
    private Session? _active;
    private AirPlayStatus _status;
    private int _disposed;

    internal event Action? Changed;
    internal AirPlayStatus Status => Volatile.Read(ref _status);
    internal AirPlayAudioOutput? Source => Volatile.Read(ref _active)?.Receiving;
    internal int MirrorFrames => Volatile.Read(ref _active)?.MirrorFrames ?? 0;
    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    internal Task Completion => _run;

    // Only the test harness asks for loopback. Production never changes Windows firewall rules.
    internal AirPlayReceiver(string name, bool loopback = false,
        Func<AndroidPcmSource, string?, CancellationToken, IPcmPlayer>? player = null, Func<Action, IDisposable>? watchDefault = null,
        bool preferHevc = false)
    {
        if (!ChaCha20Poly1305.IsSupported) throw new PlatformNotSupportedException("AirPlay control encryption is unavailable.");
        _player = player ?? ((pcm, endpoint, _) => new WasapiPcmPlayer(pcm, endpoint));
        _watchDefault = watchDefault ?? DefaultOutputNotifications.Subscribe;
        _lan = loopback ? [] : AirPlayDiscovery.ReadInterfaces();
        if (!loopback && _lan.Length == 0) throw new AirPlayNoNetworkException();
        _showWindow = !loopback;
        _hevc = preferHevc && MediaFoundationH264Decoder.SupportsHevc;
        _signingKey = new AirPlaySigningKey();
        _stop = new CancellationTokenSource();
        _listener = new TcpListener(loopback ? IPAddress.Loopback : IPAddress.Any, 0);
        try
        {
            _identity = new AirPlayIdentity(name, _signingKey.PublicKey, _hevc);
            _info = _identity.Info();
            _status = new(AirPlayStage.Waiting, _identity.Name);
            _listener.Start(8);
            if (!loopback) _discovery = new AirPlayDiscovery(_identity, Port, _lan);
            _stop.CancelAfter(WaitLimit);
            _run = RunAsync(_stop.Token);
        }
        catch { _listener.Stop(); _stop.Dispose(); _signingKey.Dispose(); throw; }
    }

    private async Task RunAsync(CancellationToken token)
    {
        var clients = new List<Task>(4);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                clients.RemoveAll(static task => task.IsCompleted);
                var local = (IPEndPoint)client.Client.LocalEndPoint!;
                var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
                bool allowed = _lan.Length == 0 ? IPAddress.IsLoopback(remote.Address)
                    : _lan.Any(l => l.Address.Equals(local.Address) && l.Includes(remote.Address));
                if (!allowed || clients.Count >= 4) { client.Dispose(); continue; }
                clients.Add(new Session(this, client, local.Address, remote.Address).RunAsync(token));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        finally
        {
            _stop.Cancel();
            _listener.Stop();
            try { await Task.WhenAll(clients).ConfigureAwait(false); }
            finally
            {
                try { if (_discovery != null) await _discovery.DisposeAsync().ConfigureAwait(false); }
                finally { _signingKey.Dispose(); Publish(new(AirPlayStage.Off)); }
            }
        }
    }

    private bool Claim(Session session)
    {
        lock (_sessionGate)
        {
            if (_active != null || _stop.IsCancellationRequested) return false;
            Volatile.Write(ref _active, session);
            return true;
        }
    }
    private void Connected(Session session, string name, bool mirroring)
    {
        lock (_sessionGate)
        {
            if (!ReferenceEquals(_active, session) || _stop.IsCancellationRequested) return;
            _stop.CancelAfter(Timeout.InfiniteTimeSpan);
            var status = _status;
            bool audioUnavailable = mirroring && session.MirrorAudioUnavailable;
            if (status.Stage == AirPlayStage.Connected && (!mirroring || status.Mirroring && status.MirrorAudioUnavailable == audioUnavailable)) return;
            Volatile.Write(ref _status, new(AirPlayStage.Connected, _identity.Name, name, Mirroring: mirroring, MirrorAudioUnavailable: audioUnavailable));
        }
        NotifyChanged();
    }
    private void Release(Session session, string? errorKey)
    {
        lock (_sessionGate)
        {
            if (!ReferenceEquals(_active, session)) return;
            Volatile.Write(ref _active, null);
            if (_stop.IsCancellationRequested) return;
            _stop.CancelAfter(WaitLimit);
            Volatile.Write(ref _status, new(AirPlayStage.Waiting, _identity.Name, ErrorKey: errorKey));
        }
        NotifyChanged();
    }
    private void Publish(AirPlayStatus status)
    {
        Volatile.Write(ref _status, status);
        NotifyChanged();
    }
    private void NotifyChanged()
    {
        if (Changed is { } handlers)
            foreach (Action handler in handlers.GetInvocationList())
                try { handler(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex.GetType().Name); }
    }

    internal void Disconnect(string sourceId)
    {
        var session = Volatile.Read(ref _active);
        if (session?.Receiving?.Id == sourceId) session.Stop();
    }

    internal void ShowMirror() => Volatile.Read(ref _active)?.ShowMirror();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _run.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }

    private sealed class Session(AirPlayReceiver receiver, TcpClient client, IPAddress local, IPAddress peer)
    {
        private CancellationTokenSource? _stop;
        private AirPlayPairing? _pairing;
        private AirPlayMirrorHandshake? _mirrorHandshake;
        private byte[]? _mediaKey, _mediaIv;
        private TcpListener? _eventListener;
        private Task? _eventTask;
        private AirPlayClock? _clock;
        private AirPlayMediaStream? _media;
        private UnplayedAudioPorts? _unplayedAudio;
        private AirPlayMirrorStream? _mirror;
        private AirPlayAudioOutput? _output;
        private bool _paired, _claimed, _recording;
        private string? _errorKey;
        private string _sender = "AirPlay";

        internal AirPlayAudioOutput? Receiving => Volatile.Read(ref _output) is { Pcm: not null } output ? output : null;
        internal int MirrorFrames => Volatile.Read(ref _mirror)?.PresentedFrames ?? 0;
        internal bool MirrorAudioUnavailable => Volatile.Read(ref _unplayedAudio) != null;
        internal void Stop() { try { _stop?.Cancel(); } catch (ObjectDisposedException) { } }
        internal void ShowMirror() => Volatile.Read(ref _mirror)?.Activate();

        internal async Task RunAsync(CancellationToken token)
        {
            using (client)
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                _stop = lifetime;
                try
                {
                    using var rtsp = new AirPlayRtsp(client.GetStream());
                    client.NoDelay = true;
                    while (!lifetime.IsCancellationRequested)
                    {
                        lifetime.CancelAfter(TimeSpan.FromSeconds(_claimed ? 75 : 12));
                        var request = await rtsp.ReadAsync(lifetime.Token).ConfigureAwait(false);
                        if (request == null || !await HandleAsync(rtsp, request, lifetime.Token).ConfigureAwait(false)) break;
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException
                    or InvalidOperationException or CryptographicException or ArgumentException or NotSupportedException)
                { if (!lifetime.IsCancellationRequested && ex is not EndOfStreamException) System.Diagnostics.Trace.WriteLine("AirPlay session: " + ex.GetType().Name); }
                finally
                {
                    Stop();
                    _eventListener?.Stop();
                    _unplayedAudio?.Dispose();
                    try
                    {
                        await Task.WhenAll(_media?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                            _mirror?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                            _clock?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                            _eventTask ?? Task.CompletedTask).ConfigureAwait(false);
                    }
                    finally
                    {
                        try
                        {
                            if (_output != null) await _output.DisposeAsync().ConfigureAwait(false);
                            _mirrorHandshake?.Dispose();
                            if (_mediaKey != null) CryptographicOperations.ZeroMemory(_mediaKey);
                            if (_mediaIv != null) CryptographicOperations.ZeroMemory(_mediaIv);
                        }
                        finally { receiver.Release(this, _errorKey); }
                    }
                }
            }
        }

        private async Task<bool> HandleAsync(AirPlayRtsp rtsp, AirPlayRtsp.Request request, CancellationToken token)
        {
            if (request.Method == "OPTIONS")
            {
                await rtsp.RespondAsync(request, token, extra: "Public: OPTIONS, SETUP, RECORD, FLUSH, FLUSHBUFFERED, TEARDOWN, GET_PARAMETER, SET_PARAMETER, SETPEERS, SETPEERSX, SETRATEANCHORTIME, SETMAGICCOOKIE\r\n").ConfigureAwait(false);
                return true;
            }
            if ((request.Method is "GET" or "POST") && request.Path == "/info")
            {
                await rtsp.RespondAsync(request, token, body: receiver._info, contentType: AirPlayRtsp.Plist).ConfigureAwait(false);
                return true;
            }
            if (!_paired)
            {
                if (request.Method == "POST" && request.Path == "/pair-setup")
                {
                    if (request.Body.Length == 32 && _pairing == null && _mirrorHandshake == null
                        && request.Header("Content-Type")?.StartsWith("application/pairing+", StringComparison.OrdinalIgnoreCase) != true)
                    {
                        _mirrorHandshake = new AirPlayMirrorHandshake();
                        await rtsp.RespondAsync(request, token, body: receiver._signingKey.PublicKey, contentType: AirPlayRtsp.Octets).ConfigureAwait(false);
                        return true;
                    }
                    if (_mirrorHandshake == null) return await PairAsync(rtsp, request, token).ConfigureAwait(false);
                }
                if (request.Method == "POST" && request.Path == "/pair-verify" && _mirrorHandshake is { } handshake)
                {
                    if (handshake.BeginVerify(request.Body, receiver._signingKey) is { } reply)
                    {
                        await rtsp.RespondAsync(request, token, body: reply, contentType: AirPlayRtsp.Octets).ConfigureAwait(false);
                        return true;
                    }
                    if (handshake.FinishVerify(request.Body))
                    {
                        _paired = true;
                        await rtsp.RespondAsync(request, token, contentType: AirPlayRtsp.Octets).ConfigureAwait(false);
                        return true;
                    }
                    await rtsp.RespondAsync(request, token, code: 403).ConfigureAwait(false);
                    return false;
                }
                await rtsp.RespondAsync(request, token, code: 403).ConfigureAwait(false);
                return true;
            }

            int code = 200;
            byte[]? response = null;
            string? type = null;
            switch (request.Method)
            {
                case "SETUP":
                    try { (code, response) = Setup(AirPlayPlist.Read(request.Body), token); type = response != null ? AirPlayRtsp.Plist : null; }
                    catch (InvalidDataException) { code = 400; }
                    catch (NotSupportedException ex) { System.Diagnostics.Trace.WriteLine("AirPlay setup: " + ex.Message); code = 415; }
                    catch (Exception ex) when (ex is SocketException or System.Runtime.InteropServices.COMException)
                    { _errorKey ??= "Phone.AirPlayAudioError"; code = 453; }
                    break;
                case "RECORD":
                    if (!_claimed) code = 455;
                    else { _recording = true; _media?.Record(); }
                    break;
                case "SETRATEANCHORTIME":
                    if (_media == null) { code = 455; break; }
                    var anchor = AirPlayPlist.Read(request.Body);
                    double rate = anchor.GetValueOrDefault("rate") is double d ? d : AirPlayPlist.Int(anchor, "rate");
                    if (rate is not (0 or 1)) { code = 400; break; }
                    _media.SetRate(rate == 1, Timestamp(AirPlayPlist.Int(anchor, "rtpTime", -1)),
                        _clock?.ToLocalTicks(AirPlayPlist.Int(anchor, "networkTimeSecs"), unchecked((ulong)AirPlayPlist.Int(anchor, "networkTimeFrac"))) ?? 0);
                    break;
                case "FLUSH":
                    _media?.Flush(null);
                    break;
                case "FLUSHBUFFERED":
                    var flush = AirPlayPlist.Read(request.Body);
                    _media?.Flush(Timestamp(AirPlayPlist.Int(flush, "flushUntilTS", -1)));
                    break;
                case "SET_PARAMETER":
                    if (_output != null && request.Header("Content-Type")?.StartsWith("text/parameters", StringComparison.OrdinalIgnoreCase) == true)
                        ReadVolume(request.Body);
                    break;
                case "GET_PARAMETER":
                    if (request.Body.AsSpan().StartsWith("volume"u8))
                    {
                        type = "text/parameters";
                        response = Encoding.ASCII.GetBytes(FormattableString.Invariant($"volume: {_output?.RemoteVolume ?? 0:F6}\r\n"));
                    }
                    break;
                case "SETPEERS":
                case "SETPEERSX":
                case "SETMAGICCOOKIE": // The negotiated stereo format supplies our decoder configuration.
                    if (!_claimed) code = 455;
                    break;
                case "TEARDOWN":
                    await rtsp.RespondAsync(request, token).ConfigureAwait(false);
                    return false;
                case "POST" when request.Path == "/fp-setup":
                    if (_mediaKey != null) { code = 455; break; }
                    response = (_mirrorHandshake ??= new AirPlayMirrorHandshake()).ExchangeFairPlay(request.Body);
                    if (response == null) code = 400;
                    else type = AirPlayRtsp.Octets;
                    break;
                case "POST" when request.Path == "/feedback":
                    if (_media != null)
                    {
                        response = AirPlayPlist.Write(new() { ["streams"] = new object[] { new Dictionary<string, object?> { ["streamID"] = 1, ["type"] = _media.Type } } });
                        type = AirPlayRtsp.Plist;
                    }
                    break;
                default: code = 501; break;
            }
            await rtsp.RespondAsync(request, token, code, response, type).ConfigureAwait(false);
            return code != 453;
        }

        private async Task<bool> PairAsync(AirPlayRtsp rtsp, AirPlayRtsp.Request request, CancellationToken token)
        {
            var values = AirPlayPairing.ReadTlv(request.Body);
            bool first = AirPlayPairing.IsState(values, 1);
            if (first && _pairing == null && AirPlayPairing.IsTransient(values))
            {
                _pairing = new AirPlayPairing();
                await rtsp.RespondAsync(request, token, body: _pairing.Challenge, contentType: AirPlayRtsp.Pairing).ConfigureAwait(false);
                return true;
            }
            if (AirPlayPairing.IsState(values, 3) && _pairing?.Verify(values) is { } pairing)
            {
                _pairing = null;
                try
                {
                    await rtsp.RespondAsync(request, token, body: pairing.Proof, contentType: AirPlayRtsp.Pairing).ConfigureAwait(false);
                    rtsp.Secure(pairing.Key);
                    _paired = true;
                }
                finally { CryptographicOperations.ZeroMemory(pairing.Key); }
                return true;
            }
            await rtsp.RespondAsync(request, token, body: AirPlayPairing.Tlv((6, [first ? (byte)2 : (byte)4]), (7, [2])),
                contentType: AirPlayRtsp.Pairing).ConfigureAwait(false);
            return false;
        }

        private (int Code, byte[]? Data) Setup(Dictionary<string, object?> setup, CancellationToken token)
        {
            if (!setup.TryGetValue("streams", out var streams))
            {
                if (_claimed) return (455, null);
                string? protocol = setup.GetValueOrDefault("timingProtocol") as string;
                if (setup.ContainsKey("ekey") || setup.ContainsKey("eiv"))
                {
                    if (setup.GetValueOrDefault("ekey") is not byte[] { Length: 72 } wrapped
                        || setup.GetValueOrDefault("eiv") is not byte[] { Length: 16 } iv
                        || _mirrorHandshake?.UnwrapKey(wrapped) is not { } key) return (400, null);
                    if (_mediaKey != null) CryptographicOperations.ZeroMemory(_mediaKey);
                    _mediaKey = key;
                    _mediaIv = iv;
                    protocol ??= "NTP";
                }
                if (protocol is not ("PTP" or "NTP")) return (415, null);
                if (!receiver.Claim(this)) return (453, null);
                _claimed = true;
                _sender = GetSenderName(setup);
                _eventListener = new TcpListener(local, 0);
                _eventListener.Start(2);
                _eventTask = HoldEventConnectionAsync(_eventListener, peer, token);
                long timingPort = _mediaKey != null ? AirPlayPlist.Int(setup, "timingPort") : 0;
                _clock = new AirPlayClock(local, peer, protocol == "PTP", receiver._identity.Id, token,
                    timingPort is > 0 and <= ushort.MaxValue ? (int)timingPort : 0);
                var answer = new Dictionary<string, object?>
                {
                    ["eventPort"] = ((IPEndPoint)_eventListener.LocalEndpoint).Port,
                    ["timingPort"] = _clock.TimingPort
                };
                if (protocol == "PTP")
                {
                    answer["timingPeerInfo"] = new Dictionary<string, object?>
                    {
                        ["Addresses"] = new object[] { local.ToString() }, ["ID"] = receiver._identity.DeviceId,
                        ["SupportsClockPortMatchingOverride"] = false
                    };
                }
                return (200, AirPlayPlist.Write(answer));
            }
            if (!_claimed) return (455, null);
            if (streams is not object[] { Length: >= 1 and <= 2 } items) return (415, null);
            var answers = new object[items.Length];
            var configured = 0;
            // Validate both requests before opening either data listener.
            Dictionary<string, object?>? audio = null;
            int audioType = 0;
            AirPlayFormat format = default;
            byte[]? audioKey = null, audioIv = null;
            ulong? mirrorId = null;
            foreach (var item in items)
            {
                if (item is not Dictionary<string, object?> settings) return (400, null);
                long type = AirPlayPlist.Int(settings, "type");
                if (type == 110)
                {
                    if (_mirror != null || mirrorId != null) return (455, null);
                    if (_mediaKey == null || settings.GetValueOrDefault("streamConnectionID") is not long id) return (400, null);
                    mirrorId = unchecked((ulong)id);
                    continue;
                }
                if (type is not (96 or 103)) return (415, null);
                if (_media != null || _unplayedAudio != null || audio != null) return (455, null);
                audio = settings;
                audioType = (int)type;
                if (settings.GetValueOrDefault("shk") is byte[] { Length: 32 } secret) audioKey = secret;
                else if (type == 96 && _mediaKey != null && _mediaIv != null)
                {
                    audioKey = _mediaKey;
                    audioIv = _mediaIv;
                }
                else return (415, null);
            }
            // Apple can attach AAC-ELD to a mirror stream. Windows' built-in AAC transform
            // handles LC, so keep the picture working and report that this audio is unavailable.
            bool pictureOnly = audioType == 96 && audio != null && AirPlayPlist.Int(audio, "ct") == 8
                && (mirrorId != null || _mirror != null || audio.GetValueOrDefault("usingScreen") is true);
            if (audio != null && !pictureOnly) format = AirPlayFormat.Parse(audio, legacy: audioIv != null);
            if (mirrorId is ulong streamId)
            {
                try
                {
                    Volatile.Write(ref _mirror, new AirPlayMirrorStream(local, peer, streamId, _mediaKey!, _sender, receiver._showWindow, receiver._hevc,
                        () => receiver.Connected(this, _sender, mirroring: true),
                        failed => StreamEnded(failed, "Phone.AirPlayMirrorError"), token));
                    answers[configured++] = new Dictionary<string, object?> { ["type"] = 110, ["dataPort"] = _mirror!.DataPort };
                }
                catch (SocketException)
                {
                    _errorKey = "Phone.AirPlayMirrorError";
                    return (453, null);
                }
            }
            if (audio != null)
            {
                try
                {
                    int controlPort, dataPort;
                    if (pictureOnly)
                    {
                        var ports = new UnplayedAudioPorts(local);
                        Volatile.Write(ref _unplayedAudio, ports);
                        controlPort = ports.ControlPort; dataPort = ports.DataPort;
                        if (MirrorFrames > 0) receiver.Connected(this, _sender, mirroring: true);
                    }
                    else
                    {
                        _output = new AirPlayAudioOutput(_sender, OutputChanged, () => StreamEnded(true, "Phone.AirPlayAudioError"),
                            receiver._player, receiver._watchDefault, token);
                        _media = new AirPlayMediaStream(audioType, format, audioKey!, local, peer, _output,
                            failed => StreamEnded(failed, "Phone.AirPlayAudioError"), token, audioIv);
                        if (_recording) _media.Record();
                        controlPort = _media.ControlPort; dataPort = _media.DataPort;
                    }
                    answers[configured] = new Dictionary<string, object?>
                    {
                        ["type"] = audioType, ["streamID"] = 1, ["controlPort"] = controlPort,
                        ["dataPort"] = dataPort, ["audioBufferSize"] = 256 * 1024
                    };
                }
                finally { if (audioIv == null) CryptographicOperations.ZeroMemory(audioKey!); }
            }
            return (200, AirPlayPlist.Write(new() { ["streams"] = answers }));
        }

        private void OutputChanged()
        {
            if (receiver.Status.Stage != AirPlayStage.Connected && Receiving is { } source) receiver.Connected(this, source.Name, mirroring: false);
            else receiver.NotifyChanged();
        }

        private void StreamEnded(bool failed, string errorKey)
        {
            if (failed) Interlocked.CompareExchange(ref _errorKey, errorKey, null);
            Stop();
        }

        private static async Task HoldEventConnectionAsync(TcpListener listener, IPAddress peer, CancellationToken token)
        {
            try
            {
                using var accepted = await AirPlayMediaStream.AcceptPeerAsync(listener, peer, token).ConfigureAwait(false);
                byte[] buffer = new byte[2048];
                var stream = accepted.GetStream();
                while (await stream.ReadAsync(buffer, token).ConfigureAwait(false) != 0) { }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
        }

        private void ReadVolume(byte[] content)
        {
            foreach (string line in Encoding.ASCII.GetString(content).Split("\r\n"))
                if (line.StartsWith("volume:", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(line.AsSpan(7), NumberStyles.Float, CultureInfo.InvariantCulture, out double db)) _output!.SetRemoteVolume(db);
        }

        private static uint? Timestamp(long value) => value is >= 0 and <= uint.MaxValue ? (uint)value : null;
        private static string GetSenderName(Dictionary<string, object?> setup)
        {
            string? name = (setup.GetValueOrDefault("name") ?? setup.GetValueOrDefault("deviceName")) as string;
            if (string.IsNullOrWhiteSpace(name)) return "AirPlay";
            return string.Concat(name.Where(c => !char.IsControl(c)).Take(64)).Trim() is { Length: > 0 } safe ? safe : "AirPlay";
        }
    }

    private sealed class UnplayedAudioPorts : IDisposable
    {
        private readonly Socket _control, _data;

        internal UnplayedAudioPorts(IPAddress address)
        {
            _control = Bind(address);
            try { _data = Bind(address); }
            catch { _control.Dispose(); throw; }
        }

        internal int ControlPort => ((IPEndPoint)_control.LocalEndPoint!).Port;
        internal int DataPort => ((IPEndPoint)_data.LocalEndPoint!).Port;

        private static Socket Bind(IPAddress address)
        {
            // Keep the advertised UDP endpoints local to this session. There is no audio
            // task, decoder or PCM queue when only the screen can be used.
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.ReceiveBufferSize = 4096;
                socket.Bind(new IPEndPoint(address, 0));
                return socket;
            }
            catch { socket.Dispose(); throw; }
        }

        public void Dispose() { _control.Dispose(); _data.Dispose(); }
    }
}
