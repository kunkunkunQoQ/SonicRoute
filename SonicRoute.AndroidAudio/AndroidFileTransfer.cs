using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SonicRoute.AndroidAudio;

internal sealed record AndroidStorageFile(string Name, bool Directory)
{
    public string Label { get; internal set; } = AndroidFileTransfer.FormatDisplayName(Name);
    internal ulong? Size { get; init; }
    internal long Modified { get; init; }
    public string SizeText => Directory || Size is not { } bytes ? "" : bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024UL * 1024 => $"{bytes / 1024d:0.#} KB",
        < 1024UL * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):0.#} GB"
    };
}

internal sealed class AndroidFileTransfer(AdbClient adb, string serial, Action? requireCurrent = null) : IPhoneFileOperations
{
    internal const string Storage = "/sdcard";
    internal const string Downloads = Storage + "/Download";
    private const int Chunk = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public async Task<string> SendAsync(string path, string destination, CancellationToken token)
    {
        requireCurrent?.Invoke();
        ValidateFolder(destination);
        string name = Path.GetFileName(path);
        if (!Path.IsPathFullyQualified(path) || !IsSafeTransferName(name))
            throw new IOException("Invalid file name.");
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var transport = await adb.ResolveTransportAsync(token).ConfigureAwait(false);
        string stage = destination + "/.sonicroute-" + Guid.NewGuid().ToString("N") + ".part";
        await Shell(destination == Downloads ? "mkdir -p " + Quote(destination) : "test -d " + Quote(destination), token).ConfigureAwait(false);
        try
        {
            await transport.PushAsync(serial, path, stage, token).ConfigureAwait(false);
            // Reserve each candidate without clobbering another sender. Only rename the completed staging file.
            string cmd = "dir=" + Quote(destination) + "; stem=" + Quote(Path.GetFileNameWithoutExtension(name)) +
                "; ext=" + Quote(Path.GetExtension(name)) + "; i=0; f=\"$dir/$stem$ext\"; " +
                "while ! (set -C; : > \"$f\") 2>/dev/null; do " +
                "[ -e \"$f\" ] || [ -L \"$f\" ] || exit 1; i=$((i+1)); f=\"$dir/$stem ($i)$ext\"; done; " +
                "if mv -f " + Quote(stage) + " \"$f\"; then printf '%s' \"$f\"; else rm -f \"$f\"; exit 1; fi";
            string saved = await Shell(cmd, token).ConfigureAwait(false);
            if (!saved.StartsWith(destination + "/", StringComparison.Ordinal)) throw new IOException("Android could not save the file.");
            return saved;
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await Shell("rm -f " + Quote(stage), cleanup.Token).ConfigureAwait(false); } catch { }
            throw;
        }

