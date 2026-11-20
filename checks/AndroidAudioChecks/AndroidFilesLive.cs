using System.Security.Cryptography;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task AndroidFilesLiveAsync(string output)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        var token = timeout.Token;
        var transport = new AdbSocketClient();
        var phone = (await transport.DevicesAsync(token)).Where(d => d.State == "device").OrderBy(d => d.IsWifi).First();
        string root = Directory.CreateDirectory(Path.Combine(output, Guid.NewGuid().ToString("N"))).FullName;
        await using var adb = new AdbClient(root, transport);
        var files = new AndroidFileTransfer(adb, phone.Serial);
        string tag = "sonicroute-check-" + Guid.NewGuid().ToString("N");
        string custom = AndroidFileTransfer.Downloads + "/" + tag;
        string name = tag + " 文件 '1'.bin";
        string source = Path.Combine(root, name);
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 7), token);
        var store = new AndroidPhoneStore(Path.Combine(root, "phones.json"));
        store.RememberDevice(phone);
        string target = store.Profile.ReceiveFolder;
        Check(target == "/sdcard/Download", "an authorized phone defaults to its Downloads folder");
        string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
        var owned = new List<string>();
        bool madeDirectory = false;
        try
        {
            string original = await files.SendAsync(source, target, token);
            owned.Add(original);
            Check(original == target + "/" + name, "a selected file is delivered to Android Downloads without a receipt prompt");
            var mkdir = await transport.RunShellAsync(phone.Serial, "mkdir " + Quote(custom), token);
            if (mkdir.ExitCode != 0) throw new IOException(mkdir.Error);
            madeDirectory = true;
            store.SetReceiveFolder(store.Profile.Id, custom);
            await store.FlushAsync();
            var restored = new AndroidPhoneStore(store.FilePath);
            Check(restored.Profile.ReceiveFolder == custom, "the selected phone receive folder survives restarting the store");
            string copy = await files.SendAsync(source, restored.Profile.ReceiveFolder, token);
            owned.Add(copy);
            string duplicate = await files.SendAsync(source, custom, token);
            owned.Add(duplicate);
            Check(copy == custom + "/" + name && duplicate != copy, "a custom phone folder receives files and preserves name collisions");
            var items = await files.ListAsync(custom, token);
            Check(items.Any(i => !i.Directory && i.Name == name), "the Android file picker lists the transferred file");
            bool wide = (await transport.FeaturesAsync(phone.Serial, token)).Contains("ls_v2");
            var remote = items.Single(i => i.Name == name);
            Check(wide ? remote.Size == (ulong)new FileInfo(source).Length && remote.SizeText == "2 MB" : remote.SizeText == "",
                wide ? "the phone reports the exact 64-bit size of the uploaded file" : "a legacy phone omits ambiguous size information");
            string restoredPath = await files.ReceiveAsync(custom, name, root, token);
            byte[] expected = SHA256.HashData(await File.ReadAllBytesAsync(source, token));
            byte[] actual = SHA256.HashData(await File.ReadAllBytesAsync(restoredPath, token));
            Check(restoredPath != source && expected.SequenceEqual(actual),
                "the phone-to-PC binary transfer preserves SHA-256 and existing downloads");
            foreach (string path in owned)
                if (!path.StartsWith(custom + "/", StringComparison.Ordinal) && path != target + "/" + name)
                    throw new IOException("Unexpected test file path.");
        }
        finally
        {
            foreach (string file in owned)
                if (file.StartsWith(custom + "/", StringComparison.Ordinal) || file == target + "/" + name)
                    await transport.RunShellAsync(phone.Serial, "rm -f " + Quote(file), CancellationToken.None);
            if (madeDirectory) await transport.RunShellAsync(phone.Serial, "rmdir " + Quote(custom), CancellationToken.None);
        }
    }
}
