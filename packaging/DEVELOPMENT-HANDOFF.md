# 开发交接

更新：2026-11-04。本文件与当前修复同一提交，后续以实际 Git HEAD、工作区状态和最新工作记录为准。

## 当前已完成

- **手机应用流转到电脑主面板实时音量条（峰值电平条）**：
  - `AndroidPcmSource` 在 `Write` 中记录音量缩放前的信号峰值并暴露 `ReadMeterPeak()` 与 `PeakActivity` 按需唤醒事件（仅在电平表等待唤醒且写入非静音样本时触发一次，静音或连续帧零重复唤醒），同时保留原始 `LastPeak` 语义。
  - `AudioMeterService` 支持负目标 ID（`pid < 0`）的外部峰值读取委托 `externalReader` 与 `WakeExternal()`，并将正 Windows PID 与负外部 ID 分离：仅监控或变更 Android 行时完全不创建、不枚举 Windows COM `MeterGraph`；可见 Android 行与原生 PC 应用共享同一套 33ms 采样、`Attack = 0.65f` / `Release = 0.15f` 平滑、`0.001f` 量化及静默归零后 `Timeout.Infinite` 休眠。
  - `QuickPanelModernWindow` 提取公共 `UpdatePeakVisual`，在 `AndroidRow` 中绑定 `RowSliderStyle` 模板内的 `PART_PeakVisual` 与 `ScaleTransform(0, 1)`，接入视口可见行过滤（`_visibleAndroidMeterRows`）、音量滑块拖动即时缩放峰值条、静音立即隐藏并移出活动采样目标、面板隐藏/关闭或设置关闭 `ShowAppPeakMeter` 时退订事件并清零。Lite 与 Legacy（`net48`）通过 `#if SONICROUTE_PHONE` 隔离，不引入手机组件。
- **既有能力保持**：
  - Lite / Legacy 保持 v1.29 的单次 `SetProcessWorkingSetSize(-1, -1)`（1500ms 延迟、关窗/预热/可见窗口门控与 30 秒限频）；Full 不裁剪工作集，保护活动手机连接、音频和虚拟屏。
  - 手机输出名称统一使用保存的设备名（如“小米13 扬声器”），入站手机应用在快速面板和完整界面均可选所属手机扬声器以返回手机播放。

## 验证与边界

- 本轮定向检查全部通过：
  - `checks/AndroidUiChecks --speaker-routing`：26 项断言通过（含 13 项 Android 实时峰值电平条与 13 项手机扬声器路由断言）。
  - `checks/AndroidAudioChecks --phone-demand`：21 项断言通过。
  - `packaging/Tests/MonitoringChecks --framework net10.0-windows`：33 项断言通过。
  - `packaging/Tests/PanelChecks --panel-performance`（Full / Lite / net48）：77 × 3 = 231 项断言通过。
- 已构建 Full/Lite x64 ReadyToRun 单 EXE 及 Legacy/net48 原 13 文件 ZIP，未启用程序压缩。未连接真机，未做真机听音、整版 CPU 基准或 ARM64 验收。
- 本地当前交付入口在父容器的“测试版”目录。构建时 `测试版/Full-x64/SonicRoute.exe` 正由用户运行（PID 10636），未结束用户进程；**包含本轮手机实时音量条的新版 Full 为顶层 `测试版/SonicRoute-v1.30-x64.exe`**（以及 `outputs/android-peak-meter-20261104/editions/Full-x64/SonicRoute.exe`）。Lite 使用 `测试版/SonicRoute-v1.30-Lite-x64.exe`（或 `测试版/Lite-x64/SonicRoute.exe`），Legacy 使用 `测试版/SonicRoute-v1.30-Legacy-x64.zip`（或 `测试版/Legacy-x64/SonicRoute.exe`）。启动新 Full 前请先从托盘退出正在运行的旧 Full 实例，避免单实例互斥只激活旧进程；退出旧实例后也可将 `SonicRoute-v1.30-x64.exe` 覆盖回 `Full-x64/SonicRoute.exe`。普通“运行版”未覆盖。

## 给下一位 AI

1. 先读本地 `AGENTS.md`、父容器 `AI_CONTEXT.md`、本文件和 [WORKLOG.md](WORKLOG.md)，检查 `git status`、分支和远端；不要假定旧记录中的分支、路径或程序版本仍有效。唯一当前源码在父容器“当前项目”，旧 Lab 仅作归档参考。
2. 本轮要求已完成，后续开发范围由用户的新请求决定。开发时优先控制闲时 CPU 和加载成本，复用事件、服务、控件与缓存；关窗只撤销 UI 自己的工作，保留活动音频、手机连接和虚拟屏。
3. 每轮向 [WORKLOG.md](WORKLOG.md) 追加用户要求、改动、原因、必要验证结果、实际运行的构建/提交和未验证项；重要取舍写清依据。结束前更新本文件的“当前已完成/后续”状态，保留历史记录，不把计划当成已实现。
4. 在本地父容器 `integration-docs` 保存本轮交付收据和交接摘要，并更新父容器 `AI_CONTEXT.md` 的最新入口；维护方便下一位 AI 继续接手的记录。公开仓库不提交私有配置、ADB 凭据、设备连接地址、日志或运行包。
5. 不恢复已撤销的“电脑应用拖出后切到其窗口”功能。Store 固定 Full，Lite/Legacy 不引入手机组件。

## 定向检查入口

- `checks/AndroidUiChecks` 的 `--speaker-routing`：使用模拟设备与会话验证手机扬声器路由及主面板 Android 应用行实时峰值电平条（`PART_PeakVisual`）。
- `checks/AndroidAudioChecks` 的 `--phone-demand`：验证 `AndroidPcmSource` SIMD/标量峰值计算与按需加载。
- `packaging/Tests/MonitoringChecks`（`--framework net10.0-windows`）：验证 `AudioMeterService` 事件驱动唤醒与会话图生命周期（若机器无 `pwsh.exe`，可在临时 PATH 目录放置指向 `powershell.exe` 的 `pwsh.cmd`）。
- `packaging/Tests/PanelChecks` 的 `--panel-performance` / `--ui-memory`：按 Full、Lite 和 net48 分别使用独立 `ArtifactsPath` 串行构建运行。

未发 Release、tag 或商店包。源码按用户要求提交并推送 GitHub，是否推送成功以远端核验和本地最新收据为准。
