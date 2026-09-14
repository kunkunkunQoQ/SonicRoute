using System;
using System.Collections.Generic;

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
        AppSwitch = 2
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
        RunPowerShell = 9
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

        /// <summary>启动程序的路径（LaunchProgram）或 PowerShell 脚本路径 / 命令（RunPowerShell），支持多个。</summary>
        public List<string> ProgramPaths { get; set; } = new();
    }

    /// <summary>
    /// 自动化规则（极简）：触发条件 + 一个或多个操作步骤。
    /// 持久化于 %LocalAppData%\SonicRoute\config.json（AppConfig.AutoRules）。
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

        /// <summary>触发目标应用进程名（AppStart / AppSwitch 触发；空 = 任意应用）。</summary>
        public string TriggerApp { get; set; } = "";

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

        /// <summary>启动程序路径 / PowerShell 脚本（旧字段，兼容旧配置；对应 Actions[0].ProgramPaths 首项）。</summary>
        public string ProgramPath { get; set; } = "";
    }
}
