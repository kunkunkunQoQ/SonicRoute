# SonicRoute 1.36 Native AOT 交接

以下为早期预览的历史检查点。1.37 的当前工作区及构建入口见 [Native AOT 产品说明](Native/Product/README.md)。

## 接手位置

- 隔离工作树 `E:\kunkun\SonarSwitch\outputs\native-aot-phase1-20261125\source`
- 分支 `codex/native-aot-phase1-20261125`
- 最新源码 WIP 提交 `08914493cae7880a5a87a56cfe5a77a419b96737`。本交接文档单独提交在它之后。
- 阶段 3 基线 `7091ad6`。2026-11-25 按用户要求停止，未找到本任务仍在运行的构建或测试进程。

## 产品要求变更

**冻结的 WPF 1.36 是后续原生版的视觉与交互基准。用户不接受当前原生 WIP 与 WPF 界面的明显差异。** 按冻结版本还原布局、密度、配色、图标、控件状态、菜单、快捷面板和拖放等操作，保留已经接通的业务服务。旧版源码在 `SonicRoute/MainWindow.xaml`、`QuickPanelModernWindow.xaml` 及其 partial，主题以 `ThemeService.cs` 等为准。视觉有歧义时以冻结的 WPF 1.36 实物为准。功能对照见 `E:\kunkun\SonarSwitch\integration-docs\native-aot-audit-20261125\02-界面与宿主.md` 和同目录的 `04-互联与媒体.md`。

## 实现状态

- 已接通的基础路径包括 Win32 托盘、窗口生命周期、Direct2D/DirectWrite 绘制、六个入口、快捷面板、分层 OSD 和 Win32 对话框。PC 页连接现有后端的实际设备、音量和静音、原生输出、捕获转发及多选。上一版 EXE 验证了两版托盘注册、打开主窗/面板/OSD、关窗重开和退出，以及实际音量和设备读回。
- 部分完成的界面包括快捷键、主题、语言与配置、自动化编辑和取消、实验/历史。OSD 接了 45 ms 判定和 500 ms 最短切换中显示。UI Automation 与自动化模板是最后加入的 WIP，未成功编译或验证。九份语言资源已接入，新增字符串和视觉细节仍需对照。
- Full 接入 Android USB 和无线发现、配对、目录、音频、媒体、虚拟屏、双向文本剪贴板、文件传输和 AirPlay 的真实服务。已将 AndroidAudio 中剩余 NAudio 类型和 PC 回环旧 COM 激活迁走。手机及 Apple 真机、重连与持续播放、双向听音和窗口关闭后的运行都未验证。旧版跨设备拖放和伴窗交互尚未迁移。WPF 视觉还原尚未开始。

## 最新编译结果与可用产物

最新 Full/Lite x64 发布均在 `Native/Product/Views/AutomationView.cs:35` 编译失败。模板保存分支对返回 `void` 的 `AutoRuleStore.Save(rule)` 使用了 `!`，报 CS0023 并带出 CS1662。Full 还报告重复链接的 `ProcessCaptureIdentity`（CS0436）。接手先修复该编译点，再以冻结 WPF 1.36 为准推进 UI。本轮停止后没有重建或修复。

上一轮成功的 Native AOT Release EXE 仍在下列路径。它们早于源码 WIP 中最后的 UI Automation 和模板变更。

- Full x64（15,314,944 字节） `E:\kunkun\SonarSwitch\outputs\native-aot-phase1-20261125\stage\native-preview\candidate\Full-x64\SonicRoute.exe`
- Lite x64（8,200,192 字节） `E:\kunkun\SonarSwitch\outputs\native-aot-phase1-20261125\stage\native-preview\candidate\Lite-x64\SonicRoute.exe`

目录里的 `publish.log` 已被较新的失败构建覆盖。构建入口为 `Native/Build-Product.ps1`，使用本机 `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`、MSVC 与 Windows SDK。

## 关键源码与约束

- `Native/Product/Views/` 下，`ProductViews.cs`、`ProductViewWindow.cs`、`ViewCanvas.cs` 和 `HostViews.cs` 管理界面、HWND、渲染和反馈。各页是 `AudioView.cs`、`SettingsView.cs`、`AutomationView.cs`；辅助功能入口是 `ViewAccessibility.cs`。
- Full 的进程级接入位于 `PhonePreview.cs`、`PhoneView.cs`、`PhoneClipboard.cs`、`AirPlayView.cs`。媒体复用 `SonicRoute.AndroidAudio` 与 `Native/Audio`；PC 回环保留其他旧工程原有编译分支，改动在 `shared/WindowsAudio/PcProcessLoopbackCapture.cs`。
- 保持 .NET 10 Native AOT、Win32 + Direct2D/DirectWrite、单主进程、按需图形资源与同一套 PC 音频后端。仅 Full 拥有 Android/AirPlay。候选发布图排除 WPF、WinForms、XAML 和外部 .NET 运行时，沿用现有协议。保持写操作和媒体任务独立于 UI 生命周期。WMI 权限问题沿用按需回退，一秒扫描可能遗漏短命进程。
- Preview 默认用 `%LOCALAPPDATA%\SonicRoute.NativeCandidate\v1.36-preview\{Full|Lite}\data`，互斥量、IPC 和更新目录按实例隔离。保持全局快捷键、开机启动和接收服务的显式启用机制，正式自动更新关闭。不得覆盖正式配置或安装。不引入 Legacy、Windows Link Beta、Store 包或额外常驻进程。当前工作只保存在隔离分支，未合并、推送或发布。
