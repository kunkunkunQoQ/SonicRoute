#if NET10_0_OR_GREATER
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SonicRoute.Core;

public static partial class ConfigService
{
    // A syntactically intact object can have a value the old contract cannot read.
    // Preserve unaffected fields, including future fields, without a reflection fallback.
    private static bool TryRecoverFields(string path, out AppConfig result, out string[] fields, out string? original)
    {
        result = new AppConfig();
        fields = [];
        original = null;
        try
        {
            byte[] raw = File.ReadAllBytes(path);
            using var reader = new StreamReader(new MemoryStream(raw), detectEncodingFromByteOrderMarks: true);
            using var document = JsonDocument.Parse(reader.ReadToEnd());
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var contracts = WriteContract.Properties.Where(p => !p.IsExtensionData).ToDictionary(p => p.Name, StringComparer.Ordinal);
            var invalid = new List<string>();
            foreach (var item in document.RootElement.EnumerateObject())
            {
                if (!contracts.TryGetValue(item.Name, out var property))
                {
                    (result.AdditionalFields ??= new(StringComparer.Ordinal))[item.Name] = item.Value.Clone();
                    continue;
                }
                if (property.Set == null) continue;
                try
                {
                    JsonTypeInfo type = WriteContract.Options.GetTypeInfo(property.PropertyType);
                    property.Set(result, item.Value.Deserialize(type));
                }
                catch (JsonException)
                {
                    invalid.Add(item.Name);
                    if (property.PropertyType == typeof(Dictionary<string, string>) && item.Value.ValueKind == JsonValueKind.Object)
                    {
                        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var entry in item.Value.EnumerateObject())
                            if (entry.Value.ValueKind == JsonValueKind.String) kept[entry.Name] = entry.Value.GetString()!;
                        property.Set(result, kept);
                    }
                    else if (property.PropertyType == typeof(List<string>) && item.Value.ValueKind == JsonValueKind.Array)
                        property.Set(result, item.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                            .Select(v => v.GetString()!).ToList());
                }
            }
            if (invalid.Count == 0) return false;
            fields = invalid.Distinct(StringComparer.Ordinal).ToArray();
            string backup = path + ".corrupt-fields-" + Convert.ToHexString(SHA256.HashData(raw));
            try
            {
                if (!File.Exists(backup)) AtomicFile.WriteAllBytes(backup, raw);
                if (!File.ReadAllBytes(backup).AsSpan().SequenceEqual(raw)) throw new IOException("The recovery copy differs from the original.");
                original = backup;
            }
            catch (Exception error) { ExceptionDiagnostics.Record("Config.PreserveFields", error); }
            return true;
        }
        catch (Exception error)
        {
            ExceptionDiagnostics.Record("Config.ReadFields", error);
            return false;
        }
    }
}
#endif
