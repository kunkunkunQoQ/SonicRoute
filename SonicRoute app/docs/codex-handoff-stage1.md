# 交接给 Codex 的 Windows 端与跨端协议配合事项（阶段 1 / 阶段 2A 准备）

> 更新时间：2026-11-27  
> 发起方：Gemini（Android 端）  
> 目标接收方：Codex（Windows 端 & 跨端协议架构）  
> Windows 只读审查基线：`E:\kunkun\SonarSwitch\当前项目`，分支 `codex/native-legacy-ui-repair-20261125`，HEAD `31c2ef4f676ba6c8912673319c62a1dcf99167e5`（工作树存在两处已有未提交 UI 修改：`Native/Product/Ui/UiPrimitives.cs`、`Native/Product/Views/PhoneDevicePageView.cs`，本次严格只读、零修改）。

---

## 1. 背景与当前边界

Android 端已严格按“Windows 只读、核心能力自主实现、优先验证本地音频/蓝牙/独立应用路由与独立音量可行性”推进完阶段 0 审查与本地第一阶段交付（对应路线图阶段 3 公开音频/设备基线 + 动态能力矩阵/租约核心 + 跨四后端可行性探针）。

后续进入跨端配对、共享操作派发与档案统一（路线图阶段 1、2A、2B 及 4）前，以下 Windows 端改造与跨端协议细化需要由 Codex 实现和定稿。

---

## 2. 需求清单（按优先级排序）

### 需求 A（P0 · 对应路线图阶段 1）：Windows 共享业务端口与 ADB 适配器收拢

- **涉及 Windows 源码入口（只读核对结果）**：
  - `Native/Product/Views/PhonePreview.cs`
  - `Native/Product/Views/PhoneDevicePageView.cs` / `PhoneDevicePageView.Connection.cs`
  - `SonicRoute.AndroidAudio/AndroidConnectionManager.cs`
  - `SonicRoute.AndroidAudio/AndroidPhoneStore.cs`
  - `SonicRoute.AndroidAudio/AndroidAudioSettingsStore.cs`
  - `SonicRoute.AndroidAudio/PcToPhoneAudioManager.cs`
  - `SonicRoute.AndroidAudio/AndroidAudioSession.cs`
- **具体需求**：
  1. 从现有 `PhonePreview` + `AndroidConnectionManager` 抽出 `PhoneTransportDiscovery`、`PhoneConnectionCoordinator`、`PhoneOperationPort` 最小产品端口（见 `docs/protocol.md`）。
  2. 现有 ADB 无线/USB 路径全部收拢为 `AdbOperationAdapter` 与 `AdbTransportDiscovery`，保持原有二维码配对、6 位码配对、直连、换端口恢复、旧 USB 档案迁移、手动断开、取消配对与移除语义完全一致。
  3. 在 `AndroidPhoneProfile` 扩展 App 公钥指纹别名元数据与原子迁移/回滚保护（保留 `android-phone.json` v1–v3 兼容、`AdditionalFields` 未知字段及原 `AndroidPhoneProfile.Id` 不变；Lite 构建继续通过 `SONICROUTE_PHONE` 隔离）。
- **原因**：
  - 保证 App 连接与现有 ADB 无线连接共用同一套设备档案、连接状态机、操作入口和错误语义，避免两套并行逻辑导致同一台手机产生重复档案或路由键失效。
- **验收标准**：
  - Full 构建在独立隔离配置目录运行原 ADB 全量回归（二维码/6 位码/直连/换端口/USB+Wi-Fi 共存/手动断开/移除/取消配对）全部通过。
  - `android-phone.json` 与 `android-audio.json` v1–v3 夹具读写保持未知字段、路由键与输出 ID 不变。
  - Lite 构建验证仍完全排除手机功能且无新增依赖。

---

### 需求 B（P0 · 音频输出设备逻辑键扩展）：支持 `wired:` 与 `usb:` 输出类型

- **只读审查发现的协议缺口**：
  - 在 `SonicRoute.AndroidAudio/AndroidModels.cs`（L5、L30-L34）：
    ```csharp
    public enum AndroidOutputSinkKind { Speaker, Bluetooth, Current }
    ```
    当前 `AndroidOutputSink.ParseList` 会把所有非 `bluetooth` / 非 `bt:` 的条目强制归类为 `Speaker` 并改写 `Key = "speaker"`。
  - Android 端阶段 3 公开音频枚举现已支持：
    - `current`（跟随系统当前输出）
    - `speaker`（手机扬声器）
    - `bt:<token>`（蓝牙 A2DP / BLE Headset / BLE Speaker / BLE Broadcast，无主蓝牙时回退 SCO）
    - `wired:<token>`（有线耳机 `TYPE_WIRED_HEADSET` / `TYPE_WIRED_HEADPHONES`）
    - `usb:<token>`（USB 声卡/耳机 `TYPE_USB_DEVICE` / `TYPE_USB_HEADSET` / `TYPE_USB_ACCESSORY`）
  - Android 端当前在 `:core` 中通过 `AudioOutputSink.windowsCompatible` 显式区分已兼容键（`current`/`speaker`/`bt:`）与仅本地键（`wired:`/`usb:`），并在 UI 标注“仅本地”，不会破坏现有 Windows 逻辑。
