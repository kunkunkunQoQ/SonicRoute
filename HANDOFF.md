# SonicRoute 1.37 应用画布开发交接文档（HANDOFF）

> **交接时间**：2026-11-27  
> **交接目标**：由 Codex 接手当前主工作区继续后续开发、真机验收与收尾  
> **说明**：本文档完全依据当前主工作区实际源码、Git 状态与实测产物编写；所有未实机验证项均已显式标注，未将未验证内容标记为通过。

---

## 1. 工作区状态

### 1.1 路径、分支与版本

- **容器根目录**：`E:\kunkun\SonarSwitch`（含 `AGENTS.md`、`AI_CONTEXT.md`、`LOCAL_WORKSPACE.md`、`运行版\SonicRoute.exe` 与 `outputs\`）
- **当前主工作区（Git 根目录）**：`E:\kunkun\SonarSwitch\当前项目`
- **解决方案**：`E:\kunkun\SonarSwitch\当前项目\SonicRoute.Native.slnx`
- **当前分支**：`codex/native-legacy-ui-repair-20261125`
- **HEAD 提交**：`be5a8cc970e9306c4c582faca4fff8d308be4c98`（`2026-11-26 23:15:23 +0800`，`Keep parallel Native AOT restores on common NuGet locks`）
- **WPF 1.36 比较基准**：本地标签 `v1.36`（`3d9f9e1083b228f666e30d97e3c8edfce5e6654a`）
- **当前版本号**：`1.37`（`SonicRouteNumericVersion = 1.37.0.0`，定义于 `build/Version.props`，**本轮未修改**）

### 1.2 Git 未提交修改总览（共 33 个已修改文件 + 5 个新增源码文件 + 本 `HANDOFF.md`）

工作区当前**全部修改均未提交（staged 为空）**，包含两类变更，请勿回滚或混淆：

#### A. 本轮前已存在于工作区的未提交变更（必须保留）
- **「连接设备 / 配置设备」导航迁移与手机设备页导航重构**：
  - `Native/Product/Views/PhoneDevicePageView.cs`（M）
  - `Native/Product/Views/PhoneDevicePageView.Navigation.cs`（??，115 行）
  - `Native/Product/Views/PhoneAudioWindowView.cs`（M）
  - `Native/Product/Views/IViewPage.cs`（M，含 `MainNavTab.Devices`）
  - `Native/Product/Views/HostViews.cs`（M，含 `OpenPhoneManagement(string? profileId, bool airPlay)`、`CreateMainPage` 注册 `MainNavTab.Devices`）
  - `Native/Product/Views/MainWindowShell.cs`（M，含 `nav.Devices` 导航与 `GetPage` 复用，同时含应用页紧凑内边距与弹窗关闭钩子）
- **主题切换分段控件与杂项微调**：
  - `Native/Product/Ui/UiSegmentedSwitch.cs`（??，54 行）
  - `Native/Product/Views/MainWindowShell.ThemeSwitch.cs`（M）
  - `Native/Product/Views/OsdWindow.cs`（M）
  - `Native/Product/Views/OutputVolumesPopupWindow.cs`（M）

#### B. 本轮「应用」可视化路由画布重设计与定向修复变更
- **新增核心文件**：
  - `Native/Product/Views/AppsCanvasEngine.cs`（??，784 行）
  - `Native/Product/Views/AppsPageView.Canvas.cs`（??，2594 行）
  - `Native/Product/Views/OutputConnectionPopupWindow.cs`（??，461 行）
- **修改核心文件**：
  - `Native/Product/Views/AppsPageView.cs`（M，1758 行）
  - `Native/Product/Views/PhonePreview.Playback.cs`（M，177 行，+1 行 `Notify()`）
  - `Native/Product/Views/HostViews.cs`（M，扩展 `EndpointVolumePending(Flow, id)` / `QueueEndpointVolume(Flow, id, level)` 并转发 `mainView?.ConfirmEndpointVolume`）
  - `Native/Product/Views/IViewPage.cs`（M，新增 `HasOpenModal`、`ClosePopups()`、`OnThemeChanged(UiThemePalette)`）
  - `Native/Product/Views/MainWindowShell.cs`（M，应用页更紧凑的 `padX=14f / padTop=10f / padBottom=10f`、`ClosePopups` 联动、`ConfirmEndpointVolume` 转发）
  - `Native/Product/HostCommands.cs`（M，`_endpointVolumeWriter` 键由 `string` 扩展为 `EndpointVolumeKey(Flow, Id)` 以支持录音设备音量节流写入）
  - `Native/Product/Ui/D2dRenderer.cs`（M，908 行，新增虚线笔刷 `DashedStrokeStyle`、缓存贝塞尔几何 `CreateCubicBezierGeometry`/`ReleaseGeometry`、`DrawGeometry`/`DrawCubicBezier`、`D2dMatrix3x2.ScaleTranslate`/`Multiply` 与 `PushTransform` 叠加）
  - `Native/Product/Ui/UiIcons.cs`（M，157 行，新增 `SpeakerMuted`、`Filter`、`FitCanvas`、`AutoArrange`、`Minus`、`MicMuted` 图标路径）
  - `Native/Product/Ui/UiPrimitives.cs`（M，653 行，`UiHitMap` 新增 `PushTransform`/`PopTransform`/`MapRect`/`VisiblePart`，`DrawVolumeSlider` 支持 `showPeak: false` 与 `compact`）
  - `Native/Product/Audio/ProductAudio.cs`（M，429 行，新增 `GetMirrorGain`/`SetMirrorGain`）
  - `Native/Product/Routing/DesktopMirrorPlayer.cs`（M，159 行，新增每转发目标无锁 Q15 PCM 独立增益/静音）
  - `Native/Product/Routing/LocalAppOutputMirrors.Native.cs`（M，103 行，新增 `MirrorGainState`、`MirrorGains` 表、`GetSinkGain`/`SetSinkGain`、`ApplyInitialGain`/`OnRouteStopped`）
  - `SonicRoute/LocalAppOutputMirrors.cs`（M，503 行，新增 `partial void ApplyInitialGain` / `OnRouteStopped` 挂载点）
  - `SonicRoute.AndroidAudio/IMultiOutputAudioSession.cs`（M，9 行，新增 `GetMirrorGain`/`SetMirrorGain` 接口）
  - `SonicRoute.AndroidAudio/PcmOutputMirrors.cs`（M，185 行，新增按目标 ID 隔离的转发增益/静音叠加计算）
  - `SonicRoute.AndroidAudio/AndroidAudioSession.cs`（M，773 行，实现转发增益转发至 `_mirrors`）
  - `SonicRoute.AndroidAudio/AirPlay/AirPlayAudioOutput.cs`（M，547 行，实现转发增益转发至 `_mirrors`）
  - `SonicRoute.Core/ConfigService.cs`（M，786 行，新增 `CanvasPoint`、`AppCanvasLayout`、`AppConfig.AppCanvasLayouts` 及归一化/快照逻辑；另原分支已将 `MainSidebarCollapsed` 默认值设为 `true`）
  - `SonicRoute.Core/PersistenceJsonContext.cs`（M，25 行，为 `CanvasPoint`、`AppCanvasLayout`、`Dictionary<string, AppCanvasLayout>` 注册 AOT 源生成）
  - `SonicRoute/Resources/Lang/*.json`（9 个语言文件均为 M：`zh-CN`、`zh-TW`、`en-US`、`ja-JP`、`ko-KR`、`fr-FR`、`de-DE`、`es-ES`、`ru-RU`，补充画布、筛选、连线、输出连接菜单与右边缘 `+` 词条）

> **行尾提示**：`git diff` 提示 `Native/Product/Views/AppsPageView.cs` 含 LF 行尾（仓库其他文件多为 CRLF），后续提交前按仓库 `.gitattributes` / 行尾规范注意统一即可。

### 1.3 构建产物与运行进程位置

> **重要**：容器正式运行入口 `E:\kunkun\SonarSwitch\运行版\SonicRoute.exe` 未被修改。交接时检测到用户进程 `PID 15584` 正在运行 `candidate-v2\Full-x64\SonicRoute.exe`，**严禁强杀用户进程**。

| 批次 | 目录 | 版型 | 大小（字节） | 生成时间 | SHA-256 | 说明 |
|---|---|---|---:|---|---|---|
| v1（初版重设计） | `E:\kunkun\SonarSwitch\outputs\native-product\candidate\Full-x64\SonicRoute.exe` | Full x64 | `20,012,544` | `2026-11-26 16:40` | `22CB528EDC1E0C21A74E47CE71BFB4820C333159FD29417E0B9834A84FCAC1E8` | 初版画布（居中 `+` 按钮） |
| v1（初版重设计） | `E:\kunkun\SonarSwitch\outputs\native-product\candidate\Lite-x64\SonicRoute.exe` | Lite x64 | `13,064,704` | `2026-11-26 16:41` | `285B01E73C4623F3D6393FB2DB8D926A8181C5ACCF4F6F3367E7897998F828B9` | 初版画布 Lite |
| v2（布局与 `+` 修复） | `E:\kunkun\SonarSwitch\outputs\native-product\candidate-v2\Full-x64\SonicRoute.exe` | Full x64 | `20,029,952` | `2026-11-27 07:43:16` | `2EF776309FA1BA6C2AE9B15CBC166F6160F15DAEE05B362BD956E88755F9AA5C` | 右边缘 `+` + 动态分区布局（用户进程锁定中） |
| v2（布局与 `+` 修复） | `E:\kunkun\SonarSwitch\outputs\native-product\candidate-v2\Lite-x64\SonicRoute.exe` | Lite x64 | `13,080,576` | `2026-11-27 07:44:15` | `65C02E678C67375C3B4BAD722D852D5B5AE4782AAEE528382AA3AB7648E919D0` | v2 Lite |
| **v3（当前源码最新）** | **`E:\kunkun\SonarSwitch\outputs\native-product\candidate-v3\Full-x64\SonicRoute.exe`** | **Full x64** | **`20,034,048`** | **`2026-11-27 08:20:40`** | **`1B9E857B5EFEA646FAE68DA2A3123981495F3AA51B59871ED0149741A8899E50`** | **含输出设备连线修复，与当前工作区源码完全一致** |
| **v3（当前源码最新）** | **`E:\kunkun\SonarSwitch\outputs\native-product\candidate-v3\Lite-x64\SonicRoute.exe`** | **Lite x64** | **`13,084,160`** | **`2026-11-27 08:21:15`** | **`40347700328A14FF0B86B38E65C3C663511CA898D9E65099FCF5E4F755738213`** | **v3 Lite，与当前工作区源码完全一致** |

---

## 2. 新应用页面架构

### 2.1 模块关系总览

```text
MainWindowShell (LayeredWindow + IViewHostActions)
 ├── 左导航栏 (Apps / Devices[Full] / Hotkeys / Theme / Settings / Automation / Experimental)
 └── AppsPageView (IViewPage, 拆分为 AppsPageView.cs + AppsPageView.Canvas.cs)
      ├── 左侧紧凑应用列表 (AppsPageView.cs)
      │    ├── 虚拟化列表 (_appsScroll, LeftRowHeight=46)
      │    ├── 搜索 (InlineEditHost) / 筛选 (All, Active, Saved, Hidden) / 稳定排序
      │    └── 多选与右键菜单 / 图标缓存 (NativeAppIconService) / 别名编辑 (AliasEditor)
      │
      ├── 右侧独立单应用画布 (AppsPageView.Canvas.cs)
      │    ├── 单画布复用状态: _deviceNodes / _orderedRenderNodes / _orderedCaptureNodes / _wires
      │    ├── AppsCanvasEngine.cs
      │    │    ├── CanvasDeviceNode / CanvasWire / AppsCanvasMetrics
      │    │    ├── AppsCanvasLayoutEngine (统一全量排布 + 增量无重叠分区求解)
      │    │    ├── CanvasWireGeometryCache (ID2D1PathGeometry 缓存与按帧回收)
      │    │    └── AppsCanvasPalette (稳定 FNV-1a 哈希设备色板 & 图标分类)
      │    ├── OutputConnectionPopupWindow.cs (独立 LayeredWindow 连续输出连接弹窗)
      │    └── 互斥交互状态机 (InteractionMode / WireDragOriginKind / RenderDeviceDropKind)
      │
      └── 下层服务 (全部复用)
           ├── ApplicationHost (HostCommands / HostViews: 限频音量队列, 路由队列, 事件通知)
           ├── ProductAudio / ProductRouting / AppPlaybackOutputs / LocalAppOutputMirrors
           ├── PhonePreview (Full 版手机发现与状态变化事件)
           └── ConfigService (AppConfig.AppCanvasLayouts 450ms 防抖原子写入)
```

### 2.2 画布渲染管线（`AppsPageView.Canvas.cs:677 RenderCanvasWorkspace`）

每帧渲染顺序严格固定，保证连线在下、节点在上、浮层工具栏在屏幕空间不失真：
1. 绘制画布外卡片与内裁切区（`PushClip`），注册背景平移热区 `"canvas.bg"`，按屏幕空间绘制低对比度点阵网格（`DrawDotGrid`）。
2. 先于绘制连线计算当前应用节点高度（`ComputeAppNodeHeight`）、应用端口与热区（`ComputeAppNodePorts`）以及全部连线数据（`BuildCanvasWires`），保证首帧坐标 100% 对齐无闪烁。
3. 开启统一世界变换：`g.PushTransform(D2dMatrix3x2.ScaleTranslate(_zoom, tx, ty))` 与 `hit.PushTransform(_zoom, tx, ty)`（100% 缩放时对平移做像素取整保证文字锐利）。
4. **图层 1**：`RenderWireLayer` —— 使用 `CanvasWireGeometryCache` 绘制全部原生实线、转发虚线与麦克风翡翠绿反向实线，以及流向箭头（`DrawDirectionChevron`）。
5. **图层 2**：低对比度分区标题（`播放设备` / `录音设备`），坐标由 `AppsCanvasLayoutEngine.TryGetSectionHeaderRect` 动态计算并避让其他节点。
6. **图层 3**：设备节点（`_orderedRenderNodes`，及在 `Config.ExperimentalMic == true` 时渲染 `_orderedCaptureNodes`），节点体内先注册卡片拖拽区再注册内部按钮/滑块，最后注册左侧连接端点确保端点命中最高优先级。
7. **图层 4**：应用节点（`RenderAppNode`）最后绘制使其在重叠时居于设备节点之上；内部自上而下展开：头部（图标/名称/笔形重命名/PID 状态/更多菜单）→ 播放主音量行 + 右缘原生输出端点 → 动态捕获取转发控制行（每行独立静音/滑块/移除/右缘虚线端点）→ 右缘可拖拽 `+` 端点与「添加输出」提示 → 条件显示的录音设备分区 → 底部快捷开关（禁用自动切换、在快速面板显示）。
8. **图层 5**：若 `_interaction == InteractionMode.DraggingWire && _dragMoved`，绘制实时贝塞尔预览线（`RenderDraggingWirePreview`）。
9. 退出世界变换，绘制屏幕空间右上角悬浮工具栏（`RenderCanvasToolbar`：自动整理、适应画布、恢复默认布局、缩小、百分比点击重置 100%、放大）。

### 2.3 节点管理与自动布局（`AppsCanvasEngine.cs:92 AppsCanvasLayoutEngine`）

- **单画布按需增量更新**：全页面仅维护一个 `_deviceNodes` 字典（key 为 `render:<id>` 或 `capture:<id>`）。切换应用时通过 `SyncCanvasNodesAndLayout` 记忆离场应用的节点位置（`_rememberedNodePositions`，每应用上限 64 条）并增量刷新当前应用的节点集合，不为每个应用常驻独立 UI 树。
- **拓扑签名跳过无关重排**：`BuildLayoutSignature` 将 `appKey | showMic | appHeight | renderKeys@H | captureKeys@H` 拼接为签名；签名未变时不进入布局求解，渲染和调音量零排布开销。
- **统一求解器 `AppsCanvasLayoutEngine.Reconcile`**：
  - `fullArrange == true`（首次未保存、点击自动整理或恢复默认布局）：应用节点固定左上 `(28, 34)`；播放设备按优先级（原生 → 转发 → 系统默认 → 其余）排入右侧上方 1 列或 2 列（设备数 `> 4` 启用 2 列）；录音设备严格起始于 `playbackBottom + DefaultSectionGapY(32) + SectionHeaderHeight(22)` 下方，同样独立支持 `> 4` 双列。
  - `fullArrange == false`（动态增删设备、手机连断、开关麦克风、应用节点因增减转发改变高度）：
    1. 将应用节点当前实际矩形加入障碍表；
    2. 先锁定已存在且 `IsUserPositioned == true` 的用户固定节点；重连的固定节点仅在记忆位置无碰撞时恢复原位；
    3. 播放分区保留现有无碰撞自动节点，仅对新接入或发生冲突的节点按 `FindFreeSlotInPartition` 寻找高优先级无碰撞空槽；
    4. 根据当前播放分区所有有效节点的真实最大底边 `renderBandBottom` 动态计算 `dynamicMicZoneTop`（不使用固定 Y 坐标或设备数量估计）；
    5. 录音分区检查现有自动节点是否侵入 `dynamicMicZoneTop`、是否碰撞，或在播放区大幅收缩（空洞 `> 72`）时平滑上收重排，同时保持用户手动固定的录音节点不动。
- **视口取景**：默认排布调用 `FrameDefaultViewport`（当设备多到整体缩放低于 `0.78f` 时优先聚焦「应用节点 + 第一列设备」以保证文字可读性，最低 `0.78f`）；点击工具栏「适应画布」调用 `FitCanvasToViewport`（将全部节点纳入视口，范围 `0.35f..1.10f`）。两者均预留上方 `SectionHeaderHeight` 与 `padTop = 52f`，避免分区标题遮挡右上角工具栏。

### 2.4 连线绘制、命中测试与拖拽状态机

- **连线绘制与缓存**：`CanvasWireGeometryCache` 以 `wire.Key` 为键缓存 `ID2D1PathGeometry`，仅当端点 `(X0, Y0, X1, Y1)` 偏移 `>= 0.25f` 时释放旧几何并重建三次贝塞尔路径；每帧递增 `_stamp`，帧末 `EndFramePrune()` 释放不再活跃的几何句柄；`OnDeactivated()` 与 `Dispose()` 立即 `Clear()` 释放全部原生 D2D 资源。
- **命中测试**：
  - `UiHitMap` 记录世界→屏幕仿射缩放平移（`PushTransform`），点击与悬停直接命中转换后的屏幕矩形。
  - **非对称缩放补偿端口命中区**（v3 修复）：
    - `GetOuterPortHitReach() = Clamp(16f / _zoom, 15f, 26f)`：向外侧空白方向扩展，低缩放（如 `49%`、`35%`）下依然易于抓中；
    - 内侧严格限制为 `12f`（应用节点避让转发 `×` 按钮 `card.Right - 16`；设备节点避让静音键 `card.X + 14`）；
    - 应用节点垂直半高 `GetAppPortHitHalfY() = Clamp(13f / _zoom, 15f, 16.5f)`，确保间距 34u 的原生端口与 `+` 端口永不重叠。
  - **视觉高亮与落点判定合一**：`GetDeviceDropRect(node)`、`ResolveRenderDeviceDropTarget(wx, wy, out fwdId)`、`IsCaptureDeviceDropOnApp(wx, wy)` 同时服务于渲染高亮与 `CompleteWireDrag`，所见即所得。
- **互斥状态机**：
  - `InteractionMode`：`None`、`DraggingAppVolume`、`DraggingForwardVolume`、`DraggingDeviceVolume`、`DraggingNode`、`PanningCanvas`、`DraggingWire`。任意时刻仅处于单一态。
  - `WireDragOriginKind`：`NativePort`、`ForwardPort`、`AddOutputPort`、`MicPort`、`RenderDevicePort`、`CaptureDevicePort`。
  - **点击 vs 拖拽判定**：在 `DraggingWire` 下使用屏幕空间欧氏距离 `AppsCanvasMetrics.DragThresholdDip = 6f`（与 DPI 和缩放无关）：
    - 未达阈值松手：`AddOutputPort` 弹出/关闭输出连接菜单；`RenderDevicePort` 切换连接/断开；`CaptureDevicePort` 切换麦克风路由。
    - 达到阈值：激活预览线并关闭已打开的输出菜单；释放时按源端口与落点执行切换原生、新增转发、重定向转发、跨设备改连、拖至空白断开或回拖原节点取消；右键、`Esc`、`OnCaptureLost` 立即干净取消。

### 2.5 弹出菜单与配置持久化

- **连续输出连接弹窗（`OutputConnectionPopupWindow`）**：
  - 继承自 `LayeredWindow`（`TopMost = true`, `ToolWindow = true`, `DestroyOnClose = true`, `OwnerHandle = owner.Handle`），宽 `304 DIP`，最大高 `368 DIP`，超出自动启用 `UiScrollState` 滚动条。
  - 分为「连接至输出」与「断开输出」两组；首次弹出时锁定屏幕像素坐标 `_fixedLeftPx/_fixedTopPx` 与高度 `_fixedHeightDip`。
  - 每次点击提交路由后由 `AppsPageView.SyncOutputPopupIfOpen()` 调用 `RefreshItems` 原地刷新分组与待处理状态，**不关闭窗口、不移动窗口坐标**；支持键盘上下/回车/`Esc` 与点击外部（`OnFocusLost`）关闭。
- **配置持久化**：
  - 写入键：`AppConfig.AppCanvasLayouts[stableAppKey]`，稳定应用键由 `GetStableAppKey` 生成（外部源优先 `ExternalPreferenceKey`，PC 应用用 `proc:<ProcessName>`，绝不使用临时 PID）。
  - 节点键：设备节点通过 `GetStableDeviceLayoutKey` 生成 `render:<prefId>` / `capture:<prefId>`（手机输出归一化为持久输出 ID），每个 `CanvasPoint` 持久化 `{ X, Y, Auto }`。
  - 写入节奏：拖动节点、平移、缩放、自动整理后调用 `ScheduleCanvasLayoutSave()` 触发 **450ms 单次防抖定时器**（`_actions.Host.ScheduleOnce`），切换应用或离开页面时 `FlushCanvasLayoutSave()` 立即刷盘并保留离线/断连设备的用户固定坐标（`Auto == false`）。

---

## 3. 核心源码索引

### 3.1 应用页面与画布核心

| 文件 | 行数 | 核心类/结构 | 关键方法与行号范围 | 职责 |
|---|---:|---|---|---|
| `Native/Product/Views/AppsPageView.cs` | 1758 | `AppsPageView` (partial), `AppFilterMode` | `OnActivated/OnDeactivated` (`102-135`), `RenderLeftSidebar` (`204-350`), `RebuildFilteredApps/SortAppsStably` (`430-468`), `TriggerRefresh` (`525-727`), `SetPrimarySelectedApp` (`805-827`), `ShowAppContextMenu/ShowAppIconContextMenu` (`850-950`), `ImportCustomIconFromPath/ResetCustomIcon` (`1072-1151`), `SubmitOutputsChange/ConnectOutputDevice/DisconnectOutputDevice/SwitchNativeOutputDevice/RetargetForwardOutput/FinishOutputAsync` (`1155-1320`), `GetDisplayedPrimaryOutput/GetDisplayedNativeOutputId` (`1322-1342`), `SelectInputRoute/FinishInputAsync` (`1344-1382`), `ApplyAppSliderVolume/ApplyForwardVolume/ApplyDeviceSliderVolume/ToggleDeviceMute/SetLocalDeviceVolumeAndMirrorDefault` (`1411-1489`), `ConfirmAppVolume/ConfirmEndpointVolume` (`1491-1508`) | 页面生命周期、手机事件订阅、左侧应用列表、异步数据刷新、图标导入/导出 PNG、路由乐观提交与失败回滚、音量与静音写入 |
| `Native/Product/Views/AppsPageView.Canvas.cs` | 2594 | `AppsPageView` (partial), `InteractionMode`, `WireDragOriginKind`, `RenderDeviceDropKind` | `SyncCanvasNodesAndLayout` (`185-415`), `ReconcileCanvasIfAppHeightChanged` (`417-440`), `ArrangeNodesDefault/FrameDefaultViewport/FitCanvasToViewport` (`451-562`), `ScheduleCanvasLayoutSave/FlushCanvasLayoutSave/ResetCurrentAppCanvasLayout` (`564-645`), `RenderCanvasWorkspace` (`677-784`), `GetOuterPortHitReach/GetAppPortHitHalfY/GetDeviceDropRect/GetAppDropRect/ResolveRenderDeviceDropTarget/IsCaptureDeviceDropOnApp` (`804-867`), `ComputeAppNodePorts` (`869-906`), `BuildCanvasWires/RenderWireLayer/RenderDraggingWirePreview` (`908-1038`), `RenderAppNode` (`1040-1336`), `RenderDeviceNode/DrawPort` (`1338-1482`), `RenderCanvasToolbar/ZoomAtPoint` (`1484-1559`), `ToggleOutputConnectionPopup/SyncOutputPopupIfOpen/BuildOutputPopupGroups` (`1561-1640`), `ShowDeviceMoreMenu` (`1650-1774`), `OnPointerMove/OnPointerDown/OnPointerUp/OnMouseWheel/OnKeyDown` (`1810-2304`), `BeginWireDrag/IsWireCompatibleWithDevice/CompleteWireDrag/HitTestDeviceNode/HitTestWire/ShowWireContextMenu/CancelCanvasInteraction` (`2320-2593`) | 单应用自由画布全部渲染、动态布局协调、连线预览与命中、拖拽状态机、工具栏、弹窗桥接、设备/连线菜单与布局持久化 |
| `Native/Product/Views/AppsCanvasEngine.cs` | 784 | `CanvasWireKind`, `CanvasDeviceNode`, `CanvasWire`, `RememberedNodePosition`, `AppsCanvasMetrics`, `AppsCanvasLayoutEngine`, `CanvasWireGeometryCache`, `AppsCanvasPalette` | `AppsCanvasLayoutEngine.Overlaps/FindFreeSlotInPartition/Reconcile/TryGetSectionHeaderRect` (`94-528`), `CanvasWireGeometryCache.BeginFrame/GetOrUpdate/EndFramePrune/Clear/ComputeControlPoints/DistanceSquaredToWire/DrawDirectionChevron` (`595-715`), `AppsCanvasPalette.DeviceWireColor/ClassifyDeviceIcon/SplitFriendlyName` (`744-783`) | 节点/连线数据模型、统一无重叠分区布局算法、D2D 贝塞尔几何缓存、稳定色板与辅助几何数学 |
| `Native/Product/Views/OutputConnectionPopupWindow.cs` | 461 | `OutputConnectionItem`, `OutputConnectionPopupWindow` | `ShowFor` (`77-90`), `RefreshItems` (`92-99`), `SetItemsCore` (`107-158`), `RebuildEntries` (`160-203`), `OnRender` (`205-330`), 指针/滚轮/键盘处理与 `TriggerItem`/`CloseToOwner` (`331-460`) | 圆形 `+` 触发的纵向滚动连续操作输出连接弹窗 |

### 3.2 支撑基础设施、路由增益与配置

| 文件 | 行数 | 关键改动 / 职责 |
|---|---:|---|
| `Native/Product/Views/PhonePreview.Playback.cs` | 177 | `OnPlaybackDevicesChanged` 末尾补发 `Notify()`，使手机播放输出变化即时推送至 `AppsPageView.OnPhoneStoreChanged` |
| `Native/Product/Audio/ProductAudio.cs` | 429 | 新增 `GetMirrorGain` / `SetMirrorGain`（统一分发到 PC 本地 `LocalAppOutputMirrors` 或外部会话 `IMultiOutputAudioSession`） |
| `Native/Product/Routing/DesktopMirrorPlayer.cs` | 159 | `SetGain(int? volumePercent, bool? muted)` 及 `OnCapturePacket` 内的独立 Q15 PCM 增益/静音处理 |
| `Native/Product/Routing/LocalAppOutputMirrors.Native.cs` | 103 | `MirrorGainState`、`MirrorGains`（按 `(Pid, SinkId)` 索引）、`GetSinkGain`/`SetSinkGain`、`ApplyInitialGain`/`OnRouteStopped` |
| `SonicRoute/LocalAppOutputMirrors.cs` | 503 | 在创建 PC / 手机转发 Sink 后调用 `ApplyInitialGain(sink)`，路由停止时调用 `OnRouteStopped()` |
| `SonicRoute.AndroidAudio/IMultiOutputAudioSession.cs` | 9 | 增加外部多输出会话的 `GetMirrorGain`/`SetMirrorGain` 契约 |
| `SonicRoute.AndroidAudio/PcmOutputMirrors.cs` | 185 | 每路转发独立增益 `_gains`，输出增益为 `_volume * (sinkPercent / 100f)`，与主音量解耦且不修改系统设备音量 |
| `SonicRoute.AndroidAudio/AndroidAudioSession.cs` & `AirPlay/AirPlayAudioOutput.cs` | 773 / 547 | 实现 `IMultiOutputAudioSession` 转发增益接口委托给 `_mirrors` |
| `Native/Product/HostCommands.cs` & `Native/Product/Views/HostViews.cs` | — | `_endpointVolumeWriter` 扩展为按 `(Flow, Id)` 限频写入并向主窗口回传 `ConfirmEndpointVolume`，支持播放+录音双流设备节点音量滑块 |
| `Native/Product/Ui/D2dRenderer.cs` | 908 | 虚线笔刷、缓存 `ID2D1PathGeometry` 创建/绘制/释放、世界矩阵 `PushTransform` 叠加 |
| `Native/Product/Ui/UiPrimitives.cs` & `UiIcons.cs` | 653 / 157 | `UiHitMap` 变换支持、`DrawVolumeSlider` 关闭电平条支持、画布/筛选/静音新矢量图标 |
| `SonicRoute.Core/ConfigService.cs` & `PersistenceJsonContext.cs` | 786 / 25 | `CanvasPoint`（含 `Auto`）、`AppCanvasLayout`（缩放裁剪 `0.35f..1.8f`、坐标裁剪 `±10000`、最多 64 设备）、`AppConfig.AppCanvasLayouts`（最多 256 应用）及 AOT JSON 源生成注册 |
| `SonicRoute/Resources/Lang/*.json` | 9 份 | 全部 9 语言同步新增画布/筛选/连线/弹窗/右边缘 `+` 文案 |

---

## 4. 功能复用情况与新增模块必要性

### 4.1 已复用的现有模块与服务
- **UI 控件与窗口基础设施**：直接复用 `LayeredWindow`、`D2dTarget`/`D2dSharedContext`、`UiThemePalette`、`UiPrimitives`（按钮、滑块、开关、下拉框、徽章、滚动条 `UiScrollState`、`UiHitMap`）、`PopupMenuWindow`、`ComboPopupWindow`、`InlineEditHost`、`AliasEditor`。
- **应用图标与别名**：复用 `NativeAppIconService`、`AppIconFiles` 与 `AliasEditor`，通过 `_actions.CommitSettings(notify: true)` 同步主窗口、快速面板与 OSD。
- **音频枚举与路由队列**：复用 `ProductAudio`（`Devices`、`Apps`、`RenderDevicesWithPhone`、`ReadRoute`、`GetSelectedOutputs`、`Volume`、`SetRoute`、`SetDefault`）、`ProductCurrentApp`（`SelectOutputAsync`、`SetOutputsAsync`）、`ProductRouting`、`AppPlaybackOutputs`、`AppPlaybackRestore`、`LocalAppOutputMirrors`、`ExternalAudioBridge`、`PlaybackSelection`。
- **限频音量写入与事件总线**：复用 `ApplicationHost` 的 `QueueAppVolume`、`QueueEndpointVolume`、`FlushVolumeWrites`、`SubmitPanelAudio`、`NotifyAudioVisualChanged`、`ScheduleOnce` 与 `PhonePreview.Changed`。
- **设备管理入口**：复用了已迁移完成的 `MainNavTab.Devices`（`PhoneDevicePageView`，经 `OpenPhoneManagement(profileId, airPlay: false)` 跳转「配置设备」）与 `MainNavTab.Settings`（跳转「设置」）。
- **配置与本地化**：复用 `ConfigService.TryCommit` / `ConfigService.QueueSave` 原子写盘机制、`PersistenceJsonContext` 与 `L10n.T(...)`。

### 4.2 新增模块及扩展的必要性
1. **`AppsCanvasEngine.cs`**：封装节点/连线模型、单一分区碰撞求解器与 D2D 路径几何缓存，避免在 UI 绘制类中堆叠布局/几何数学，同时保障静态帧零重建与退出可彻底释放。
2. **`AppsPageView.Canvas.cs`**：按 `partial class` 将画布渲染与手势状态机从左侧列表/路由逻辑解耦，保持代码可维护性。
3. **`OutputConnectionPopupWindow.cs`**：现有 `PopupMenuWindow` / `ComboPopupWindow` 均为单次点击即关闭；需求要求「连接至输出 / 断开输出」分组连续点击不关闭、原地刷新且位置不跳动，必须独立轻量化分层弹窗承载。
4. **独立转发增益路径**（`DesktopMirrorPlayer` / `LocalAppOutputMirrors.Native` / `PcmOutputMirrors`）：原有捕获转发缺少每目标独立音量与静音控制，若直接复用系统端点音量会违反「不伪造、不串控设备总音量」的语义要求。
5. **D2D 虚线/几何缓存/`UiHitMap` 变换与 `AppCanvasLayouts` 配置结构**：原有渲染层仅支持实线基础图元与屏幕坐标命中，原有配置无每应用节点坐标持久化字段。

### 4.3 已清理的旧实现
- `AppsPageView.cs` 中旧版右侧 4 张堆叠卡片 UI、`_detailsScroll` 垂直滚动区、横向设备按钮瀑布流以及旧应用页专属 `80ms` `_peakTimer` 峰值轮询与电平条渲染已全部移除。

---

## 5. 最新修复进度（v1 → v2 → v3）

1. **右侧 `+` 输出端点与直接拖拽新增连线（已完成，v2 新增、v3 强化）**
   - 圆形 `+` 移至应用节点右边缘 `X = card.Right`，与原生输出端点及各捕获转发端点保持垂直对齐，随转发行增减自动上下平移，左侧配低对比度「添加输出」标签。
   - 6 DIP 屏幕空间阈值区分单击（开/关连续菜单）与拖拽（自动关闭菜单、拉出虚线连线至未连接播放设备建立转发，无效落点/右键/`Esc` 取消不残留、不误弹菜单）。
2. **输出设备连线交互全链路修复（已完成，v3 实测 38/38 通过）**
   - 修复从输出设备端口拖至应用节点原生端口右半区落点截断与误增转发问题；支持拖至原生端口切换原生、拖至指定转发端口重定向该路转发、拖至 `+` 端口/转发区新增转发，预览线同步区分实线/虚线并高亮对应目标区与端点。
   - 补齐已连接输出设备（及麦克风）端口拖离至空白画布断开、拖至另一台兼容设备直接改连、拖回自身卡片取消的能力。
   - 修复目标设备左侧端口悬停无高亮问题，并引入低缩放非对称端口命中区（实测 `85%` 与 `49%` 缩放全数命中且不误触静音键/转发 `×` 键）。
   - 修复从应用节点端口拖出后放回应用卡片内误断开的问题，以及 `SubmitOutputsChange` / `SelectInputRoute` 补加 `_stateGen++` 防止进行中的后台刷新覆盖刚提交状态。
3. **节点自动避让与播放/录音严格分区（已完成，单测 10/10 + 实机 20 设备截图验证）**
   - `AppsCanvasLayoutEngine` 统一承载默认排布、自动整理、重置布局与动态增量更新；播放设备严格在右上分区、录音设备严格在播放分区动态底边之下，绝不交错；分区内设备 `> 4` 独立双列。
   - 应用节点因转发增减变高时调用 `ReconcileCanvasIfAppHeightChanged()` 立即无冲突避让；保留用户手动摆放节点（`Auto = false`），重连先检冲突再恢复；关闭 `ExperimentalMic` 彻底隐藏并释放录音节点与连线。
4. **手机设备动态接入（代码与布局引擎已完成，物理真机插拔待验）**
   - `PhonePreview.OnPlaybackDevicesChanged` 补发 `Notify()`，`AppsPageView.OnActivated` 在 `phone.Discovery.CurrentDevices == null` 时触发后台发现刷新。
   - 手机输出节点以 `PhoneDeviceNodeHeight = 68f` 无冲突插入播放分区，显示远程设备提示而不伪造系统音量滑块；接入不改变当前应用既有输出路由。

---

## 6. 功能与配置兼容性

### 6.1 音频路由与音量语义

| 控制项 | 底层机制 | 语义与隔离保证 |
|---|---|---|
| 应用主音量 & 静音 | `ProductAudio.Volume(app)` / `QueueAppVolume` | 控制该应用系统会话主音量，与系统设备总音量解耦 |
| 原生输出（实线） | Windows `AudioPolicyConfig` 单路主端点（`SelectOutputAsync` / `SetOutputsAsync` 主端点）；外部源为其主输出 | 不重复创建独立原生输出行，连接端点直接位于应用主音量行右侧 |
| 捕获转发（虚线） | PC 应用：`LocalAppOutputMirrors` 进程环回捕获 + 各目标独立 `DesktopMirrorPlayer`（Q15 PCM 缩放/静音，100% 且未静音走零拷贝直通）或 `AndroidPcmSource.Volume`；外部源：`PcmOutputMirrors`（`_volume * sinkPercent / 100f`） | 每路转发滑块与静音**真正独立**，不修改任何设备系统总音量，也不影响其他转发目标或应用主音量；**说明**：单路转发增益目前随活动路由驻留内存（路由停止或重启后恢复 100%），未新增磁盘持久化字段 |
| 设备节点系统音量 & 静音 | `QueueEndpointVolume(flow, id, level)` / `ProductAudio.Volume(flow, id)` | 修改 Windows 端点系统总音量，全应用画布共享缓存；`系统默认` 节点与当前实际 Windows 默认物理设备节点双向同步；远程手机输出无 Windows 端点，**不伪造滑块** |
| 麦克风输入（翡翠绿反向实线） | `ProductAudio.SetRoute(pid, Flow.Capture, deviceId)` | 严格复用全局开关 `Config.ExperimentalMic`，不新增重复每应用开关；外部应用（Android/AirPlay）禁用麦克风连接 |

### 6.2 1.36 → 1.37 配置兼容性
- `AppConfig.AppCanvasLayouts` 默认为空字典，缺失时由 `Normalize()` 初始化，**纯 1.36 `config.json` 可零错加载**并保留全部原有规则、设备隐藏、别名、图标、热键与语言设置；未知历史字段通过 `[JsonExtensionData] AdditionalFields` 保留。
- 异常 `AppCanvasLayouts`（越界 `Zoom`/`Pan`、`NaN`/`Infinity`、`null` 节点、空键、超量条目）在 `AppCanvasLayout.Snapshot()` 与 `NormalizeCanvasLayouts()` 中安全清洗，不影响启动。
- **注意（v1 → v2/v3 升级细节）**：v1 初版保存的 `CanvasPoint` 尚无 `Auto` 字段，在 v2/v3 读取旧 `config.json` 时 `Auto` 默认反序列化为 `false`（视为用户手动定位），已在用户本地 `config.json` 中观察到该现象；用户点击画布右上角「自动整理」或「恢复默认布局」即可重置为自动排布。

---

## 7. 性能与资源管理

### 7.1 已实现的资源与空闲控制机制
- **零前台/后台轮询**：移除旧应用页 `80ms` `_peakTimer` 与电平条绘制；新画布完全事件驱动，仅在用户输入或 `AudioChanged`/`Phone.Changed` 通知时按需刷新与 `Invalidate()`。
- **几何与图标资源生命周期**：
  - `CanvasWireGeometryCache` 按端点缓存 `ID2D1PathGeometry`，每帧 `EndFramePrune()` 回收过期几何，`OnDeactivated()` / `Dispose()` 全量释放。
  - 复用 `NativeAppIconService` 图标缓存，不重复解码；应用列表虚拟化仅绘制可见行。
  - 关闭 `ExperimentalMic` 时 `ReleaseMicResources()` 立即清空 `_inputs`、`_orderedCaptureNodes` 与录音连线。
- **防抖写盘与计算剪枝**：拖拽/缩放布局变更采用 `450ms` 防抖单次定时器写盘；`BuildLayoutSignature` 阻止无拓扑变化帧进入布局求解。

### 7.2 实测性能数据（隔离 Full x64，150% DPI，10 播放 + 8 录音 + 6 应用，开启麦克风）

> 测量条件：独立 `SONICROUTE_CANDIDATE_ROOT`，同一脚本采集。**注意：以下数据来自 v1 与 v2 发布包实测；v3 仅改动了指针命中、落点分支与 `_stateGen++`（未新增定时器、缓存或常驻资源），未单独重跑全套性能基准。**

| 指标 | 改造前旧版 Full x64 | 画布 v1 Full x64 | 画布 v2 Full x64 |
|---|---:|---:|---:|
| 后台静置 6s CPU / 定时器触发 | `0 ms` / `0` | `0 ms` / `0` | `0 ms` / `0` |
| 后台静置私有工作集 / GDI / USER | `3.60 MB` / `7` / `9` | `3.94 MB` / `7` / `9` | `3.84 MB` / `7` / `9` |
| 应用页前台静置 4s CPU / 定时器 | `15.625 ms` / `0` | `0 ms` / `0` | `15.625 ms`（单次系统计时粒度）/ `0` |
| 应用页前台静置 4s 托管分配速率 | `10,977 B/s` | `2,002 B/s` (`-81.8%`) | `2,022 B/s` (`-81.6%`) |
| 应用页前台私有工作集 / GDI / USER / 句柄 | `35.45 MB` / `75` / `59` / `763` | `37.36 MB` / `75` / `59` / `757` | `37.24 MB` / `75` / `59` / `762` |
| 连续 5 次关窗再打开 GDI / USER / 句柄 | `75` / `59` / `765` | `75` / `59` / `757` | `75` / `59` / `762`（0 泄漏） |
| 关窗后静置 4s CPU / 定时器 / 私有工作集 | `0 ms` / `0` / `30.92 MB` | `0 ms` / `0` / `33.32 MB` | `0 ms` / `0` / `33.50 MB` |

---

## 8. 遗留问题、验证边界与风险

### 8.1 已完成且已实测验证
- **Native AOT 与多目标编译**：v3 Full x64 与 Lite x64 构建零错误、发布日志 `IL2xxx`/`IL3xxx` 警告均为 `0`；v2 阶段亦验证 `SonicRoute.Core`（`net10.0-windows` + `net48`）零错误编译与 `SerializationChecks`（9 种语言 JSON 契约）通过。
- **v3 输出设备连线全套交互（38/38 项实机验证）**：在 `candidate-v3\Full-x64\SonicRoute.exe` + `DemoAudioApp` 上，分别于 `85%` 缩放与 `49%` 低缩放下实测通过全部 19 类操作（原生拖连/左端口边缘拖连/回拖取消/拖空重置、`+` 连续拖连多路转发/单击弹窗/`Esc` 关闭、转发端点重定向/取消/断开、设备端口单击切换、设备端口拖至原生端口右缘切换原生、拖至 `+` 端口右缘新增转发、拖至指定转发端口右缘重定向、已连设备端口拖至空白断开、拖至其他设备改连原生与转发、拖回自身取消），并截屏核验了视觉高亮反馈。
- **动态分区布局求解器（10/10 项单测验证 + 20 设备适应画布截图）**：手机接入/断开/重连、重连冲突检测、20 设备双列严格分区、开关麦克风避让、应用节点增高 `+216px` 避让全部 0 重叠通过。
- **真实多路捕获转发与退出零句柄残留（v1 实测 Full & Lite）**：双路事件驱动转发投递非静音 PCM 帧、非法端点事务回滚、进程重启 PID 变化自动恢复、`last-exit.json` 退出后音频句柄残留为 `0`。
- **1.36 配置兼容与异常清洗（v1 实测）**：纯 1.36 配置无错加载，越界/空值 `AppCanvasLayouts` 安全清洗。

### 8.2 已实现但**未经真机实测**（切勿标记为已通过）
1. **物理 Android 手机 USB / 无线真实链路**：虽检测到用户正式配置中存在 `device:e1868f06`（`android-out:device:e1868f06:current` / `:speaker`）与安卓外部应用 `cn.wenyu.bodian`，但本轮隔离测试环境**未接入物理手机**。以下路径**尚未真机实测**：
   - 真机插拔时手机输出节点实时出现/消失与无重叠避让；
   - PC 应用连接/独享手机输出（`GetDisplayedNativeOutputId` 返回 `null` 时从原生端口/转发端口/设备端口拖连）；
   - 外部 Android 应用（`app.IsExternal == true`，含 `SourcePhoneOutputId` 别名互斥规则）在画布上的连线、多输出转发与音量控制。
2. **AirPlay 真实接收端音频链路**：`AirPlayAudioOutput` 已接入 `IMultiOutputAudioSession` 转发增益接口，**未做真机 AirPlay 投送测试**。
3. **多物理扬声器/耳机人工听感隔离**：已验证双路非静音帧投递与 Q15 增益源码路径，**未经人耳在双物理输出设备上实听确认**。
4. **跨显示器不同 DPI 热切换（如 100% ↔ 150% ↔ 200%）与物理 USB 声卡热插拔**：仅在单屏 150% DPI 验证，**未做多屏跨 DPI 热切与物理声卡热插拔实测**。
5. **v3 单独的 Lite 运行时回归与完整性能重测**：v3 确认了 Lite x64 AOT 零警告编过及 Full x64 38 项连线实测，但**未对 v3 重跑 `Test-ProductMirrors.ps1` 与完整内存/CPU 基准**。

### 8.3 已知遗留细节与潜在风险
- **既有测试工程 `packaging/Tests/PersistenceChecks`**：原工程通过反射读取旧私有字段 `ConfigService.SaveOptions` 且要求为所有新 `AppConfig` 属性补充测试夹具，本轮未改动无关测试工程；若后续跑全仓库旧测试需同步适配 `AppCanvasLayouts`。
- **v1 旧配置 `Auto` 默认值**：v1 保存的 `AppCanvasLayouts` 无 `Auto` 字段，读取为 `Auto = false`（固定坐标），在动态新增大量设备时可能使旧节点不参与自动重排，需通过「自动整理」或「恢复默认布局」重置，或由后续按需增加一次性迁移策略。
- **外部应用与只连手机输出时的视觉/交互边界**：当 PC 应用仅路由到手机输出时，原生输出端点显示为「未设置」且手机路由展示为捕获转发行；建议在真机环境下重点复测此时从手机设备端口拖回主音量区、从另一 Windows 设备拖入原生区以及点击 `+` 菜单的体验。
- **候选目录清理提醒**：`outputs/native-product/` 下目前并存 `candidate`（v1）、`candidate-v2`（v2，被用户进程 PID 15584 占用）、`candidate-v3`（v3 最新）。在用户关闭 `candidate-v2` 前不要尝试覆写或删除 `candidate-v2`。

---

## 9. 构建与验证方式

以下命令均在主工作区根目录 `E:\kunkun\SonarSwitch\当前项目` 下于 PowerShell 中执行（.NET 10 SDK + Windows SDK + MSVC 工具链，由 `Native/Set-Phase1Toolchain.ps1` 自动配置）：

### 9.1 Native AOT Full / Lite 构建

```powershell
# 构建 Full x64（含手机功能）与 Lite x64（无手机功能与资源）
# 如默认 outputs/native-product/candidate 存在文件占用，使用 -PublishDirectory 指定新目录
& ./Native/Build-Product.ps1 -Edition Full -Architecture x64 -PublishDirectory 'E:\kunkun\SonarSwitch\outputs\native-product\candidate-next\Full-x64'
& ./Native/Build-Product.ps1 -Edition Lite -Architecture x64 -PublishDirectory 'E:\kunkun\SonarSwitch\outputs\native-product\candidate-next\Lite-x64'

# 检查裁剪与 AOT 警告（预期 0 条）
Select-String -Path '..\outputs\native-product\build\Full-x64\publish.log','..\outputs\native-product\build\Lite-x64\publish.log' -Pattern 'warning IL[23]\d\d\d'
```

### 9.2 单元/契约与镜像验证命令

```powershell
# Core 多目标编译（net10.0-windows + net48）
dotnet build SonicRoute.Core/SonicRoute.Core.csproj -c Release

# 9 语言资源与序列化契约检查
dotnet run --project packaging/Tests/SerializationChecks/Checks.csproj -c Release

# Full / Lite 真实多路捕获转发、回滚、PID 恢复与退出 0 句柄残留检查
& ./Native/ProductChecks/Test-ProductMirrors.ps1 `
  -Executable 'E:\kunkun\SonarSwitch\outputs\native-product\candidate-v3\Full-x64\SonicRoute.exe' `
  -OutputDirectory 'E:\kunkun\SonarSwitch\outputs\native-product\verify-mirrors-full'

# 性能与资源占用测量（详见 Native/Measurements/README.md）
& ./Native/Measurements/Measure-Product.ps1 -Mode Native -Edition Full `
  -Executable 'E:\kunkun\SonarSwitch\outputs\native-product\candidate-v3\Full-x64\SonicRoute.exe' `
  -OutputDirectory 'E:\kunkun\SonarSwitch\outputs\native-product\verify-perf-full'
```

### 9.3 隔离运行方式与关键环境变量

```powershell
$exe = 'E:\kunkun\SonarSwitch\outputs\native-product\candidate-v3\Full-x64\SonicRoute.exe'
$env:SONICROUTE_CANDIDATE_ROOT = 'E:\kunkun\SonarSwitch\outputs\native-product\dev-run'
$env:SONICROUTE_CANDIDATE_ID = 'dev-canvas'          # 隔离配置/互斥量/命名管道，不干扰用户运行版
$env:SONICROUTE_DISABLE_DEFAULT_HOTKEYS = '1'        # 避免与运行版争抢全局快捷键
$env:SONICROUTE_CANDIDATE_METRICS = '1'              # 可选：启用镜像指标并在退出时写入 last-exit.json

Start-Process -FilePath $exe -WindowStyle Hidden
& $exe --control status                              # 查询服务状态
& $exe --control activate                            # 唤出主窗口
& $exe --control apps                                # 枚举当前应用
& $exe --control devices render                      # 枚举播放设备
& $exe --control outputs-get <PID>                   # 读取应用当前全部输出
& $exe --control outputs-clear-saved                 # 清理已保存多输出
& $exe --control exit                                # 优雅退出并写出退出收据
```

> **本地临时参考脚本**（位于外部 scratch 目录，不属于仓库内容）：`C:\Users\20905\.gemini\antigravity\brain\bdc4d359-07ca-4acb-9dfd-bfc329054d99\scratch\` 下留有 `verify-wires.ps1`（38 项窗口连线自动化脚本）、`verify-fixes.ps1`、`bench-apps.ps1` 与 `LayoutCheck/` 供需要时参考。

---

## 10. 后续开发约束

1. **架构与复用约束**：
   - 严禁重构整套 UI 框架，严禁引入新 UI 框架或虚拟声卡。
   - 修改或新增任何 UI/逻辑前，先检查并复用现有控件（`UiPrimitives`、`LayeredWindow`、`AliasEditor`、`OutputConnectionPopupWindow`）、音频服务（`ProductAudio`、`ProductRouting`、`AppPlaybackOutputs`）、设备与图标缓存、路由与音量队列以及本地化机制，不重复实现两套逻辑。
   - **完整保留已迁移完成的「连接设备」「配置设备」导航与页面入口**（`MainNavTab.Devices`、`PhoneDevicePageView`、`PhoneDevicePageView.Navigation.cs`），不得恢复旧入口或覆盖现有未提交迁移改动。
2. **性能与低资源占用约束（`AGENTS.md`）**：
   - 优先控制闲时 CPU 消耗：不新增后台轮询、周期定时器、重复扫描、重复 I/O 或无用唤醒；保持事件驱动渲染、按需加载与仅窗口可见时刷新；功能关闭或窗口关闭后停止对应页面级活动并释放资源。
   - 保持 Full / Lite / Legacy 边界：Full 含手机功能且不带 Lite 后缀（保护活动连接/音频/虚拟屏，不做全进程工作集裁剪）；Lite 排除手机功能和资源。
3. **验证与范围约束（`AGENTS.md`）**：
   - 按本次改动范围做必要验证，不因小改动堆叠重复全套性能基准；出现新失败或明确需要基准时再扩测。
   - 未经真实硬件验证的项（尤其是物理 Android 手机、AirPlay、多物理扬声器听感、跨屏 DPI）严禁标记为通过。
4. **工作区与安全红线（`AGENTS.md`）**：
   - **不得结束用户正在运行的 `SonicRoute.exe` 进程**，不得重启共享 ADB 或关闭活动手机流。
   - 不得触碰或删除策略保留的历史目录（如退役源树、Lab `staged/SonicRoute` 克隆、旧 ADB 工具目录）。
   - 未经用户明确指令，不修改 `build/Version.props` 版本号，不执行 Git 提交、推送、打标签或 Release 发布。
