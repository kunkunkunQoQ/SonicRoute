# 开发工作记录

后续AI每轮追加记录，并同步 `DEVELOPMENT-HANDOFF.md` 和本地父容器的当前交接。记录区分已实现、已验证、待验证和明确不做；不能用模拟检查代替真实CPU、音频或设备验收。

## 2026-11-04：恢复轻量回收、手机扬声器选项

- 用户要求：Lite/Legacy按1.29方式回收；手机扬声器使用设备名；入站手机应用可切回其手机扬声器；完成后交接，并提交GitHub。
- 基线：`e8eadf5`（前一轮本地内存实现），当时远端master为 `1bb8cc2`。
- 实现：Lite/Legacy取消显式全代GC，使用v1.29单次原生请求并保留门控/限频；统一设备名；共用入站输出候选和异步返回手机路径，防止误把手机虚拟设备ID传入Windows播放器。完整界面、快速面板、改名和拖拽提示同步。PC原路由路径与Full活动服务所有权保留。
- 必要检查：Full13、Lite16、Legacy16、手机路由13，共58项通过。Full没有原生收缩入口，Lite/Legacy回调不增加全代收集；源手机/其他手机和邻接应用隔离通过。手机部分无真机访问，不宣称CPU降幅或声音验收。
- 构建：Full/Lite x64单EXE、Legacy13文件ZIP成功。Full发布完成后顶层旧包被用户打开，替换被文件占用挡住；保留运行进程，新Full固定在Full-x64目录。相应脚本补齐重命名EXE与目标资产的运行保护。Lite、Legacy已更新固定测试包。
- 测试修正：初次Startup隔离使用派生Application加载原BAML失败，改为复用现有测试资源加载器；一项改名检查揭示已展开设备选项文字未更新，修复后通过。失败过程保留在本地证据，不作发布验收。
- 未验证：用户实际CPU/重开体验、真机手机扬声器听音、长时与ARM64。普通运行版未覆盖，未发Release/tag/商店包；GitHub推送结果由最新Git远端和本地交接收据记录。

## 2026-11-04：手机应用流转到电脑主面板实时音量条（峰值电平条）显示

- 用户要求：让手机应用流转到电脑主面板（`QuickPanelModernWindow`“应用音量”列表）时，也能像电脑原生应用一样在音量滑块内实时显示音量条（`PART_PeakVisual` 峰值电平条）。
- 基线：`0772345`（本地 `codex/memory-reclaim-20261104` 与远端 `master` 同步）。
- 实现：
  - `SonicRoute.AndroidAudio/AndroidPcmSource.cs`：保留原始 `LastPeak` 供既有 SIMD/诊断检查使用；新增音量缩放前的实时电平峰值采样（`_meterUnreadPeak`、`_meterActivePeak`、`_lastWriteTick`、`_lastNonSilentWriteTick`、`_meterWaiting`）、`ReadMeterPeak()` 与静态事件 `PeakActivity`。仅当 `AudioMeterService` 处于等待状态（`_meterWaiting == 1`）且收到非静音 PCM 写入时通过 `Interlocked.Exchange` 触发单次唤醒，静音帧或连续活跃帧不重复唤醒。
  - `SonicRoute.Core/AudioMeterService.cs`：新增可选外部峰值读取委托 `externalReader` 与 `WakeExternal()`，允许负目标 ID（`pid < 0`）代表 Android 外部音频流，同时将正 PID（`pid > 0`）保留给 Windows COM `MeterGraph`。仅存在或增删 Android 行时不创建、不重建 Windows COM 会话枚举图；可见负 ID 与原生应用共用同一套 33ms 采样周期、`Attack = 0.65f` / `Release = 0.15f` 平滑、`0.001f` 量化、`PeaksUpdated` 快照发布，以及无活动且衰减归零后的 `Timeout.Infinite` 休眠。
  - `SonicRoute/QuickPanelModernWindow.xaml.cs` 与 `SonicRoute/QuickPanelModernWindow.AndroidAudio.cs`：提取公共 `UpdatePeakVisual` 方法供 PC `AppRow` 与手机 `AndroidRow` 复用；在 `CreateAndroidRow` 中绑定 `RowSliderStyle` 模板的 `PART_PeakVisual` 与 `ScaleTransform(0, 1)`，分配互不冲突的负数 `MeterId`，接入视口可见行裁剪（`_visibleAndroidMeterRows`）、滑块音量变更即时重算长度、静音/取消静音及总静音同步、面板关闭/禁用电平表时取消 `PeakActivity` 订阅并清零。`#else`（`!SONICROUTE_PHONE`）提供零开销空实现，保证 Lite 与 Legacy（`net48`）不引入任何手机组件。
