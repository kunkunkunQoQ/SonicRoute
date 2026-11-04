# 开发工作记录

后续AI每轮追加记录，并同步 `DEVELOPMENT-HANDOFF.md` 和本地父容器的当前交接。记录区分已实现、已验证、待验证和明确不做；不能用模拟检查代替真实CPU、音频或设备验收。

## 2026-11-04：v1.30 正式发布与 Full 商店包

- 用户授权正式发布 1.30 并构建微软商店包，随后明确 Wiki/README 由其他 AI 更新、Lite/Legacy 不显示“开源许可”链接。本轮生成过的 README 与生成源已撤回，最终内容相对接手基线没有变化，Wiki 未改；独立发行说明保存于 `docs/release-v1.30.md`。
- 修正发布工作流中残留的 Full ZIP 路径、自动发行说明中的 ZIP 升级提示及相关测试/报告文案，使其符合现有单 EXE 布局，没有修改本轮产品功能。
- 发行标签 `v1.30` 固定绑定干净源码提交 `1079f743618981972dfb620ddae47ecec82ad2fd`。构建通过：Full/Lite x64、ARM64 和 Legacy x64；.NET 10.0.402 SDK；停止生成 NET8。开发工作区已有的 SmartVolume 曲线调参保留且排除于正式构建。
- 必要验证：发布流程 78、Full 17、Lite 13、Full 真实包暂存/回滚 3、NET10/net48 更新器各 23、实际工作进程 33 组通过。工作进程首次受工作目录非规范 ACL 影响，未能添加测试拒绝规则；原失败留档，转到规范 ACL 的新 TEMP 隔离目录重跑 33 组通过，未调整项目权限。以上是断言/场景组，不是整版性能结果。没有新增手机操作、原生 ARM64 或商店安装验收。
- GitHub 已发布稳定 Latest，7 个附件全部下载回读、SHA-256 与本地一致；上传一次网络超时后按既有流程重试成功。MSIX 不纳入便携版 7 个附件。
- Full x64 商店 MSIX 版本 `1.30.0.0`，沿用已有 Identity/Publisher，MakePri/MakeAppx 通过；包内 EXE 与正式 Full x64 资产相同。MSIX 为未签名交付，未安装、未上传或提交商店。正常运行入口已更新，旧测试进程与共享 ADB 保留，未启动新包。
- 发行与商店收据见本地父容器 `outputs/official-release-20261104/` 和 `integration-docs/35-RELEASE-1.30.md`。本记录属于发行后的文档提交，不改变 `v1.30` 标签与已校验资产。

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


## 2026-11-04：明确手机输出并保留主面板接管

- 用户更正：电脑应用到手机、手机应用收到电脑后再选手机输出，两条路径的扬声器都不能误走手机默认蓝牙；选扬声器/蓝牙继续受主面板音量、静音、智能音量控制，退出才交还手机。拖回左侧手机面板复用退出。此要求取代上轮“点源手机输出即返回手机”的设计。
- 原因与实现：
  - `AudioOutputBridge.java`：普通 `setPreferredDevice` 被接受并不表示实际输出已切换；小米13有其他媒体在蓝牙播放时，深缓冲媒体输出仍会合并到蓝牙。为自己的 AudioTrack 请求低延迟输出、注册会话范围的 render 策略，按真实硬件 ID 验证；未验证时保持静音，路由回调和设备回调推进状态，限定失败超时；撤销盲选唯一蓝牙的兜底。未修改手机全局媒体策略。
  - `AndroidPcmOutput.cs`：提取两条路径共用的帮助进程/ADB 转发/PCM 连接，严格确认目标，忽略快速切换后滞后的旧路由通知；电脑进程保留原回环捕获和静音租约。入站手机应用通过同一接口渲染原 PCM 源的处理后数据，保留主面板控制；同手机扬声器/蓝牙切换复用连接、捕获与解码。
  - `AndroidAudioSession.cs`：手机输出不交给 WASAPI，不伪造 Windows PID；只有原生同时播放标记变化才重建捕获以消除默认设备上的未受控副本，保留源对象和控制偏好，切回 PC 恢复原同时播放偏好。输出错误纳入现有失败/清理流程。
  - `ApplicationAudio.cs`、`QuickPanelModernWindow.AndroidAudio.cs`：选择手机设备只改 renderer，退出与拖回手机面板共用原返回操作，不在输出选择时关闭虚拟屏。
