using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace SonicRoute.AndroidAudio;

internal enum SrapFrameType : byte { Control = 1, Data = 2 }

internal sealed record SrapFrame(SrapFrameType Type, uint StreamId, byte[] Payload);
internal sealed record SrapCapability(string State, string? Reason, string? Scope, IReadOnlyDictionary<string, string>? Limits);

/// <summary>SRAP framing and shared wire vocabulary. TLS and session admission are separate.</summary>
internal static class SrapProtocol
{
    internal const int HeaderSize = 16;
    internal const int MaxPayload = 64 * 1024;
    internal const string Name = "sonicroute.android";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> CapabilityIds = new(StringComparer.Ordinal)
    {
        "catalog.apps", "media.control", "volume.device", "audio.capture.device", "audio.capture.app",
        "audio.capture.exclusive", "audio.play", "audio.devices", "audio.route.owned", "audio.route.other_app",
        "audio.volume.other_app", "bluetooth.control", "display.mirror", "display.virtual", "display.input",
        "files.transfer", "clipboard.sync", "automation.rules", "identity.alias.adb"
    };
    private static readonly HashSet<string> CapabilityStates = new(StringComparer.Ordinal)
    {
        "not_implemented", "needs_authorization", "unsupported", "unavailable", "available"
    };

    internal static async ValueTask<SrapFrame?> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[HeaderSize];
        int first = await stream.ReadAsync(header, token).ConfigureAwait(false);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(first), token).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("SRAP"u8) || header[5] != 0 || header[6] != 0 || header[7] != 0)
            throw new InvalidDataException("Invalid SRAP frame header.");
        var type = (SrapFrameType)header[4];
        uint streamId = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
        ValidateFrame(type, streamId, length);
        var payload = new byte[(int)length];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return new(type, streamId, payload);
    }

    internal static byte[] Encode(SrapFrame frame)
    {
        var buffer = new byte[HeaderSize + frame.Payload.Length];
        WriteHeader(buffer, frame.Type, frame.StreamId, frame.Payload.Length);
        frame.Payload.CopyTo(buffer, HeaderSize);
        return buffer;
    }

    internal static void WriteHeader(Span<byte> buffer, SrapFrameType type, uint streamId, int length)
    {
        ValidateFrame(type, streamId, (uint)length);
        "SRAP"u8.CopyTo(buffer);
        buffer[4] = (byte)type;
        buffer.Slice(5, 3).Clear();
        BinaryPrimitives.WriteUInt32BigEndian(buffer[8..], streamId);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[12..], (uint)length);
    }

    private static void ValidateFrame(SrapFrameType type, uint streamId, uint length)
    {
        if (length is 0 or > MaxPayload ||
            (type switch { SrapFrameType.Control => streamId != 0, SrapFrameType.Data => streamId == 0, _ => true }))
            throw new InvalidDataException("Invalid SRAP type, stream ID or payload length.");
    }

    // Caller owns the document. Use a UTF-8 decoder that rejects invalid bytes rather than replacing them.
    internal static JsonDocument ParseControl(SrapFrame frame)
    {
        if (frame.Type != SrapFrameType.Control) throw new InvalidDataException("Expected an SRAP control frame.");
        JsonDocument? document = null;
        try
        {
            _ = StrictUtf8.GetCharCount(frame.Payload);
            document = JsonDocument.Parse(frame.Payload, new JsonDocumentOptions { MaxDepth = 16 });
            var envelope = document.RootElement;
            ValidateUniqueKeys(envelope);
            _ = RequiredText(envelope, "type");
            _ = Required(envelope, "body", JsonValueKind.Object);
            return document;
        }
        catch (Exception error) when (error is DecoderFallbackException or JsonException or InvalidDataException)
        {
            document?.Dispose();
            throw new InvalidDataException("Invalid SRAP control JSON.", error);
        }
    }

    private static void ValidateUniqueKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw new InvalidDataException("Duplicate SRAP key.");
                ValidateUniqueKeys(field.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateUniqueKeys(item);
    }

    internal static int? NegotiateMinor(JsonElement body, int major, int minorMin, int minorMax)
    {
        string name = RequiredText(body, "protocol");
        int peerMajor = VersionNumber(body, "major");
        int peerMin = VersionNumber(body, "minorMin");
        int peerMax = VersionNumber(body, "minorMax");
        if (peerMax < peerMin) throw new InvalidDataException("Invalid SRAP version range.");
        int highest = Math.Min(minorMax, peerMax);
        return name == Name && peerMajor == major && Math.Max(minorMin, peerMin) <= highest ? highest : null;
    }

    private static int VersionNumber(JsonElement body, string name)
    {
        if (Required(body, name, JsonValueKind.Number).TryGetInt32(out int value) && value is >= 0 and <= 65535)
            return value;
        throw new InvalidDataException("Invalid SRAP version field.");
    }

    internal static (long Revision, IReadOnlyDictionary<string, SrapCapability> Capabilities) ReadCapabilities(JsonElement body)
    {
        if (!Required(body, "capabilityRevision", JsonValueKind.Number).TryGetInt64(out long revision) || revision < 0)
            throw new InvalidDataException("Invalid SRAP capability revision.");
        var capabilities = new Dictionary<string, SrapCapability>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Required(body, "entries", JsonValueKind.Array).EnumerateArray())
        {
            string id = RequiredText(entry, "id");
            if (!seen.Add(id)) throw new InvalidDataException("Duplicate SRAP capability.");
            if (!CapabilityIds.Contains(id)) continue;
            string state = RequiredText(entry, "state");
            if (!CapabilityStates.Contains(state)) continue;
            Dictionary<string, string>? limits = null;
            if (entry.TryGetProperty("limits", out var values))
            {
                limits = new(StringComparer.Ordinal);
                foreach (var item in Required(entry, "limits", JsonValueKind.Object).EnumerateObject())
                    limits.Add(item.Name, RequiredText(values, item.Name));
            }
            capabilities.Add(id, new(state, OptionalText(entry, "reason"), OptionalText(entry, "scope"), limits));
        }
        return (revision, capabilities);
    }

    private static string? OptionalText(JsonElement element, string name) =>
        element.TryGetProperty(name, out _) ? RequiredText(element, name) : null;

    private static string RequiredText(JsonElement element, string name)
    {
        string value = Required(element, name, JsonValueKind.String).GetString()!;
        int limit = name == "scope" ? 265 : 128;
        if (value.Length == 0 || value.Length > limit) throw new InvalidDataException($"Invalid SRAP field {name}.");
        return value;
    }

    private static JsonElement Required(JsonElement element, string name, JsonValueKind kind)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw new InvalidDataException($"Missing or invalid SRAP field {name}.");
        return value;
    }
}
