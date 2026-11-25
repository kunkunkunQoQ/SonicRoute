using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonicRoute
{
    internal static partial class RuleCommandLine
    {
        internal sealed class Result
        {
            public Result() { } // System.Text.Json 在两个 TFM 下均使用公共无参构造。
            public int ExitCode { get; set; }
            public string Code { get; set; } = "";
            public string RuleId { get; set; } = "";
            public string RuleName { get; set; } = "";
            public int Succeeded { get; set; }
            public int Failed { get; set; }
            public bool Stopped { get; set; }
            public int Skipped { get; set; }
        }

        internal static Result? ReadResult(string json) =>
#if NET10_0_OR_GREATER
            JsonSerializer.Deserialize(json, RuleCommandJson.Default.Result);
#else
            JsonSerializer.Deserialize<Result>(json);
#endif

        internal static string EncodeResult(Result value) =>
#if NET10_0_OR_GREATER
            JsonSerializer.Serialize(value, RuleCommandJson.Default.Result);
#else
            JsonSerializer.Serialize(value);
#endif
    }

#if NET10_0_OR_GREATER
    [JsonSerializable(typeof(RuleCommandLine.Result))]
    internal partial class RuleCommandJson : JsonSerializerContext { }
#endif
}
