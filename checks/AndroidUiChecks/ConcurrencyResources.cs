using System.Collections;
using System.IO;
using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private sealed class CleanupInputStream : Stream
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Block, FailFlush;
        internal int Packets;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { if (FailFlush) throw new IOException("fixture closed input flush"); }
        public override Task FlushAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Flush(); return Task.CompletedTask; }
        public override void Write(byte[] buffer, int offset, int count) => Interlocked.Increment(ref Packets);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        {
            Entered.TrySetResult();
            if (Block) await Resume.Task.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Packets);
        }
    }

    private sealed class CleanupShell : IAdbShellSession
    {
        private readonly Pipe _output = new(), _error = new();
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly CleanupInputStream Input = new();
        internal bool Disposed;
        public CleanupShell()
        {
            OutputStream = _output.Reader.AsStream(); ErrorStream = _error.Reader.AsStream();
        }
        public Stream OutputStream { get; }
        public Stream ErrorStream { get; }
        public Stream InputStream => Input;
        public bool HasExited => _exit.Task.IsCompleted;
        public int? ExitCode => HasExited ? 0 : null;
        internal Task PublishAsync(string line) => _output.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n")).AsTask();
        public Task<int> WaitForExitAsync(CancellationToken token = default) => _exit.Task.WaitAsync(token);
        public Task CloseInputAsync(CancellationToken token = default) { End(); return Task.CompletedTask; }
        public void Kill() => End();
        private void End()
        {
            if (!_exit.TrySetResult(0)) return;
            _output.Writer.Complete(); _error.Writer.Complete();
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true; End(); OutputStream.Dispose(); ErrorStream.Dispose(); return ValueTask.CompletedTask;
        }
    }

    private sealed class CleanupTransport(CleanupShell shell, Func<string, Task>? push = null) : IAdbTransport
    {
        internal int Removed;
        public Task<AdbShellResult> RunShellAsync(string serial, string command, CancellationToken token)
        { if (command.StartsWith("rm -f ")) Interlocked.Increment(ref Removed); return Task.FromResult(new AdbShellResult("", 0)); }
        public Task<IAdbShellSession> OpenShellAsync(string serial, string command, bool redirectInput, CancellationToken token)
            => Task.FromResult<IAdbShellSession>(shell);
        public Task PushAsync(string serial, string localPath, string remotePath, CancellationToken token) => push?.Invoke(remotePath) ?? Task.CompletedTask;
        public Task<string> VersionAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<AndroidDevice>> DevicesAsync(CancellationToken token) => throw new NotSupportedException();
        public Task TrackDevicesAsync(Action<IReadOnlyList<AndroidDevice>> changed, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> FeaturesAsync(string serial, CancellationToken token) => throw new NotSupportedException();
        public Task<string> GetSerialNoAsync(string serial, CancellationToken token) => throw new NotSupportedException();
        public Task<int> ForwardTcpToLocalAbstractAsync(string serial, string name, CancellationToken token) => throw new NotSupportedException();
        public Task RemoveForwardAsync(string serial, string local, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<AdbForwardRule>> ListForwardsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<Stream> OpenLocalAbstractAsync(string serial, string name, CancellationToken token) => throw new NotSupportedException();
        public Task<string> PairAsync(string address, string password, CancellationToken token) => throw new NotSupportedException();
        public Task<string> ConnectAsync(string address, CancellationToken token) => throw new NotSupportedException();
        public Task<string> DisconnectAsync(string address, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<AndroidMdnsService>> MdnsServicesAsync(CancellationToken token) => throw new NotSupportedException();
    }

    private static async Task CheckMediaResourceCleanupAsync(string output)
    {
        foreach (bool clipboard in new[] { true, false })
        {
            var shell = new CleanupShell(); shell.Input.Block = true; shell.Input.FailFlush = true;
            var transport = new CleanupTransport(shell);
            using var adb = new AdbClient(Path.Combine(output, "fake-runtime"), transport);
            typeof(AdbClient).GetField("_catalogVerified", ConcurrencyHidden)!.SetValue(adb, true);
            var phone = new AndroidDevice("offline-cleanup-fixture", "Fixture", "device", false);
            using var stop = new CancellationTokenSource();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task watch, send;
            if (clipboard)
            {
                var client = new AndroidClipboardClient(adb);
                watch = client.WatchAsync(phone, () => ready.TrySetResult(), _ => { }, stop.Token);
                await shell.PublishAsync("{\"ready\":true,\"revision\":0}");
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
                send = client.SendAsync(new string('x', 8192), 0, default);
            }
            else
            {
                var client = new AndroidMediaSessionClient(adb);
                watch = client.WatchAsync(phone, _ => ready.TrySetResult(), stop.Token);
                await shell.PublishAsync("{\"package\":\"fixture.app\",\"title\":\"fixture\",\"artist\":\"fixture\",\"playing\":false,\"actions\":0,\"position\":0,\"duration\":0}");
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
                send = client.SendAsync("toggle", default);
            }
            await shell.Input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            stop.Cancel();
            try { await send.WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { }
            try { await watch.WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { }
            Check(shell.Disposed && transport.Removed == 1,
                (clipboard ? "clipboard" : "media") + " teardown cancels an in-flight write and still removes/disposes the helper when writer flush throws");
        }

        var outputType = typeof(AdbClient).Assembly.GetType("SonicRoute.AndroidAudio.AndroidAudioOutputConnection")!;
        var outputShell = new CleanupShell(); outputShell.Input.FailFlush = true;
        var outputTransport = new CleanupTransport(outputShell);
        using (var adb = new AdbClient(Path.Combine(output, "fake-output-runtime"), outputTransport))
        {
            var connection = (IAsyncDisposable)Activator.CreateInstance(outputType, ConcurrencyHidden, null, new object?[]
            { adb, new AndroidDevice("offline-output-fixture", "Fixture", "device", false), "speaker", CancellationToken.None, null, null }, null)!;
            outputType.GetField("_helper", ConcurrencyHidden)!.SetValue(connection, outputShell);
            outputType.GetField("_writer", ConcurrencyHidden)!.SetValue(connection, new StreamWriter(outputShell.Input));
            await connection.DisposeAsync();
            bool lifetimeDisposed = false;
            try { _ = ((CancellationTokenSource)outputType.GetField("_lifetime", ConcurrencyHidden)!.GetValue(connection)!).Token; }
            catch (ObjectDisposedException) { lifetimeDisposed = true; }
            Check(outputShell.Disposed && outputTransport.Removed == 1 && lifetimeDisposed,
                "PCM connection cleanup releases helper and cancellation state after a throwing control writer");
        }

        await CheckStdinCloseRaceAsync();
        await CheckRetiringPcRoutesAsync(output);
        await CheckUsbDisposalGatesAsync(output);
    }

    private static async Task CheckUsbDisposalGatesAsync(string output)
    {
        var assembly = typeof(AdbClient).Assembly;
        var descriptor = assembly.GetType("SonicRoute.AndroidAudio.WinUsbDeviceDescriptor")!;
        var keyType = assembly.GetType("SonicRoute.AndroidAudio.AdbKeyStore")!;
        using var key = (IDisposable)Activator.CreateInstance(keyType, ConcurrencyHidden, null,
            new object[] { Path.Combine(output, "unused-usb-key") }, null)!;
        using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        int scans = 0;
        Func<object> enumerate = () =>
        {
            Interlocked.Increment(ref scans); entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("held fixture enumeration");
            return Array.CreateInstance(descriptor, 0);
        };
        var returnType = typeof(IReadOnlyList<>).MakeGenericType(descriptor);
        var delegateType = typeof(Func<>).MakeGenericType(returnType);
        var expression = System.Linq.Expressions.Expression.Convert(
            System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(enumerate)), returnType);
        var typedEnumerate = System.Linq.Expressions.Expression.Lambda(delegateType, expression).Compile();
        var direct = (IAdbTransport)Activator.CreateInstance(assembly.GetType("SonicRoute.AndroidAudio.DirectAdbTransport")!, ConcurrencyHidden, null,
            new object?[] { key, false, typedEnumerate, null, null, null, null, null }, null)!;
        var first = Task.Run(() => direct.DevicesAsync(default));
        if (!entered.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("USB scan did not enter fixture");
        var queued = direct.DevicesAsync(default);
        var closing = ((IAsyncDisposable)direct).DisposeAsync().AsTask();
        Check(!closing.IsCompleted, "direct transport shutdown joins an in-flight USB scan before its first slot is published");
        resume.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        bool rejected = false;
        try { await queued.WaitAsync(TimeSpan.FromSeconds(3)); } catch (ObjectDisposedException) { rejected = true; }
        await closing.WaitAsync(TimeSpan.FromSeconds(3));
        Check(rejected && scans == 1 && !Directory.Exists(Path.Combine(output, "unused-usb-key")),
            "queued USB scan observes disposal after entering its gate without scanning hardware or opening keys");

        // The guard must run before touching any native USB or staging resources.
        var linkType = assembly.GetType("SonicRoute.AndroidAudio.WinUsbAdbLink")!;
        object link = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(linkType);
        using var rx = new SemaphoreSlim(1, 1); using var tx = new SemaphoreSlim(1, 1);
        linkType.GetField("_disposed", ConcurrencyHidden)!.SetValue(link, 1);
        linkType.GetField("_rxOpLock", ConcurrencyHidden)!.SetValue(link, rx);
        linkType.GetField("_txOpLock", ConcurrencyHidden)!.SetValue(link, tx);
        var read = (Task)linkType.GetMethod("ReadExactUsbAsync", ConcurrencyHidden)!.Invoke(link,
            new object[] { new byte[1], 1, false, CancellationToken.None })!;
        var write = (Task)linkType.GetMethod("WriteExactUsbAsync", ConcurrencyHidden)!.Invoke(link,
            new object[] { new ReadOnlyMemory<byte>(new byte[1]), CancellationToken.None })!;
        rejected = false;
        try { await read; } catch (ObjectDisposedException) { rejected = true; }
        bool writeRejected = false;
        try { await write; } catch (ObjectDisposedException) { writeRejected = true; }
        Check(rejected && writeRejected && rx.CurrentCount == 1 && tx.CurrentCount == 1,
            "late WinUSB reads/writes reject under their gates and still release them before any native access");
    }

    private static async Task CheckStdinCloseRaceAsync()
    {
        var inputType = typeof(AdbSocketClient).GetNestedType("AdbV2ShellSession", BindingFlags.NonPublic)!
            .GetNestedType("AdbV2StdinStream", BindingFlags.NonPublic)!;
        foreach (bool asyncClose in new[] { false, true })
        {
            var underlying = new CleanupInputStream { Block = true };
            var input = (Stream)Activator.CreateInstance(inputType, ConcurrencyHidden, null, new object[] { underlying }, null)!;
            Task first = input.WriteAsync(new byte[] { 1 }, 0, 1);
            await underlying.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Task queued = input.WriteAsync(new byte[] { 2 }, 0, 1);
            Task close;
            if (asyncClose) close = input.DisposeAsync().AsTask();
            else { input.Dispose(); close = Task.CompletedTask; }
            underlying.Resume.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(3));
            bool closed = false;
            try { await queued.WaitAsync(TimeSpan.FromSeconds(3)); } catch (ObjectDisposedException) { closed = true; }
            await close.WaitAsync(TimeSpan.FromSeconds(3));
            Check(closed && underlying.Packets == (asyncClose ? 2 : 1),
                (asyncClose ? "async" : "sync") + " stdin close lets the active writer release its gate and rejects queued data after close");
        }
    }

    private static async Task CheckRetiringPcRoutesAsync(string output)
    {
        var manager = new PcToPhoneAudioManager(() => throw new Exception("no ADB access")) { EnableLiveStreaming = false };
        string target = AndroidOutputIdentity.FormatDeviceId("device:fixture", "speaker");
        manager.RouteProcess(24680, target, new AndroidDevice("offline-fixture", "Fixture", "device", false));
        var routes = (IDictionary)typeof(PcToPhoneAudioManager).GetField("_routes", ConcurrencyHidden)!.GetValue(manager)!;
        object route = routes[24680]!;
        var stream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        route.GetType().GetField("_streamTask", ConcurrencyHidden)!.SetValue(route, stream.Task);
        manager.StopAll();
        Task stopping = manager.DisposeAsync().AsTask();
        Check(!stopping.IsCompleted, "manager shutdown joins a route removed earlier by StopAll");
        Task sameStop = (Task)route.GetType().GetMethod("StopAsync", ConcurrencyHidden)!.Invoke(route, new object[] { true })!;
        Check(!sameStop.IsCompleted, "a second stop shares the original cleanup task instead of reporting early completion");
        stream.TrySetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(3));
        Check(manager.ActiveCount == 0 && !manager.RouteProcess(24680, target, null).Success,
            "disposed manager rejects new routes after all retiring streams finish");

        manager = new PcToPhoneAudioManager(() => throw new Exception("no ADB access")) { EnableLiveStreaming = false };
        manager.RouteProcess(24680, target, new AndroidDevice("offline-fixture", "Fixture", "device", false));
        routes = (IDictionary)typeof(PcToPhoneAudioManager).GetField("_routes", ConcurrencyHidden)!.GetValue(manager)!;
        route = routes[24680]!;
        string replacement = AndroidOutputIdentity.FormatDeviceId("device:replacement", "speaker");
        manager.RouteProcess(24680, replacement, new AndroidDevice("offline-fixture", "Replacement", "device", false));
        bool removed = (bool)typeof(PcToPhoneAudioManager).GetMethod("StopOwnedRoute", ConcurrencyHidden)!.Invoke(manager, new[] { route })!;
        Check(!removed && manager.ActiveCount == 1 && manager.GetPersistedEndpoint(24680) == replacement,
            "a late failure callback from a replaced route cannot stop the newer route for the same PID");
        await manager.DisposeAsync();
    }
}
