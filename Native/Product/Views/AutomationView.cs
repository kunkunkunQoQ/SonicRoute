using SonicRoute.Core;
using SonicRoute.Core.Models;
using SonicRoute;

namespace SonicRoute.Native.Product;

internal sealed partial class ProductViews
{
    private string? automationSelectedId;
    private int automationStep = -1;
    private string ruleSearch = "";

    private void BuildAutomation()
    {
        Add(Label("搜索规则", "Find rules"), ruleSearch, () =>
        {
            if (NativeDialogs.Input(Owner, Label("搜索规则", "Find rules"), ruleSearch) is { } filter)
            { ruleSearch = filter; Refresh(); }
        });
        Add("新建规则", "创建一条未绑定快捷键的规则。", () =>
        {
            var name = NativeDialogs.Input(Owner, "新建自动化规则", "新规则");
            if (string.IsNullOrWhiteSpace(name)) return;
            var rule = new AutoRule { Name = name.Trim(), Enabled = false };
            rule.SetSteps([new AutoRuleStep()]);
            AutoRuleStore.Save(rule);
            automationSelectedId = rule.Id;
            Refresh();
        });

        Add(Label("从模板新建", "New from template"), click: () => Menu(AutoRuleTemplates.All
            .Select<AutoRuleTemplate, (string, Action?)>(template => (T(template.NameKey), () =>
            {
                var rule = template.CreateDraft(T(template.NameKey));
                if (!AutoRuleStore.Save(rule)) { Notice(T("Tray.SaveFailed")); return; }
                automationSelectedId = rule.Id;
                automationStep = -1;
                Refresh();
            })).ToArray()));
        var order = config.AutomationRuleOrder;
        var rules = AutoRuleStore.LoadAll()
            .OrderBy(rule => order.IndexOf(rule.Id) is int index && index >= 0 ? index : int.MaxValue).ToArray();
        if (rules.Length == 0) Add("还没有自动化规则", "选择“新建规则”开始。");
        foreach (var rule in rules.Where(rule => rule.Name.Contains(ruleSearch, StringComparison.CurrentCultureIgnoreCase)))
        {
            bool selected = rule.Id == automationSelectedId;
            Add($"{(rule.Enabled ? "●" : "○")}  {rule.Name}",
                $"{AutomationTriggerName(rule.Trigger)} · {rule.CloneSteps().Count} 个操作 · {(AutoRuleService.IsRunning(rule.Id) ? "运行中" : "空闲")}",
                () => { automationSelectedId = rule.Id; automationStep = -1; Refresh(); }, selected: selected);
        }

        var current = rules.FirstOrDefault(r => r.Id == automationSelectedId);
        if (current == null) return;
        var edit = current.Clone();
        Header(edit.Name);
        Add(Label("固定到托盘", "Pin to tray"), click: () => ToggleAppList(config.FavoriteRuleIds, edit.Id),
            selected: config.FavoriteRuleIds.Contains(edit.Id));
        int ruleIndex = Array.FindIndex(rules, rule => rule.Id == edit.Id);
        void MoveRule(int delta)
        {
            var ids = rules.Select(rule => rule.Id).ToList();
            (ids[ruleIndex], ids[ruleIndex + delta]) = (ids[ruleIndex + delta], ids[ruleIndex]);
            config.AutomationRuleOrder = ids;
            Persist();
        }
        if (ruleIndex > 0) Add(Label("规则上移", "Move rule up"), click: () => MoveRule(-1));
        if (ruleIndex + 1 < rules.Length) Add(Label("规则下移", "Move rule down"), click: () => MoveRule(1));
        if (AutoRuleService.IsRunning(edit.Id))
            Add(Label("取消规则", "Cancel rule"), click: () => AutoRuleService.CancelRule(edit.Id));
        else Add(Label("立即运行", "Run now"), click: () => Run(async () =>
        {
            var result = await AutoRuleService.ExecuteWithResultAsync(edit, "manual").ConfigureAwait(false);
            host.TryPost(() => Notice(AutoRuleService.DescribeResult(result)));
        }));
        Add(edit.Enabled ? "停用自动触发" : "启用自动触发", "手动运行不受此开关影响。", () =>
        {
            edit.Enabled = !edit.Enabled;
            AutomationSave(edit);
        }, selected: edit.Enabled);
        Add("重命名", "修改规则显示名称。", () =>
        {
            var value = NativeDialogs.Input(Owner, "规则名称", edit.Name);
            if (!string.IsNullOrWhiteSpace(value)) { edit.Name = value.Trim(); AutomationSave(edit); }
        });
        Add("触发方式：" + AutomationTriggerName(edit.Trigger), "点击循环切换：快捷键、应用启动、应用切换、应用退出、定时、SonicRoute 启动。", () =>
        {
            edit.Trigger = (AutoRuleTrigger)(((int)edit.Trigger + 1) % 6);
            AutomationSave(edit);
        });
        if (edit.Trigger == AutoRuleTrigger.Hotkey)
            Add("快捷键：" + (string.IsNullOrWhiteSpace(edit.Hotkey) ? "未设置" : edit.Hotkey), "录制规则触发快捷键。", () =>
            {
                string? value;
                host.HoldHotkeys(true);
                try { value = NativeDialogs.RecordHotkey(Owner, Label("录制规则快捷键", "Record rule shortcut"), edit.Hotkey); }
                finally { host.HoldHotkeys(false); }
                if (value != null) { edit.Hotkey = value; AutomationSave(edit); }
            });
        if (edit.Trigger is AutoRuleTrigger.AppStart or AutoRuleTrigger.AppSwitch or AutoRuleTrigger.AppExit)
            Add("目标进程：" + (string.IsNullOrWhiteSpace(edit.TriggerApp) ? "任意应用" : edit.TriggerApp), "留空时匹配任意应用；填写进程名，不含 .exe。", () =>
            {
                var value = NativeDialogs.Input(Owner, "触发目标进程名", edit.TriggerApp);
                if (value != null) { edit.TriggerApp = value.Trim(); AutomationSave(edit); }
            });
        if (edit.Trigger == AutoRuleTrigger.Schedule)
        {
            Add("计划模式：" + new[] { "仅一次", "每天", "每周" }[Math.Clamp(edit.ScheduleMode, 0, 2)], "点击循环切换计划模式。", () =>
            {
                edit.ScheduleMode = (edit.ScheduleMode + 1) % 3;
                AutomationSave(edit);
            });
            Add("执行时间：" + (string.IsNullOrWhiteSpace(edit.ScheduleTime) ? "未设置" : edit.ScheduleTime), "使用 24 小时制 HH:mm。", () =>
            {
                var value = NativeDialogs.Input(Owner, "计划执行时间 (HH:mm)", edit.ScheduleTime);
                if (value != null && TimeOnly.TryParse(value, out _)) { edit.ScheduleTime = value.Trim(); AutomationSave(edit); }
                else if (value != null) Notice("时间格式无效，请输入 HH:mm。");
            });
            if (edit.ScheduleMode == 2)
                Add("每周日期：" + string.Join(", ", edit.ScheduleWeekdays.Select(d => ((DayOfWeek)d).ToString())), "输入星期数字，0=周日，6=周六；逗号分隔。", () =>
                {
                    var value = NativeDialogs.Input(Owner, "星期数字 (0-6，逗号分隔)", string.Join(",", edit.ScheduleWeekdays));
                    if (value == null) return;
                    var days = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (days.Length > 0 && days.All(d => int.TryParse(d, out var n) && n is >= 0 and <= 6))
                    { edit.ScheduleWeekdays = days.Select(int.Parse).Distinct().Order().ToList(); AutomationSave(edit); }
                    else Notice("请输入 0 到 6 之间的星期数字。");
                });
        }

        var steps = edit.CloneSteps();
        Add("添加操作", "新操作追加到执行顺序末尾。", () => { steps.Add(new AutoRuleStep()); edit.SetSteps(steps); AutomationSave(edit); });
        for (int i = 0; i < steps.Count; i++)
        {
            int index = i;
            var step = steps[i];
            Add($"操作 {i + 1}：{AutomationActionName(step.Action)}",
                $"延时 {step.DelayMs} ms · {(step.StopOnFailure ? "失败后停止" : "失败后继续")}",
                () => { automationStep = automationStep == index ? -1 : index; Refresh(); }, selected: automationStep == index);
            if (automationStep == index) AutomationEditStep(edit, index);
            Add($"操作 {i + 1} 上移", "调整规则中的执行顺序。", () =>
            {
                var list = edit.CloneSteps();
                if (index > 0) (list[index - 1], list[index]) = (list[index], list[index - 1]);
                edit.SetSteps(list); AutomationSave(edit);
            }, selected: i == 0);
            Add($"操作 {i + 1} 下移", "调整规则中的执行顺序。", () =>
            {
                var list = edit.CloneSteps();
                if (index + 1 < list.Count) (list[index], list[index + 1]) = (list[index + 1], list[index]);
                edit.SetSteps(list); AutomationSave(edit);
            }, selected: i == steps.Count - 1);
            Add($"删除操作 {i + 1}", "至少保留一个操作。", () =>
            {
                var list = edit.CloneSteps();
                if (list.Count > 1) list.RemoveAt(index);
                edit.SetSteps(list); AutomationSave(edit);
            });
        }

        Add("删除规则", "此操作会从规则目录永久删除该规则。", () =>
        {
            if (!NativeDialogs.Confirm(Owner, "删除规则", $"确定删除“{edit.Name}”？")) return;
            AutoRuleStore.Delete(edit.Id);
            automationSelectedId = null;
            Refresh();
        });

        if (config.ExperimentalMode && config.ExecutionHistoryEnabled)
            Add("查看最近执行记录", "读取现有执行历史存储。", () => Run(async () =>
            {
                var store = AutoRuleService.GetHistoryStore();
                var entries = store == null ? [] : await store.ReadRecentAsync();
                var text = string.Join(Environment.NewLine, entries.Take(30).Select(e =>
                    $"{e.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {e.RuleName}  {e.Status}  成功 {e.Succeeded} / 失败 {e.Failed}"));
                host.TryPost(() => { if (Owner != 0) Invoke(() => NativeDialogs.Input(Owner, Label("最近执行记录", "Recent executions"), text, readOnly: true, multiline: true)); });
            }));
    }

