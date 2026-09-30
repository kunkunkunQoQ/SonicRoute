# SonicRoute（音跃）— AI 开发助手项目摘要

> 本文档面向**后续接手的 AI 开发助手**，基于当前实际源码编写（2026-10-01 更新，工作区 v1.20）。
> 规则：不确定处标注「待确认」；计划中的功能不写成已实现；历史版本状态不代表当前状态。
> **维护义务（v1.18 起写进开发规范，强制）**：后续任何改动（新增 / 删除 / 回档等）都必须同步本文档——功能增删改名、关键实现、配置字段、快捷键、架构调整（含回滚）、已知问题、版本号、发布形态变化均须更新对应章节；被删除或回档的功能在「历史问题」中记录结论，**禁止直接从文档抹除**，避免新 AI 误判为已实现。本文档不推送 GitHub。

---

## 1. 项目介绍

- **用途**：Windows 按应用音频路由 / 音量管理工具（类似 EarTrumpet 的 Per-App Audio Routing，但为自行重新实现）。可单独控制每个应用音量、静音、输出/输入设备，切换系统默认设备，全局麦克风静音，托盘滚轮调音量，自定义快捷键，OSD 提示，多主题/多语言，极简自动化规则。
- **核心功能**：三档音频路由（当前应用 / 全局应用 / 系统默认）＋ 简洁/经典双快速面板 ＋ 完整管理界面 ＋ 12 项自定义快捷键 ＋ 全局麦克风静音 ＋ 自动化规则 ＋ 9 语言。
- **当前版本**：README 对应已发布版 **1.19**；**工作区已提升为 1.20（未发布）**。主程序 UI 从 `SonicRoute.csproj` 的 `<Version>` 读取版本，`SonicRoute.Core` 与 `SonicRoute.Legacy` 的 `<Version>` 及 Legacy 的 app.manifest 须同步。
- **发布形态**：绿色免安装（含运行环境版 + Lite 轻量版；**x64 与 ARM64 双架构各自独立单文件**）＋ 微软商店 Lite MSIX（自动更新，优先推荐；**当前 MSIX 清单 `ProcessorArchitecture` 仍固定 x64，见下方 ARM64 说明**）。
- **仓库**：GitHub `https://github.com/kunkunkunQoQ/SonicRoute`（master）；微软商店 `https://apps.microsoft.com/detail/9NQZGRTPM1NT`。

## 2. 技术栈

| 项 | 值 |
| --- | --- |
| 框架 | .NET 8（`net8.0-windows10.0.19041.0`，Nullable enable），WPF（UseWPF）＋ Windows Forms（仅托盘 NotifyIcon，UseWindowsForms） |
| **兼容版（v1.19 新增）** | **`SonicRoute.Legacy`（`net48` / x64 / Prefer32Bit=false）**：.NET Framework 4.8 兼容版，**链接共享** `SonicRoute\` 的同一批 .cs/.xaml/资源（**不复制源码**），只通过 ProjectReference 引用 Core 的 net48 目标；目标系统仅 Windows 10/11；**不参与 ARM64、不参与 Microsoft Store/MSIX、不做单文件发布**。详见「9. 开发注意事项」与主规范「net48 Legacy 版规范」 |
| 平台 | Windows 10/11，**x64 与 ARM64 原生双架构**（`PlatformTarget` 按 RID 推导：无 RID 的普通构建默认 x64；`-r win-x64` → x64、`-r win-arm64` → arm64，**不使用 AnyCPU 代替 ARM64**），WinExe；Release 启用 PublishReadyToRun；单文件/自包含由发布配置 `Properties/PublishProfiles/win-{x64,arm64}.pubxml` 承载（**RID 必须写在命令行**，见「发布纪律」） |
| 语言 | C#（**两个工程都必须显式 `<LangVersion>latest</LangVersion>`**：net48 的 SDK 默认 C# 7.3，与 `Nullable=enable` 冲突会直接 CS8630 编译失败），无第三方音频库（CoreAudio/WASAPI 全部手写 COM 互操作） |
| 配置 | `%LocalAppData%\SonicRoute\config.json`（System.Text.Json，缩进）；自动化规则独立存 `%LocalAppData%\SonicRoute\Automation\{规则名称}.json`（v1.18 起，同名加 (1)(2)…，见 AutoRuleStore）。**net8 与 net48 共用同一份配置格式**（Legacy 直接读写同一个 config.json，可互相切换） |
| 语言文件 | 嵌入资源 `SonicRoute\Resources\Lang\*.json`（9 种，UTF-8 BOM）；外置导入目录 `%LocalAppData%\SonicRoute\Lang`。Legacy 侧通过 `LogicalName` 显式指定为 `SonicRoute.Resources.Lang.<code>.json`，与 net8 版本名称完全一致 |

**项目结构（单进程三工程）**：

- `SonicRoute.sln` — 解决方案
- `SonicRoute\` — UI / 编排层（App、MainWindow、两个 QuickPanel、托盘、快捷键、OSD、内存回收、语言、主题）。**net8 与 net48 共用这一份源码**
- `SonicRoute.Core\` — 核心库（音频服务、设备/会话、配置、麦克风、互操作、模型、AutoRuleStore 自动化规则独立存储）。**双目标 `net8.0-windows;net48`**
- `SonicRoute.Legacy\` — **仅含 csproj + app.manifest + App.config**（无 .cs/.xaml 副本），net48 x64 兼容版入口
- `SonicRoute源码\` — 本地规范/备份/留档（gitignore，**不入库**）：`README.md`（主规范）、`改动记录.md`、`留档.txt`、`翻译规范.md`、各版本备份目录
- `SonicRoute.wiki\` — GitHub Wiki 独立仓库（本地镜像）
- `archive\` — GitHub 归档（旧版本、双进程废案）
- `docs\images\preview.gif` — README 预览动图

## 3. 核心架构

```
App.xaml.cs（启动/托盘/热键/OSD/面板入口/内存回收/单实例）
 ├─ TrayWheelService       托盘滚轮（WH_MOUSE_LL 钩子）+ OSD 全生命周期
 ├─ HotkeyService          全局快捷键（RegisterHotKey）＋ 热键注册表
 ├─ HotkeyActions          12 个动作常量/默认组合/格式化
 ├─ MouseHotkeyHook        鼠标按键/滚轮快捷键（低级钩子）
 ├─ CurrentAppService      当前应用解析 + 前台监听（统一各入口操作同一应用）
 ├─ QuickPanelWindow      经典快速面板（IQuickPanel）
 ├─ QuickPanelModernWindow 简洁快速面板（IQuickPanel）
 ├─ MainWindow            完整界面（概览/应用/设备/设置/实验/主题/快捷键/自动化）
 ├─ AutoRuleService        极简自动化规则引擎
 ├─ L10n / ThemeService    多语言 / 主题（mode×accent×透明度）
 ├─ AppIconService / IconFactory / AppItem  应用图标懒加载缓存
 └─ SonicRoute.Core：
     ├─ AudioService        设备枚举(3s TTL)/应用枚举(1s 缓存)/Per-App 路由/一键还原
     ├─ SessionVolumeService 按 PID 聚合会话：读组内第一个、写遍历全部；5s 新鲜度
     ├─ SystemVolumeService  系统主音量
     ├─ GlobalMicMuteService 全局麦克风静音（设备级 eCapture）+ 默认输入静音检测
     ├─ ForegroundAppService 前台窗口 PID
     ├─ ConfigService        配置加载/保存/导入导出
     ├─ OpenWithService      「打开方式」候选应用枚举（Shell SHAssocEnumHandlers，按扩展名缓存）
     ├─ AppDisplayName       应用显示名（优先用户自定义名）
     ├─ Interop\             手写 CoreAudio/WASAPI COM（见「关键实现」）
     └─ Compat\              net8 / net48 共享兼容层（见「关键实现 12」）
         ├─ WindowsVersion   RtlGetVersion 取真实 Windows Build（替代 Environment.OSVersion）
         ├─ MathEx           Math.Clamp 垫片（netstandard2.1+ 才有）
         ├─ CompatEnv        Environment.TickCount64 垫片
         ├─ AppInfo          Environment.ProcessPath / ProcessId 垫片
         └─ Net48CompilerShims  net48 下的 IsExternalInit / RequiredMemberAttribute 等（#if NET48，public）

SonicRoute.Legacy（net48 x64，仅 csproj + app.manifest + App.config）
 └─ 通过 <Compile/Page/ApplicationDefinition Include="..\SonicRoute\**" Link> 复用上面同一份 UI 源码
