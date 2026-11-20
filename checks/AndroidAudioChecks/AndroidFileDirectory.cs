using System.Buffers.Binary;
using System.Text;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task AndroidFileDirectoryAsync()
    {
        static string Label(string name) => AndroidFileTransfer.FormatDisplayName(name);
        Check(Label("%E4%B8%AD%E6%96%87%20A+B.pdf") == "中文 A+B.pdf"
            && Label("0%E2%82%AC+1") == "0€+1",
            "safe UTF-8 percent escapes and interior spaces are readable and literal plus is preserved");
        Check(new[] { "%2e%2e", "%2F", "%5C", "%25E9", "%ff", "%C3", "%E2%80%AE", "%00", "a%", "a%20", "%20a" }
            .All(n => Label(n) == n) && Label("a\u202Eb") == "a\\u202Eb",
            "malformed, ambiguous, hidden or separator escapes stay literal, and a raw bidi override is shown visibly");

        static async Task<List<AndroidStorageFile>> Directory(bool wide)
        {
            using var data = new MemoryStream();
            void Entry(string name, int mode, ulong size, int when)
            {
                byte[] entry = Encoding.UTF8.GetBytes(name);
                byte[] head = new byte[wide ? 76 : 20];
                (wide ? "DNT2"u8 : "DENT"u8).CopyTo(head);
                BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(wide ? 24 : 4), mode);
                if (wide)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(40), size);
                    BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(56), when);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), unchecked((uint)size));
                    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), (uint)when);
                }
                BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(head.Length - 4), entry.Length);
                data.Write(head);
                data.Write(entry);
            }
            Entry("older", 0x8000, 1024, 31);
            Entry("%E4%B8%AD%E6%96%87", 0x8000, 1, 21);
            Entry("中文", 0x8000, 1, 20);
            Entry(".sonicroute-" + Guid.NewGuid().ToString("N") + ".part", 0x8000, 0, 98);
            Entry(".sonicroute-user", 0x8000, 2, 90);
            Entry("folder", 0x4000, 4096, 15);
            Entry(".hidden", 0x4000, 4096, 82);
            Entry("newest", 0x8000, 0x1_4000_0000, 82);
            data.Write("DONE\0\0\0\0"u8);
            data.Position = 0;
            return await AndroidFileTransfer.ReadDirectoryAsync(data, wide, default);
        }
        foreach (bool wide in new[] { false, true })
        {
            var files = await Directory(wide);
            Check(files.Select(f => f.Name).SequenceEqual(["folder", "newest", "older", "%E4%B8%AD%E6%96%87", "中文", ".hidden", ".sonicroute-user"]),
                $"ADB {(wide ? "LIS2" : "LIST")} sorts folders and recent files ahead of dot entries and skips only transfer staging files");
            Check(files[3].Label == files[3].Name && files[4].Label == files[4].Name,
                "conflicting friendly names retain distinguishable labels and exact transfer paths");
            Check(files[0].SizeText == "" && (wide ? files[1].SizeText == "5 GB" && files[2].SizeText == "1 KB"
                : files.All(f => f.SizeText == "")), wide ? "64-bit listings display the real size above 4 GiB" :
                "a 32-bit listing cannot mislabel a wrapped file size");
        }
    }
}
