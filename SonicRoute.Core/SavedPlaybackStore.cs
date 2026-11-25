using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace SonicRoute.Core;

public sealed class SavedPlaybackSelection
{
    public IReadOnlyList<string> Outputs { get; }
    public string Preferred { get; }

    [JsonConstructor]
    public SavedPlaybackSelection(IReadOnlyList<string> outputs, string? preferred)
    {
        if (outputs == null || outputs.Any(string.IsNullOrWhiteSpace))
            throw new JsonException("Expected a list of playback endpoint IDs.");
        var ids = outputs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Outputs = Array.AsReadOnly(ids);
        Preferred = ids.FirstOrDefault(id => string.Equals(id, preferred, StringComparison.OrdinalIgnoreCase))
            ?? ids.FirstOrDefault() ?? "";
    }

    internal bool Matches(SavedPlaybackSelection other) =>
        string.Equals(Preferred, other.Preferred, StringComparison.OrdinalIgnoreCase)
        && Outputs.SequenceEqual(other.Outputs, StringComparer.OrdinalIgnoreCase);
}

public static class SavedPlaybackStore
{
    private static readonly StringComparer Keys = StringComparer.OrdinalIgnoreCase;
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(AppDataRoot.DirectoryPath, "playback-outputs.json");
    private static readonly Lazy<string> WriterName = new(() =>
    {
        // All editions sharing this data directory merge their per-app edits under the same writer lock.
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(FilePath.ToUpperInvariant()));
        return @"Global\SonicRoute.PlaybackOutputs." + BitConverter.ToString(hash).Replace("-", "");
    });
    private static IReadOnlyDictionary<string, SavedPlaybackSelection>? _snapshot;
#if NET10_0_OR_GREATER
    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<Dictionary<string, SavedPlaybackSelection>> Options => PersistenceJsonContext.Default.DictionaryStringSavedPlaybackSelection;
#else
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
#endif

    public static IReadOnlyDictionary<string, SavedPlaybackSelection> ReadAll()
    {
        if (Volatile.Read(ref _snapshot) is { } cached) return cached;
        lock (Gate)
        {
            if (_snapshot != null) return _snapshot;
            Dictionary<string, SavedPlaybackSelection> entries;
            try { entries = ReadCurrent(out _, out _); }
            catch (Exception ex) when (IsStorageError(ex))
            {
                ExceptionDiagnostics.Record("SavedPlayback.Load", ex);
                entries = new(Keys);
            }
            return Publish(entries);
        }
    }

    // One Windows endpoint is already remembered by the system. Store only additional playback choices.
    public static bool TrySave(string appKey, IReadOnlyList<string> outputs, string? preferred)
    {
        if (string.IsNullOrWhiteSpace(appKey) || outputs == null || outputs.Any(string.IsNullOrWhiteSpace)) return false;
        var selection = new SavedPlaybackSelection(outputs, preferred);
        return Update(appKey, selection.Outputs.Count > 1 ? selection : null);
    }

    public static bool TryClear() => Update(null, null);

    private static bool Update(string? appKey, SavedPlaybackSelection? selection)
    {
        lock (Gate)
        {
            try
            {
                using var mutex = new Mutex(false, WriterName.Value);
                try { if (!mutex.WaitOne(TimeSpan.FromSeconds(3))) return false; }
                catch (AbandonedMutexException) { /* Ownership was transferred to this thread. */ }
                try
                {
                    // Read under the interprocess lock: an edition with a stale startup snapshot must not erase another app's edit.
                    var entries = ReadCurrent(out bool exists, out bool corrupt);
                    bool changed;
                    if (appKey == null) { changed = entries.Count != 0; entries.Clear(); }
                    else if (selection == null) changed = entries.Remove(appKey);
                    else
                    {
                        changed = !entries.TryGetValue(appKey, out var previous) || !selection.Matches(previous);
                        if (changed) entries[appKey] = ReuseSelection(appKey, selection);
                    }
                    if (changed)
                    {
                        Directory.CreateDirectory(AppDataRoot.DirectoryPath);
                        if (exists)
                        {
                            string backup = corrupt ? FilePath + ".corrupt-" + Guid.NewGuid().ToString("N") : FilePath + ".bak";
                            using var original = OpenRead(FilePath);
                            AtomicFile.Write(backup, stream => original.CopyTo(stream));
                        }
                        AtomicFile.Write(FilePath, stream => JsonSerializer.Serialize(stream, entries, Options));
                    }
                    Publish(entries);
                    return true;
                }
                finally { mutex.ReleaseMutex(); }
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                ExceptionDiagnostics.Record("SavedPlayback.Save", ex);
                return false;
            }
        }
    }

    private static IReadOnlyDictionary<string, SavedPlaybackSelection> Publish(Dictionary<string, SavedPlaybackSelection> entries)
    {
        if (_snapshot is { } current && current.Count == entries.Count
            && entries.All(p => current.TryGetValue(p.Key, out var saved) && ReferenceEquals(saved, p.Value)))
            return current;
        var snapshot = new ReadOnlyDictionary<string, SavedPlaybackSelection>(entries);
        Volatile.Write(ref _snapshot, snapshot);
        return snapshot;
    }

    private static Dictionary<string, SavedPlaybackSelection> ReadCurrent(out bool exists, out bool corrupt)
    {
        corrupt = false;
        try { return ReadFile(FilePath, out exists); }
        catch (JsonException ex)
        {
            exists = corrupt = true;
            ExceptionDiagnostics.Record("SavedPlayback.ReadBackup", ex);
            return ReadFile(FilePath + ".bak", out _);
        }
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    private static Dictionary<string, SavedPlaybackSelection> ReadFile(string path, out bool exists)
    {
        var entries = new Dictionary<string, SavedPlaybackSelection>(Keys);
        FileStream stream;
        try { stream = OpenRead(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { exists = false; return entries; }
        exists = true;
        using (stream)
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, SavedPlaybackSelection>>(stream, Options)
                ?? throw new JsonException("Expected saved playback selections.");
            foreach (var item in saved)
            {
                if (string.IsNullOrWhiteSpace(item.Key) || item.Value == null) throw new JsonException("Invalid playback selection.");
                if (item.Value.Outputs.Count > 1) entries[item.Key] = ReuseSelection(item.Key, item.Value);
            }
        }
        return entries;
    }

    private static SavedPlaybackSelection ReuseSelection(string key, SavedPlaybackSelection selection) =>
        _snapshot != null && _snapshot.TryGetValue(key, out var known) && known.Matches(selection) ? known : selection;

    private static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or JsonException or WaitHandleCannotBeOpenedException;
}