```

## 4. 功能清单

### 已完成（确认实现）
| 功能 | 核心位置 | 说明 |
| --- | --- | --- |
| 应用检测/列表 | `CurrentAppService.cs`、`SonicRoute.Core\AudioService.cs`（GetApps） | 前台 PID → 进程名 → 最近有音频前台 → 上次操作（UI 文案为「最近使用」） |
| 应用级音量/静音 | `SonicRoute.Core\SessionVolumeService.cs` | 按 PID 聚合全部 eRender/eCapture 会话 |
| 应用级输出/输入设备切换 | `AudioService.ApplyEndpoint` + `Interop\AudioPolicyConfig.cs` | WinRT AudioPolicyConfig 手写 vtable（Set=25/Get=26/ClearAll=27） |
| 设备枚举/筛选 | `AudioService.GetDevices`（3s TTL）、`MainWindow.BuildDeviceFilter` | 保留设备隐藏；下拉框始终显示全部设备 |
| 系统默认设备切换 | `AudioService` + `Interop\PolicyConfigClient.cs` | IPolicyConfig.SetDefaultEndpoint；`@@SYSTEM_DEFAULT@@` 虚拟项=跟随系统 |
| 一键还原全部应用设备 | `AudioService.ResetAllPersistedEndpoints` | ClearAll + 逐进程置 null + 注册表 PolicyStore 整树删除（覆盖已退出应用） |
| 托盘滚轮调音量 | `TrayWheelService.cs` | 设置「杂项」区块（v1.18 起，原「音量与托盘滚轮」卡片）开关「托盘区域滚轮调音量」（默认开，响应整个托盘通知区）；关则仅音跃图标矩形 |
| 全局快捷键 | `HotkeyService` + `HotkeyActions` | 12 动作（音量±/当前应用静音/麦克风静音当前应用/切当前应用快捷设备/切当前应用麦克风设备/切全局应用输出/切全局应用输入/还原全部应用默认设备/切系统默认输出/切系统默认输入/打开快速面板）；支持组合键+滚轮/鼠标侧键；Esc 取消绑定；快捷键设置页按音量/应用/全局/UI 四类分组 |
| 快捷面板（经典/简洁） | `QuickPanelWindow` / `QuickPanelModernWindow` | 设置切换（默认简洁 modern）；可拖拽定位（QuickPanelPosition.cs） |
| 简洁面板每应用实时电平（v1.20 工作区） | `SonicRoute.Core/AudioMeterService.cs` + `QuickPanelModernWindow` | Core 从各输出设备的应用会话 QI `IAudioMeterInformation`，按 PID 取多会话最大 Peak；单 MTA 线程约 30Hz 采样，2s 低频重扫，Attack 0.65/Release 0.15；目标只包含面板列出且未静音、滑块可用的应用（滚出屏幕仍采样）；目标集合只在变化时复制，电平不变不创建快照/投递 UI，回零帧保留；轨道宽度变化/Slider.ValueChanged 才更新滑块布局；UI 只更新 Slider 内 `PART_PeakVisual` 的 `ScaleTransform.ScaleX = Peak × Volume`，保留原 Thumb/音量和静音 RGB 反色；电平色 `Theme.AccentPeak` 在深色模式将强调色向白混合 82%，浅色模式向黑混合 68%，强调色亮度接近极值时反向混合 60% 保持区分，主题和自定义 RGB 即时更新；主题页 `ShowAppPeakMeter` 开关默认开，关闭或面板 Closed 后 Dispose 停止线程并释放 COM，关闭视觉与 1.19 一致。Lite x64 优先验证；Legacy 共享源码。 |
| 完整界面 | `MainWindow.xaml.cs` | 导航七项：概览/应用/快捷键/主题/设置/自动化 + 实验设置（实验模式开启后显示） |
| OSD | `TrayWheelService.cs`（App.ShowOsd 等接口） | 复用单窗口、固定宽度防跳动、淡入淡出、九宫格+自定义拖拽、缩放、麦克风静音常驻 |
| 全局麦克风静音 | `GlobalMicMuteService.cs` | 设备级静音；状态检测（事件+低频轮询）；OSD 常驻开关。设置页常驻「麦克风选项」（不依赖实验模式），开启后才显示概览/应用/设置的输入设备 UI 与「切换当前应用麦克风设备」快捷键 |
| 多语言 | `L10n.cs` | 9 语言；导入/导出/打开文件夹/还原默认/附加语言（自定义名）；切换即时生效 |
| 主题 | `ThemeService.cs` + `MainWindow` 设置页 | system/light/dark × 强调色（预设+RGB 自定义+预设增删）；背景透明度（默认 85，可到 0） |
| 自动化 | `AutoRuleService.cs` + `AutoRuleStore.cs` + `MainWindow` 自动化页 | 触发（快捷键/应用启动/应用切换）× 动作（系统/应用音量静音设备切换/启动程序/PowerShell/OSD）；多步骤带延时；拖拽路径；规则独立存 `Automation\{规则名称}.json`（v1.18 起，同名自动加 (1)(2)…；config.json 不再含 AutoRules）。**步骤编辑为积木式**（v1.18 工作区）：分类配色圆角积木（系统音频=蓝/应用=橙/高级=紫/OSD=青，随主题深浅切换色板）+ 内联参数区 + 底部延时行；**积木支持「⋮⋮」把手拖拽排序**（安全实时换位 + 滑动动画：拖动时积木逐格让位、150ms 缓动从旧位滑到新位；Items 重排经 Dispatcher.BeginInvoke 在消息循环内执行 + 防抖，避免事件内改集合崩溃，换位后恢复鼠标捕获；AutoDragHitSlot 按积木中线算槽位，AutoItemY 取容器视觉 Y）；音量滑块支持鼠标滚轮 ±5 调节（`AutoBlockPalette`/`BuildAutoStepRow`/`BuildStepParams`/`AutoStepGrip_*`）。**启动程序动作改造（v1.18 二次，启动项独立打开方式）**：原「一组程序路径」改为**启动项列表**——每项 = `AutoLaunchItem{Path, OpenWith, OpenWithName}`，**打开方式独立保存**；「＋ 添加文件/程序」= 打开文件选择器（**多选**、默认展示全部文件）→ 逐个创建启动项（`OpenWith` 空 = 默认程序），与「选择打开方式」是互不覆盖的两个操作（也保留拖拽追加）。打开方式下拉 = `默认程序` / **按该启动项扩展名向系统查询的关联应用**（`OpenWithService.GetHandlers`，数据源 = Shell `SHAssocEnumHandlers`，与资源管理器「打开方式」同源，**不写死任何程序名**；展开下拉时才枚举并按扩展名缓存，项显示图标+名称，复用 `AutoAppItemTemplate`）/ `选择其他应用…`（OpenFileDialog 浏览 EXE）；保存的是 **EXE 完整路径**（`OpenWith`），`OpenWithName` 仅作显示名；**打包（UWP/MSIX）处理程序无 EXE 路径，不作为可选打开方式**（该类场景用「默认程序」）。执行：`openWith` 空 = Windows 默认方式（.exe 直接启动、其他文件走文件关联），非空 = **启动该 EXE 并把目标路径作为参数**（`QQ音乐.exe "音乐.mp3"`）；**启动模式**（`AutoRuleStep.LaunchMode`：0 全部启动 / 1 随机启动一个，随机只挑启动项、不改该项打开方式）；旧配置兼容 = `EffectiveLaunchItems()`（LaunchItems 为空时由 ProgramPaths 逐条转换，openWith 空）+ 保存时同步写 ProgramPaths 路径镜像（旧版本降级读取仍可启动，仅丢打开方式）；PowerShell 动作路径区不变（仍用 ProgramPaths）。**自动化页顶部操作区（v1.19）**= Grid 两列（`*` + `Auto`）：左「＋ 新建规则」（PrimaryButton，**铺满至右侧按钮之前**，右留 8px 间距）+ 右「打开脚本文件夹」（**复用设置 → 语言页「打开语言文件夹」同款 GhostButton 样式与 ShellOpen.Folder 实现**，点击 → `AutoRuleStore.RulesDir` 不存在则先创建再在资源管理器中打开） |
| 应用名/设备名自定义 | `AppDisplayName.cs`、`ConfigService.DeviceNames/AppNames` | 通知/OSD/列表统一生效 |
| 内存回收 | `App.xaml.cs`（GcNow/TrimWorkingSet/idle 回收） | 主窗口或快速面板关闭且无其他 UI 时，延迟 1.5s 等待 Dispatcher 空闲，仅执行一轮 GcNow 和工作集修剪；重新打开 UI 会取消待执行回收。另有 idle 定时回收（15s 首启、每 120s，仅无 UI 且最近 120s 未回收时执行）。当前设置页无「关闭 UI 释放内存」开关。 |
| 开机自启 | `MainWindow.SettingsAutoStart_Changed`、`CleanAutoStart_Click` | 正常版与商店版分开（AutoStart/AutoStartStore）；商店版不显示清理自启动项 |
| 实验设置 | `MainWindow` 实验页（`ExpResetAll`/`ExpClearConfig`/`ExpExportConfig`/`ExpImportConfig`/`ExpExportLang`/`ExpImportLang`/`ExpOpenLangDir`/`ExpRestoreLang`） | 解锁：设置页底部点「困困困」（作者名）5 次 → 持久化 ExperimentalUnlocked；开启实验模式后显示。内容：一键还原全部应用输出/输入默认设备、一键清理配置文件、导出/导入配置、语言导出导入/打开文件夹/还原默认。**折叠设备区块开关（`ExpCollapseCheck`）位于设置页「杂项」卡片**（v1.18 起，不依赖实验模式） |
| 托盘图标 | `IconFactory.cs` | 🎧 表情风格图标，深浅色跟随任务栏 |
| 检查更新 | `MainWindow.xaml.cs`（CheckUpdate_Click / CheckUpdateAsync / FinishCheck / ParseVersion） | 作者主页行「检查更新」入口，**仅点击触发（无自动检测/定时器）**；GitHub Releases API 比对版本（`releases/latest` tag_name，版本解析 vX.Y[.Z][rN]）；非商店版弹窗→跳 GitHub `releases/latest`，商店版（IsPackaged）弹窗→打开微软商店 `ms-windows-store://pdp/?ProductId=9NQZGRTPM1NT`；新增 9 语言 7 键（St.CheckUpdate 等） |
| 本地配置编辑器 | `E:\kunkun\SonarSwitch\config-editor\index.html`（单文件 HTML，非音跃主程序） | 纯前端本地配置工作台（v1.19 规则模型对齐版）：**配置页**=左侧分类导航（常规/快捷键/设备/应用/OSD 显示/实验/旧字段/其他字段，未知键自动归入「其他字段」；麦克风选项 ExperimentalMic、快速面板显示麦克风 MicInPanel、折叠设备区块 CollapseDeviceSections 按主程序归属「常规」，实验分类仅 ExperimentalUnlocked/ExperimentalMode；**QuickPanelHeight 已归入「常规」**，350–800）+ 配置项搜索（命中按分类分组定位）+ 右侧当前分类表单；字段按类型分发控件（布尔/数字/枚举/强调色/文本/标量字典/字符串数组/嵌套对象递归/复杂数组提示用 JSON；布尔开关修复：`.switch input` z-index:1 + `.tk` pointer-events:none，解决轨道盖住透明 input 导致开关点不动；**Accent 控件 = 预设 blue/green/purple（#2F80ED/#22C55E/#EC4899，对齐 `ThemeService.Apply`）+ 自定义 #RRGGBB**；Hotkeys 快捷键字典为**可编辑键名 + 可录制值 + 添加键 + 删除**（渲染时按主程序 `HotkeyActions.All` 预填全部 12 个动作，缺失值显示「点击绑定…」，空值导出后主程序回退默认键位；键名提示需与主程序动作名一致，随意改名主程序不识别））；**未知字段与原始类型一律保留**；JSON 源码模式为高级入口（解析失败不覆盖原配置、错误定位兼容 Chrome/Firefox）。**规则页**=左栏规则列表（启用圆点+名称+触发摘要）+ 右栏详情（触发 chip + 步骤纵向流程 + 编辑/复制/导出/删除）；编辑器为**纵向流程**（触发条件 → 步骤 1 → 步骤 2 → ＋添加步骤，操作 chip 菜单 11 种），每步上移/下移/复制/删除（拖拽手柄仅作排序补充）、低频参数折叠进高级设置（延时 0–60000 双层 clamp）。**规则数据模型与 `AutoRule.cs` 严格对齐**：步骤字段 `Action/TargetApp/TargetDeviceId/Volume/DelayMs/OsdTitle/OsdText/ProgramPaths/LaunchItems/LaunchMode`，规则字段 `Id/Name/Enabled/Trigger/Hotkey/TriggerApp/ScheduleMode/ScheduleTime/ScheduleWeekdays/Actions`（`LastRunKey` 原样保留）；步骤数组名是 **Actions**；触发 5 种（0 快捷键/1 打开应用/2 切换应用/3 应用退出/4 定时，定时含 仅一次·每天·每周 + HH:mm + 星期多选）；**启动程序动作支持启动项列表（每项独立 Path/OpenWith/OpenWithName）+ 启动模式（全部启动/随机启动一个）**，PowerShell 动作为 ProgramPaths 多路径；`normalizeStep/normalizeRule` 导入时自动迁移历史格式（App/Device/Paths/Script/OsdSubtitle/Steps/单操作 Action），`syncLegacy` 把 Actions[0] 同步回旧单操作字段，导出前 `persistRule` 等价主程序 `ToPersisted()`（重建 ProgramPaths 镜像、清空非当前触发字段）；导出单条 = `{规则名}.json`（可直接放入 `Automation\`），「导出全部」= 按规则名逐条下载（不再导出 rules.json 数组）。桌面双栏、窄屏单栏、深浅主题；可双击本地打开或部署 GitHub Pages。回滚基准 `_backup\index-v1.html`（重构前原版）、`_backup\index-v2.html`（v1.18 重构版 / 本次改动前） |