        async Task<string> Shell(string command, CancellationToken ct)
        {
            var result = await transport.RunShellAsync(serial, command, ct).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(result.Error) ? $"Android could not write to {destination}." : result.Error.Trim());
            return result.Output.TrimEnd('\r', '\n');
        }
    }

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    internal static string FormatDisplayName(string name)
    {
        if (name.IndexOf('%') < 0) return EscapeUnsafeCharacters(name);
        var label = new StringBuilder(name.Length);
        byte[] bytes = new byte[name.Length / 3];
        for (int i = 0; i < name.Length;)
        {
            if (name[i] != '%') { label.Append(name[i++]); continue; }
            int count = 0;
            while (i < name.Length && name[i] == '%')
            {
                if (i + 2 >= name.Length || !Uri.IsHexDigit(name[i + 1]) || !Uri.IsHexDigit(name[i + 2]))
                    return EscapeUnsafeCharacters(name);
                bytes[count++] = (byte)(Uri.FromHex(name[i + 1]) * 16 + Uri.FromHex(name[i + 2]));
                i += 3;
            }
            string segment;
            try { segment = Utf8.GetString(bytes, 0, count); }
            catch (DecoderFallbackException) { return EscapeUnsafeCharacters(name); }
            foreach (Rune rune in segment.EnumerateRunes())
                if ((rune.IsAscii && rune.Value != ' ') || UnsafeCharacter(rune))
                    return EscapeUnsafeCharacters(name);
            label.Append(segment);
        }
        string decoded = label.ToString();
        // Encoded leading or trailing spaces would be invisible and alter the apparent name.
        if (decoded.Length != decoded.Trim().Length || decoded is "." or "..") return EscapeUnsafeCharacters(name);
        return EscapeUnsafeCharacters(decoded);
    }

    private static bool UnsafeCharacter(Rune rune) => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
        or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
        || rune.Value != ' ' && Rune.IsWhiteSpace(rune)
        || rune.Value is '/' or '\\' or 0x2044 or 0x2215 or 0xff0f or 0xff3c;

    private static string EscapeUnsafeCharacters(string name)
    {
        StringBuilder? escaped = null;
        int index = 0;
        foreach (Rune rune in name.EnumerateRunes())
        {
            if (UnsafeCharacter(rune))
            {
                escaped ??= new StringBuilder(name.Length).Append(name, 0, index);
                escaped.Append(rune.Value <= 0xffff ? $"\\u{rune.Value:X4}" : $"\\U{rune.Value:X8}");
            }
            else escaped?.Append(name.AsSpan(index, rune.Utf16SequenceLength));
            index += rune.Utf16SequenceLength;
        }
        return escaped?.ToString() ?? name;
    }

    public async Task<List<AndroidStorageFile>> ListAsync(string folder, CancellationToken token)
    {
        requireCurrent?.Invoke();
        var transport = await adb.ResolveTransportAsync(token).ConfigureAwait(false);
        bool wide;
        try { wide = (await transport.FeaturesAsync(serial, token).ConfigureAwait(false)).Contains("ls_v2"); }
        catch (IOException) when (!token.IsCancellationRequested) { wide = false; } // Old ADB hosts can reject the feature query.
        await using var stream = await OpenAsync(wide ? "LIS2" : "LIST", folder, token, transport).ConfigureAwait(false);
        return await ReadDirectoryAsync(stream, wide, token).ConfigureAwait(false);
    }

    internal static async Task<List<AndroidStorageFile>> ReadDirectoryAsync(Stream stream, bool wide, CancellationToken token)
    {
        var files = new List<AndroidStorageFile>();
        byte[] header = new byte[wide ? 76 : 20];
        byte[] entry = new byte[1024];
        while (true)
        {
            await stream.ReadExactlyAsync(header.AsMemory(0, 8), token).ConfigureAwait(false);
            await CheckFailure(stream, header, token).ConfigureAwait(false);
            if (header.AsSpan(0, 4).SequenceEqual("DONE"u8)) break;
            if (!header.AsSpan(0, 4).SequenceEqual(wide ? "DNT2"u8 : "DENT"u8)) throw new InvalidDataException("Invalid directory entry.");
            await stream.ReadExactlyAsync(header.AsMemory(8), token).ConfigureAwait(false);
            int mode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(wide ? 24 : 4)) & 0xf000;
            int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(header.Length - 4));
            if (length < 1 || length > entry.Length) throw new InvalidDataException("Invalid directory entry length.");
            await stream.ReadExactlyAsync(entry.AsMemory(0, length), token).ConfigureAwait(false);
            if (mode is not (0x4000 or 0x8000) || wide && BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)) != 0)
                continue;
            string name;
            try { name = Utf8.GetString(entry, 0, length); } catch (DecoderFallbackException) { continue; }
            if (ValidSegment(name) && (mode == 0x4000 || !IsStagingFile(name)))
                files.Add(new(name, mode == 0x4000)
                {
                    // LIST v1 truncates size modulo 2^32, even when the returned value is zero.
                    Size = wide ? BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(40)) : null,
                    Modified = wide ? BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(56))
                        : BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12))
                });
        }
        if (files.Any(f => f.Label != f.Name))
        {
            var aliases = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var file in files)
                if (file.Label != file.Name) aliases.TryAdd(file.Label, 0);
            foreach (var file in files)
                if (aliases.TryGetValue(file.Label, out int count)) aliases[file.Label] = count + 1;
            foreach (var file in files)
                if (aliases.TryGetValue(file.Label, out int count) && count > 1) file.Label = EscapeUnsafeCharacters(file.Name);
        }
        files.Sort((a, b) =>
        {
            int order = a.Name.StartsWith('.').CompareTo(b.Name.StartsWith('.'));
            if (order == 0) order = b.Directory.CompareTo(a.Directory);
            if (order == 0 && !a.Directory) order = b.Modified.CompareTo(a.Modified);
            if (order == 0) order = StringComparer.CurrentCultureIgnoreCase.Compare(a.Label, b.Label);
            return order != 0 ? order : string.CompareOrdinal(a.Name, b.Name);
        });
        return files;
    }

    private static bool IsStagingFile(string name) => name.Length == ".sonicroute-".Length + 32 + ".part".Length
        && name.StartsWith(".sonicroute-", StringComparison.Ordinal) && name.EndsWith(".part", StringComparison.Ordinal)
        && Guid.TryParseExact(name.AsSpan(".sonicroute-".Length, 32), "N", out _);

    public async Task<string> ReceiveAsync(string folder, string name, string destination, CancellationToken token)
    {
        requireCurrent?.Invoke();
        if (!ValidSegment(name) || !IsSafeTransferName(name)) throw new IOException("Invalid Windows file name.");
        Directory.CreateDirectory(destination);
        string temp = Path.Combine(destination, ".sonicroute-" + Guid.NewGuid().ToString("N") + ".part");
        await using var input = await OpenAsync("RECV", folder + "/" + name, token).ConfigureAwait(false);
        var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, Chunk, true);
        try
        {
            await using (output)
            {
                byte[] header = new byte[8];
                byte[] buf = ArrayPool<byte>.Shared.Rent(Chunk);
                try
                {
                    while (true)
                    {
                        await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
                        await CheckFailure(input, header, token).ConfigureAwait(false);
                        if (header.AsSpan(0, 4).SequenceEqual("DONE"u8)) break;
                        int n = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                        if (!header.AsSpan(0, 4).SequenceEqual("DATA"u8) || n is < 1 or > Chunk)
                            throw new InvalidDataException("Invalid file data.");
                        await input.ReadExactlyAsync(buf.AsMemory(0, n), token).ConfigureAwait(false);
                        await output.WriteAsync(buf.AsMemory(0, n), token).ConfigureAwait(false);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buf); }
            }
            return CommitToReceiveDirectory(temp, Path.Combine(destination, name), token);
        }
        finally { File.Delete(temp); }
    }

    internal static string CommitToReceiveDirectory(string temp, string path, CancellationToken token)
    {
        string directory = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        string candidate = path;
        for (int index = 1; ; index++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                File.Move(temp, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 80 or 183 || Directory.Exists(candidate) || File.Exists(candidate)) { }
            string suffix = $" ({index})";
            // File components are limited to 255 UTF-16 code units, including the suffix.
            string ext = TrimFilePart(extension, 255 - suffix.Length);
            string prefix = TrimFilePart(stem, 255 - suffix.Length - ext.Length);
            candidate = Path.Combine(directory, prefix + suffix + ext);
        }
    }

    private static string TrimFilePart(string value, int limit)
    {
        if (value.Length <= limit) return value;
        if (limit > 0 && char.IsHighSurrogate(value[limit - 1])) limit--;
        return value[..limit];
    }

    internal static bool IsSafeTransferName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.EndsWith('.') || name.EndsWith(' ')
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Any(c => char.IsControl(c)
                || c is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069')) return false;
        string device = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        return device is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$")
            && !(device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT"))
                && "0123456789¹²³".Contains(device[3]));
    }

    private static bool ValidSegment(string name) => name.Length != 0 && name is not "." and not ".."
        && !name.Contains('/') && !name.Any(char.IsControl);

    internal static void ValidateFolder(string path)
    {
        if (!IsSharedPath(path))
            throw new ArgumentException("Choose a folder in shared storage.", nameof(path));
    }

    internal static bool IsSharedPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var segments = path.Split('/');
        return segments.Length >= 2 && segments[0] == "" && segments[1] == "sdcard"
            && segments.Skip(2).All(ValidSegment);
    }

    private async Task<Stream> OpenAsync(string request, string path, CancellationToken token, IAdbTransport? transport = null)
    {
        requireCurrent?.Invoke();
        ValidateFolder(path);
        byte[] data = Utf8.GetBytes(path);
        byte[] header = new byte[8];
        Encoding.ASCII.GetBytes(request, header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), data.Length);
        transport ??= await adb.ResolveTransportAsync(token).ConfigureAwait(false);
        var stream = await transport.OpenFileSyncAsync(serial, token).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, token).ConfigureAwait(false);
            await stream.WriteAsync(data, token).ConfigureAwait(false);
            return stream;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static async Task CheckFailure(Stream stream, byte[] header, CancellationToken token)
    {
        if (!header.AsSpan(0, 4).SequenceEqual("FAIL"u8)) return;
        int n = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (n is < 0 or > Chunk) throw new InvalidDataException("Invalid ADB error.");
        byte[] message = new byte[n];
        await stream.ReadExactlyAsync(message, token).ConfigureAwait(false);
        throw new IOException(Encoding.UTF8.GetString(message));
    }
}
