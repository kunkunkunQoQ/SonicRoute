using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task IdentityReviewAsync(string output)
    {
        Directory.CreateDirectory(output);
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var keys = new AdbKeyStore(Path.Combine(output, "synthetic-client-key"));
        byte[] publicKey = Encoding.UTF8.GetBytes(keys.GetPublicKeyLine());
        using var expected = CreateLoopbackServerCert("CN=ExpectedAdbd");
        using var different = CreateLoopbackServerCert("CN=DifferentAdbd");
        string expectedHash = expected.GetCertHashString(HashAlgorithmName.SHA256);

        async Task<(bool Accepted, string? Hash)> Connect(X509Certificate2 certificate,
            Func<X509Certificate2?, bool>? validator)
        {
            await using var server = new FakeAdbdServer(true, false, publicKey, 64 * 1024, certificate);
            await using var link = await OpenClientLinkAsync(server.Port, true);
            try
            {
                await using var connection = await AdbMuxConnection.ConnectAsync(link, keys, validator,
                    "127.0.0.1", safety.Token);
                return (connection.IsTls && connection.IsConnected, connection.PeerCertificateSha256);
            }
            catch (Exception ex) when (ex is AuthenticationException or System.IO.IOException)
            { return (false, link.PeerCertificateSha256); }
        }

        var first = await Connect(expected, null);
        Check(first.Accepted && first.Hash == expectedHash,
            "baseline direct STLS accepts a self-signed server certificate with no validator");
        var substituted = await Connect(different, null);
        Check(substituted.Accepted && substituted.Hash != expectedHash,
            "baseline direct STLS also accepts a substituted server identity without a trust policy");
        bool Match(X509Certificate2? cert) => cert?.GetCertHashString(HashAlgorithmName.SHA256) == expectedHash;
        Check((await Connect(expected, Match)).Accepted, "an explicit connection identity validator accepts its expected certificate");
        Check(!(await Connect(different, Match)).Accepted, "an explicit connection identity validator rejects a different certificate");

        // This exercises the independent SPAKE2 + TLS-exporter path, including a wrong PIN.
        // It deliberately does not apply a transport-certificate pin to the pairing protocol.
        await CheckSspiPairingLoopbackAsync().WaitAsync(safety.Token);
        await File.WriteAllTextAsync(Path.Combine(output, "identity-review.json"), JsonSerializer.Serialize(new
        {
            Success = true, Assertions = _checks, UntrustedDefaultAccepted = first.Accepted && substituted.Accepted,
            ExplicitValidatorRejectsMismatch = true, PairingSpake2AndWrongPinChecked = true,
            PairingCertificatePinAdded = false, RealPhone = false, UserKeysAccessed = false, SharedAdb = false,
            Interpretation = "Default TLS encrypts the channel without authenticating a stable server identity; AOSP compatibility and server key lifetime must be reviewed before adding pins."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