- **接口变更建议**：
  1. `AndroidOutputSinkKind` 增补 `Wired`、`Usb`（可选保留 `Other` 以向前兼容未知次版类型）。
  2. `AndroidOutputSink.ParseList` 与 `NormalizeList` 按 `kind` (`"wired"` / `"usb"`) 与前缀 (`"wired:"` / `"usb:"`) 保留原键与名称，不再强行改写为 `speaker`。
  3. `AndroidOutputIdentity.TryParseDeviceId` 支持解析 `android-out:<phoneKey>:wired:<token>` 与 `android-out:<phoneKey>:usb:<token>`（避免 `token` 内含 `:` 如 `card=1:dev=0` 时被 `LastIndexOf(':')` 截断，处理方式与 `:bt:` 一致）。
  4. `AudioOutputBridge.java`（ADB 路径）可按需同步补充 `wired:`/`usb:` 枚举，使 App 与 ADB 两端输出设备集合完全等价。
- **验收标准**：
  - `AndroidOutputSink.ParseList` 与 `AndroidOutputIdentity.TryParseDeviceId` 单元测试覆盖 `speaker`、`current`、`bt:AA:BB:CC:DD:EE:FF`、`wired:Wired_Headset`、`usb:card=1;device=0` 及含冒号 token，互转无损且旧配置键完全兼容。

---

### 需求 C（P1 · 对应路线图阶段 2A）：冻结 SRAP v1.0 报文、能力快照字段与黄金样本

- **具体需求**：
  1. 冻结 `docs/protocol.md` 中的 `SRAP` 16 字节二进制帧头与 JSON 控制帧 Schema（`hello`、`pair.*`、`capabilities`、`request`、`response`、`cancel`、`event`）。
  2. 确认 Android `:core` 已按规范落地的字段集合可以直接进入黄金样本（Golden Vectors）：
     - 18 个 `Capability.wireId`（见 `core/src/main/kotlin/org/sonicroute/core/PermissionState.kt`）
     - 5 个 `CapabilityStatus.wireId`：`not_implemented`、`needs_authorization`、`unsupported`、`unavailable`、`available`
     - `CapabilityEntry` 结构：`{id, state, reason?, scope?, limits?}` + 单调递增 `capabilityRevision`
     - 14 个 `ProtocolError.wireId`：`not_implemented`、`permission_required`、`permission_lost`、`unsupported`、`target_unavailable`、`identity_conflict`、`not_paired`、`version_mismatch`、`stale_session`、`invalid_argument`、`busy`、`timeout`、`canceled`、`io_failure`
  3. 明确 `audio.play`（自有轨道独立增益 `trackGainPercent` / `trackMuted`）与 `volume.device`（系统 `STREAM_MUSIC`）以及 `audio.volume.other_app`（第三方应用独立音量）在 `volume.get` / `volume.set` 中的 `scope` 字段约定，避免三类音量作用域混淆。
  4. 提供 C# 与 Kotlin 共用的首版黄金帧测试向量（合法帧、半帧拆包、超限 64 KiB 拒收、未知能力忽略、版本无交集 `version_mismatch`）。
- **验收标准**：
  - C# 与 Kotlin 两端测试套件对同一批黄金二进制样本的编解码逐字节一致，所有异常边界测试通过。

---

### 需求 D（P1 · 面向阶段 4 音频实测反馈）：第三方应用独立路由/音量及独占采集的协议能力细分

- **Android 16（API 36 / HyperOS）真机实测新发现**：
  - 在 Xiaomi 13（Android 16 / HyperOS V816）上经官方 Shizuku UserService（UID 0）实测：
    1. `audio.route.other_app`：`AudioPolicy` + `RULE_MATCH_UID` + `ROUTE_FLAG_RENDER` + `setDevice(Speaker)` 注册返回 `0` 并即时安全注销，验证可行。
    2. `audio.volume.other_app`：`AudioManager.getActivePlaybackConfigurations()` 可枚举全部活跃播放会话与 UID，且 `PlayerProxy.setVolume(float)` 在自有会话上实测调用成功；但该方式作用于活跃播放器实例，需在播放器重建时由 Android 端持续监听并重应用。
    3. `audio.capture.exclusive`：`AudioPolicy` + `RULE_MATCH_UID` + `ROUTE_FLAG_LOOP_BACK` 注册返回 `0` 且分配了真实回环地址（`reg=...mixp:0`），但 `AudioPolicy.createAudioRecordSink(mix)` 在 Android 16 上返回 `null`，后续阶段 4 需要像 `PlaybackAudioCapture.java` 一样走 `REMOTE_SUBMIX` + `addr=<reg>` 回退路径，并在建流后以真实首帧非静音/未静音态回读为准。
- **需要协议/Windows 侧配合确认的语义**：
  1. 允许 `audio.route.other_app`、`audio.volume.other_app`、`audio.capture.exclusive` 在能力快照中分别独立上报状态（例如 `audio.route.other_app=available` 而 `audio.capture.exclusive=unavailable`），UI 不做捆绑假设。
  2. 对 `audio.route.owned` / `audio.route.other_app` 的切换指令，Windows 侧仅在收到 Android 端真实 `routed` 回读确认（`verified=true`）后再视为切换完成，与现有 `AudioOutputBridge.java` 的 2000 ms 校验语义保持一致。
- **验收标准**：
  - Windows 设备页/画布按单项能力状态独立启用或禁用对应控件，并以 Android 回读事件更新当前生效路由与错误原因码。
