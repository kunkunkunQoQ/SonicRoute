using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonicRoute.Core.Models;

// WPF 1.36 writes Int32. Accept recognized textual names on import and keep its numeric wire format.
internal sealed class StoredRuleEnum<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number))
            return (T)Enum.ToObject(typeof(T), number);
        if (reader.TokenType == JsonTokenType.String && Enum.TryParse<T>(reader.GetString(), true, out var named)
            && Enum.IsDefined(typeof(T), named)) return named;
        throw new JsonException("Unrecognized automation rule enum value.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteNumberValue(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
}
