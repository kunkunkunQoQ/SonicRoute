using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

[JsonSerializable(typeof(Endpoint[]))]
[JsonSerializable(typeof(AppSession[]))]
[JsonSerializable(typeof(VolumeState))]
internal partial class AudioJson : JsonSerializerContext { }

internal sealed partial class ProductAudio
{
    private static Flow ParseFlow(string value) => value switch
    {
        "render" => Flow.Render, "capture" => Flow.Capture, _ => throw new ArgumentException("Expected render or capture.")
    };
    private static int Pid(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0
        ? pid : throw new ArgumentException("Invalid PID.");
    private static Role ParseRole(string role) => role switch
    {
        "console" => Role.Console, "multimedia" => Role.Multimedia, "communications" => Role.Communications,
        _ => throw new ArgumentException("Invalid endpoint role.")
    };

    internal Task<ControlReply> ExecuteAsync(ControlRequest command)
    {
        if (command.Verb is "outputs-get" or "outputs-set" or "mirror-status" or "outputs-clear-saved")
            return _routing.ExecuteAsync(command);
        return Task.Run(() => new ControlReply(Execute(command)));
    }

    private string Execute(ControlRequest command)
    {
        string[] args = command.Args;
        switch (command.Verb)
        {
            case "devices" when args is [var flow]:
                return JsonSerializer.Serialize(Devices(ParseFlow(flow)), AudioJson.Default.EndpointArray);
            case "apps" when args.Length <= 1:
                int offset = args.Length == 0 ? 0 : int.Parse(args[0], CultureInfo.InvariantCulture);
                if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
                return JsonSerializer.Serialize(Apps().Skip(offset).Take(40).Select(a =>
                    new AppSession(a.ProcessId, a.ProcessName, a.Label, a.HasActiveSession)).ToArray(), AudioJson.Default.AppSessionArray);
            case "default" when args is [var defaultFlow, var role]:
                return DefaultId(ParseFlow(defaultFlow), ParseRole(role));
            case "default-set" when args is [var setFlow, var id]:
                SetDefault(ParseFlow(setFlow), id);
                return "ok";
            case "route" when args is [var pid, var routeFlow]:
                return ReadRoute(Pid(pid), ParseFlow(routeFlow)) ?? "default";
            case "route" when args is [var idText, "capture", var destination]:
                SetRoute(Pid(idText), Flow.Capture, destination == "default" ? null : destination);
                return "ok";
            case "volume" when args.Length is 2 or 4:
                var direction = ParseFlow(args[0]);
                string target = args[1];
                int process = int.TryParse(target, out int number) ? Pid(target) : 0;
                string? endpoint = process == 0 && target != "default" ? target : null;
                int? level = null; bool? muted = null;
                if (args.Length == 4)
                {
                    if (args[2] == "level" && int.TryParse(args[3], out int value) && value is >= 0 and <= 100) level = value;
                    else if (args[2] == "mute" && bool.TryParse(args[3], out bool flag)) muted = flag;
                    else throw new ArgumentException("Expected level 0..100 or mute true/false.");
                }
                return JsonSerializer.Serialize(Volume(direction, endpoint, process, level, muted), AudioJson.Default.VolumeState);
            case "observe" when args is [var f, var subject]:
                BeginObservation(ParseFlow(f), subject);
                return "ok";
            case "notifications" when args.Length == 0:
                return Volatile.Read(ref _notifications).ToString(CultureInfo.InvariantCulture);
            case "unobserve" when args.Length == 0: StopObservation(); return "ok";
            default: return "error: unknown_command";
        }
    }
}
