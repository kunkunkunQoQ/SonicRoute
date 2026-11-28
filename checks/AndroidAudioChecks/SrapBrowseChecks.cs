using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task SrapBrowseRecoveryAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        using var identity = new SrapIdentity(directory);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = SrapFixtureCertificate(signer);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        string pin = Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo()));
        string parent = Guid.NewGuid().ToString("N");
        var accept = AcceptSrapFixtureAsync(listener, cert, identity.Fingerprint, parent,
            [new JsonObject { ["id"] = "volume.device", ["state"] = "available" }], ct);
        using var session = await new SrapClient(identity).ConnectAsync(endpoint, pin, ct);
        using var peer = await accept;
        var store = new AndroidPhoneStore(Path.Combine(directory, "android-phone.json"));
        var profile = store.RegisterApp(pin, endpoint, store.ManagementRevision);
        bool lan = false;
        int starts = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkedOffline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<bool> Watch([EnumeratorCancellation] CancellationToken active)
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                first.TrySetResult();
                await finishFirst.Task.WaitAsync(active);
                yield break;
            }
            second.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, active); }
            finally { stopped.TrySetResult(); }
        }
        bool HasLan()
        {
            bool available = Volatile.Read(ref lan);
            if (!available) checkedOffline.TrySetResult();
            return available;
        }
        await using var transport = new SrapPhoneTransport(store, directory, Watch, HasLan);
        var device = transport.Admit(profile, session);
        transport.Start();
        await first.Task.WaitAsync(ct);
        transport.OnNetworkAddressChanged();
        finishFirst.TrySetResult();
        await checkedOffline.Task.WaitAsync(ct);
        Check(starts == 1, "a network event during a failing browse does not rearm an offline LAN");
        Volatile.Write(ref lan, true);
        transport.OnNetworkAddressChanged();
        await second.Task.WaitAsync(ct);
        Check(starts == 2, "the next usable network event re-registers the terminated DNS-SD browse");
        transport.OnNetworkAddressChanged();
        transport.OnNetworkAddressChanged();
        var reading = session.ReadVolumeAsync(ct);
        var request = await SrapClient.ReadAsync(peer, ct);
        await SrapClient.WriteAsync(peer, "response", new() { ["status"] = "done",
            ["result"] = new JsonObject { ["scope"] = "system_media", ["percent"] = 20 } }, ct, SrapClient.Text(request, "id"), parent);
        Check(starts == 2 && ReferenceEquals(transport.Session(device), session) && (await reading).GetProperty("percent").GetInt32() == 20,
            "further network events preserve one browser and the original responsive mutual TLS session");
        var downgraded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.CapabilitiesChanged += () => downgraded.TrySetResult();
        await SrapClient.WriteAsync(peer, "capabilities", new() { ["capabilityRevision"] = 2,
            ["entries"] = new JsonArray(new JsonObject { ["id"] = "volume.device", ["state"] = "needs_authorization" }) },
            ct, session: parent);
        await downgraded.Task.WaitAsync(ct);
        bool declinedLocally = false;
        try { await session.ReadVolumeAsync(ct); }
        catch (SrapException error) when (error.Code == "unsupported") { declinedLocally = true; }
        Check(declinedLocally && !session.Token.IsCancellationRequested && ReferenceEquals(transport.Session(device), session),
            "a newer capability revision disables volume operations while preserving the paired session");
        transport.RevokeTrust(profile.Id, pin);
        await stopped.Task.WaitAsync(ct);
        transport.OnNetworkAddressChanged();
        Check(starts == 2 && store.Profile.AppTrustRevoked && transport.Resolve(store.Profile) == null && session.Token.IsCancellationRequested,
            "revocation cancels the registration and later network changes cannot reattach the peer");
        Console.WriteLine($"PASS {_checks} SRAP browse recovery checks");
    }
}
