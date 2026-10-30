using System.Net;
using System.Security.Cryptography;
using SonicRoute.Updates;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string label)
    { if (!value) throw new Exception(label); _checks++; Console.WriteLine("PASS " + label); }
    private sealed class Fake : HttpMessageHandler
    {
        internal byte[] Payload = Array.Empty<byte>();
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) }); }
    }
    private static async Task<int> Main(string[] args)
    {
        try
        {
            string directory = Path.GetFullPath(args.Single()); Directory.CreateDirectory(directory);
            byte[] payload = new byte[2 * 1024 * 1024]; new Random(42).NextBytes(payload);
            string expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            string file = Path.Combine(directory, "source.bin"); File.WriteAllBytes(file, payload);
            using var validToken = new CancellationTokenSource();
            Check(UpdateProtocol.Hash(file) == expected && UpdateProtocol.Hash(file, validToken.Token) == expected, "cancellable buffered hash matches SHA256 and existing hash API");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            bool canceledHash = false;
            try { UpdateProtocol.Hash(file, canceled.Token); } catch (OperationCanceledException) { canceledHash = true; }
            Check(canceledHash, "hash accepts cancellation before file work");
            var fake = new Fake { Payload = payload }; using var transport = new UpdateTransport(fake);
            var release = new UpdateRelease { Tag = "v1.30", Package = new UpdatePackage { Sha256 = expected, Size = payload.Length },
                Asset = new ReleaseAsset { Name = "fixture.bin", Url = UpdateProtocol.Repository + "/releases/download/v1.30/fixture.bin" } };
            string destination = Path.Combine(directory, "download.bin"); int verified = 0;
            using var verificationCancel = new CancellationTokenSource(); bool canceledDownload = false;
            try { await transport.DownloadAsync(release, destination, (_, _) => { }, verificationCancel.Token,
                () => { verified++; verificationCancel.Cancel(); }); }
            catch (OperationCanceledException) { canceledDownload = true; }
            Check(canceledDownload && verified == 1 && !File.Exists(destination) && !File.Exists(destination + ".partial"), "cancel at verification cannot install or retain partial payload");
            release.Package.Sha256 = new string('0', 64); bool rejected = false;
            try { await transport.DownloadAsync(release, destination, (_, _) => { }, CancellationToken.None); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && !File.Exists(destination) && !File.Exists(destination + ".partial"), "checksum mismatch rejects payload and cleans its own partial file");
            release.Package.Sha256 = expected;
            await transport.DownloadAsync(release, destination, (_, _) => { }, CancellationToken.None, () => verified++);
            Check(File.ReadAllBytes(destination).SequenceEqual(payload) && verified == 2, "verified payload moves atomically to download cache");
            int before = fake.Calls;
            await transport.DownloadAsync(release, destination, (_, _) => { }, CancellationToken.None, () => verified++);
            Check(fake.Calls == before && verified == 3, "cached payload is verified without network");
            Console.WriteLine($"RESULT passed={_checks} NETWORK=FAKE INSTALLED_OR_EXECUTED=false"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
