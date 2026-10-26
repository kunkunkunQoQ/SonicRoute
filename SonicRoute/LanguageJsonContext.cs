#if NET10_0_OR_GREATER
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SonicRoute
{
    [JsonSerializable(typeof(Dictionary<string, string>))]
    internal partial class LanguageJsonContext : JsonSerializerContext { }
}
#endif
