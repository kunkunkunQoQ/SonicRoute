using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SonicRoute;

internal static partial class Program
{
    private sealed class ImageMemorySample(long managedBytes, long privateBytes, long workingSetBytes, int handles)
    {
        public long ManagedBytes { get; } = managedBytes;
        public long PrivateBytes { get; } = privateBytes;
        public long WorkingSetBytes { get; } = workingSetBytes;
        public int Handles { get; } = handles;
    }

    private static void CheckImageMemoryRetention(string output, bool baseline)
    {
        Directory.CreateDirectory(output);
        var decode = (Func<byte[], int, BitmapImage>)typeof(AppIconService)
            .GetMethod("DecodePngSized", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<byte[], int, BitmapImage>));
        byte[] template = MakeMemoryTestPng(64, 64);
        decode(template, 64); // Warm the WIC/PNG path outside the retained-object sample.
        CollectMemoryFixture();
        ImageMemorySample before = SampleImageMemory();
        var batch = DecodeMemoryBatch(decode, template, 128);
        CollectMemoryFixture();
        ImageMemorySample retained = SampleImageMemory();
        int alive = batch.Inputs.Count(reference => reference.IsAlive);
        Check(batch.Images.All(image => image.IsFrozen && image.PixelWidth == 64 && image.PixelHeight == 64),
            "all decoded icons retain the same frozen 64px image dimensions");
        if (!baseline) Check(alive == 0, "decoded images do not retain any of their 128 source PNG arrays");
        byte[] pixels = CopyImagePixels(batch.Images[0]);
        Check(pixels.SequenceEqual(CopyImagePixels(decode(template, 64))),
            "cached pixels remain byte-identical after source disposal and collection");
        var clone = batch.Images[0].Clone(); clone.Freeze();
        Check(CopyImagePixels(clone).SequenceEqual(pixels), "a frozen image clone still reads pixels after source disposal");
        var wide = decode(MakeMemoryTestPng(128, 32), 64);
        Check(wide.PixelWidth == 64 && wide.PixelHeight == 16, "bounded decoding preserves a wide image's aspect ratio");

        bool phone = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost") != null;
        string edition = phone ? "Full" : Environment.Version.Major < 5 ? "Legacy" : "Lite";
        if (!baseline)
        {
            CheckImageInputStreamContract();
            if (phone) CheckPhoneArtworkInputLifetime();
        }
        var encoded = batch.EncodedBytes;
        var imageCount = batch.Images.Count;
        batch.Images.Clear();
        CollectMemoryFixture();
        ImageMemorySample released = SampleImageMemory();
        File.WriteAllText(Path.Combine(output, "image-memory.json"), JsonSerializer.Serialize(new
        {
            Success = true, Edition = edition, Baseline = baseline, Assertions = _checks,
            ImageCount = imageCount, EncodedBytes = encoded, RetainedInputArrays = alive,
            RetainedEncodedBytes = alive * template.Length, Samples = new { Before = before, ImagesRetained = retained, ImagesReleased = released },
            ProductStartup = false, PhoneAccess = false, UserProcessCollected = false,
            Scope = "128 retained frozen icons in an isolated fixture; not whole-product memory or GPU usage."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"MEMORY_INPUTS edition={edition} baseline={baseline} retained={alive}/{imageCount} bytes={alive * template.Length} encodedBytes={encoded}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (List<BitmapImage> Images, List<WeakReference> Inputs, long EncodedBytes) DecodeMemoryBatch(
        Func<byte[], int, BitmapImage> decode, byte[] template, int count)
    {
        var images = new List<BitmapImage>(count); var inputs = new List<WeakReference>(count);
        for (int i = 0; i < count; i++)
        {
            byte[] png = template.ToArray();
            inputs.Add(new WeakReference(png)); images.Add(decode(png, 64));
        }
        return (images, inputs, (long)template.Length * count);
    }

    private static byte[] MakeMemoryTestPng(int width, int height)
    {
        var random = new Random(133);
        byte[] pixels = new byte[width * height * 4]; random.NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    private static byte[] CopyImagePixels(BitmapSource source)
    {
        if (source.Format != PixelFormats.Bgra32) source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0); return pixels;
    }

    private static void CheckImageInputStreamContract()
    {
        var streamType = typeof(App).Assembly.GetType("SonicRoute.ImageDataStream", throwOnError: true)!;
        byte[] bytes = [1, 2, 3, 4, 5];
        using var stream = (Stream)Activator.CreateInstance(streamType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [bytes], null)!;
        byte[] read = new byte[3];
        Check(stream.CanRead && stream.CanSeek && !stream.CanWrite && stream.Length == 5
            && stream.Read(read, 0, 3) == 3 && read.SequenceEqual(new byte[] { 1, 2, 3 }),
            "image input supports WIC's read and seek contract without writable access");
        stream.Seek(-2, SeekOrigin.End);
        Check(stream.ReadByte() == 4 && stream.Position == 4, "image input preserves seek and byte-read positions");
        stream.Dispose(); stream.Dispose();
        bool closed = false;
        try { stream.ReadByte(); } catch (ObjectDisposedException) { closed = true; }
        Check(!stream.CanRead && !stream.CanSeek && closed, "image input releases its backing stream and rejects reads after idempotent disposal");
    }

    private static void CheckPhoneArtworkInputLifetime()
    {
        var phoneWindow = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindow", throwOnError: true)!;
        var decode = (Func<byte[], BitmapSource>)phoneWindow.GetMethod("DecodeBoundedArtwork", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<byte[], BitmapSource>));
        var artwork = DecodeArtworkMemoryFixture(decode);
        CollectMemoryFixture();
        Check(!artwork.Input.IsAlive && artwork.Image.IsFrozen && artwork.Image.PixelWidth == 112 && artwork.Image.PixelHeight == 28,
            "Full media artwork releases its JPEG input while retaining the existing 112px aspect-preserving image");
        Check(CopyImagePixels(artwork.Image).Any(value => value != 0), "Full media artwork stays readable after input collection");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (BitmapSource Image, WeakReference Input) DecodeArtworkMemoryFixture(Func<byte[], BitmapSource> decode)
    {
        var png = new BitmapImage();
        using (var source = new MemoryStream(MakeMemoryTestPng(128, 32)))
        {
            png.BeginInit(); png.CacheOption = BitmapCacheOption.OnLoad; png.StreamSource = source; png.EndInit(); png.Freeze();
        }
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(png));
        using var encoded = new MemoryStream(); encoder.Save(encoded);
        byte[] bytes = encoded.ToArray();
        return (decode(bytes), new WeakReference(bytes));
    }

    private static void CollectMemoryFixture()
    {
        // Only this isolated check process is collected; production reclaim policy is unchanged.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    private static ImageMemorySample SampleImageMemory()
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return new(GC.GetTotalMemory(false), process.PrivateMemorySize64, process.WorkingSet64, process.HandleCount);
    }
}
