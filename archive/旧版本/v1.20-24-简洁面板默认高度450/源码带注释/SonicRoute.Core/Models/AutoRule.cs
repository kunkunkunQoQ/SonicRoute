using System;
using System.Collections.Generic;
using System.Linq;

namespace SonicRoute.Core.Models
{
    /// <summary>自动化规则触发方式。</summary>
    public enum AutoRuleTrigger
    {
        /// <summary>按下快捷键（全局）</summary>
        Hotkey = 0,

        /// <summary>打开某个应用时（进程启动）</summary>
        AppStart = 1,

        /// <summary>切换到某个应用时（前台窗口变化）</summary>
        AppSwitch = 2,

        /// <summary>关闭某个应用时（进程退出）</summary>
        AppExit = 3,

        /// <summary>定时（计划任务：仅一次 / 每天 / 每周，见 Schedule* 字段）</summary>
        Schedule = 4
    }

    /// <summary>自动化规则执行的操作。</summary>
    public enum AutoRuleAction
    {
        /// <summary>切换系统播放设备</summary>
        SetSystemOutput = 0,

        /// <summary>切换系统录音设备</summary>
        SetSystemInput = 1,

        /// <summary>调整系统音量（0~100）</summary>
        SetSystemVolume = 2,

        /// <summary>系统静音 / 取消静音（切换）</summary>
        ToggleSystemMute = 3,

        /// <summary>调整指定应用音量（0~100）</summary>
        SetAppVolume = 4,

        /// <summary>指定应用静音 / 取消静音（切换）</summary>
        ToggleAppMute = 5,

        /// <summary>切换指定应用的播放设备</summary>
        SetAppOutput = 6,

        /// <summary>切换指定应用的录音设备</summary>
        SetAppInput = 7,

        /// <summary>启动程序</summary>
        LaunchProgram = 8,

        /// <summary>运行 PowerShell 脚本</summary>
        RunPowerShell = 9,

        /// <summary>显示 OSD（自定义主标题 / 副标题）</summary>
        ShowOsd = 10
    }

    /// <summary>
    /// 启动程序动作中的一个启动项（v1.18 起，每个条目独立保存路径与打开方式）。
    /// </summary>
    public sealed class AutoLaunchItem
    {
        /// <summary>要启动的文件 / 程序路径。</summary>
        public string Path { get; set; } = "";

        /// <summary>
        /// 打开方式（用于打开该启动项的 EXE 完整路径）。
        /// 空 = 默认方式：.exe 直接启动，其他文件交给 Windows 当前文件关联程序；
        /// 非空 = 启动该 EXE，并把 <see cref="Path"/> 作为参数传入。
        /// </summary>
        public string OpenWith { get; set; } = "";

        /// <summary>
        /// 打开方式的显示名（仅用于界面展示，可由 EXE 动态获取；不参与执行逻辑）。
        /// 程序被移动或卸载时以 <see cref="OpenWith"/> 为准。
        /// </summary>
        public string OpenWithName { get; set; } = "";
    }

    /// <summary>
    /// 自动化规则中的一个操作步骤（一个规则可含多个步骤，按顺序执行）。
    /// </summary>
    public sealed class AutoRuleStep
    {
        /// <summary>执行的操作。</summary>
        public AutoRuleAction Action { get; set; } = AutoRuleAction.SetSystemOutput;

        /// <summary>应用控制的目标进程名（SetAppVolume / ToggleAppMute / SetAppOutput / SetAppInput）。</summary>
        public string TargetApp { get; set; } = "";

        /// <summary>目标设备短 ID（设备切换操作；空或系统默认虚拟 id = 跟随系统默认）。</summary>
        public string TargetDeviceId { get; set; } = "";

        /// <summary>音量（SetSystemVolume / SetAppVolume，0~100）。</summary>
        public int Volume { get; set; } = 50;

        /// <summary>执行该步骤前的启动延时（毫秒，0 = 立即执行）。</summary>
        public int DelayMs { get; set; }

        /// <summary>OSD 主标题（ShowOsd 操作）。</summary>
        public string OsdTitle { get; set; } = "";