- 性能：按 PCM 到达事件处理，缓冲复用；没有新增闲时扫描或周期清理。保持手机播放受电脑控制需要活动 PCM 上行约 192,000 B/s，低延迟轨道也有必要的活动开销，未宣称整版 CPU/内存降低。关闭只清理自己的帮助进程、端口和文件，不重启/结束共享 ADB。
- 保留原改动：接手已有 `AndroidPcmSource.cs` SmartVolume 调参，剔除新增 PCM 通知后内容与接手指纹一致。本次提交只纳入新增通知，不纳入已有调参；本地运行包包含该调参。
- 必要检查：`AndroidUiChecks --speaker-routing` 31、`AndroidAudioChecks --explicit-output` 9、`--phone-demand` 21、`--device-actions` 39、真机 `--explicit-output-live` 9、真机 `--pc-to-phone-live` 9，共 118 项通过。真实手机为小米13、波点音乐和已连接蓝牙；分别有真实非静音 Opus/PCM 及 Windows 进程回环音频，均验证扬声器→蓝牙→扬声器的实际类型，入站路径另验证静音与仍受控退出。
- 诊断过程边界：首次 native 静音探针成功不足以覆盖其他媒体正在蓝牙播放的场景；电脑到手机真机检查暴露共享深缓冲路由问题，低延迟输出修复后通过。滞后的路由回调还暴露检查需等待当前目标，已同时限定生产确认逻辑和检查条件。音乐暂停时的静音不能当作声音验证通过；最终真机检查明确播放前置条件后重新通过。早期探针/中间构建不作为最终交付证据。
- 构建：只更新固定 Full x64 ReadyToRun 单 EXE 入口，无程序压缩、无新版本目录。普通运行版和 Lite/Legacy 未动；Store 保持 Full。原运行的旧 Full 由用户从托盘退出后替换，未结束用户进程。
- 未测：人工听感、整版长期 CPU/内存、其他 Android 机型/版本及 ARM64。没有 Release/tag/Store 提交。本地证据及源码/包/远端提交的最终状态见父容器 `integration-docs/33-EXPLICIT-PHONE-OUTPUT.md` 和 `explicit-phone-output-receipt.json`。


## 2026-11-04：关于页开源许可文字链接

- 用户要求：将“许可与第三方声明”按钮改成“开源许可”文字链接，放在作者主页旁边。
- 实现：移除独立按钮，在原作者主页链接行 GitHub 后追加相同分隔符及 Hyperlink，复用原字体和 Theme.Accent 动态颜色。仅 Full 显示，加载时防止重复插入；点击继续按需读取原内嵌许可并打开文本，不在页面加载时读取许可内容或启动连接。
- 验证：Full x64 ReadyToRun 单 EXE 发布构建通过，检查 XAML 事件与 Hyperlink 类型；未扩展手机真机/性能测试，人工视觉与点击可在新版中确认。
- 交付：固定 `测试版/Full-x64/SonicRoute.exe` 已更新。顶层 `测试版/SonicRoute-v1.30-x64.exe` 正由用户运行，保留该文件与会话，未结束用户进程；当前新版入口以本地 34 号记录和收据为准。Lite/Legacy 和普通运行版未构建或替换，原有 SmartVolume 调参保留。
- 同步：本轮 UI 与交接随同提交到 GitHub；实际提交及文件 Hash 见父容器 `integration-docs/about-license-link-receipt.json`。没有 Release/tag/Store 发布。


## 2026-11-04：核对并补齐 v1.29 -> v1.30 发行说明与更新记录

- 用户要求：调用子代理核对 `v1.29` 到 `v1.30` 的 GitHub Releases、Wiki 更新记录与真实代码提交差异，并将遗漏的真实更新内容补充到发布说明与版本历史文档中。
- 核对结果（`v1.29` `18e11a6` -> `v1.30` `1079f74`，共 37 个提交、340 个文件变更）：
  - 原 `docs/release-v1.30.md`、GitHub Release `v1.30` 正文及 `SonicRoute.wiki/09-版本历史.md` 主要记录了 Full 手机集成改动，遗漏了 `3c0fcfc..29a46d8` 期间合入的桌面端功能与体验改进，以及 `README.ai.md` 中未同步的 `v1.30` 版本归档条目与 Store Full 打包说明。
- 补充内容：
  1. `docs/release-v1.30.md`、GitHub Release `v1.30` 在线正文（Release ID `433710996`）与 `SonicRoute.wiki/09-版本历史.md`：
     - 主题页新增快速面板应用行布局选项（紧凑单行 / 完整双行布局切换，以及紧凑/完整模式下输出设备名显示开关）、加大应用音量滑块垂直拖拽命中区域、修复滚动条显隐引起的宽度跳变。
     - 自动化新增 `SetCurrentAppInputMute`（37）与 `ToggleCurrentAppInputMute`（38）两项当前前台应用麦克风静音动作（共 39 种动作）、动作选择器按类别分组展示、执行历史多目标确认统计（“已确认 X / Y 个目标”）及一次性定时触发状态提示。
     - 概览页与应用页音量卡片静音按钮与状态标签对齐、精简冗余“当前：”前缀、移除主窗口与快速面板外层 `DropShadowEffect` 阴影、统一 9 语言“播放 / 录音”术语、Full 版关于页 GitHub 旁“开源许可”文字链接。
     - 配置导入前结构化预览确认弹窗（`MainWindow.ImportPreview.cs`）、高频滑块拖动合并写入器（`CoalescedVolumeWriter`）、快照异步配置落盘（`ConfigService.QueueSave`）、更新器下载与校验支持取消。
  2. `docs/readme/generate.cjs` 与 `README.ai.md`：修正 Store 打包说明（复用 NET10 Full x64 EXE），在 `VERSION_ARCHIVE_AND_CHANGELOG` 补充 `v1.30` 完整变更记录，并通过 `node docs/readme/generate.cjs --check` 校验。
  3. 修正 `测试版/README.md` 历史段落中残留的“点击源手机扬声器或蓝牙返回手机”旧表述，明确自 `ccc4254` 起切换至源手机输出继续受主面板控制、退出或拖回左侧手机面板才交还手机。
- 边界：仅更新文档与在线 Release 说明正文，不移动 `v1.30` 标签（`1079f743618981972dfb620ddae47ecec82ad2fd`），不改动已发布的 7 个 Release 附件，保留开发工作区未提交的 `SonicRoute.AndroidAudio/AndroidPcmSource.cs` SmartVolume 调参，不结束任何运行中进程或共享 ADB。

