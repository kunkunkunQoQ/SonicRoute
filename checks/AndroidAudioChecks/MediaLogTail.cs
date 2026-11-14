using System.Reflection;
using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task MediaLogTailAsync(string output)
    {
        var method = typeof(AndroidMediaSessionClient).GetMethod("ReadDiagnosticTailAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var read = (Func<TextReader, CancellationToken, Task<string>>)method.CreateDelegate(typeof(Func<TextReader, CancellationToken, Task<string>>));
        const int limit = 16 * 1024;
        Check(await read(new StringReader(""), default) == "", "empty helper stderr needs no retained log");
        string shortLog = "警告 😀\r\nlast error";
        Check(await read(new StringReader(shortLog), default) == shortLog, "short Unicode diagnostics remain unchanged");
        string longLine = new('x', 1024 * 1024);
        longLine += "\nfinal helper failure 中文";
        Check(await read(new StringReader(longLine), default) == longLine[^limit..],
            "a one-megabyte newline-free log retains exactly the newest bounded tail");
        Check(await read(new DiagnosticChunks(longLine), default) == longLine[^limit..],
            "fragmented reads preserve tail ordering across buffer growth and wraparound");
        string unicodeBoundary = "prefix😀" + new string('x', limit - 1);
        Check(await read(new StringReader(unicodeBoundary), default) == new string('x', limit - 1),
            "head truncation never leaves a dangling Unicode low surrogate");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool observed = false;
        try { await read(new StringReader("ignored"), cancelled.Token); }
        catch (OperationCanceledException) { observed = true; }
        Check(observed, "the existing pipe read remains cancellable without a polling loop");
        Check(!typeof(AndroidRuntimeBundle).Assembly.GetManifestResourceNames()
            .Any(n => n.StartsWith("SonicRoute.Notices.", StringComparison.Ordinal)),
            "Full executable embeds no open-source notice resources");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "media-log-tail.json"), JsonSerializer.Serialize(new
        { Assertions = _checks, RetainedCharacters = limit, PhoneAccess = false,
            Scope = "Unicode, fragmented input, oversized newline-free diagnostics and cancellation; not whole-process memory measurement." },
            new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class DiagnosticChunks(string text) : StringReader(text)
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(37, buffer.Length)], cancellationToken);
    }
}