- 必要检查：
  - `checks/AndroidUiChecks`（`--speaker-routing`）：26 项断言通过（含新增 13 项 Android 实时峰值电平条断言：负 `MeterId` 隔离、`PART_PeakVisual` 模板与 `Theme.AccentPeak` 绑定、静音/有声 PCM 唤醒合并、`AudioMeterService` 按需采样可见目标、滑块音量缩放、静音隐藏与移除采样目标、关闭电平表清理）。
  - `checks/AndroidAudioChecks`（`--phone-demand`）：21 项断言通过（SIMD/标量峰值语义与按需加载未受影响）。
  - `packaging/Tests/MonitoringChecks`（`net10.0-windows`）：33 项断言通过（`AudioMeterService` 事件驱动休眠/唤醒与 COM 会话图生命周期）。
  - `packaging/Tests/PanelChecks`（`--panel-performance`，覆盖 `Full`、`Lite`、`Legacy`/`net48`）：77 × 3 = 231 项断言通过。
- 构建：已构建 Full/Lite x64 ReadyToRun 单 EXE 与 Legacy/net48 13 文件 ZIP（未启用压缩）。构建时用户正运行 `测试版/Full-x64/SonicRoute.exe`（PID 10636），按规范未结束用户进程；新版 Full 已写入顶层 `测试版/SonicRoute-v1.30-x64.exe` 及 `outputs/android-peak-meter-20261104/editions/Full-x64/SonicRoute.exe`；Lite 与 Legacy 已直接更新至 `测试版/`；普通 `运行版/SonicRoute.exe` 未覆盖。
- 未验证：真机流转下快速面板实时峰值条的肉眼观感与听音验收、整版长时 CPU 占用与 ARM64。未发 Release/tag/商店包。

## 2026-11-04：入站手机应用显示源手机蓝牙设备、固定直显与默认智能音量

- 用户要求：
  1. 让快速面板主面板（`QuickPanelModernWindow`）和完整界面（`MainWindow`）中流转到电脑的手机应用，在输出设备列表中除源手机扬声器外也显示该源手机已连接的蓝牙输出设备，点击源手机扬声器或蓝牙设备均返回手机播放。
  2. 完整界面手机设备页（`PhoneDevicePage`）“快速面板显示应用”列表：只要固定（保留在列表中）就直接显示在快速手机面板（`PhoneAudioWindow`），取消固定则移除，无需额外显示开关。
  3. 将完整界面每个固定应用右侧的开关改为“智能音量”（`SmartVolume`），默认开启，开启时自动将手机流转音频控制在舒适响度范围（解决开 10% 音量仍过大的问题）。
