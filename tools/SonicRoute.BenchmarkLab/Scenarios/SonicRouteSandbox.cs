using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SonicRoute.BenchmarkLab.Core;

namespace SonicRoute.BenchmarkLab.Scenarios;

public sealed class SonicRouteSandbox : IDisposable
{
    public string SandboxPath { get; }
    public string InstanceId { get; } = "Bench_" + Guid.NewGuid().ToString("N");
    public Dictionary<string, string> EnvironmentVariables { get; } = new();
    public bool ReadinessVerified { get; private set; }
    public string MissingTarget => "SRBenchMissing_" + InstanceId;
    private readonly Dictionary<int, string> _ruleIds = new();
    private readonly OwnedTestDirectory _ownedDirectory;
    private bool _disposed;
    private int _revision;
    private string _lastRuleName = "";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    public SonicRouteSandbox(string? customBasePath = null)
    {
        _ownedDirectory = new OwnedTestDirectory("Bench", customBasePath, InstanceId);
        SandboxPath = _ownedDirectory.DirectoryPath;
        Directory.CreateDirectory(Path.Combine(SandboxPath, "Automation"));
        EnvironmentVariables["SONICROUTE_DATA_DIRECTORY"] = SandboxPath;
        EnvironmentVariables["SONICROUTE_INSTANCE_ID"] = InstanceId;
        WriteConfig(new { Language = "en-US", AutoStart = false, AutoStartStore = false,
            StartMinimized = true, StartPanelOnStart = false, AutoCheckUpdates = false,
            ThemeMode = "light", Accent = "blue", ExperimentalMode = true,
            ExecutionHistoryEnabled = true, BackgroundPollingFallbackEnabled = false, TrayWheelPaused = true,
            Hotkeys = new Dictionary<string, string>() });
    }

    public void WriteConfig(object configObject) => File.WriteAllText(Path.Combine(SandboxPath, "config.json"), JsonSerializer.Serialize(configObject, Json));

    public void PopulateRules(int count, string triggerAppName = "SRBenchAbsentTarget", int triggerType = 1, int delayMs = 0, string? hotkey = null)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        string directory = Path.Combine(SandboxPath, "Automation");
        foreach (int key in _ruleIds.Keys.Where(i => i > count).ToArray())
        { File.Delete(Path.Combine(directory, $"Benchmark_Rule_{key:D4}.json")); _ruleIds.Remove(key); }
        _revision++;
        int firstKey = 9;
        if (hotkey != null && int.TryParse(hotkey[(hotkey.LastIndexOf('F') + 1)..], out var parsed) && parsed is >= 9 and <= 12) firstKey = parsed;
        for (int i = 1; i <= count; i++)
        {
            if (!_ruleIds.ContainsKey(i)) _ruleIds[i] = Guid.NewGuid().ToString("N");
            _lastRuleName = $"Benchmark Rule {i:D4} revision {_revision:D4}";
            var rule = new { Id = _ruleIds[i], Name = _lastRuleName, Enabled = true, Trigger = triggerType,
                TriggerApp = Path.GetFileNameWithoutExtension(triggerAppName),
                Hotkey = triggerType == 0 ? $"Ctrl+Alt+Shift+F{9 + (firstKey - 9 + i - 1) % 4}" : "",
                ScheduleMode = 1, ScheduleTime = DateTime.Now.AddMinutes(10 + i + _revision % 5).ToString("HH:mm", CultureInfo.InvariantCulture),
                Action = 4, TargetApp = MissingTarget, Volume = 50, DelayMs = delayMs,
                Actions = new[] { new { Action = 4, TargetApp = MissingTarget, Volume = 50, DelayMs = delayMs } } };
            string destination = Path.Combine(directory, $"Benchmark_Rule_{i:D4}.json");
            string temporary = destination + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(rule, Json));
            File.Move(temporary, destination, overwrite: true);
        }
    }

    private async Task<string?> RequestAsync(ProcessMonitor monitor, string selector, CancellationToken token)
    {
        string edition = FileVersionInfo.GetVersionInfo(monitor.Process.StartInfo.FileName).FileDescription?.Contains("Legacy", StringComparison.OrdinalIgnoreCase) == true ? "Legacy" : "Lite";
        using var identity = WindowsIdentity.GetCurrent();
        string name = $"SonicRoute.RuleCommand.{InstanceId}_{edition}.{Process.GetCurrentProcess().SessionId}.{identity.User!.Value}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid) || pid != monitor.ProcessId)
            throw new InvalidDataException("Readiness endpoint does not belong to the owned target process.");
        byte[] request = Encoding.UTF8.GetBytes(selector);
        await pipe.WriteAsync(BitConverter.GetBytes(request.Length), timeout.Token);
        await pipe.WriteAsync(request, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        byte[] prefix = new byte[4]; await pipe.ReadExactlyAsync(prefix, timeout.Token);
        int size = BitConverter.ToInt32(prefix);
        if (size < 1 || size > 16384) throw new InvalidDataException("Invalid readiness reply.");
        byte[] reply = new byte[size]; await pipe.ReadExactlyAsync(reply, timeout.Token);
        using var document = JsonDocument.Parse(reply);
        return document.RootElement.GetProperty("Code").GetString();
    }

    public async Task VerifyReadyAsync(ProcessMonitor monitor, CancellationToken token)
    {
        if (ReadinessVerified) return;
        try
        {
            if (await RequestAsync(monitor, "Benchmark_Missing_" + Guid.NewGuid().ToString("N"), token) != "rule_not_found")
                throw new InvalidDataException("Isolated endpoint did not confirm readiness.");
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException("Isolated SonicRoute command readiness was not confirmed. Automatic SonicRoute scenarios require v1.25+ isolation support; older clients or a mutex collision cannot be reported as a valid run.", error); }
        ReadinessVerified = true;
    }

    public async Task<bool> VerifyReloadAsync(ProcessMonitor monitor, CancellationToken token)
    {
        if (_lastRuleName.Length == 0) return false;
        // A deliberately absent target can only fail. Finding its latest rule name
        // proves reload, without changing any user's audio. Done after measurement.
        var code = await RequestAsync(monitor, _lastRuleName, token);
        return code == "step_failed";
    }

    public JsonElement[] ReadHistory()
    {
        string directory = Path.Combine(SandboxPath, "ExecutionHistory");
        if (!Directory.Exists(directory)) return Array.Empty<JsonElement>();
        var entries = new List<JsonElement>();
        // The product partitions history by command edition (Lite / Legacy).
        foreach (string file in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("RuleId", out var id) && _ruleIds.Values.Contains(id.GetString())) entries.Add(doc.RootElement.Clone());
                }
                catch (JsonException) { /* A concurrently written final line is never counted as execution. */ }
            }
        }
        return entries.ToArray();
    }

    public object CaptureEvidence() => new { ReadinessVerified,
        Rules = Directory.GetFiles(Path.Combine(SandboxPath, "Automation"), "*.json").Select(File.ReadAllText).ToArray(), History = ReadHistory() };

    public string CreateProbeExecutable()
    {
        string directory = Path.Combine(SandboxPath, "probe"); Directory.CreateDirectory(directory);
        foreach (string file in Directory.GetFiles(AppContext.BaseDirectory))
            if (new[] { ".exe", ".dll", ".json", ".config" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
        string probe = Path.Combine(directory, "SRB_" + InstanceId[6..] + ".exe");
        File.Move(Path.Combine(directory, "SonicRoute.BenchmarkLab.exe"), probe);
        return probe;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ownedDirectory.Dispose();
    }
}
