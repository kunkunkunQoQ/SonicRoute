using SonicRoute.Core;

namespace SonicRoute.Native.Product;

internal sealed partial class ApplicationHost
{
    private void StartServices(bool resident) { }
    private void StopServices() { }
    private void BusinessSignal(ShellSignal signal) { }
    private Task<ControlReply> BusinessCommandAsync(ControlRequest request)
        => Task.FromResult(new ControlReply("error: unknown_command"));

    private Task<string> ExecuteRuleAsync(string selector) => Task.Run(() =>
    {
        AutoRuleStore.Invalidate();
        var rule = RuleCommandLine.SelectRule(selector, AutoRuleStore.LoadAll(), out var failure);
        return RuleCommandLine.EncodeResult(failure ?? new RuleCommandLine.Result
        {
            RuleId = rule!.Id, RuleName = rule.Name, ExitCode = 8, Code = "rule_engine_unavailable"
        });
    });
}
