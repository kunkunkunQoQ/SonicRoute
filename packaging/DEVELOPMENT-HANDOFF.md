# 开发交接

更新：2026-11-04。本文件与当前修复同一提交，后续以实际 Git HEAD、工作区状态和最新工作记录为准。

## 当前已完成

- **入站手机应用显示源手机蓝牙设备 + 固定直显 + 默认开启“智能音量”（SmartVolume）**：
  - `AndroidAudioHost.OutputsForDevice(device)` 与 `ApplicationAudio.OutputsForApp`：流转到电脑的手机应用在快速面板（`QuickPanelModernWindow`）和完整界面（`MainWindow`）中，除 PC 输出设备与源手机扬声器外，同时展示该源手机已连接的蓝牙输出设备（仍排除其他手机输出）；点击源手机扬声器或蓝牙输出均走 `ReturnAndroidToPhoneAsync` 返回手机播放。
  - `AndroidPhoneStore`：只要应用固定在 `RetainedApps` 中就直接显示在快速手机面板（`QuickPanelApps == RetainedApps`），取消固定则移除；原显示开关位置改为按应用独立持久化的“智能音量”（`DisabledSmartVolumeApps` 禁用表，所有新旧固定应用默认开启 `SmartVolume = true`）。
  - `AndroidPcmSource` / `AndroidAudioSession` / `AndroidAudioManager` / `PhoneDevicePage`：开启“智能音量”时，按感知曲线 `volume * (0.5f + 0.5f * volume)` 与峰值包络动态响度因子自动压制满幅过响信号（如 10% 滑块音量下满幅 `0.9` 输入有效增益约 `0.021`），完整界面开关切换后实时同步到活动手机流。
- **手机应用流转到电脑主面板实时音量条（峰值电平条）**：
  - `AndroidPcmSource.ReadMeterPeak()`、`AudioMeterService` 负目标 ID（`pid < 0`）与 `QuickPanelModernWindow` `AndroidRow` 共享 `PART_PeakVisual` 实时电平条，仅在面板可见、开启峰值显示且未静音时按需采样，静音或衰减归零后自动休眠。
- **既有能力保持**：
  - Lite / Legacy 保持 v1.29 的单次 `SetProcessWorkingSetSize(-1, -1)`（1500ms 延迟、关窗/预热/可见窗口门控与 30 秒限频）；Full 不裁剪工作集，保护活动手机连接、音频和虚拟屏。

## 验证与边界

- 本轮定向检查共 505 项断言全部通过：
  - `checks/AndroidUiChecks --speaker-routing`：29 项断言通过（源手机扬声器+蓝牙胶囊与下拉框、改名同步、点击蓝牙返回手机、SmartVolume 默认开启/10% 衰减/实时开关、Android 实时峰值电平条）。
  - `checks/AndroidUiChecks --device-actions`：31 项断言通过。
  - `checks/AndroidUiChecks --phone-layout`：72 项断言通过。
  - `checks/AndroidUiChecks --phone-refinements`：60 项断言通过。
  - `checks/AndroidAudioChecks --phone-demand`：21 项断言通过。
  - `checks/AndroidAudioChecks --device-actions`：39 项断言通过。
  - `checks/AndroidAudioChecks --phone-library`：22 项断言通过。
  - `packaging/Tests/PanelChecks --panel-performance`（Full / Lite / net48）：77 × 3 = 231 项断言通过。
- 已构建 Full/Lite x64 ReadyToRun 单 EXE 及 Legacy/net48 原 13 文件 ZIP，未启用程序压缩。未连接真机，未做真机听音、整版 CPU 基准或 ARM64 验收。
- 本地当前交付入口在父容器的“测试版”目录：`测试版/SonicRoute-v1.30-x64.exe`（及 `测试版/Full-x64/SonicRoute.exe`）、`测试版/SonicRoute-v1.30-Lite-x64.exe`（及 `测试版/Lite-x64/SonicRoute.exe`）、`测试版/SonicRoute-v1.30-Legacy-x64.zip`（及 `测试版/Legacy-x64/SonicRoute.exe`）均已更新至最新。普通“运行版”未覆盖。

## 给下一位 AI

1. 先读本地 `AGENTS.md`、父容器 `AI_CONTEXT.md`、本文件和 [WORKLOG.md](WORKLOG.md)，检查 `git status`、分支和远端；不要假定旧记录中的分支、路径或程序版本仍有效。唯一当前源码在父容器“当前项目”，旧 Lab 仅作归档参考。
2. 本轮要求已完成，后续开发范围由用户的新请求决定。开发时优先控制闲时 CPU 和加载成本，复用事件、服务、控件与缓存；关窗只撤销 UI 自己的工作，保留活动音频、手机连接和虚拟屏。
3. 每轮向 [WORKLOG.md](WORKLOG.md) 追加用户要求、改动、原因、必要验证结果、实际运行的构建/提交和未验证项；重要取舍写清依据。结束前更新本文件的“当前已完成/后续”状态，保留历史记录，不把计划当成已实现。
4. 在本地父容器 `integration-docs` 保存本轮交付收据和交接摘要，并更新父容器 `AI_CONTEXT.md` 的最新入口；维护方便下一位 AI 继续接手的记录。公开仓库不提交私有配置、ADB 凭据、设备连接地址、日志或运行包。
5. 不恢复已撤销的“电脑应用拖出后切到其窗口”功能。Store 固定 Full，Lite/Legacy 不引入手机组件。

## 定向检查入口

- `checks/AndroidUiChecks` 的 `--speaker-routing` / `--device-actions` / `--phone-layout` / `--phone-refinements`：验证源手机扬声器+蓝牙路由、智能音量开关、固定直显与主面板 Android 实时峰值电平条。
- `checks/AndroidAudioChecks` 的 `--phone-demand` / `--device-actions` / `--phone-library`：验证 `AndroidPcmSource` SIMD/标量峰值计算、`AndroidPhoneStore` 固定直显与 SmartVolume 持久化。
- `packaging/Tests/PanelChecks` 的 `--panel-performance` / `--ui-memory`：按 Full、Lite 和 net48（`-p:PanelTestFramework=net48`）分别使用独立 `ArtifactsPath` 串行构建运行。

未发 Release、tag 或商店包。源码按用户要求提交并推送 GitHub，是否推送成功以远端核验和本地最新收据为准。
