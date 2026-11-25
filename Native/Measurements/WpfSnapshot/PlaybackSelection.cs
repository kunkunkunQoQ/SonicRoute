using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;

// Measurement adapter loaded only into the frozen WPF release. It invokes the original
// production SetAsync and verifies the target PID belongs to our private PCM source.
internal static class PlaybackSelection
{
    private static int _used;
    internal static bool Begin()
    {
        string directory = Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")!;
        string requestPath = Path.Combine(directory, "measure-route.json");
        if (!File.Exists(requestPath) || Interlocked.Exchange(ref _used, 1) != 0) return false;
        _ = Task.Run(async () =>
        {
            string result;
            try
            {
                using var request = JsonDocument.Parse(File.ReadAllBytes(requestPath));
                int pid = request.RootElement.GetProperty("Pid").GetInt32();
                string[] outputs = request.RootElement.GetProperty("Outputs").EnumerateArray().Select(e => e.GetString()!).ToArray();
                if (outputs.Length != 3 || outputs[0] != "@@SYSTEM_DEFAULT@@") throw new InvalidDataException("Unexpected benchmark selection.");
                using var source = Process.GetProcessById(pid);
                if (!string.Equals(source.MainModule!.FileName, Path.Combine(directory, "DisposablePcmEmitter.exe"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unexpected benchmark source process.");
                var app = System.Windows.Application.Current.GetType().Assembly;
                const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
                var set = app.GetType("SonicRoute.AppPlaybackOutputs", true)!.GetMethod("SetAsync", flags)!;
                var modelType = set.GetParameters()[0].ParameterType;
                var model = Activator.CreateInstance(modelType)!;
                modelType.GetProperty("ProcessId")!.SetValue(model, (uint)pid);
                var clock = Stopwatch.StartNew();
                var work = (Task<(bool Success,int HResult,string Message)>)set.Invoke(null, [model, outputs, outputs[0]])!;
                var outcome = await work.ConfigureAwait(false);
                double elapsed = clock.Elapsed.TotalMilliseconds;
                if (!outcome.Success) throw new InvalidOperationException(outcome.Message);
                var extras = (string[])app.GetType("SonicRoute.LocalAppOutputMirrors",true)!.GetMethod("GetExtraOutputs",flags)!.Invoke(null,[pid])!;
                if (!extras.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(outputs.Skip(1)))
                    throw new InvalidOperationException("Original graph failed to attach both mirror renderers.");
                result = JsonSerializer.Serialize(new {ElapsedMs=elapsed, ExtraSinks=extras, Pid=pid, HResult=outcome.HResult});
            }
            catch(Exception error){result=JsonSerializer.Serialize(new {Error=error.ToString()});}
            string file = Path.Combine(directory,"route-result.json");
            File.WriteAllText(file+".tmp",result);
            File.Move(file+".tmp",file);
        });
        return true;
    }
}
