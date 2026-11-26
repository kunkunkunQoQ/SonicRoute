using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SonicRoute.AndroidAudio;

namespace SonicRoute.AirPlay;

internal enum AirPlayStage { Off, Starting, Waiting, Connected, Failed, Stopping }
internal sealed record AirPlayStatus(AirPlayStage Stage, string? Name = null, string? Sender = null, string? ErrorKey = null,
    int? NetworkError = null, bool Mirroring = false, bool MirrorAudioUnavailable = false,
    bool Topmost = false, bool Fullscreen = false, string? Code = null, AirPlayMetadata? Metadata = null, string? SenderId = null);
internal sealed class AirPlayNoNetworkException() : InvalidOperationException("No active IPv4 LAN interface.");

/// <summary>An on-demand AirPlay receiver for audio and screen mirroring.</summary>
internal sealed class AirPlayReceiver : IAsyncDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(2);
    private const int CodeAttemptLimit = 6;
    private sealed record PairingCode(string Value);
    private readonly TcpListener _listener;
    private readonly AirPlayListenerPorts _ports;
    private readonly AirPlayDiscovery? _discovery;
    private readonly CancellationTokenSource _stop;
    private readonly Task _run;
    private readonly AirPlayIdentity _identity;
    private readonly AirPlaySigningKey _signingKey;
    private readonly bool _showWindow;
    private readonly bool _hevc;
    private readonly bool _requireCode;
    private readonly AirPlayDiscovery.Lan[] _lan;
    private readonly Func<AndroidPcmSource, string?, CancellationToken, IPcmPlayer> _player;
    private readonly Func<Action, IDisposable> _watchDefault;
    private readonly AndroidAudioSettingsStore? _preferences;
    private readonly Func<string?[], string?[]>? _resolveOutputs;
    private readonly byte[] _info;
    private readonly object _sessionGate = new();
    private Session? _active;
    private PairingCode? _code;
    private int _codeAttempts;
    private int _lastCode = 3939;
    private AirPlayStatus _status;
    private int _disposed;

    internal event Action? Changed;
    internal event Action? DisplayChanged;
    internal AirPlayStatus Status => Volatile.Read(ref _status);
    internal AirPlayAudioOutput? Source => Volatile.Read(ref _active)?.Receiving;
    internal int MirrorFrames => Volatile.Read(ref _active)?.MirrorFrames ?? 0;
    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    internal Task Completion => _run;
    internal bool MatchesInterfaces() => _lan.ToHashSet().SetEquals(AirPlayDiscovery.ReadInterfaces());

    // Only the test harness asks for loopback. Production never changes Windows firewall rules.
    internal AirPlayReceiver(string name, bool loopback = false,
        Func<AndroidPcmSource, string?, CancellationToken, IPcmPlayer>? player = null, Func<Action, IDisposable>? watchDefault = null,
        bool preferHevc = false, bool requireCode = false, bool useStorePorts = false,
        AndroidAudioSettingsStore? preferences = null, Func<string?[], string?[]>? resolveOutputs = null)
    {
        if (!ChaCha20Poly1305.IsSupported) throw new PlatformNotSupportedException("AirPlay control encryption is unavailable.");
        _player = player ?? ((pcm, endpoint, _) => new WasapiPcmPlayer(pcm, endpoint));
        _watchDefault = watchDefault ?? DefaultOutputNotifications.Subscribe;
        _preferences = preferences;
        _resolveOutputs = resolveOutputs;
        _lan = loopback ? [] : AirPlayDiscovery.ReadInterfaces();
        if (!loopback && _lan.Length == 0) throw new AirPlayNoNetworkException();
        _showWindow = !loopback;
        _hevc = preferHevc && MediaFoundationH264Decoder.SupportsHevc;
        _requireCode = requireCode;
        _signingKey = new AirPlaySigningKey();
        _stop = new CancellationTokenSource();
        _ports = new AirPlayListenerPorts(useStorePorts);
        try
        {
            _identity = new AirPlayIdentity(name, _signingKey.PublicKey, _hevc, requireCode, preferences?.AirPlayId);
            _info = _identity.Info();
            _status = WaitingStatus(null);
            _listener = _ports.StartTcp(loopback ? IPAddress.Loopback : IPAddress.Any, 8);
            if (!loopback) _discovery = new AirPlayDiscovery(_identity, Port, _lan);
            if (_preferences == null) _stop.CancelAfter(WaitLimit);
            _run = RunAsync(_stop.Token);
        }
        catch { _listener?.Stop(); _stop.Dispose(); _signingKey.Dispose(); throw; }
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

    private AirPlayStatus WaitingStatus(string? errorKey)
    {
        if (_requireCode)
        {
            // Exclude the public default PIN so protection can never silently use it.
            int value;
            do { value = RandomNumberGenerator.GetInt32(10000); } while (value == 3939 || value == _lastCode);
            _lastCode = value;
            _code = new(value.ToString("D4", CultureInfo.InvariantCulture));
            _codeAttempts = 0;
        }
        return new(AirPlayStage.Waiting, _identity.Name, ErrorKey: errorKey, Code: _code?.Value);
    }

    private bool TryBeginPairing(out PairingCode? code)
    {
        code = null;
        if (!_requireCode) return true;
        lock (_sessionGate)
        {
            if (_active != null || _code == null || _stop.IsCancellationRequested) return false;
            if (_codeAttempts < CodeAttemptLimit)
            {
                ++_codeAttempts;
                code = _code;
                return true;
            }
            if (_status.Code == null) return false;
            Volatile.Write(ref _status, _status with { Code = null, ErrorKey = "Phone.AirPlayPinAttempts" });
        }
        Notify(DisplayChanged);
        return false;
    }

    private bool Claim(Session session, PairingCode? code)
    {
        lock (_sessionGate)
        {
            if (_active != null || _stop.IsCancellationRequested) return false;
            if (_requireCode && (code == null || !ReferenceEquals(_code, code))) return false;
            _code = null;
            Volatile.Write(ref _active, session);
            if (_status.Code != null || _status.ErrorKey != null)
                Volatile.Write(ref _status, _status with { Code = null, ErrorKey = null });
        }
        if (_requireCode) Notify(DisplayChanged);
        return true;
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
            Volatile.Write(ref _status, status with { Stage = AirPlayStage.Connected, Sender = name, SenderId = session.SenderId, ErrorKey = null,
                Mirroring = mirroring, MirrorAudioUnavailable = audioUnavailable });
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
            if (_preferences == null) _stop.CancelAfter(WaitLimit);
            Volatile.Write(ref _status, WaitingStatus(errorKey));
        }
        NotifyChanged();
    }
    private void Publish(AirPlayStatus status)
    {
        Volatile.Write(ref _status, status);
        NotifyChanged();
    }
    private void NotifyChanged() => Notify(Changed);

    private static void Notify(Action? handlers)
    {
        if (handlers != null)
            foreach (Action handler in handlers.GetInvocationList())
                try { handler(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex.GetType().Name); }
    }

    private void SetMetadata(Session session, AirPlayMetadata metadata)
    {
        lock (_sessionGate)
        {
            if (!ReferenceEquals(_active, session) || _stop.IsCancellationRequested) return;
            Volatile.Write(ref _status, _status with { Metadata = metadata });
        }
        Notify(DisplayChanged);
    }

    private void MirrorModeChanged(Session session, bool topmost, bool fullscreen)
    {
        lock (_sessionGate)
        {
            if (!ReferenceEquals(_active, session) || _stop.IsCancellationRequested) return;
            if (_status.Topmost == topmost && _status.Fullscreen == fullscreen) return;
            Volatile.Write(ref _status, _status with { Topmost = topmost, Fullscreen = fullscreen });
        }
        Notify(DisplayChanged);
    }

    internal void Disconnect(string sourceId)
    {
        var session = Volatile.Read(ref _active);
        if (session?.Receiving is { } source && source.Id == sourceId)
        {
            _preferences?.StopExternalAudio(source.PreferenceKey);
            session.Stop();
        }
    }

    internal void ShowMirror() => Volatile.Read(ref _active)?.ShowMirror();
    internal void SetMirrorTopmost(bool value) => Volatile.Read(ref _active)?.SetMirrorTopmost(value);
    internal void SetMirrorFullscreen(bool value) => Volatile.Read(ref _active)?.SetMirrorFullscreen(value);

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
        private AirPlayPairing? _legacyPin;
        private PairingCode? _pairingCode;
        private byte[]? _legacySessionKey, _pinClientKey;
        private bool _waitingForCode;
        private AirPlayMirrorHandshake? _mirrorHandshake;
        private AirPlayMetadataReader? _metadata;
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
        internal string? SenderId { get; private set; }

        internal AirPlayAudioOutput? Receiving => Volatile.Read(ref _output) is { Pcm: not null } output ? output : null;
        internal int MirrorFrames => Volatile.Read(ref _mirror)?.PresentedFrames ?? 0;
        internal bool MirrorAudioUnavailable => Volatile.Read(ref _unplayedAudio) != null;
        internal void Stop() { try { _stop?.Cancel(); } catch (ObjectDisposedException) { } }
        internal void ShowMirror() => Volatile.Read(ref _mirror)?.Activate();
        internal void SetMirrorTopmost(bool value) => Volatile.Read(ref _mirror)?.SetTopmost(value);
        internal void SetMirrorFullscreen(bool value) => Volatile.Read(ref _mirror)?.SetFullscreen(value);

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
                        lifetime.CancelAfter(TimeSpan.FromSeconds(_claimed ? 75 : _waitingForCode && !_paired ? 50 : 12));
                        var request = await rtsp.ReadAsync(lifetime.Token, allowArtwork: _claimed).ConfigureAwait(false);
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
                            if (_legacySessionKey != null) CryptographicOperations.ZeroMemory(_legacySessionKey);
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
                if (receiver._requireCode && request.Method == "POST")
                {
                    if (request.Path == "/pair-pin-start")
                    {
                        _waitingForCode = true;
                        await rtsp.RespondAsync(request, token).ConfigureAwait(false);
                        return true;
                    }
                    if (request.Path == "/pair-setup-pin") return await PairLegacyPinAsync(rtsp, request, token).ConfigureAwait(false);
                }
                if (request.Method == "POST" && request.Path == "/pair-setup")
                {
                    if (request.Body.Length == 32 && _pairing == null
                        && request.Header("Content-Type")?.StartsWith("application/pairing+", StringComparison.OrdinalIgnoreCase) != true)
                    {
                        if (receiver._requireCode && !request.Body.AsSpan().SequenceEqual(_pinClientKey))
                        {
                            await rtsp.RespondAsync(request, token, code: 470).ConfigureAwait(false);
                            return false;
                        }
                        _mirrorHandshake ??= new AirPlayMirrorHandshake();
                        await rtsp.RespondAsync(request, token, body: receiver._signingKey.PublicKey, contentType: AirPlayRtsp.Octets).ConfigureAwait(false);
                        return true;
                    }
                    if (_mirrorHandshake == null && _legacyPin == null && _legacySessionKey == null)
                        return await PairAsync(rtsp, request, token).ConfigureAwait(false);
                }
                if (request.Method == "POST" && request.Path == "/pair-verify" && _mirrorHandshake is { } handshake)
                {
                    bool validKey = !receiver._requireCode || request.Body.Length == 68 && request.Body.AsSpan(36).SequenceEqual(_pinClientKey);
                    if (validKey && handshake.BeginVerify(request.Body, receiver._signingKey) is { } reply)
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
                    await rtsp.RespondAsync(request, token, code: receiver._requireCode ? 470 : 403).ConfigureAwait(false);
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
                    try { (code, response) = Setup(AirPlayPlist.Read(request.Body), token, request.Header("X-Apple-Device-ID")); type = response != null ? AirPlayRtsp.Plist : null; }
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
                    ReadParameter(request);
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
            if (first && _pairing == null && AirPlayPairing.IsTransient(values) && receiver.TryBeginPairing(out _pairingCode))
            {
                _waitingForCode = receiver._requireCode;
                _pairing = new AirPlayPairing(_pairingCode?.Value ?? "3939");
                await rtsp.RespondAsync(request, token, body: _pairing.Challenge, contentType: AirPlayRtsp.Pairing).ConfigureAwait(false);
                return true;
            }
            if (AirPlayPairing.IsState(values, 3) && values.TryGetValue(3, out var publicKey) && values.TryGetValue(4, out var proof)
                && _pairing?.Verify(publicKey, proof) is { } pairing)
            {
                _pairing = null;
                try
                {
                    await rtsp.RespondAsync(request, token, body: AirPlayPairing.Tlv((6, [4]), (4, pairing.Proof)), contentType: AirPlayRtsp.Pairing).ConfigureAwait(false);
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

        private async Task<bool> PairLegacyPinAsync(AirPlayRtsp rtsp, AirPlayRtsp.Request request, CancellationToken token)
        {
            byte[]? reply = null;
            if (request.Body.Length <= 4096 && request.Header("Content-Type")?.StartsWith(AirPlayRtsp.Plist, StringComparison.OrdinalIgnoreCase) == true)
            {
                try { reply = ReadLegacyPin(AirPlayPlist.Read(request.Body)); }
                catch (CryptographicException ex) { System.Diagnostics.Trace.WriteLine("AirPlay PIN: " + ex.GetType().Name); }
                catch (InvalidDataException) { }
            }
            await rtsp.RespondAsync(request, token, code: reply == null ? 470 : 200, body: reply,
                contentType: reply == null ? null : AirPlayRtsp.Plist).ConfigureAwait(false);
            return reply != null;
        }

        private byte[]? ReadLegacyPin(Dictionary<string, object?> values)
        {
            if (_pairing != null || _mirrorHandshake != null) return null;
            if (_legacyPin == null && _legacySessionKey == null && values.GetValueOrDefault("method") is "pin"
                && values.GetValueOrDefault("user") is string { Length: > 0 and <= 128 } user
                && receiver.TryBeginPairing(out _pairingCode))
            {
                _waitingForCode = true;
                _legacyPin = new AirPlayPairing(_pairingCode!.Value, user);
                return AirPlayPlist.Write(new() { ["pk"] = _legacyPin.PublicKey, ["salt"] = _legacyPin.Salt });
            }
            if (_legacyPin is { } legacy && values.GetValueOrDefault("pk") is byte[] pk && values.GetValueOrDefault("proof") is byte[] proof)
            {
                _legacyPin = null;
                if (legacy.Verify(pk, proof) is not { } verified) return null;
                _legacySessionKey = verified.Key;
                return AirPlayPlist.Write(new() { ["proof"] = verified.Proof });
            }
            if (_legacySessionKey is not { } key || values.GetValueOrDefault("epk") is not byte[] { Length: 32 } epk
                || values.GetValueOrDefault("authTag") is not byte[] { Length: 16 } tag) return null;
            _legacySessionKey = null;
            byte[] clientKey = new byte[32], ciphertext = new byte[32], serverTag = new byte[16];
            try
            {
                if (!AirPlayPinExchange.Confirm(key, epk, tag, receiver._signingKey.PublicKey, clientKey, ciphertext, serverTag)) return null;
                _pinClientKey = clientKey;
                _mirrorHandshake = new AirPlayMirrorHandshake();
                return AirPlayPlist.Write(new() { ["epk"] = ciphertext, ["authTag"] = serverTag });
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }

        private (int Code, byte[]? Data) Setup(Dictionary<string, object?> setup, CancellationToken token, string? headerDeviceId)
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
                if (!receiver.Claim(this, _pairingCode)) return (453, null);
                _claimed = true;
                _sender = GetSenderName(setup);
                if ((setup.GetValueOrDefault("deviceID") as string ?? headerDeviceId) is { Length: > 0 and <= 64 } senderId
                    && !string.IsNullOrWhiteSpace(senderId) && !senderId.Any(char.IsControl))
                    SenderId = senderId.Trim();
                _eventListener = receiver._ports.StartTcp(local, 2);
                _eventTask = HoldEventConnectionAsync(_eventListener, peer, token);
                long timingPort = _mediaKey != null ? AirPlayPlist.Int(setup, "timingPort") : 0;
                _clock = new AirPlayClock(receiver._ports, local, peer, protocol == "PTP", receiver._identity.Id, token,
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
            bool pictureOnly = false;
            if (audio != null)
            {
                try { format = AirPlayFormat.Parse(audio, legacy: audioIv != null); }
                catch (NotSupportedException) when (audioType == 96 && AirPlayPlist.Int(audio, "ct") == AirPlayFormat.Eld
                    && (mirrorId != null || _mirror != null || audio.GetValueOrDefault("usingScreen") is true))
                {
                    // Keep the mirror usable with other ELD profiles, such as low-delay SBR.
                    pictureOnly = true;
                }
            }
            if (mirrorId is ulong streamId)
            {
                try
                {
                    Volatile.Write(ref _mirror, new AirPlayMirrorStream(receiver._ports, local, peer, streamId, _mediaKey!, _sender, receiver._showWindow, receiver._hevc,
                        () => receiver.Connected(this, _sender, mirroring: true),
                        (topmost, fullscreen) => receiver.MirrorModeChanged(this, topmost, fullscreen),
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
                        var ports = new UnplayedAudioPorts(receiver._ports, local);
                        Volatile.Write(ref _unplayedAudio, ports);
                        controlPort = ports.ControlPort; dataPort = ports.DataPort;
                        if (MirrorFrames > 0) receiver.Connected(this, _sender, mirroring: true);
                    }
                    else
                    {
                        _output = new AirPlayAudioOutput(_sender, OutputChanged, () => StreamEnded(true, "Phone.AirPlayAudioError"),
                            receiver._player, receiver._watchDefault, token, receiver._preferences,
                            ExternalAudioPreference.AirPlayKey(SenderId, _sender), receiver._resolveOutputs);
                        _media = new AirPlayMediaStream(receiver._ports, audioType, format, audioKey!, local, peer, _output,
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

        private void ReadParameter(AirPlayRtsp.Request request)
        {
            if (!_claimed || request.BodySkipped || request.Header("Content-Type") is not string contentType) return;
            var type = contentType.Split(';', 2)[0].Trim();
            if (type.Equals("text/parameters", StringComparison.OrdinalIgnoreCase))
            {
                if (_output != null) ReadVolume(request.Body);
                return;
            }
            if (!type.Equals("application/x-dmap-tagged", StringComparison.OrdinalIgnoreCase) && !AirPlayMetadataReader.IsArtworkType(type)) return;
            var metadata = _metadata ??= new();
            uint? time = null;
            if (request.Header("RTP-Info") is { } rtpInfo)
                foreach (string part in rtpInfo.Split(';', StringSplitOptions.TrimEntries))
                    if (part.StartsWith("rtptime=", StringComparison.OrdinalIgnoreCase)
                        && uint.TryParse(part.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out uint value)) time = value;
            if (metadata.Update(type, request.Body, time)) receiver.SetMetadata(this, metadata.Value);
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

        internal UnplayedAudioPorts(AirPlayListenerPorts ports, IPAddress address)
        {
            // Retain the advertised endpoints for picture-only sessions.
            _control = ports.BindUdp(address, receiveBufferSize: 4096);
            try { _data = ports.BindUdp(address, receiveBufferSize: 4096); }
            catch { _control.Dispose(); throw; }
        }

        internal int ControlPort => ((IPEndPoint)_control.LocalEndPoint!).Port;
        internal int DataPort => ((IPEndPoint)_data.LocalEndPoint!).Port;

        public void Dispose() { _control.Dispose(); _data.Dispose(); }
    }
}