### 未完成 / 存在问题（明确标注）
| 项 | 状态 |
| --- | --- |
| MainWindow.xaml 完整 XAML | 未逐行审阅（158KB）；`MainWindow.xaml.cs` 已按方法清单审阅 |
| 内存占用 | 关闭完整 UI 后仍有部分场景回收不完全（1~4MB 级差异，历史问题，用户已暂不追究） |

## 5. 关键实现（写代码前必读）

1. **当前应用解析**（`CurrentAppService.Resolve`）：模式 `recent`（最近使用，自动跟随前台）→ `last`（上次操作）→ `fixed`（指定应用）；解析顺序：前台精确 PID → 前台进程名 → 最近有音频前台 → 上次操作 → 兜底跳过禁用列表。`StartForegroundWatcher` 1.5s 轮询，前台 PID 变化才后台枚举，避免高频 COM 枚举拖慢 UI。
2. **按 PID 聚合会话**（`SessionVolumeService`）：一个进程可能有多个 Audio Session，聚合后读组内第一个、写遍历全部；**5 秒新鲜度强制刷新**（流媒体 Session 随播放重建，复用旧会话会导致 SetMute/SetMasterVolume 静默失败——历史「音量/静音不生效」根因）。输入静音写后强制重枚举读真实状态。
3. **Per-App 设备路由**（`AudioPolicyConfig.cs`）：WinRT `Windows.Media.Internal.AudioPolicyConfig` 手写 vtable（HSTRING 手动创建，见 `ComInterop.cs` 说明）；`SetPersistedDefaultAudioEndpoint` 为 eMultimedia+eConsole 双 role 设置；`EnsureFullDeviceId`/`UnpackDeviceId` 处理短 ID。**系统默认虚拟项选中即清除持久化路由跟随系统默认**。
4. **一键还原**（`ResetAllPersistedEndpoints`）：`ClearAllPersistedApplicationDefaultEndpoints`（Win11 22H2+）＋ 逐进程 `SetDefaultEndpoint(null)` ＋ 注册表 `HKCU\...\LowRegistry\Audio\PolicyConfig\PropertyStore` 整树删除——覆盖「已退出但曾设置过」的应用。
5. **托盘滚轮**（`TrayWheelService`）：WH_MOUSE_LL 钩子；`TrayWheelEverywhere=true` 响应整个托盘通知区（含溢出区/第二任务栏），false 仅音跃图标矩形（`Shell_NotifyIconGetRect` 先调用再查矩形——Win11 返回 False 但矩形有效的历史坑，含 30s 成功矩形缓存回退 + Toolbar 按钮枚举回退）；钩子线程只检测合并滚轮量，操作派发 UI 线程。
6. **OSD**（App.ShowOsd / BeginOsdAdjust / CancelOsdAdjust / PreviewOsd / SetOsdSize / ApplyOsdSize）：懒创建后永久复用一个 Window（1 Border + 2 TextBlock）；固定宽度 `OsdWidth` 防文本变化跳动；`_osdPositionDirty` + `_osdRepositionPending` 脏标记防重复定位；默认 TR 用 `SystemParameters.WorkArea`（**WPF DIP，禁止再除 DPI**——历史错误来源）；Custom 模式直接用保存坐标；连续操作只 Stop/Start Timer + 改文本；淡入淡出 `OsdFadeInMs`(默认100)/`OsdFadeOutMs`(默认200)，0=禁用；麦克风静音常驻 = `persistent=true` + `_micMutePersistentActive` + 覆盖后恢复（`RestoreMicOverlayIfNeededAsync`）。
7. **麦克风静音**（`GlobalMicMuteService`）：默认 Capture 端点级静音；检测 = IMMDeviceEnumerator 事件监听为主 + 5~10s 低频轮询兜底；状态统一入口 `UpdateMicMuteState`（以 Windows 真实 GetMute 为准，不信任自己 SetMute 的结果）；OSD 常驻选项（默认关，开启后静音常驻、开启时普通 OSD 自动隐藏）。
8. **语言**（`L10n.cs`）：懒加载当前语言 + zh-CN 回退表；外置目录优先于内置；特殊键 `Lang.Code/Lang.NativeName/Lang.Custom`（带非空 Lang.Custom 为「附加语言」：直接入下拉框、不替换内置 9 语言、文件名撞内置用 `code#` 内部码）；导入原子写 `.json.tmp`→Move；导入/切换即时生效（重建缓存）。**新增功能必须补 9 语言 JSON**（翻译规范见 `SonicRoute源码\翻译规范.md`）。
9. **配置**（`ConfigService`）：单进程内存缓存、Save 同步更新缓存；ResetToDefault/ExportTo/ImportFrom；设置页另有导入配置文件（支持拖拽）。**ConfigService 字段默认值 = 旧配置兼容线**，新增字段必须给默认值。**自动化规则自 v1.18 起独立存储**：`%LocalAppData%\SonicRoute\Automation\{规则名称}.json`（AutoRuleStore 静态类，每规则一文件，文件名=规则名称，同名加 (1)(2)…；旧 {Id} 命名文件下次保存自动迁移；LoadAll 缓存+Save/Delete 失效）；ConfigService.Load 首次读盘前自动迁移旧 config.json 的 AutoRules；「一键清理配置 / 导出导入配置」不再联动自动化规则。
10. **内存回收**（`App.xaml.cs`）：`GcNow()`（两轮强制阻塞 GC + LOH CompactOnce + 等终结器）+ `TrimWorkingSet()`（SetProcessWorkingSetSize(-1,-1)）；主窗口或快速面板关闭且无其他 UI 时延迟 1.5s、等待 Dispatcher 空闲，执行一次 GcNow 与修剪，重新打开 UI 取消待回收；idle 定时回收首次 15s、之后每 120s，仅无 UI 且距上次回收至少 120s 才执行；`AppIconService` 256 上限 FIFO 缓存，仅最后一个 UI 关闭时 Clear()。当前设置页无「关闭 UI 释放内存」开关。
11. **自动化步骤持久化契约（v1.18 起，改自动化必读）**：四个取值/转换入口各司其职，不要另写映射——
   - `AutoRuleStep.Clone()`：**编辑器载入规则时唯一允许的克隆方式**（复制全部字段，含 LaunchItems/LaunchMode）；
   - `AutoRuleStep.ToPersisted()`：**保存规则时唯一的落盘映射**（启动程序写 `LaunchItems` + `LaunchMode`，并写 `ProgramPaths` 路径镜像；其他动作 ProgramPaths 原样保留）；
   - `CurrentLaunchItems()`：严格取值（只认 LaunchItems，不回退），用于保存与保存前校验；
   - `EffectiveLaunchItems()`：含旧配置回退（LaunchItems 空时按 ProgramPaths 转换），用于执行与编辑器载入。
   **新增步骤字段必须同时更新 `Clone()` 与 `ToPersisted()`**——v1.18 曾因克隆漏拷 `LaunchItems`/`LaunchMode` 导致「随机启动一个」与各启动项打开方式保存后从脚本文件中消失。
