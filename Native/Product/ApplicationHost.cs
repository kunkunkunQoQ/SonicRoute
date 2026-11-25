using System.Runtime.CompilerServices;
using System.Text.Json;
using SonicRoute.Core;
using SonicRoute.Core.Models;
using SonicRoute.Updates;

namespace SonicRoute.Native.Product;

internal sealed partial class ApplicationHost : IDisposable, IMainThread
{
    private readonly CandidateEnvironment _environment;
    private readonly NativeApplicationWindow _window;
    private readonly ProductInstance _instance;
    private readonly FeedbackState _feedback = new();
    private readonly UpdateService _updates;
    private readonly AppConfig _config;
    private long _events, _generation;
    private string _lastSignal = "";
    private bool _disposed;
    private int _handoff;

    internal ApplicationHost(CandidateEnvironment environment, NativeApplicationWindow window, ProductInstance instance)
    {
        _environment = environment;
        _window = window;
        _instance = instance;
        _updates = ProductProtocol.CreateUpdates(environment, (action, token) => OnOwner(() =>
        {
            token.ThrowIfCancellationRequested();
            action();
            return true;
        }));
        _config = ConfigService.Load();
        if (string.IsNullOrWhiteSpace(_config.Language))
        {
            _config.Language = HostStartup.DetectSystemLanguage();
            ConfigService.Save(_config);
        }
        L10n.Instance.SetLanguage(_config.Language);
        if (ConfigService.LoadWarning is { } warning)
            _feedback.Publish(FeedbackKind.SettingsWarning, "Config.Load", warning.PreservedPath ?? ConfigService.ConfigPath);
        _window.CallbackError += HandleError;
        _window.Signal += OnSignal;
    }

    public bool TryPost(Action work) => _window.TryPost(work);
    public IDisposable ScheduleOnce(TimeSpan delay, Action work) => _window.ScheduleOnce(delay, work);

    internal int Run(string[] args, string? selector)
    {
        if (selector == null)
        {
            _window.SetInterest(ShellInterest.Power | ShellInterest.Session | ShellInterest.Theme | ShellInterest.Time | ShellInterest.Device);
            _ = _updates.StartAsync(Task.CompletedTask);
            switch (HostStartup.InitialView(_config, args))
            {
                case StartupView.Main: _feedback.Publish(FeedbackKind.OpenMain); break;
                case StartupView.QuickPanel: _feedback.Publish(FeedbackKind.TogglePanel); break;
            }
        }
        StartServices(selector == null);
        _instance.Listen(ExecuteRuleAsync, ExecuteControlAsync);
        if (!_window.TryPost(() =>
        {
            _ = ProductProtocol.ConfirmUpdatedStartupAsync(() => _window.RequestExit());
            if (selector != null) _ = RunTemporaryAsync(selector);
        })) throw new InvalidOperationException("Startup dispatch failed.");
        return _window.Run();
    }

    private async Task RunTemporaryAsync(string selector)
    {
        try { _window.RequestExit(Program.WriteRule(await ExecuteRuleAsync(selector))); }
        catch (Exception error) { HandleError(error); _window.RequestExit(8); }
    }

    private void OnSignal(ShellSignal signal)
    {
        ++_events;
        _lastSignal = signal.Kind.ToString();
        switch (signal.Kind)
        {
            case ShellSignalKind.Activated:
            case ShellSignalKind.MainWindow: _feedback.Publish(FeedbackKind.OpenMain); break;
            case ShellSignalKind.QuickPanel: _feedback.Publish(FeedbackKind.TogglePanel); break;
            case ShellSignalKind.TrayMenu: _feedback.Publish(FeedbackKind.TrayMenu); break;
        }
        BusinessSignal(signal);
    }

    private static void HandleError(Exception error) => ExceptionDiagnostics.Record("NativeHost.Callback", error);

    private Task<T> OnOwner<T>(Func<T> action)
    {
        var reply = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_window.TryPost(() =>
        {
            try { reply.TrySetResult(action()); }
            catch (Exception e) { reply.TrySetException(e); }
        })) reply.TrySetException(new InvalidOperationException("Host is busy or closing."));
        return reply.Task;
    }

    private HostStatus Status() => new(Environment.ProcessId, _environment.Edition, _environment.IsPackaged,
        !RuntimeFeature.IsDynamicCodeSupported, JsonSerializer.IsReflectionEnabledByDefault, _environment.DataDirectory,
        _window.Handle, _window.TrayRegistered, _window.MessageCount, _window.TrayRegistrationCount, _window.DispatchCount, _window.DeadlineCount, _window.TimerCount, _window.HotkeyCount, _events, _lastSignal, _config.Language, _feedback.Last,
        GC.GetTotalAllocatedBytes(true), GC.GetTotalMemory(false), GC.GetGCMemoryInfo().HeapSizeBytes,
        GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), _generation);

    private async Task<ControlReply> ExecuteControlAsync(string frame)
    {
        try
        {
            var request = JsonSerializer.Deserialize(frame, HostJson.Default.ControlRequest);
            if (request?.Args == null || request.Verb == null) return new("error: invalid_arguments");
            if (request.Verb is "exit" or "restart" or "install" && ProductProtocol.TransactionActive())
                return new("error: update_in_progress");
            switch (request.Verb)
            {
                case "status" when request.Args.Length == 0:
                    return new(JsonSerializer.Serialize(await OnOwner(Status), HostJson.Default.HostStatus));
                case "language" when request.Args is [var language]:
                    return new(await OnOwner(() =>
                    {
                        if (!L10n.SupportedLanguages.Any(x => string.Equals(x.Code, language, StringComparison.OrdinalIgnoreCase)))
                            return "error: unknown_language";
                        string previous = _config.Language;
                        _config.Language = language;
                        if (!ConfigService.TrySave(_config)) { _config.Language = previous; return "error: save_failed"; }
                        L10n.Instance.SetLanguage(language);
                        ++_generation;
                        return "ok";
                    }));
                case "activate" when request.Args.Length == 0:
                    await OnOwner(() => { _feedback.Publish(FeedbackKind.OpenMain); return true; });
                    return new("ok");
                case "exit" when request.Args.Length == 0:
                    return await HandoffAsync(() => Task.FromResult(true));
                case "restart" when request.Args.Length == 0:
                    return await HandoffAsync(ProductProtocol.RestartAsync);
                case "install" when request.Args is [var manifest]:
                    return await HandoffAsync(() => ProductProtocol.InstallCachedAsync(_environment, manifest));
                default: return await BusinessCommandAsync(request);
            }
        }
        catch (Exception e) { HandleError(e); return new("error: " + e.Message); }
    }

    private async Task<ControlReply> HandoffAsync(Func<Task<bool>> prepare)
    {
        if (Interlocked.CompareExchange(ref _handoff, 1, 0) != 0) return new("error: handoff_in_progress");
        bool ready = false;
        try
        {
            ready = await prepare();
            return ready ? new("ok", () => _window.RequestExit()) : new("error: handoff_failed");
        }
        finally { if (!ready) Volatile.Write(ref _handoff, 0); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Signal -= OnSignal;
        _window.CallbackError -= HandleError;
        StopServices();
        _updates.Dispose();
        AutoRuleStore.Shutdown();
        ConfigService.FlushPendingSaves();
    }
}