    private void AutomationEditStep(AutoRule rule, int index)
    {
        var steps = rule.CloneSteps();
        if ((uint)index >= (uint)steps.Count) return;
        var step = steps[index];
        Add("操作类型：" + AutomationActionName(step.Action), "点击循环选择 AutoRuleAction 枚举中的 39 种既有动作。", () =>
        {
            step.Action = (AutoRuleAction)(((int)step.Action + 1) % Enum.GetValues<AutoRuleAction>().Length);
            steps[index] = step; rule.SetSteps(steps); AutomationSave(rule);
        });
        Add("应用进程：" + (string.IsNullOrWhiteSpace(step.TargetApp) ? "未设置" : step.TargetApp), "供需要指定应用的动作使用。", () =>
        {
            var value = NativeDialogs.Input(Owner, "目标进程名", step.TargetApp);
            if (value != null) { step.TargetApp = value.Trim(); steps[index] = step; rule.SetSteps(steps); AutomationSave(rule); }
        });
        Add("设备 ID：" + (string.IsNullOrWhiteSpace(step.TargetDeviceId) ? "未设置" : step.TargetDeviceId), "供设备路由和设备音量动作使用。", () =>
        {
            var value = NativeDialogs.Input(Owner, "目标设备短 ID", step.TargetDeviceId);
            if (value != null) { step.TargetDeviceId = value.Trim(); steps[index] = step; rule.SetSteps(steps); AutomationSave(rule); }
        });
        Add("数值：" + step.Volume, "音量动作取 0 到 100；增量动作也可输入负数。", () =>
        {
            var value = NativeDialogs.Input(Owner, "数值", step.Volume.ToString());
            if (int.TryParse(value, out int parsed)) { step.Volume = Math.Clamp(parsed, 0, 100); steps[index] = step; rule.SetSteps(steps); AutomationSave(rule); }
        });
        Add("变化量：" + step.VolumeDelta, "增量音量动作取 -100 到 100。", () =>
        {
            var value = NativeDialogs.Input(Owner, "音量变化量", step.VolumeDelta.ToString());
            if (int.TryParse(value, out int parsed)) { step.VolumeDelta = Math.Clamp(parsed, -100, 100); steps[index] = step; rule.SetSteps(steps); AutomationSave(rule); }
        });
        Add("静音状态：" + (step.Muted ? "静音" : "取消静音"), "点击切换目标静音状态。", () =>
        { step.Muted = !step.Muted; steps[index] = step; rule.SetSteps(steps); AutomationSave(rule); }, selected: step.Muted);
        Add("步骤延时：" + step.DelayMs + " ms", "0 到 600000 毫秒。", () =>
        {
            var value = NativeDialogs.Input(Owner, "步骤延时 (ms)", step.DelayMs.ToString());
            if (int.TryParse(value, out int parsed)) { step.DelayMs = Math.Clamp(parsed, 0, 600000); steps[index] = step; rule.SetSteps(steps); AutomationSave(rule); }
        });
        Add("失败后停止：" + (step.StopOnFailure ? "是" : "否"), "点击切换失败策略。", () =>
        { step.StopOnFailure = !step.StopOnFailure; steps[index] = step; rule.SetSteps(steps); AutomationSave(rule); }, selected: step.StopOnFailure);
        if (step.Action == AutoRuleAction.ExecuteRule)
            Add("目标规则 ID", "规则改名后仍使用 ID 引用。", () =>
            {
                var options = AutoRuleStore.LoadAll().Where(r => r.Id != rule.Id).ToList();
                if (options.Count == 0) { Notice("没有可调用的其他规则。"); return; }
                string choices = string.Join(Environment.NewLine, options.Select(r => $"{r.Name}  [{r.Id}]"));
                var value = NativeDialogs.Input(Owner, "输入目标规则名称或 ID", step.TargetRuleId + Environment.NewLine + Environment.NewLine + choices);
                if (value == null) return;
                string selector = value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
                var chosen = options.FirstOrDefault(r => string.Equals(r.Id, selector, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(r.Name, selector, StringComparison.OrdinalIgnoreCase));
                if (chosen == null) { Notice("未找到该规则，请输入列表中的名称或 ID。"); return; }
                step.TargetRuleId = chosen.Id; steps[index] = step; rule.SetSteps(steps); AutomationSave(rule);
            });
        if (step.Action == AutoRuleAction.ShowOsd)
        {
            Add("OSD 标题", "编辑 OSD 主标题。", () => { var v = NativeDialogs.Input(Owner, "OSD 标题", step.OsdTitle); if (v != null) { step.OsdTitle = v; steps[index] = step; rule.SetSteps(steps); AutomationSave(rule); } });
            Add("OSD 内容", "编辑 OSD 副标题。", () => { var v = NativeDialogs.Input(Owner, "OSD 内容", step.OsdText); if (v != null) { step.OsdText = v; steps[index] = step; rule.SetSteps(steps); AutomationSave(rule); } });
        }
        if (step.Action is AutoRuleAction.LaunchProgram or AutoRuleAction.RunPowerShell)
            Add("程序 / 脚本路径", "可输入多行路径；执行前请确认文件来源。", () =>
            {
                var paths = step.Action == AutoRuleAction.LaunchProgram ? step.EffectiveLaunchItems().Select(i => i.Path) : step.ProgramPaths;
                var v = NativeDialogs.Input(Owner, "路径（每行一个）", string.Join(Environment.NewLine, paths), multiline: true);
                if (v != null)
                {
                    var list = v.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    if (step.Action == AutoRuleAction.LaunchProgram) step.LaunchItems = list.Select(p => new AutoLaunchItem { Path = p }).ToList(); else step.ProgramPaths = list;
                    steps[index] = step; rule.SetSteps(steps); AutomationSave(rule);
                }
            });
    }

    private void AutomationSave(AutoRule rule)
    {
        AutoRuleStore.Save(rule);
        Refresh();
    }

    private static string AutomationTriggerName(AutoRuleTrigger trigger) => trigger switch
    {
        AutoRuleTrigger.Hotkey => "快捷键", AutoRuleTrigger.AppStart => "应用启动", AutoRuleTrigger.AppSwitch => "应用切换",
        AutoRuleTrigger.AppExit => "应用退出", AutoRuleTrigger.Schedule => "定时", AutoRuleTrigger.Startup => "SonicRoute 启动", _ => trigger.ToString()
    };

    private static string AutomationActionName(AutoRuleAction action) => action.ToString();
}