12. **net8 / net48 共享兼容层（v1.19 起，改 Core 或共享 UI 代码前必读）**：`SonicRoute.Core\Compat\` 提供两个 TFM 共用的最小垫片，**单一代码路径**（net8 分支不走 `#if`，直接转发 BCL，行为与改造前逐值一致）：
   - `WindowsVersion.Build` —— **用 `RtlGetVersion` 取真实 Build 号**，替代 `Environment.OSVersion.Version.Build`。原因：net48 的 `Environment.OSVersion` 走 `GetVersionEx`，**受 app.manifest 的 `<supportedOS>` 声明限制**，未声明 Win10 时返回 6.2/9200 → `AudioPolicyConfig.IsWin11`（阈值 22000）判错 → **Win11 上按应用音频路由静默失败**。判定阈值与上层逻辑一字未改。
   - `MathEx.Clamp` / `CompatEnv.TickCount64` / `AppInfo.ExecutablePath` / `AppInfo.CurrentProcessId` —— 分别替代 `Math.Clamp`（netstandard2.1+）、`Environment.TickCount64`、`Environment.ProcessPath`、`Environment.ProcessId`。**这四个类型必须是 `public`**（共享源码里的调用点位于别的程序集，internal 会 CS0122）。
   - `Net48CompilerShims.cs` —— `#if NET48` 的 `IsExternalInit` / `RequiredMemberAttribute` / `CompilerFeatureRequiredAttribute` / `SetsRequiredMembersAttribute`。**必须 `public`**（编译器查找 well-known 特性要求从使用点可访问；Legacy 链接的共享源码含 `required`/`init`），且**必须加 `#if NET48`**（net8 分支由 BCL 提供同名类型，否则 CS0433）。**禁止在 Legacy 工程里再链接一份**（会与 Core 的 public 版本冲突）。
   - `SonicRoute.Core` 的 net48 分支**唯一新增 NuGet 依赖**：`System.Text.Json 8.0.5`（+8 个传递依赖），**仅 net48 目标**（条件 PackageReference）；net8 分支保持 **0 个 NuGet 包**。另有 SDK 自动注入的 `Microsoft.NETFramework.ReferenceAssemblies(.net48) 1.0.3`（本机未装 net48 Developer Pack，首次还原需联网）。

## 6. 重要设计约束（必须遵守）

- **版本号**：唯一来源 `SonicRoute.csproj` `<Version>`，UI 显示联动；测试版不带 a 后缀（`1.10a`→版本号 `1.10`，`a2`→`10r`，`a3`→`10r2`）。
- **发布纪律**：发布新版本必须同步更新 GitHub Wiki；GitHub Release 只展示最新版日志（完整历史在 Wiki「版本相关」）；Release 描述用 UTF-8 字节流防中文问号。
- **发布资产矩阵（v1.19 起，3 个目标）**：**制作顺序 ① Lite x64 → ② ARM64（Lite）→ ③ Legacy（net48）**；`dist/` 下按版本分目录、**互不混装**——
  `SonicRoute-v1.19-Lite-x64/`（框架依赖单文件，**需 .NET 8 Desktop Runtime**）、`SonicRoute-v1.19-Lite-arm64/`（同，🧪 实验）、`SonicRoute-v1.19-Legacy-x64/`（net48 多文件 + zip）。
  对外资产（**共 5 个**）：`SonicRoute-v1.19-Lite-x64.exe|zip`、`SonicRoute-v1.19-Lite-arm64.exe|zip`、`SonicRoute-v1.19-Legacy-x64.zip`。
- **⚠️ v1.19 起不再制作「含运行环境版本」（自包含单文件，`-x64` / `-arm64`，237–265MB）**：停止构建 / 上传 / 归档；"无需安装运行时"的用户由 **Legacy（0.78MB）** 承接。历史配方保留在 `Properties/PublishProfiles/win-{x64,arm64}.pubxml`（**不再使用、不要删除**）。
  **发布包内不得含 `.pdb`**；**默认不制作 Installer**（MSI/MSIX 按需，见主规范）。
  **上传规范已纳入 Legacy**（主规范 `SonicRoute源码\README.md`）：发布流程规范第 1 步（版本号改 7 处，含 Core / Legacy）、第 2 步（Legacy 构建与三项校验 + 分目录结构 + 单文件校验 + 净化环境验证）、第 4 步（归档必须含 `SonicRoute.Legacy\`）、第 5 步（提交范围必须含 `SonicRoute.Legacy/` 与 `Core/Compat/`）、第 6 步（**上传清单 9 个资产**，含 `-Legacy-x64.zip`）、Release body 模板下载区（🪟 兼容版）；「新增版本归档流程」第 2/3 步也已补 Legacy。
- **版本号统一（v1.19 起）**：`SonicRoute`、`SonicRoute.Legacy`、`SonicRoute.Core` **三个工程都必须显式 `<Version>`**（唯一来源仍是 `SonicRoute/SonicRoute.csproj`；改动时三处一起改），否则随包发布的 `SonicRoute.Core.dll` 会显示 1.0.0.0 与产品版本不一致。
- **单实例标识按 TFM 区分（v1.19 起）**：`App.xaml.cs` 的互斥体名 / 激活窗口标题 / 注册消息名由 `#if NET48` 切换——net8 保持 `Local\SonicRoute_9NQZGRTPM1NT`（**行为零变化**），Legacy 用 `Local\SonicRoute_Legacy_9NQZGRTPM1NT`。**两版可同时安装并各自独立运行**（实测互不阻止）；同一版本内单实例仍生效。**改动这三个常量时必须保持 net8 分支的值不变**。
- **双架构发布（v1.19 起）**：x64 与 ARM64 各自独立发布，均为自包含单文件，**RID 必须写在命令行**（写在 .pubxml 里的 `RuntimeIdentifier` 是普通属性、不会流向 `ProjectReference`，会把 `SonicRoute.Core` 编成 x64 产生混合架构并触发 CS8012）：
  ```
  dotnet publish SonicRoute/SonicRoute.csproj -c Release -r win-x64   -p:PublishProfile=win-x64
  dotnet publish SonicRoute/SonicRoute.csproj -c Release -r win-arm64 -p:PublishProfile=win-arm64
  ```
  产物：`dist/publish/win-x64/SonicRoute.exe`、`dist/publish/win-arm64/SonicRoute.exe`（`dist/` 已 gitignore）。历史内联命令（`-r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded`）依旧可用、行为不变。
  **架构自检**：单文件 exe 的 PE Machine 必须为 `0x8664`(x64) / `0xAA64`(ARM64)，且 ARM64 包内不得出现 AMD64 映像（可用脚本扫描包内 `PE\0\0` 头统计）。
  **ARM64 已知差异**：`D3DCompiler_47_cor3.dll` 仅 x64 运行时包提供，arm64 输出不含该文件（WPF 在 ARM64 上走系统组件），属预期非缺陷。
  **MSIX**：`dist/msix/*/AppxManifest.xml` 的 `ProcessorArchitecture` 仍为 `x64`；要做 ARM64 商店包需改为 `arm64` 并用 arm64 产物打包、重新签名（本轮未做，未新增安装框架）。