        /// <summary>OSD 副标题（ShowOsd 操作）。</summary>
        public string OsdText { get; set; } = "";

        /// <summary>
        /// PowerShell 脚本路径 / 命令（RunPowerShell），支持多个。
        /// LaunchProgram 自 v1.18 起改用 <see cref="LaunchItems"/>；该字段在 LaunchProgram 中仅作为旧配置兼容来源
        /// （读取时由 <see cref="EffectiveLaunchItems"/> 转换，保存时同步写入路径镜像，便于旧版本降级读取）。
        /// </summary>
        public List<string> ProgramPaths { get; set; } = new();

        /// <summary>启动项列表（LaunchProgram，v1.18 起；每项独立保存路径与打开方式）。</summary>
        public List<AutoLaunchItem> LaunchItems { get; set; } = new();

        /// <summary>启动模式（LaunchProgram）：0 = 全部启动，1 = 随机启动一个。</summary>
        public int LaunchMode { get; set; }

        /// <summary>
        /// 取当前启动项（严格：只认 <see cref="LaunchItems"/>，不做旧配置回退）。
        /// 保存与校验用此方法：否则用户删光启动项后，<see cref="ProgramPaths"/> 镜像会把已删除的项"复活"。
        /// </summary>
        public List<AutoLaunchItem> CurrentLaunchItems()
        {
            return (LaunchItems ?? new List<AutoLaunchItem>())
                .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Path))
                .Select(i => new AutoLaunchItem
                {
                    Path = i.Path.Trim(),
                    OpenWith = (i.OpenWith ?? "").Trim(),
                    OpenWithName = (i.OpenWithName ?? "").Trim()
                })
                .ToList();
        }

        /// <summary>
        /// 取规范化后的启动项列表，统一供执行与编辑器载入使用。
        /// 兼容旧配置：<see cref="LaunchItems"/> 为空而 <see cref="ProgramPaths"/> 有值时，按路径逐个转换（打开方式为空 = 默认方式），
        /// 因此旧自动化规则的启动行为完全不变。
        /// </summary>
        public List<AutoLaunchItem> EffectiveLaunchItems()
        {
            var list = CurrentLaunchItems();
            if (list.Count == 0 && ProgramPaths != null)
            {
                list = ProgramPaths
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => new AutoLaunchItem { Path = p.Trim(), OpenWith = "" })
                    .ToList();
            }
            return list;
        }

        /// <summary>
        /// 复制一个步骤（编辑器载入规则时使用，避免直接改动存储中的对象）。
        /// ⚠️ 新增字段必须同步复制。v1.18 曾因漏拷 <see cref="LaunchItems"/> 与 <see cref="LaunchMode"/>，
        /// 导致「随机启动一个」与各启动项的打开方式在编辑器载入后被重置、保存后从脚本文件中消失。
        /// </summary>
        public AutoRuleStep Clone() => new()
        {
            Action = Action,
            TargetApp = TargetApp ?? "",
            TargetDeviceId = TargetDeviceId ?? "",
            Volume = Volume,
            DelayMs = DelayMs,
            OsdTitle = OsdTitle ?? "",
            OsdText = OsdText ?? "",
            ProgramPaths = new List<string>(ProgramPaths ?? new List<string>()),
            LaunchItems = Action == AutoRuleAction.LaunchProgram
                ? EffectiveLaunchItems()
                : new List<AutoLaunchItem>(),
            LaunchMode = LaunchMode
        };

        /// <summary>
        /// 生成用于持久化的步骤副本（保存规则时使用）。
        /// - 启动程序：写入 <see cref="LaunchItems"/> 与 <see cref="LaunchMode"/>（新配置的唯一真实数据源），
        ///   并同步写入 <see cref="ProgramPaths"/> 路径镜像——镜像只用于旧版本降级读取，不会被反读回覆盖启动项。
        /// - 其他动作：ProgramPaths 原样保留（RunPowerShell 使用）。
        /// </summary>
        public AutoRuleStep ToPersisted()
        {
            bool isLaunch = Action == AutoRuleAction.LaunchProgram;
            var items = isLaunch ? CurrentLaunchItems() : new List<AutoLaunchItem>();
            return new AutoRuleStep
            {
                Action = Action,
                TargetApp = TargetApp ?? "",
                TargetDeviceId = TargetDeviceId ?? "",
                Volume = Volume,
                DelayMs = DelayMs,
                OsdTitle = OsdTitle ?? "",
                OsdText = OsdText ?? "",
                LaunchItems = items,
                LaunchMode = isLaunch && LaunchMode == 1 ? 1 : 0,
                ProgramPaths = isLaunch
                    ? items.Select(i => i.Path).ToList()
                    : new List<string>((ProgramPaths ?? new List<string>()).Where(p => !string.IsNullOrWhiteSpace(p)))
            };
        }
    }

    /// <summary>
    /// 自动化规则（极简）：触发条件 + 一个或多个操作步骤。
    /// 持久化于 %LocalAppData%\SonicRoute\Automation\{Id}.json（独立目录，v1.17 起；见 AutoRuleStore）。
    /// 应用/设备均按进程名 / 设备短 ID 记忆（不记 PID）。
    /// </summary>
    public sealed class AutoRule
    {
        /// <summary>规则唯一 ID（快捷键注册键 Rule:{Id}）。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>规则名称。</summary>
        public string Name { get; set; } = "";

        /// <summary>启用状态（停用规则不注册快捷键、不参与触发监听、不执行）。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>触发方式。</summary>
        public AutoRuleTrigger Trigger { get; set; } = AutoRuleTrigger.Hotkey;

        /// <summary>快捷键组合（Hotkey 触发；空 = 未绑定）。</summary>
        public string Hotkey { get; set; } = "";

        /// <summary>触发目标应用进程名（AppStart / AppSwitch / AppExit 触发；空 = 任意应用）。</summary>
        public string TriggerApp { get; set; } = "";

        /// <summary>定时计划模式（Schedule 触发）：0=仅一次 1=每天 2=每周。</summary>
        public int ScheduleMode { get; set; }

        /// <summary>执行时间（HH:mm，三种模式共用）。</summary>
        public string ScheduleTime { get; set; } = "";

        /// <summary>每周：执行星期（DayOfWeek 数值 0-6，可多选）。</summary>
        public List<int> ScheduleWeekdays { get; set; } = new();

        /// <summary>最近一次已执行的调度时刻（yyyy-MM-dd HH:mm），用于防止同一时刻重复执行。</summary>
        public string LastRunKey { get; set; } = "";

        /// <summary>执行的操作步骤（多操作，按顺序执行；为空时兼容旧配置，回退用 Action 等单操作字段）。</summary>
        public List<AutoRuleStep> Actions { get; set; } = new();

        /// <summary>执行的操作（旧单操作字段，兼容旧配置；新数据与 Actions[0] 同步）。</summary>
        public AutoRuleAction Action { get; set; } = AutoRuleAction.SetSystemOutput;

        /// <summary>应用控制的目标进程名（旧字段，兼容旧配置；对应 Actions[0].TargetApp）。</summary>
        public string TargetApp { get; set; } = "";

        /// <summary>目标设备短 ID（旧字段，兼容旧配置；对应 Actions[0].TargetDeviceId）。</summary>
        public string TargetDeviceId { get; set; } = "";

        /// <summary>音量（旧字段，兼容旧配置；对应 Actions[0].Volume）。</summary>
        public int Volume { get; set; } = 50;

        /// <summary>步骤启动延时毫秒（旧字段，兼容旧配置；对应 Actions[0].DelayMs）。</summary>
        public int DelayMs { get; set; }

        /// <summary>OSD 主标题（旧字段，兼容旧配置；对应 Actions[0].OsdTitle）。</summary>
        public string OsdTitle { get; set; } = "";

        /// <summary>OSD 副标题（旧字段，兼容旧配置；对应 Actions[0].OsdText）。</summary>
        public string OsdText { get; set; } = "";

        /// <summary>启动程序路径 / PowerShell 脚本（旧字段，兼容旧配置；对应 Actions[0].ProgramPaths 首项）。</summary>
        public string ProgramPath { get; set; } = "";
    }
}
