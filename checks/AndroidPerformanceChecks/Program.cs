using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

internal static partial class Program
{
    private sealed record Trial(double WallMs, double CpuMs, long AllocatedBytes, double Checksum);
    private sealed record Metric(string Name, int Operations, string Scope, IReadOnlyList<Trial> Trials);
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static HashSet<string>? _onlyMetrics;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 1 && args[1] == "--interaction")
                return InteractionAudit(args[0], args.Length > 2 ? args[2] : "interaction");
            string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            string label = args[1];
            if (args.Length > 2) _onlyMetrics = args[2].Split(',').ToHashSet(StringComparer.Ordinal);
            Environment.SetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY", Path.Combine(output, "data"));
            Environment.SetEnvironmentVariable("SONICROUTE_INSTANCE_ID", "AndroidPerformanceChecks");
            var metrics = new List<Metric>();

            var pcm = new AndroidPcmSource { Volume = 0.35f, HeaderGain = 1.1f };
            float[] samples = Enumerable.Range(0, 1920).Select(i => (float)Math.Sin(i * 0.071) * 1.2f).ToArray();
            byte[] destination = new byte[samples.Length * sizeof(float)];
            metrics.Add(Measure("pcm-write-read", 20000, "48kHz stereo, 20ms blocks; diagnostics, gain and clipping included", () =>
            {
                double checksum = 0;
                for (int i = 0; i < 20000; i++)
                {
                    pcm.Write(samples); pcm.Read(destination, 0, destination.Length);
                    checksum += MemoryMarshal.Cast<byte, float>(destination)[i % samples.Length];
                }
                return checksum;
            }));

            var manager = new AndroidAudioManager(() => new AndroidAudioSession(() => new FixtureTransport(), (_, _) => new SilentPlayer()));
            try
            {
                var device = new AndroidDevice("offline", "Fixture", "device", false);
                var source = manager.AddAsync(device, new AndroidApp("cn.test.one", "One", 1001, "media")).GetAwaiter().GetResult();
                manager.AddAsync(device, new AndroidApp("cn.test.two", "Two", 1002, "media")).GetAwaiter().GetResult();
                metrics.Add(Measure("source-registry-read", 250000, "Two fixture sources; registry snapshot reads, no discovery or device access", () =>
                {
                    int checksum = 0;
                    for (int i = 0; i < 250000; i++) checksum += manager.Sources.Count;
                    return checksum;
                }));
                source.Session.SetVolume(50);
                int notifications = 0;
                source.Session.Changed += _ => notifications++;
                metrics.Add(Measure("unchanged-volume", 25000, "Repeated identical source volume; checksum is emitted notification count", () =>
                {
                    notifications = 0;
                    for (int i = 0; i < 25000; i++) source.Session.SetVolume(50);
                    return notifications;
                }));
            }
            finally { manager.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

            var app = new App(); app.InitializeComponent();
            L10n.Instance.SetLanguage("zh-CN"); ThemeService.Apply("light", "#00CBE9");
            byte[] icon = MakeIcon();
            var phoneApp = new AndroidApp("cn.test.icon", "Fixture", 2001, "media", icon);
            var catalogueDevice = new AndroidDevice("fixture-cache", "Fixture", "device", false);
            var catalogueApps = Enumerable.Range(0, 155).Select(i => phoneApp with { Package = "cn.test.cache" + i }).ToArray();
            string catalogueFile = Path.Combine(output, "catalogue.json");
            Func<AndroidDevice, CancellationToken, Task<IReadOnlyList<AndroidApp>>> fetchCatalogue = (_, _) => Task.FromResult<IReadOnlyList<AndroidApp>>(catalogueApps);
            new AndroidAppCatalogCache(catalogueFile, fetchCatalogue).GetAsync(catalogueDevice, true, default).GetAwaiter().GetResult();
            metrics.Add(Measure("catalogue-save", 1, "155 icon-bearing apps; atomic UTF-8 disk persistence, all managed threads", () =>
                new AndroidAppCatalogCache(catalogueFile, fetchCatalogue).GetAsync(catalogueDevice, true, default).GetAwaiter().GetResult().Count,
                totalAllocations: true));
            metrics.Add(Measure("catalogue-cold-read", 1, "155 icon-bearing apps; a new cache reads existing disk data without ADB, all managed threads", () =>
                new AndroidAppCatalogCache(catalogueFile, (_, _) => throw new InvalidOperationException("unexpected device query"))
                    .GetAsync(catalogueDevice, false, default).GetAwaiter().GetResult().Count, totalAllocations: true));
            metrics.Add(Measure("shared-app-icon", 500, "Same app PNG requested by repeated phone list/view models", () =>
            {
                int checksum = 0;
                for (int i = 0; i < 500; i++) if (new PhoneAppItem(phoneApp).Icon is { IsFrozen: true }) checksum++;
                return checksum;
            }));

            // Each cold catalogue has distinct immutable PNG arrays, as a fresh device response does.
            foreach (int visibleIcons in new[] { 0, 10 })
            {
                metrics.Add(Measure(visibleIcons == 0 ? "catalog-models-cold" : "catalog-first-visible-rows", 155,
                    $"155 new app models and PNG arrays; request only {visibleIcons} visible row icons, no ADB or window", () =>
                {
                    var models = Enumerable.Range(0, 155).Select(i => new PhoneAppItem(phoneApp with
                        { Package = "cn.test.cold" + i, Name = "App " + i, IconPng = icon.ToArray() })).ToArray();
                    for (int i = 0; i < visibleIcons; ++i)
                        if (models[i].Icon?.IsFrozen != true) throw new InvalidOperationException("Visible icon was not decoded/frozen.");
                    return models.Length;
                }));
            }

            var phone = new PhoneAudioWindow(false);
            var items = Enumerable.Range(0, 155).Select(i => new PhoneAppItem(phoneApp with { Package = "cn.test.app" + i, Name = "App " + i })).ToArray();
            typeof(PhoneAudioWindow).GetField("_apps", Hidden)!.SetValue(phone, items);
            typeof(PhoneAudioWindow).GetMethod("ApplyFilter", Hidden)!.Invoke(phone, null);
            var root = (FrameworkElement)phone.Content;
            void Layout() { root.Measure(new Size(352, 450)); root.Arrange(new Rect(0, 0, 352, 450)); root.UpdateLayout(); }
            var list = (ListBox)phone.FindName("PhoneApps");
            // Compare the exact same viewport even if the user changes the surrounding footer
            // or removes categories. Product windows continue to use the available height.
            list.Height = 150; list.VerticalAlignment = VerticalAlignment.Top;
            Layout();
            var viewer = Descendants<ScrollViewer>(list).Single();
            var containers = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
            metrics.Add(Measure("phone-list-scroll", 160, "155 apps; offline layout/scroll, checksum counts distinct realized row containers", () =>
            {
                containers.Clear();
                for (int i = 0; i < 160; i++)
                {
                    viewer.ScrollToVerticalOffset(i % 20 * 7); Layout();
                    foreach (var container in Descendants<ListBoxItem>(list)) containers.Add(container);
                }
                return containers.Count;
            }, rounds: 3));
            phone.Close();

            var phoneStore = (AndroidPhoneStore)typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!
                .GetProperty("Phone", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            for (int i = 0; i < 30; ++i) phoneStore.Retain(phoneApp with { Package = "cn.test.retained" + i });
            phoneStore.FlushAsync().GetAwaiter().GetResult();
            var retainedPhone = new PhoneAudioWindow(false);
            var notifyRetained = typeof(PhoneAudioWindow).GetMethod("RetainedAppsChanged", Hidden)!;
            notifyRetained.Invoke(retainedPhone, null);
            metrics.Add(Measure("unchanged-retained-notification", 1000,
                "30 retained apps; unchanged profile notifications, cached metadata only, no disk/device access", () =>
                {
                    for (int i = 0; i < 1000; ++i) notifyRetained.Invoke(retainedPhone, null);
                    return ((ListBox)retainedPhone.FindName("PhoneApps")).Items.Count;
                }));
            retainedPhone.Close();

            byte[] artwork = new byte[19432]; new Random(37).NextBytes(artwork);
            var fields = new Dictionary<string, object> { ["package"] = "cn.test.music", ["title"] = "Track", ["artist"] = "Artist",
                ["playing"] = true, ["actions"] = 822, ["position"] = 12000, ["duration"] = 148867, ["playingPackages"] = new[] { "cn.test.music" } };
            string delta = JsonSerializer.Serialize(fields);
            fields["artwork"] = Convert.ToBase64String(artwork);
            string full = JsonSerializer.Serialize(fields);
            var deltaMethod = typeof(AndroidMediaState).GetMethod("Parse", new[] { typeof(string), typeof(AndroidMediaState) });
            var parseDelta = deltaMethod == null ? null : (Func<string, AndroidMediaState?, AndroidMediaState>)deltaMethod.CreateDelegate(typeof(Func<string, AndroidMediaState?, AndroidMediaState>));
            var previous = AndroidMediaState.Parse(full);
            metrics.Add(Measure("media-state-update", 2000, "19,432-byte artwork; original full snapshots versus supported artwork deltas", () =>
            {
                long checksum = 0;
                for (int i = 0; i < 2000; i++)
                {
                    previous = parseDelta == null ? AndroidMediaState.Parse(full) : parseDelta(delta, previous);
                    checksum += previous.ArtworkPng?.Length ?? 0;
                }
                return checksum;
            }));

            metrics = metrics.Where(m => m.Trials.Count > 0).ToList();
            using var process = Process.GetCurrentProcess();
            var report = new { Created = DateTimeOffset.Now, Label = label, Framework = RuntimeInformation.FrameworkDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(), LogicalProcessors = Environment.ProcessorCount,
                InputProductAssembly = typeof(App).Assembly.Location, InputProductHash = Hash(typeof(App).Assembly.Location),
                InputBackendHash = Hash(typeof(AndroidAudioManager).Assembly.Location), InputHarnessHash = Hash(typeof(Program).Assembly.Location),
                PhoneApps = items.Length, PhoneListHeight = list.ActualHeight, PhoneViewportWidth = viewer.ViewportWidth,
                FullMediaUpdateBytes = Encoding.UTF8.GetByteCount(full),
                MeasuredMediaUpdateBytes = Encoding.UTF8.GetByteCount(parseDelta == null ? full : delta),
                Metrics = metrics, Scope = "Bounded offline component microbenchmarks. No ADB, playback, global input, visible windows or installed configuration. Not whole-app idle CPU, real sound/latency or long-run memory evidence." };
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var metric in metrics)
            {
                var wall = metric.Trials.Select(t => t.WallMs).Order().ToArray();
                var alloc = metric.Trials.Select(t => t.AllocatedBytes).Order().ToArray();
                Console.WriteLine($"PERF {metric.Name} operations={metric.Operations} medianMs={wall[wall.Length / 2]:F3} allocatedBytes={alloc[alloc.Length / 2]} checksums={string.Join(',', metric.Trials.Select(t => t.Checksum))}");
            }
            Console.WriteLine("RESULT " + Path.Combine(output, "results.json"));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    private static Metric Measure(string name, int operations, string scope, Func<double> action, int rounds = 5, bool totalAllocations = false)
    {
        if (_onlyMetrics != null && !_onlyMetrics.Contains(name)) return new(name, operations, scope, Array.Empty<Trial>());
        action(); // JIT/resources warmup is excluded.
        Thread.Sleep(50); // Bounded measurement settling only; no timer in the product.
        var trials = new List<Trial>();
        using var process = Process.GetCurrentProcess();
        for (int i = 0; i < rounds; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var cpu = process.TotalProcessorTime;
            long allocated = totalAllocations ? GC.GetTotalAllocatedBytes(true) : GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            double checksum = action();
            clock.Stop();
            trials.Add(new(clock.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - cpu).TotalMilliseconds,
                (totalAllocations ? GC.GetTotalAllocatedBytes(true) : GC.GetAllocatedBytesForCurrentThread()) - allocated, checksum));
        }
        return new(name, operations, scope, trials);
    }

    private static byte[] MakeIcon()
    {
        var pixels = new byte[64 * 64 * 4]; new Random(17).NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var image = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private sealed class SilentPlayer : IPcmPlayer { public void Play() { } public void Stop() { } public void Dispose() { } }
    private sealed class FixtureTransport : IAudioTransport
    {
        public Task<Stream> ConnectAsync(AndroidDevice device, AndroidApp app, bool keepPhoneAudio, CancellationToken token)
        {
            using var wire = new MemoryStream();
            wire.Write(Encoding.ASCII.GetBytes("opus"));
            byte[] header = new byte[12], opusHead = new byte[19];
            Encoding.ASCII.GetBytes("OpusHead").CopyTo(opusHead, 0); opusHead[8] = 1; opusHead[9] = 2;
            BinaryPrimitives.WriteUInt64BigEndian(header, 1UL << 63); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 19);
            wire.Write(header); wire.Write(opusHead);
            BinaryPrimitives.WriteUInt64BigEndian(header, 0); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 3);
            wire.Write(header); wire.Write(new byte[] { 0xfc, 0xff, 0xfe });
            return Task.FromResult<Stream>(new HoldingStream(wire.ToArray()));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class HoldingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, token).ConfigureAwait(false);
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); return 0;
        }
    }
}