- **ARM64 定位 = 实验测试版（v1.19 起，已写入主规范）**：ARM64 **不保证能正常使用**（无 ARM64 真机验证条件），对外一律按「实验性」表述，**禁止写成「已支持 / 已适配 / 已验证」**；随正式版一起上传 GitHub Release 作为额外资产，资产名必须带架构后缀（`-x64` / `-arm64`，v1.18 及之前无后缀）；Release body 下载区须写 `🧪 ARM64（实验）` 并注明未真机验证；**不提交 Microsoft Store**（MSIX 保持 x64）。完整规则见 `SonicRoute源码\README.md`「🧪 ARM64 实验版发布规范」，构建层规则见「🔧 开发规范 · 双架构构建规范」。
- **Lite 版（v1.19 起同样双架构）**：框架依赖单文件（`--self-contained false -p:PublishSingleFile=true`，**禁止附加 DebugType/DebugSymbols**），产物 `dist\publish\lite-x64\SonicRoute.exe` / `dist\publish\lite-arm64\SonicRoute.exe`；发布资产名 `SonicRoute-vX.Y.Z-Lite-x64.exe` / `-Lite-arm64.exe`（+ 同名 zip）。**用户测试统一用 Lite 版**（内存更低）。
- **Wiki 同步（v1.19 起）**：ARM64 条例已写入 `SonicRoute.wiki/`——`06-版本相关.md`（发布规则：实验定位 / 资产命名 / 商店仅 x64）、`05-技术实现.md`（双架构构建命令 + 系统要求）、`04-常见问题.md`（3 条 ARM64 FAQ）、`01-使用指南.md`（安装表）；**Wiki 是独立 git 仓库，需单独提交推送**。
- **多语言**：新增功能/文案必须补全 9 语言（zh-CN/zh-TW/en-US/ja-JP/ko-KR/fr-FR/de-DE/es-ES/ru-RU，UTF-8 BOM），翻译参考 `翻译规范.md`，意思不变、简洁干净。
- **UI 折叠**：一律用「更多选项」展开样式（实验设置折叠选项、设备区块、语言卡片等均如此），且全部带展开/收起动画（`AnimatePanelExpand`：展开淡入+上滑 130ms / 收起淡出+下滑 100ms，动画结束再 Collapsed）。**简洁面板应用行 ▾ 展开/收起也带同款动画**（展开在设备按钮异步构建完成后播放，收起动画结束后 Collapsed，同时只展开一行）。**简洁面板展开动画 2026-09-18 改为高度生长**：面板默认贴底定位（QuickPanelPosition 锚定工作区右下角），展开 = 高度 0→内容高（HeaderBorder 宽度离线 Measure，+6 底部 Margin）130ms EaseOut 平滑生长 + 淡入，完成回 Auto——「下面不动、上面向上长」（上下裁剪时底部固定顶部动）；收起仍为高度收拢+淡出（顶部向下收、下方行上移）。**2026-09-18 上修**：动画期间 SizeChanged 实时同步底部定位（_animatingHeight 标志 + _anchorBottom 锚点，动画中 Top = 锚定底部 - 当前高度，Left 不动，默认/Custom 均生效；动画结束 FinishHeightAnimation 清标志/锚点，Custom 写回新 Top 保持锚定底部，默认重新右下角锚定；锚点记录时取整到物理像素网格（GetDpiScaleY，Math.Round((Top+ActualHeight)*s)/s），Top 设置加 0.01px 防抖，消除 DIP 小数导致的 1px 底部晃动；_heightAnimDone 覆盖回 Auto 布局收敛期（收敛 SizeChanged 先锚定再收尾，Dispatcher Loaded 兜底；FinishHeightAnimation 幂等；_heightAnimCount 活动动画计数：切换展开时旧行收起与新行展开并行，收尾只允许最后一个动画完成时执行；展开标志提前置位 + 收起提前返回分支接管标志，防锚定模式泄漏；RowExpand_Changed 重入保护（切换展开时显式收起旧行后 IsChecked=false 递归不再重复执行收起动画，杜绝动画覆盖导致的 _heightAnimCount 泄漏与底部错位）——展开时底部不动、顶部向上生长，收起时底部不动、顶部向下收缩。**2026-09-18 行级动画标志防覆盖泄漏**：AppRow 新增 HeightAnimating 行级标志——展开/收起动画开始只计一次数（切换展开/反向操作覆盖旧动画、旧 Completed 不触发时，由新动画归还计数），Completed 清标志并减数，消除 _heightAnimCount 永久泄漏导致的锚定模式不退、面板定位异常；AnimateRowPanelCollapse 改签名接收 AppRow；BuildRowDevicesAsync 整体包 try/catch、展开分支 Measure 包 try/catch（设备枚举/解包异常不冒泡跳过展开动画、不泄漏计数）。**2026-09-18 切换展开不播动画**：已展开一个应用时展开另一个应用 = 切换场景（foreach 收起旧行时置 switching），新行直接显示（Height=targetH、Opacity=1 即时到位），不播生长/淡入动画，避免新旧双动画并行导致面板抖动；旧行收起动画保留（平滑带动高度变化），底部锚定下无晃动；首次展开（无其他展开行）仍播放原生长动画。**2026-09-18 切换高度不变**：切换场景下旧行改为「瞬间收起」（不播收起动画）+ 新行直接显示（不播展开动画），同一布局帧内完成；**2026-09-18 窗口高度恒定（Grid 固定布局，当前方案）**：简洁面板去掉 SizeToContent 改为固定高度 Grid——Row0 标题 / Row1 输出设备 / Row2 主音量 / Row3 分隔+应用音量标题 / Row4 应用区域(*)唯一伸缩滚动 / Row5 分隔 / Row6 底部操作栏(Auto 永远贴底)；窗口高度 = 用户设置的固定高度（QuickPanelHeight，默认 350、350–800、步进 10，主题页滑块可调并立即应用，clamp 工作区不超屏幕），打开后永不变；应用区域 = 窗口高 - 固定行 DesiredSize（不足至少一行），行展开/收起/切换只在 AppArea 内变化（ExpandWrap 生长/收拢 + ScrollRowIntoView 滚动到该行），不改变窗口尺寸；底部操作栏永远贴底；删除整套窗口级高度动画/锚定逻辑（_animatingHeight/_anchorBottom/_heightAnimCount/_fixedExpandH 等）；曾有的 QuickPanelMaxApps 行数滑块已删除并清理残余。**2026-09-18 呼出动画（当前方案）**：只对 RootBorder 做入场动画——TranslateTransform.Y 6→0 + Opacity 0→1，140ms QuadraticEase EaseOut，Window 位置/高度全程不变；_entranceAnimating 标志防连呼重复叠加（动画中连呼=不位移不重播仅刷新；已可见动画完成=保持原位；首次=屏幕外→定位→播动画）。**2026-09-18 版本号 1.18 + 备份**：SonicRoute.csproj 1.17→1.18；备份 SonicRoute源码\v1.18\（源码带注释，含改动说明.txt）；规范新增：备份/归档一律写入 SonicRoute源码\vX.Y\，禁止放到 archive\旧版本 等其他位置。

**2026-09-18 备份**：SonicRoute源码\v1.18-自动化\（源码带注释 + Debug exe + 改动说明.txt）。

**2026-09-20 备份**：SonicRoute源码\v1.18-启动项打开方式\（源码带注释 + Debug exe + 改动说明.txt；含启动项独立打开方式、添加文件选择器、持久化 Bug 修复三轮改动，为版本号提升 1.19 前的留档）。

**2026-09-20 备份（1.19 工作区）**：SonicRoute源码\v1.19-自动化页顶部\（源码带注释 + Debug exe + 改动说明.txt；含版本号 1.19、自动化页顶部操作区与「打开脚本文件夹」按钮、Core ShellOpen 统一实现）。

**2026-09-20 备份（1.19 + ARM64 双架构，最新）**：SonicRoute源码\v1.19-ARM64双架构\（源码带注释 + Debug exe + **含运行环境版 win-x64 / win-arm64 两个自包含单文件** + 改动说明.txt，约 505MB；含 ARM64 原生支持、双架构发布配置与 v1.19 发布收尾）。

