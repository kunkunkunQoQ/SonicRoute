#if NET10_0_OR_GREATER
using System.Collections.Generic;
using System.Text.Json.Serialization;
using SonicRoute.Core.Models;

namespace SonicRoute.Core
{
    // Generate only the existing persistence contracts. Both metadata (reads) and the
    // synchronous write fast path use the same options and cached type information.
    // Older targets retain their existing serializer; no reflection fallback is needed
    // for this closed set of config/rule types on NET10.
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(AppConfig))]
    [JsonSerializable(typeof(AutoRule))]
    [JsonSerializable(typeof(List<AutoRule>))]
    internal partial class PersistenceJsonContext : JsonSerializerContext
    {
    }
}
#endif
