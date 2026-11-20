using SonicRoute.WindowsLink;

internal static partial class Program
{
    // The runner supplies a private fixture with an 'outside' sibling and a junction named 'jump' inside 'listed'.
    private static void CheckFileBrowsePaths(string fixture)
    {
        string parent = Path.GetFullPath(fixture);
        string listed = Path.Combine(parent, "listed");
        string jump = Path.Combine(listed, "jump");
        if (!File.GetAttributes(jump).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Missing test junction.");
        using var cursor = new LinkBrowseDirectory.Cursor();
        Check(new[] { @"relative", @"C:relative", @"C:\test\..\Windows", @"C:\one\test:stream",
            @"C:\test\CON.txt", @"\\server\share", @"\\?\C:\work", @"\\.\C:", @"C:\out/file", @"C:\one\\file" }
            .All(path => !LinkBrowseDirectory.ValidPath(path, false)), "file browsing rejects relative paths, device names, ADS, UNC and namespace bypasses");
        bool Refuses(string path)
        {
            try { using var lease = LinkBrowseDirectory.Pin(path, Directory.Exists(path)); return false; }
            catch (IOException) { return true; }
        }
        Check(Refuses(jump) && Refuses(Path.Combine(jump, "outside.txt")), "a directory junction and a descendant through that junction cannot be opened");

        var names = new HashSet<string>(StringComparer.Ordinal);
        int offset = 0, pages = 0;
        do
        {
            var page = cursor.Read(listed, offset, default);
            Check(page.Entries.Length is > 0 and <= 64 && page.Entries.All(e => names.Add(e.Name)), "a directory page has bounded, unique entries");
            pages++;
            if (page.NextOffset == null) break;
            offset = page.NextOffset.Value;
        } while (pages < 10);
        Check(names.Count == 140 && !names.Contains("jump") && pages >= 3, "a full paginated walk returns every ordinary fixture file and omits reparse targets");
        Check(Refuses(Path.Combine(listed, "nosuch")), "vanished entries fail before sending data");
        string sample = Path.Combine(listed, names.First());
        using var held = LinkBrowseDirectory.Pin(sample, false);
        bool blocked = false;
        try { using var modified = new FileStream(sample, FileMode.Open, FileAccess.Write, FileShare.Read); }
        catch (IOException) { blocked = true; }
        Check(blocked, "an approved download source stays pinned against concurrent modification");
    }
}
