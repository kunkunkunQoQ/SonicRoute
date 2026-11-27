using System.Text;
using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task GeminiAndroidContractAsync(string output, string vectors)
    {
        CheckGeminiOutputKeys();
        await CheckGeminiAliasAsync(output);
        foreach (string line in File.ReadLines(vectors).Where(line => line.Length > 0 && line[0] != '#'))
        {
            string[] fields = line.Split('|');
            var wire = Convert.FromHexString(fields[6]);
            using var source = new SrapChunkStream(wire, int.Parse(fields[3]));
            string actual;
            try
            {
                var frame = (await SrapProtocol.ReadAsync(source, default))!;
                var expected = new SrapFrame((SrapFrameType)byte.Parse(fields[1]), uint.Parse(fields[2]), Encoding.UTF8.GetBytes(fields[5]));
                Check(frame.Type == expected.Type && frame.StreamId == expected.StreamId && frame.Payload.SequenceEqual(expected.Payload)
                    && SrapProtocol.Encode(expected).SequenceEqual(wire), fields[0] + " lossless framing");
                if (frame.Type == SrapFrameType.Data) actual = "data";
                else
                {
                    try
                    {
                        using var document = SrapProtocol.ParseControl(frame);
                        var body = document.RootElement.GetProperty("body");
                        actual = document.RootElement.GetProperty("type").GetString() switch
                        {
                            "hello" => SrapProtocol.NegotiateMinor(body, 1, 0, 0) == 0 ? "hello" : "version_mismatch",
                            "capabilities" => CheckGoldenCapabilities(body),
                            _ => "control"
                        };
                    }
                    catch (InvalidDataException) { actual = "invalid_json"; }
                }
            }
            catch (InvalidDataException) { actual = "invalid_data"; }
            catch (EndOfStreamException) { actual = "short_read"; }
            Check(actual == fields[4], fields[0] + " expected " + fields[4]);
        }
        Check(SrapProtocol.Encode(new(SrapFrameType.Data, 1, new byte[SrapProtocol.MaxPayload])).Length ==
            SrapProtocol.HeaderSize + SrapProtocol.MaxPayload, "SRAP maximum legal payload");
        Console.WriteLine($"PASS {_checks} Gemini Android contract checks");
    }

    private static string CheckGoldenCapabilities(JsonElement body)
    {
        var (revision, values) = SrapProtocol.ReadCapabilities(body);
        return revision == 7 && values.Count == 3
            && values["audio.route.owned"] is { State: "available", Scope: "owned_track" }
            && values["audio.route.other_app"].State == "not_implemented"
            && values["audio.capture.exclusive"].State == "unavailable" ? "unknown_ignored" : "bad_capabilities";
    }

    private static void CheckGeminiOutputKeys()
    {
        const string json = """
            [{"key":"current"},{"kind":"speaker","key":"speaker"},
             {"kind":"bluetooth","key":"bt:AA:BB:CC:DD:EE:FF","name":"Bluetooth headset"},
             {"kind":"bluetooth","key":"bt:Speaker:speaker","name":"Bluetooth speaker"},
             {"kind":"wired","key":"wired:Wired_Headset","name":"Headset"},
             {"kind":"wired","key":"wired:port:second","name":"Headset"},
             {"kind":"usb","key":"usb:card=1;device=0","name":"USB DAC"},
             {"kind":"usb","key":"usb:card=1:dev=0:bt:","name":"USB DAC"},
             {"kind":"future","key":"future:one","name":"Future"}]
            """;
        using var document = JsonDocument.Parse(json);
        var outputs = AndroidOutputSink.ParseList(document.RootElement);
        Check(outputs.Select(s => s.Kind).SequenceEqual([AndroidOutputSinkKind.Speaker, AndroidOutputSinkKind.Bluetooth,
            AndroidOutputSinkKind.Bluetooth, AndroidOutputSinkKind.Wired, AndroidOutputSinkKind.Wired, AndroidOutputSinkKind.Usb,
            AndroidOutputSinkKind.Usb]), "known output kinds survive; current is virtual and unknown types are ignored");
        Check(outputs[3].Name == outputs[4].Name && outputs[3].Key != outputs[4].Key, "same-name wired devices retain distinct keys");
        foreach (string phoneKey in new[] { "device:fixture", "endpoint:[fe80::1%3]:4567", "device:bt", "device:wired", "device:usb" })
            foreach (string sink in outputs.Select(s => s.Key).Append("current"))
                Check(AndroidOutputIdentity.TryParseDeviceId(AndroidOutputIdentity.FormatDeviceId(phoneKey, sink),
                    out string phone, out string key) && phone == phoneKey && key == sink, $"output ID round trips {phoneKey} {sink}");
    }

    private static async Task CheckGeminiAliasAsync(string output)
    {
        const string fingerprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        const string phoneKey = "device:fixture";
        const string package = "com.example.music";
        string outputId = AndroidOutputIdentity.FormatDeviceId(phoneKey, "bt:AA:BB:CC:DD:EE:FF");
        var phone = new AndroidDevice("fixture", "Device", "device", false, "fixture", "1");
        var other = new AndroidDevice("other", "Device", "device", false, "other", "2");
        Directory.CreateDirectory(output);
        for (int version = 1; version <= 3; version++)
        {
            string dir = Path.Combine(output, $"v{version}");
            Directory.CreateDirectory(dir);
            string profileFile = Path.Combine(dir, "android-phone.json");
            string audioFile = Path.Combine(dir, "android-audio.json");
            const string savedDevice = """{"Serial":"fixture","Name":"Device","State":"device","IsWifi":false,"HardwareId":"fixture","TransportId":"1"}""";
            string oldProfile = version == 1
                ? $$"""{"Version":1,"Name":"Original","Device":{{savedDevice}},"Apps":[],"FutureRoot":"keep"}"""
                : $$"""{"Version":{{version}},"SelectedId":"original","Phones":[{"Id":"original","Name":"Original","Device":{{savedDevice}},"Apps":[],"FutureProfile":true}],"FutureRoot":"keep"}""";
            string savedKey = version == 1 ? package : $"{phoneKey}\n{package}";
            await File.WriteAllTextAsync(profileFile, oldProfile);
            await File.WriteAllTextAsync(audioFile, $$$"""
                {"Version":{{{version}}},"Apps":{ {{{JsonSerializer.Serialize(savedKey)}}}:{"Volume":62,"OutputId":"{{{outputId}}}","FutureApp":5}},"FutureRoot":"keep"}
                """);
            var store = new AndroidPhoneStore(profileFile);
            var settings = new AndroidAudioSettingsStore(audioFile);
            var discovery = new AndroidDeviceDiscovery(_ => Task.FromResult<IReadOnlyList<AndroidDevice>>([phone]));
            discovery.Publish([phone]);
            var connections = new AndroidConnectionManager(store, () => discovery, settings.MigrateConnectionPreferences);
            string originalId = store.Profile.Id;
            var app = store.RegisterApp(fingerprint, System.Net.IPEndPoint.Parse("127.0.0.1:12345"), store.ManagementRevision);
            long revision = store.LibraryRevision;
            bool Merge(string id, string pin, long generation)
            {
                try { store.MergeVerifiedAppAlias(id, app.Id, pin, generation, true, () => { }, settings.MigrateConnectionPreferences); return true; }
                catch (SrapException) { return false; }
            }
            Check(!Merge(originalId, fingerprint, revision - 1) && !Merge(originalId, "not a fingerprint", revision),
                $"v{version} store rejects stale or invalid App alias");
            Check(!Merge(other.Serial, fingerprint, revision) && !Merge(originalId, new('B', 64), revision),
                $"v{version} store rejects unrelated profile or key");
            Check(Merge(originalId, fingerprint, revision), $"v{version} preserves verified alias in existing profile");
            Check(new AndroidPhoneStore(profileFile).Profile is { Name: "Original" } loaded
                && loaded.Id == originalId && loaded.AppPublicKeySha256 == fingerprint
                && File.ReadAllText(profileFile + ".pre-connection-management.bak") == oldProfile,
                $"v{version} alias preserves profile and durable pre-migration backup");
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(profileFile));
            Check(saved.RootElement.GetProperty("FutureRoot").GetString() == "keep" &&
                (version == 1 || saved.RootElement.GetProperty("Phones")[0].GetProperty("FutureProfile").GetBoolean()),
                $"v{version} profile unknown fields round trip");
            settings.RememberStreaming(phone, new(MaxFps: 45));
            await settings.FlushAsync();
            var reloaded = new AndroidAudioSettingsStore(audioFile);
            using var audio = JsonDocument.Parse(await File.ReadAllTextAsync(audioFile));
            Check(reloaded.Get(phone, package) is { Volume: 62 } preference && preference.OutputId == outputId
                && audio.RootElement.GetProperty("Apps").GetProperty(savedKey).GetProperty("FutureApp").GetInt32() == 5
                && audio.RootElement.GetProperty("FutureRoot").GetString() == "keep", $"v{version} audio keeps route ID, settings and unknown fields");
        }
    }

    private sealed class SrapChunkStream(byte[] wire, int chunkSize) : MemoryStream(wire)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default) =>
            base.ReadAsync(destination[..Math.Min(chunkSize, destination.Length)], token);
    }
}
