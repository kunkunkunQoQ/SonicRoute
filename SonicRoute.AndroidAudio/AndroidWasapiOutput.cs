using System.Runtime.InteropServices;
using SonicRoute.Native.Audio;

namespace SonicRoute.AndroidAudio;

// Marshals stream activation, buffer access and release onto the same MTA thread.
internal sealed class AndroidWasapiOutput : IDisposable
{
    private readonly IWasapiPlaybackSource _source;
    private readonly string? _requestedDevice;
    private readonly AutoResetEvent _control = new(false);
    private readonly AutoResetEvent _samples = new(false);
    private readonly object _gate = new();
    private readonly object _commandGate = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private bool _playing, _closing, _exited;
    private int _disposed;
    private Exception? _failure;
    private TaskCompletionSource? _pending;
    internal string EndpointId { get; private set; } = "";
    internal int LatencyMs { get; private set; }
    internal double DefaultPeriodMs { get; private set; }
    internal event Action<Exception>? Failed;

    internal AndroidWasapiOutput(IWasapiPlaybackSource source, string? endpoint)
    {
        _source = source;
        _requestedDevice = endpoint;
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "SonicRoute WASAPI playback" };
        _thread.SetApartmentState(ApartmentState.MTA);
        try { _thread.Start(); }
        catch { _samples.Dispose(); _control.Dispose(); throw; }
        try { _ready.Task.WaitAsync(TimeSpan.FromSeconds(6)).GetAwaiter().GetResult(); }
        catch { Dispose(); throw; }
    }

    private unsafe void RenderLoop()
    {
        try
        {
            using var mta = new MtaScope();
            using var devices = new Endpoints();
            using var device = _requestedDevice == null ? devices.Default(Flow.Render, Role.Multimedia) : devices.Open(_requestedDevice);
            EndpointId = Endpoints.Id(device);
            using (var query = Endpoints.Activate(device, Endpoints.AudioClient))
            {
                long normal = 0;
                Com.Check(((delegate* unmanaged[Stdcall]<nint,long*,long*,int>)query.Slot(9))(query.Handle,&normal,null), "GetDevicePeriod");
                DefaultPeriodMs = normal / 10000d;
            }
            using var client = OpenClient(device);
            using var render = SharedAudioBuffers.OpenRenderService(client);
            uint capacity = SharedAudioBuffers.Capacity(client);
            var data = new byte[checked((int)capacity * _source.WaveFormat.BlockAlign)];
            WaitHandle[] waits = [_control, _samples];
            bool running = false;
            _ready.TrySetResult();
            try
            {
                while (true)
                {
                    bool play;
                    TaskCompletionSource? command;
                    lock (_gate) { if (_closing) break; play = _playing; command = _pending; }
                    if (play)
                    {
                        Fill(client, render, capacity, data);
                        if (!running) { Com.Check(client.Call(10), "IAudioClient.Start"); running = true; }
                    }
                    else if (running)
                    {
                        Com.Check(client.Call(11), "IAudioClient.Stop");
                        running = false;
                        Com.Check(client.Call(12), "IAudioClient.Reset");
                    }
                    command?.TrySetResult();
                    if (running) WaitHandle.WaitAny(waits);
                    else _control.WaitOne();
                }
            }
            finally { if (running) client.Call(11); }
        }
        catch (Exception ex)
        {
            lock (_gate) { _failure = ex; _pending?.TrySetException(ex); }
            if (!_ready.TrySetException(ex)) ThreadPool.QueueUserWorkItem(_ => Failed?.Invoke(ex));
        }
        finally
        {
            lock (_gate)
            {
                _exited = true;
                _samples.Dispose();
                _control.Dispose();
            }
        }
    }

    private unsafe Com OpenClient(Com device)
    {
        var mode = _source is AndroidPcmSource pcm ? pcm.LatencyMode : AudioLatencyMode.Balanced;
        int[] candidates = AudioLatencyProfile.For(mode).GetWasapiLatencyCandidates(DefaultPeriodMs);
        var wave = _source.WaveFormat;
        var format = new PcmFormat
        {
            Tag = (ushort)wave.Encoding, Channels = checked((ushort)wave.Channels),
            Rate = checked((uint)wave.SampleRate), AverageBytes = checked((uint)wave.AverageBytesPerSecond),
            Alignment = checked((ushort)wave.BlockAlign), Bits = checked((ushort)wave.BitsPerSample)
        };
        for (int i = 0; i < candidates.Length; i++)
        {
            var client = Endpoints.Activate(device, Endpoints.AudioClient);
            try
            {
                Com.Check(SharedAudioBuffers.Initialize(client, format, 0, candidates[i] * 10000L, null, _samples),
                    "Initialize shared WASAPI event output");
                LatencyMs = candidates[i];
                return client;
            }
            catch (Exception ex) when (i + 1 < candidates.Length && candidates[i] < 100 && ex is COMException or ArgumentException)
            { client.Dispose(); }
            catch { client.Dispose(); throw; }
        }
        throw new IOException("No WASAPI latency profile was accepted.");
    }

    private void Fill(Com client, Com render, uint capacity, byte[] data)
    {
        uint frames = capacity - SharedAudioBuffers.Padding(client);
        if (frames == 0) return;
        int bytes = checked((int)frames * _source.WaveFormat.BlockAlign);
        int actual = _source.Read(data, 0, bytes);
        if ((uint)actual > (uint)bytes) throw new InvalidDataException("Invalid PCM read length.");
        Array.Clear(data, actual, bytes - actual);
        SharedAudioBuffers.Write(render, data.AsSpan(0, bytes), _source.WaveFormat.BlockAlign);
    }

    internal void SetPlaying(bool value)
    {
        lock (_commandGate)
        {
            var command = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_failure != null) throw new IOException("WASAPI render thread stopped", _failure);
                ObjectDisposedException.ThrowIf(_closing, this);
                _playing = value;
                _pending = command;
                _control.Set();
            }
            try { command.Task.WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult(); }
            catch (TimeoutException) { Dispose(); throw; }
            lock (_gate) _pending = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_gate)
        {
            _closing = true;
            _pending?.TrySetCanceled();
            if (!_exited) _control.Set();
        }
        // A stuck driver cannot hold the application UI on shutdown. The MTA remains
        // the sole owner and releases COM objects and wait handles upon returning.
        if (_thread.IsAlive && _thread != Thread.CurrentThread) _thread.Join(TimeSpan.FromSeconds(2));
    }
}

