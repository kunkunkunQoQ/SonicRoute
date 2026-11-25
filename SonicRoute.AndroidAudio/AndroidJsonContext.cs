using System.Text.Json.Serialization;

namespace SonicRoute.AndroidAudio;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, WriteIndented = true)]
[JsonSerializable(typeof(AndroidAudioSettingsStore.SettingsFile))]
[JsonSerializable(typeof(AndroidPhoneStore.LegacyFile))]
[JsonSerializable(typeof(AndroidPhoneStore.PhoneFile))]
internal partial class AndroidSettingsJsonContext : JsonSerializerContext { }

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AndroidRuntimeBundle.RuntimeFile[]))]
[JsonSerializable(typeof(AndroidAppCatalogCache.Saved))]
[JsonSerializable(typeof(ClipboardWrite))]
internal partial class AndroidWireJsonContext : JsonSerializerContext { }

internal sealed record ClipboardWrite(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("revision")] long Revision);