- 基线：`c6d418c`（本地 `codex/memory-reclaim-20261104` 与远端 `master` 同步）。
- 实现：
  - **源手机蓝牙 + 扬声器路由**：
    - `SonicRoute/AndroidAudioHost.cs`：新增 `OutputsForDevice(AndroidDevice device)`，基于 `GetDropZonesForDevice(device)` 返回该源手机的扬声器与全部已连接蓝牙输出设备（`AudioDeviceInfo`）；在 `AddAudioAsync` 启动入站流转时按 3 秒限频顺带触发一次非阻塞 `ProbeOutputsIfConnectedAsync([device])`，确保尚未打开快速手机面板时也能探测源手机蓝牙输出。
    - `SonicRoute/ApplicationAudio.cs`：`OutputsForApp` 对外部 Android 应用追加 `AndroidAudioHost.OutputsForDevice(source.Device)`（仍排除其他手机输出）；将 `IsSourceSpeaker` 扩展为 `IsSourcePhoneOutput`，使快速面板或完整界面选中该源手机的扬声器或蓝牙设备时均走异步 `ReturnAndroidToPhoneAsync` 返回手机，避免把手机虚拟设备 ID 传入 Windows WASAPI 播放器。
    - `SonicRoute/QuickPanelModernWindow.AndroidAudio.cs`：`RefreshAndroidRow` 檢測已展开的 `AndroidRow` 上源手机输出集合（`phoneOutputsChanged`）变化并即时刷新设备胶囊按钮。
  - **固定即显示在快速手机面板 & 默认开启“智能音量”**：
    - `SonicRoute.AndroidAudio/AndroidPhoneStore.cs`：`AndroidPhoneProfile.QuickPanelApps` 始终等于 `RetainedApps`（固定即显示，取消固定即移除）；将原 `HiddenQuickPanelApps` 替换为按禁用列表持久化的 `DisabledSmartVolumeApps`（JSON 字段 `DisabledSmartVolumePackages`），使所有新旧固定应用默认开启智能音量（`IsSmartVolumeEnabled(device, package) == true`），并提供 `SetSmartVolume(package, enabled)`。
    - `SonicRoute.AndroidAudio/AndroidModels.cs`、`AndroidPcmSource.cs`、`AndroidAudioSession.cs`、`AndroidAudioManager.cs`：`AndroidAudioState` 新增 `bool SmartVolume = true`；`AndroidPcmSource` 在 `Write` 中维护峰值包络 `_smartEnvelope`（快攻 `0.35f`、慢释 `0.04f`），在 `ComputeEffectiveGain()` 中当 `SmartVolume` 开启时结合感知音量曲线 `volume * (0.5f + 0.5f * volume)` 与动态响度范围因子 `Clamp(0.35f / Pow(Max(env, 0.02f), 0.70f), 0.20f, 1.40f)` 计算实际增益（例如满幅 `0.9` 信号在 10% 滑块音量下有效增益约 `0.021` 而非线性 `0.10`）；`AndroidAudioHost.OnPhoneStoreChanged` 实时调用 `Sources.SyncSmartVolume(Phone.IsSmartVolumeEnabled)` 同步活动会话。
    - `SonicRoute/PhoneDevicePage.xaml` 与 `PhoneDevicePage.xaml.cs`：将“快速面板显示应用”行右侧 `AutomationSwitch` 绑定改为 `SmartVolume`（`AutomationProperties.Name="智能音量"`、`ToolTip="智能音量"`），副标题更新为“固定在此处的应用会显示在快速手机面板中；右侧开关控制智能音量（默认开启，自动平衡音量范围）。”
- 必要检查：
  - `checks/AndroidUiChecks`：
    - `--speaker-routing`：29 项断言通过（源手机扬声器+蓝牙胶囊与下拉框展示、改名同步、点击蓝牙胶囊返回手机、SmartVolume 默认开启/10% 满幅信号衰减/实时开关同步、实时峰值条）。
    - `--device-actions`：31 项断言通过。
    - `--phone-layout`：72 项断言通过（含 `PhoneDevicePage` 智能音量开关默认开启、切换后保持 `QuickPanelApps` 显示等）。
    - `--phone-refinements`：60 项断言通过。
  - `checks/AndroidAudioChecks`：
    - `--phone-demand`：21 项断言通过（`new AndroidPcmSource()` 原始实例保持线性增益，SIMD/标量精确采样断言全过）。
    - `--device-actions`：39 项断言通过。
    - `--phone-library`：22 项断言通过（v1 迁移默认开启 SmartVolume、固定直显、按手机独立持久化 SmartVolume 状态）。
  - `packaging/Tests/PanelChecks`（`--panel-performance`，覆盖 `Full`、`Lite`、`Legacy`/`net48`）：77 × 3 = 231 项断言通过。
- 构建：已重新构建 Full/Lite x64 ReadyToRun 单 EXE 与 Legacy/net48 13 文件 ZIP（无运行进程占用，`测试版/Full-x64/SonicRoute.exe`、`测试版/SonicRoute-v1.30-x64.exe`、`测试版/Lite-x64/SonicRoute.exe`、`测试版/SonicRoute-v1.30-Lite-x64.exe`、`测试版/Legacy-x64/`、`测试版/SonicRoute-v1.30-Legacy-x64.zip` 全部更新到最新；普通 `运行版/SonicRoute.exe` 未覆盖）。
- 未验证：真机蓝牙连接下入站手机应用切回手机蓝牙的真机听音、真机 10% 智能音量主观听感、整版长时 CPU 占用与 ARM64。未发 Release/tag/商店包。
