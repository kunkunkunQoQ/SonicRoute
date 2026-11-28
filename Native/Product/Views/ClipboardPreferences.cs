#if SONICROUTE_PHONE
using System.Text.Json;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

namespace SonicRoute.Native.Product;

internal sealed record ClipboardPreferences(AndroidDevice[] Enabled, AndroidDevice[] Archived, Dictionary<string, JsonElement>? Other)
{
    internal ClipboardPreferences() : this([], [], null) { }

    internal static AndroidDevice[] Snapshot(IEnumerable<AndroidDevice> source) =>
        source.Select(d => d with { Name = "", State = "offline", TransportId = null }).ToArray();

    internal static ClipboardPreferences? TryLoad(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > 64 * 1024) throw new JsonException("Clipboard preferences exceed the expected size.");
            using var document = JsonDocument.Parse(file);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Version", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 1
                || !root.TryGetProperty("EnabledDevices", out var enabled))
                throw new JsonException("Unsupported clipboard preferences.");
            var archived = root.TryGetProperty("ArchivedDevices", out var value) && value.ValueKind != JsonValueKind.Null
                ? ReadDevices(value, limitEnabledDevices: false) : [];
            Dictionary<string, JsonElement>? other = null;
            foreach (var entry in root.EnumerateObject())
                if (entry.Name is not ("Version" or "EnabledDevices" or "ArchivedDevices"))
                    (other ??= new(StringComparer.Ordinal))[entry.Name] = entry.Value.Clone();
            return new(ReadDevices(enabled, limitEnabledDevices: true), archived, other);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            ExceptionDiagnostics.Record("PhoneClipboard.Load", error);
            return null;
        }
    }

    private static AndroidDevice[] ReadDevices(JsonElement list, bool limitEnabledDevices)
    {
        if (list.ValueKind != JsonValueKind.Array || (limitEnabledDevices && list.GetArrayLength() > 32))
            throw new JsonException("Invalid clipboard device list.");
        var devices = list.Deserialize(AndroidSettingsJsonContext.Default.AndroidDeviceArray) ?? [];
        return devices.Where(d => d is { IsApp: false } && !string.IsNullOrWhiteSpace(d.Serial) && d.Serial.Length <= 512
            && (d.HardwareId?.Length ?? 0) <= 128).DistinctBy(AndroidDeviceIdentity.Key).ToArray();
    }

    internal void Write(Stream stream)
    {
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteNumber("Version", 1);
        writer.WritePropertyName("EnabledDevices");
        JsonSerializer.Serialize(writer, Enabled, AndroidSettingsJsonContext.Default.AndroidDeviceArray);
        if (Archived.Length > 0)
        {
            writer.WritePropertyName("ArchivedDevices");
            JsonSerializer.Serialize(writer, Archived, AndroidSettingsJsonContext.Default.AndroidDeviceArray);
        }
        if (Other != null)
            foreach (var (name, value) in Other) { writer.WritePropertyName(name); value.WriteTo(writer); }
        writer.WriteEndObject();
    }
}
#endif