**2026-09-21 v1.19 正式归档**：SonicRoute源码\v1.19\（约 744MB，按规范「新增版本归档流程」建立）——`源码带注释\`（含 .sln + 图标.png + 两个 pubxml，无 bin/obj）+ `可运行版本\`（8 个资产：自包含 `-x64`/`-arm64` 与 Lite `-Lite-x64`/`-Lite-arm64` 的 exe+zip）+ `安装包\`（`SonicRoute-v1.19-Lite.msix` + 证书）+ `改动说明.txt`；所有资产 SHA256 与 `dist\` 一致。规范 README 的「版本更新注释」已补 v1.19、「已归档版本」表已补 v1.19 行（并标注 v1.12~v1.18 缺失）。

**2026-09-18 自动化新增「定时」触发（Schedule=4）**：AutoRule 新增 ScheduleMode(0=仅一次/1=每天/2=每周)/ScheduleTime/ScheduleWeekdays/LastRunKey（ScheduleDate 字段保留仅旧配置兼容，UI 不再使用）；时间用小时+分钟下拉选择（禁止手输）；新增 AutoRuleScheduler（单发 System.Threading.Timer 只等最近一次执行，无轮询；SystemEvents.TimeChanged + PowerModeChanged(Resume) 重算；睡眠错过 2 分钟容差内不补执行；仅一次=下一个到达时刻、执行后自动禁用不删除；LastRunKey 防重复）；App 启动 Start/退出 Shutdown、ReloadHotkeys 挂 RefreshScheduler；UI 定时编辑区 + 9 语言文案；执行复用 AutoRuleService.ExecuteAsync。
  - 清理：ScheduleDate 字段与 Auto.Enabled 语言 key 均 0 引用已删除；ScheduleDate 旧 JSON 反序列化自动忽略。
  - 状态点：仅一次定时规则已执行（LastRunKey 非空）或规则被禁用的，规则名称右上角显示主题色 RGB 反色 6px 圆点（悬停提示已执行/已禁用，9 语言 Auto.Disabled / Auto.OnceExecuted；点位于名称左上角）；规则行按钮为 编辑 | 禁用/启用 | 删除（AutoToggle_Click 切换 Enabled + 保存 + ReloadHotkeys，禁用后快捷键/应用/定时均不触发，9 语言 Auto.Disable / Auto.Enable；禁用状态随规则 JSON 持久化，重新启用后按下一个到达时刻正常触发）；scheduler.log 诊断日志（%LocalAppData%\SonicRoute\，限长 256KB）。

**2026-09-18 自动化应用列表图标 + 慢刷新（2026-10-01 优化）**：自动化两个应用下拉改用 AppItem（复用懒加载图标 + INPC，共享 DataTemplate AutoAppItemTemplate 图标 18px）；自动化页可见时每 8s 后台刷新 `_autoApps`，仅当 PID、名称、活动状态或顺序变化时重填下拉（保留选中项），离开页面立即停止计时并在后台枚举前检查可见性；异步刷新防重入与切页代际保护。图标只给选中项预取，其余在展开下拉时加载，UI 更新走 Dispatcher Background；相关断言 AudioAppInfo→AppItem。

**2026-09-18 自动化新增「应用退出」触发（AppExit）**：AutoRuleTrigger 加 AppExit=3；AutoRuleService 复用进程快照 diff 反向检测（v1.18 修复：退出 diff 原嵌套在 AppStart 块内导致仅建退出规则不触发，已重构为 AppStart/AppExit 独立外块）（上一秒存在、当前消失 → 触发），RefreshWatcher need/TickAsync 过滤纳入 AppExit；UI 触发下拉框加「应用退出时」、摘要加分支、非 Hotkey 复用应用选择面板；9 语言加 Auto.TriggerAppExit；规范新增「自动化触发监听实现规范（v1.18 起）」记录 AppStart/AppExit/AppSwitch 实现方式（1 秒轮询 + 进程快照 diff + 前台 PID）。

**2026-09-18 主题页小字调整**：删除简洁面板高度小字（XAML 移除，Exp.PanelHeightHint key 保留不显示）；St.PanelChangeSystemDefaultHint 9 语言简短化。

**2026-09-18 快速面板高度设置（经典面板隐藏 + 改名）**：主题页高度区块包入 PanelHeightSection；LoadTheme/LoadSettings/QuickPanelStyleCombo_Changed 三处按经典面板隐藏（切换样式即时生效）；标题改「简洁面板高度」、小字简化为「简洁面板固定高度（350–800）」，9 语言同步。

**2026-09-18 主题页快速面板高度显示修复**：滑块/文本初始化原只写在 LoadSettings()（设置页），主题页 LoadTheme() 遗漏 → 进入主题页显示 XAML 硬编码初值 500 px。已补 LoadTheme 初始化（_suppressSettings 内、350–800 钳制），XAML 初值改 350 px。

**2026-09-18 自动化页面 UI（紧凑化）**：AutomationPage 整页 Grid（标题/列表/编辑卡占剩余，MaxWidth 760 居中）；编辑卡 = 头部（触发+快捷键紧凑成行）+ 操作列表（*独立滚动）+ 底部固定栏（添加操作虚线按钮+保存/取消）；规则一行卡片（名称·触发→操作）、编辑轻量小按钮+右上角×删除；操作卡 Padding/Margin 紧凑、删除×、操作下拉 230；添加操作点击弹操作类型菜单后添加并滚动到底；规则列表 MaxHeight 240 内部滚动。功能/数据结构不变（拖拽排序、快捷键录制、参数、延时、保存/取消保留）。 **2026-09-18 已回档撤销**（用户要求回档到 v1.18-固定高度面板 备份）：AutomationPage 恢复原 ScrollViewer+StackPanel 布局，BuildAutoRuleRow/BuildAutoStepRow 恢复原尺寸，AutoAddStep_Click 恢复直接添加默认步骤，AutoStepsScroll 移除。当前自动化页 = 备份快照状态。
- **测试**：之后所有测试统一用 Lite 版。
- **MSI/MSIX**：默认**不制作**；用户明确要求时才做完整版并放入源码文件夹，**不上传 GitHub**；微软商店版本只做 Lite；MSIX 制作规范见主规范（签名、版本 0 修订号等）。
- **内存/性能**：正常版本也要低内存占用（与 Lite 基本一致）。代码现状 = 主窗口关闭自动回收（多次延迟 GcNow+TrimWorkingSet）与 idle 定时回收并存；历史曾讨论精简为「单次 GcNow + 2s 延迟 TrimWorkingSet、不恢复 idle 频繁 GC」，**与当前代码存在出入**——改动内存逻辑前先与用户确认最新意向。
- **不引入双进程**：v1.15 双进程架构已整体废弃（废案见 `SonicRoute源码\废案\双进程架构\` 与 GitHub `archive\`）。
- **README 底部固定**：作者主页三链接（B站 / 爱发电 / GitHub），置于最底部。
- **商店版判定统一（v1.18 起）**：区分微软商店版（MSIX）与绿色免安装版一律复用 `MainWindow.IsPackaged()`（`Windows.ApplicationModel.Package.Current` 尝试 + catch 返回 false），**禁止另写第二套检测**；清理自启动按钮显隐、开机自启 AutoStartStore/AutoStart 读写、检查更新引导路径（商店版→微软商店 / 非商店版→GitHub）均走同一判断。
- **源码注释**：代码为自行重新实现、仅参考 EarTrumpet 机制，注释不得写得像直接移植（历史教训）；源码/打包上传可去注释减小体积（已写入规范）。

## 7. 历史问题（已解决 / 已回滚）

| 问题 | 结论 |
| --- | --- |
| 自动化触发「任意应用」选项（v1.20） | 原下拉条目使用空进程名，编辑器保存校验会拒绝，无法通过主程序界面创建规则；v1.20 移除该条目和对应 9 语言文案。已有或外部创建的空 `TriggerApp` 规则仍由执行器按任意应用处理。 |
| 音量/静音不生效 | 根因 = 会话失效后仍复用旧 ISimpleAudioVolume；已用 5s 新鲜度 + 按 PID 聚合修复 |
| OSD 默认位置偏屏幕中央 | 根因 = `SystemParameters.WorkArea` 被错误除 DPI；已修复（WorkArea 即 WPF DIP） |
| 一键还原 OSD 位置错误 | 根因 = Custom 坐标残留 + 坐标系混用；已修复（复位 TR + Custom=-1） |
| 快捷键静音不生效 | 根因 = 静音作用到错误会话；统一走 CurrentAppService + SessionVolumeService 修复 |
| 托盘滚轮偶尔失效 | 曾误判为内存/工作集换出问题并做修复，随后按用户指示删除；最终根因 = 任务管理器为前台焦点时启动、权限不足 |
| 双进程架构 | 阶段 0~3 全部完成过，但用户拍板整体废弃、回退单进程（含 BackendHost/Named Pipe），废案归档 |
| 简洁面板展开闪烁、OSD 闪烁 | 多轮修复后用户要求「复原刚执行的并清理残留」——以当前代码为准，勿重蹈 |
| MainWindow 资源迁移至 App.xaml | 已放弃（隐式样式冲突风险），已写入规范 |
| 简洁面板应用音量条 Peak Meter（2026-09-18 历史回档） | 当时实现后因用户反馈「显示不了设置也设置不了」而整体回档到 v1.18-定时；旧方案用面板 `DispatcherTimer`、按布局宽度绘制，已废弃。2026-09-30 的 v1.20 工作区按独立 Core 采样线程与 `ScaleTransform` 重新实现，详见上方功能表；旧回档仍保留在历史记录中。 |
| v1.20 实时电平初版导致简洁面板加载失败（2026-09-30） | 根因：模板内声明的 `ScaleTransform` 被 WPF 冻结为只读；初始化第一行 Slider.Value 时 `UpdatePeakVisual` 写 `ScaleX` 抛 `InvalidOperationException`，行构建中断，只剩空白图标的首行，外层 OSD 提示加载失败。修复：模板只保留 Peak Border，每个应用行在 `BuildAppRowsAsync` 中创建自己的可写 `ScaleTransform` 并赋给 `RenderTransform`。Lite x64 运行时 UIA 检查：系统滑块 + 3 个未被用户配置隐藏的应用滑块全部出现；Legacy Debug 编译通过。 |
| 随机启动 / 各启动项打开方式保存后丢失（v1.18 工作区） | 根因 = **编辑器载入规则时克隆 `AutoRuleStep` 漏拷 `LaunchItems` 与 `LaunchMode`**，随后 `NormalizeLaunchSteps` 用旧 `ProgramPaths` 重建启动项 → `OpenWith`/`OpenWithName` 归零、`LaunchMode` 回落 0，保存时把丢失状态写回磁盘（实测用户脚本 `111.json`：LaunchItems 结构在、OpenWith 全空、LaunchMode=0）。已修复：克隆/落盘统一收敛到 `AutoRuleStep.Clone()` 与 `ToPersisted()` 单一入口，并新增严格取值 `CurrentLaunchItems()`（保存/校验用，防镜像复活已删除项）；`NormalizeLaunchSteps` 改为仅在「从未载入过启动项」时转换一次，不再每次重渲染替换列表对象 |

## 8. 当前开发状态

- **当前工作区（2026-10-01）**：版本 1.20（未发布）；`SonicRoute`、`SonicRoute.Core`、`SonicRoute.Legacy` 和 Legacy app.manifest 已同步。自动化触发应用下拉移除「任意应用」，对应 9 语言键已删除；执行器仍兼容空 `TriggerApp` 的已有或外部规则。简洁面板实时应用电平已按 Core 统一采样重新实现，主题页开关可完全停用；性能优化前后快照分别在 `SonicRoute源码\v1.20-01-实时电平-优化前\`、`SonicRoute源码\v1.20-02-实时电平-性能优化\`（各含三工程源码 + Lite x64/Legacy 可运行文件 + 改动说明，排除 bin/obj）。优化版完成目标集合低频复制、静态电平跳过快照/Dispatcher、轨道 SizeChanged 触发布局更新，采样目标范围不变；Lite x64 发布和 Legacy Debug 编译通过，未做优化后运行时性能实测。此前 Core 实测测试音 Peak 0.107、独立播放进程退出后 Peak 回零、7 应用 29.8Hz、200 次启停无停止后回调；Lite 主题页 UI 自动化验证开关关闭/开启与配置持久化，检查后恢复原 config.json。临时诊断入口按 `App.ToggleQuickPanel` 真实关闭路径循环 100 次：opened/closed 均为 100，进程 Private Bytes 第 50 次 146.46MB、第 100 次 146.58MB；诊断入口随后移除。真实音乐/游戏/Discord、主题色和滑块拖动视觉仍待交互验收。根 README 保留已发布版本 1.19 的说明。
- **v1.20 UI/内存优化（2026-10-01，本地未发布）**：设备缓存 TTL 单位修正为 3000ms（修复原 8.33 小时）；自动化每秒进程快照和全局清除路由的进程句柄均及时 Dispose，自动化 Tick 单飞且进程枚举放入后台；主题画刷缓存限制为 256、自定义 RGB 33ms 合并预览与 200ms 防抖保存，窗口关闭补存；自动化页 8s 应用列表相同时跳过 ComboBox/图标重建；主窗口与快速面板关闭后仅在无其他 UI 时延迟一次 GC/修剪，重开取消；`SessionVolumeService.Refresh` 单飞，成功完成后才更新时间戳，异常释放新收集的 COM 引用。快速面板采样目标筛选规则与前一阶段相同，未做虚拟化。Lite x64 单文件和 Legacy Release 编译成功，尚未做运行时内存/交互实测。阶段备份位于 `SonicRoute源码\v1.20-03-UI内存优化\`。
- **v1.20 Legacy 切页卡顿优化（2026-10-01，本地未发布）**：设置页 `GetApps()` 和自动化页首次 `GetApps()` 均由 UI 线程移至后台，结果仅在当前页与导航代际仍匹配时绑定；设置页保留上次应用项、选中项和设备筛选控件，设备或语言改变才重建筛选；自动化页离开即停 8s 定时器，切回立即异步刷新；规则列表比较规则 JSON、语言和快捷键冲突状态，未变化不重建，变化时在首帧后按每 8 条让出 Dispatcher；应用下拉图标按选中/展开加载，图标更新用 Background 优先级并以单项加载标记避免重复提取。窗口启动后若已离开概览页，跳过无关的首次概览刷新。Lite x64 单文件和 Legacy Release 编译成功；Legacy 测试目录 `dist\SonicRoute-v1.20-Legacy-x64-nav-opt\`，阶段备份 `SonicRoute源码\v1.20-04-Legacy切页优化\`。尚未做运行时切页耗时实测。
- **历史基线（v1.18）**：1.18 已正式发布（2026-09-18，含运行环境版 + Lite + MSIX 商店版）。简洁面板 Peak Meter 旧实现曾因用户反馈问题整体回档；v1.20 当前重新实现与旧方案不同，历史条目保留作为注意事项。
- **v1.18 发布后工作区改动（未发布，2026-09-20）**：启动程序动作改为**启动项列表 + 每项独立打开方式 + 启动模式（全部启动 / 随机启动一个）**，打开方式候选来自**系统文件关联**（Shell `SHAssocEnumHandlers`，非写死列表）且「添加文件/程序」改为文件选择器（详见「功能清单 · 自动化」行与 `改动记录.md` 2026-09-20 条目）；旧配置读取行为不变。新增文件 `SonicRoute.Core/OpenWithService.cs`、`SonicRoute.Core/Interop/AssocHandlerInterop.cs`。UI 入口与文案键（`Auto.LaunchMode*`/`Auto.OpenWith*`/`Auto.AddFile`/`Auto.AddFileTitle`/`Auto.PickProgram`/`Auto.FileFilter*`，9 语言已补；原 `Auto.Program` 已 0 引用删除）。**持久化 Bug 已修**（编辑器载入漏拷新字段导致保存后丢失，见「历史问题」与「关键实现 11」）。
- **v1.19（工作区，未发布，2026-09-20）**：版本号 `1.19`；自动化页顶部改为 Grid 两列——左「＋ 新建规则」（铺满至右侧按钮之前）+ 右「打开脚本文件夹」；新增 Core `ShellOpen.Folder()` 统一「打开目录」实现（语言文件夹与自动化脚本文件夹共用，L10n.OpenExternalLangDir 已改为调用它）；9 语言 +2 键（`Auto.OpenFolder` / `Auto.OpenFolderFail`，共 364 键）。备份：`SonicRoute源码\v1.18-启动项打开方式\`。README 仍为已发布版 1.18，按发布纪律待正式发布时统一更新。
- **原生 ARM64 支持（v1.19，2026-09-20）**：新增 `win-arm64` RID 与 `Properties/PublishProfiles/win-{x64,arm64}.pubxml`，双架构各自自包含单文件发布；x64 行为完全不变。**架构扫描结论：全项目纯托管、零第三方 NuGet 包、零自带 native DLL**，P/Invoke 仅调用系统 DLL（user32/kernel32/gdi32/shell32/ole32/combase），句柄与指针一律 `IntPtr`、手工 vtable 用 `IntPtr.Size` 步进、无 `unsafe`/`fixed`/`sizeof`、无架构分支——**无需为 ARM64 改任何业务代码**。详见「发布纪律 · 双架构发布」。
- **v1.19 发布收尾（2026-09-20）**：清空 bin/obj 后最终双架构发布完成——`dist/publish/win-x64/SonicRoute.exe`（237.8MB，PE=0x8664 AMD64）、`dist/publish/win-arm64/SonicRoute.exe`（265.0MB，PE=0xAA64 ARM64），两者包内均无另一架构映像；FileVersion=1.19.0.0。x64 本机启动通过；**ARM64 未做真机验证**（本机 x64，运行报 Exec format error）。发布说明 `dist/release_body_v1.19.md`。**Store/MSIX 本轮未动，仍为 x64**；zip 与重命名资产未生成。
- **v1.19 全部打包完成（2026-09-21）**：`dist/` 下 8 个发布资产就绪——自包含 `SonicRoute-v1.19-{x64,arm64}.exe|.zip`、Lite `SonicRoute-v1.19-Lite-{x64,arm64}.exe|.zip`。**MSIX 已制作并签名**：`dist/msix/SonicRoute-v1.19-Lite.msix`（7.56MB，Identity Version=1.19.0.0、`ProcessorArchitecture=x64`、签名 CN=1517F817-…），归档于 `SonicRoute源码\v1.19-ARM64双架构\安装包\`；`pkg\`（含运行环境版 MSIX）自 v1.10 起停维护未更新。Wiki `03-快捷键.md` 混合换行已统一为纯 CRLF。**待确认**：MSIX 清单 `<Resources>` 只声明 8 种语言（缺 `zh-tw`），规范写 9 种，因涉及 Store 已声明语言集未擅自改动。
- **.NET Framework 4.8 Legacy 版（v1.19 工作区，2026-09-22，未发布）**：新增 `SonicRoute.Legacy\`（net48 / x64 / Prefer32Bit=false，仅 csproj + app.manifest + App.config，**链接共享** `SonicRoute\` 源码）；`SonicRoute.Core` 改为双目标 `net8.0-windows;net48` 并新增 `Compat\` 共享兼容层（见「关键实现 12」）。**两版共用同一份 UI 源码与同一份配置格式**，功能完全对齐。编译与运行验证：Legacy Debug/Release **0 错误**（47 个警告，全部为 net48 BCL 缺 Nullable 注解导致的可空性提示）；启动实测主窗口（`音跃 SonicRoute v1.19`，920×620）与简洁面板（372×440 @ 右下角）均正常；net8 侧 Debug/Release 0 错误、警告集合不变、**0 个 NuGet 包**、双 RID 发布架构正确。**net8/net48 行为一致性已实测**：同一份只读探针在两个 TFM 下各 50 项检查全部通过且结果一致（含 `WindowsVersion.Build=26100`、设备枚举 9 输出/6 输入、应用会话、系统音量、幂等写回 `HRESULT=0x00000000`）。详见 `改动记录.md` 2026-09-22 条目。**Legacy 未做 MSIX / 未做 ARM64 / 未做单文件**。
- **DPI 感知等级实测（2026-09-22，窗口级 `GetWindowDpiAwarenessContext`）**：**Legacy = `PER_MONITOR_AWARE_V2`**（manifest + App.config 双开关生效）；**net8 主版本 = `SYSTEM_AWARE`**——其 exe 内嵌 manifest 只有 `asInvoker`、**无任何 DPI 声明**，即 net8 目前只做系统级 DPI 感知，多显示器混合缩放下跨屏移动会被系统位图拉伸。这是**既有情况、非本次迁移引入**；按「不允许改变 .NET 8 行为」的约束**未改动**，如需提升应给 `SonicRoute` 单独加 `app.manifest`（独立议题）。该发现也部分解释了历史上「OSD 默认位置偏屏幕中央 / 一键还原 OSD 位置错误」这类定位问题的成因。
- **发布包**：`dist/SonicRoute-v1.19-Legacy-x64.zip`（0.78 MB，12 文件，含 `SonicRoute.exe` + `SonicRoute.exe.config` + `SonicRoute.Core.dll` + 9 个依赖 DLL，无 pdb），中间目录 `dist/publish/legacy-x64/`；已解压端到端验证可运行。
- **已知行为**：两版**共用单实例互斥体** `Local\SonicRoute_9NQZGRTPM1NT` → 同时运行会被判第二实例而退出（实测第二实例 exitcode=0），测试需先关闭另一个。
- **第三阶段：发布 / 打包 / 兼容性验证（2026-09-22 完成）**：`dist/` 下按版本分目录产出 5 个发布目标 + 9 个对外资产（含新增 `SonicRoute-v1.19-Legacy-x64.zip` 0.78MB），详见「6. 重要设计约束 · 发布资产矩阵」。**验证结果**：三个可在本机运行的版本（自包含 x64 / Lite x64 / Legacy）启动 + 快速面板 + 主窗口均实测通过；**自包含 x64 在净化环境（PATH 无 dotnet、清空 `DOTNET_ROOT`）下正常启动** → 不依赖已安装 .NET 8；Legacy 的 Core 层回归 **51/51 项通过**且与 net8 逐项一致；**配置完全互通**（同一份 config.json 42 字段、快捷键、自动化规则、设备/应用自定义名全部一致读取，且未被改写）；**两版单实例互不阻止**（各自独立运行，版本内单实例仍生效）；**内存无泄漏**且空闲回收在 net48 下同样生效（关闭 UI 后工作集降至 1–14MB）；**ARM64 静态验证通过**（单文件包内仅 win-arm64 条目、无架构分支、P/Invoke 全为系统 DLL、零第三方 NuGet）。**未做**：干净系统测试（无虚拟机/第二台机器）、ARM64 真机运行、安装/卸载测试（本项目默认不制作 Installer）。完整报告见 `SonicRoute源码\v1.19发布与兼容性验证报告（第三阶段）.md`。
- **待定论（未复现）**：`--panel` 启动偶发出现 `240×63` @ 右上角 OSD（一轮 5 次中 4 次，此后 17 次均未复现，依赖环境负载）。面板路径中唯一非用户触发的 OSD 来源是 `QuickPanelModernWindow` 第 333–336 行的 `catch → ShowOsd(Qp.Panel, Qp.LoadFail)`，**该 catch 是原有代码、本次未改**，net8 与 Legacy 共用 → **非本次引入**；如日常出现「面板加载失败」提示请单独开议题。
- **1.16 → 1.17 期间改动**（见备份 `改动说明.txt`）：主题透明度提示「建议 50 及以上」；设置导入语言/导入配置支持拖拽；自动化延时输入防呆（0~60000，失焦/输入校准）；语言文件卡片折叠；深色模式下拉框前景色修正；README 已知问题段删除、改「下一版本设计理念」。**自动化规则独立存储**：从 config.json 移出至 `Automation\{Id}.json`（AutoRuleStore + 旧配置自动迁移），随后改为按规则名称命名（同名加 (1)(2)…）。**本地配置编辑器（config-editor）**：`config-editor\index.html` 完成积木式规则编辑器（拖拽排序 / 分类配色 / 添加菜单）、删除九宫格死代码、快捷键栏恢复可编辑、音量滑块滚轮调节、按键录制式快捷键输入；规范见主规范「配置编辑器规范（config-editor，v1.18 起）」，改动见 `改动记录.md` 2026-09-16 条目。
- **已确认**：`AI_CONTEXT.md` **不推送 GitHub**（仅存项目根目录本地）。
- **下一步建议**：按用户需求迭代；发布前先备份、再按发布纪律走（同步 wiki、Release 只展示最新日志）。

## 9. 开发注意事项（高风险区域）

- `App.xaml.cs`：托盘/热键/OSD/内存回收/单实例全部在此，**改动前确认**；热键反向调面板的委托依赖集中于此。
- `MainWindow.xaml(.cs)`：3395 行 + 158KB XAML，所有设置页/自动化页集中，改动风险高；自动化 UI 动态构建控件（BuildAutoRuleRow/BuildStepParams/BuildPathRow）。
- `TrayWheelService.cs`：托盘滚轮 + OSD 全生命周期（含麦克风常驻横幅），高频链路，改错直接闪退/OSD 异常。
- `SessionVolumeService.cs` / `AudioService.cs`：音频核心，改错会导致音量/静音/切换回归；设备枚举 TTL 缓存、幽灵会话过滤勿动。
- `ConfigService.cs`：配置兼容线；新增字段必须带默认值；导入导出/重置逻辑勿破坏。
- `AutoRuleStore.cs`（Core）：自动化规则独立存储；文件名为规则名称（清洗非法字符，同名加 (1)(2)…），旧 {Id} 命名文件下次保存自动迁移；改动注意迁移逻辑（MigrateLegacyIfNeeded）与缓存失效（Save/Delete 后 Invalidate）。
- `L10n.cs`：语言系统复杂（内置/外置/附加语言/导入校验），改动需回归 9 语言 + 导入导出。
- `ThemeService.cs`：冻结画刷使用 256 容量 FIFO 缓存，动态资源只在画刷引用变化时写入；透明度与主题切换联动，自定义 RGB 预览以 33ms 合并、配置写入以 200ms 防抖并在窗口关闭时补存。
- **`SonicRoute.Core\Compat\`（v1.19 起）**：net8/net48 共享垫片。**四个类型必须保持 `public`**（internal 会让共享源码 CS0122）；`Net48CompilerShims.cs` 必须保持 `#if NET48`；`WindowsVersion` 的 `RtlGetVersion` 主路径勿改回 `Environment.OSVersion`。
- **`SonicRoute\` 的 .cs/.xaml 被两个工程共用（net8 + net48 链接）**：改这些共享文件时，**必须两个 TFM 都编译通过**，且 net8 行为不得改变。允许的差异只有两类：①`#if NET48` 条件编译块；②语义等价的 API 替换（走 Core 的 Compat 垫片）。**禁止在共享文件里写只对某一个 TFM 有效的逻辑**。
- **`SonicRoute.Legacy\`**：只放 csproj / app.manifest / App.config，**严禁把 `SonicRoute\` 的源码复制进来**（会产生双份维护）。`app.manifest` 的 `<supportedOS>`（Win10 GUID）与 `dpiAwareness=PerMonitorV2` 是**必需项**，删掉会导致 Win11 按应用路由失效 / OSD 定位偏移；`App.config` 的 `Switch.System.Windows.DoNotScaleForDpiChanges=false` 与 manifest 配套，缺一不可。
- 修改前先读 `SonicRoute源码\README.md`（主规范）与 `改动记录.md`，遵守「优先查看本地备份规范条例」。

---

## 自评（本文档是否足以让新 AI 理解）

- ✅ 已覆盖：项目定位、技术栈、模块职责、关键实现原理、设计约束、历史教训、开发风险。
- ✅ 遵循「以源码为准」：所有关键实现均来自当前工作区实际代码审阅（2026-09-15 两轮：动作数、导航结构、内存回收流程、实验设置、麦克风选项、托盘滚轮根因均已对照源码核实）。
- ⚠️ 未逐行审阅：`MainWindow.xaml`（158KB）、两个 QuickPanel 的 `.xaml`、`AudioPolicyConfig.cs` 中段（早前已读全文，本轮未重读）——新 AI 如需改这些文件应先读对应 XAML。
- ✅ 已确认：`AI_CONTEXT.md` 不推送 GitHub。

**保存位置**：`E:\kunkun\SonarSwitch\AI_CONTEXT.md`
