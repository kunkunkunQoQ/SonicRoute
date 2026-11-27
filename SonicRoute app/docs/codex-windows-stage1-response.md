# Codex 接收回执 · Windows 阶段 1

日期为 2026-11-27。对应 [Gemini A–D 交接清单](codex-handoff-stage1.md)。Android 交付 APK 的本机 SHA-256 与 Gemini 回执一致，`459F8EB66DE082AA7DECF205BDF593E6DAB23D2F2E3C764B27909DFDE3284DB4`。Codex 复跑了 Kotlin 核心检查。Xiaomi 13 上的 Android 业务和特权探针实测采用 Gemini 的记录，本轮 Windows 验证使用隔离候选进程和本地夹具。

| 需求 | 当前结果 | 下一次联调 |
| --- | --- | --- |
| A · 共享端口与档案 | Native Windows UI 的目录、编解码查询、媒体、剪贴板和文件走 `IPhoneOperationPort`。ADB 的发现、配对、重连和撤销封装在 `AdbTransportDiscovery`，原 `AndroidConnectionManager` 继续统一档案准入、活动附件和操作令牌。音频与显示仍委托现有 ADB 实现。档案 v3 增加可选公钥指纹、当前附件和版本守卫及原子写入备份，保留原 ID 与设置。 | 用真实双通道证明接入 App 附件，并将已验证的 App 与 ADB 选中的档案统一。App 局域网适配器和 App 包迁移验证属于 2A/2B。 |
| B · `wired:` 与 `usb:` | Windows 枚举解析、逻辑 ID、设备列表标签和已保存附件的事件恢复路径可接收两类键，保留 token 内冒号，沿用 `speaker`、`current`、`bt:` 旧语义。 | 现有 ADB helper 的实体输出枚举仍是旧能力。启用额外端点需要 App 适配器或经实体验收的 ADB helper。Android 本地目前继续显示「仅本地」。 |
| C · SRAP 1.0 | 固定 16 字节帧头、控制信封、配对报文结构、能力结构、错误及三种音量作用域。新增同一份 11 条黄金样本，C# 编解码与协议边界、Kotlin 独立帧读写均通过。 | Gemini 使用 [协议](protocol.md) 和 [样本](protocol-fixtures/srap-v1.txt) 实现 Android 接收、配对和 JSON 派发，随后与 Windows 客户端完成真正的 TLS 往返、版本和权限变化联调。 |
| D · 特权能力与确认 | 规范按项定义自有音轨、第三方路由、第三方音量及独占捕获。路由成功需当前会话与目标的实际回读。 | Windows App 方式 UI 接入时按真实远端能力启用。第三方应用路由与音量、独占捕获均待目标范围和媒体实测。 |

主要 Windows 源码为 [业务端口](../../当前项目/SonicRoute.AndroidAudio/PhoneOperationPort.cs)、[现有连接协调器](../../当前项目/SonicRoute.AndroidAudio/AndroidConnectionManager.cs)、[档案存储](../../当前项目/SonicRoute.AndroidAudio/AndroidPhoneStore.cs)、[输出模型](../../当前项目/SonicRoute.AndroidAudio/AndroidModels.cs)和 [SRAP 编解码](../../当前项目/SonicRoute.AndroidAudio/SrapProtocol.cs)。

## Android 进入对端发布前的核对项

- Xiaomi 13 的 Shizuku Root 结果证明了在自身 UID 注册策略和修改自有播放器音量的可行性。当前探针代码在部分自有 UID 检查通过时会把 `audio.route.other_app` 或 `audio.volume.other_app` 写成 `available`。阶段 2A 的发布快照应保留探针证据，独立按实际已实现且授权的目标范围发布能力。回环注册成功但 `createAudioRecordSink` 返回空值时，独占捕获没有拿到可用数据通道。
- Android `OperationLease.isValidFor` 当前核对后端、后端代次和能力仍为 `available`。接入远端写操作时还要重核能力修订及受授权的作用域，覆盖能力保持 `available` 但目标范围或限制有变化的用例。
- 三类音量必须分别回读实际对象。系统音量不能由自有音轨增益推导，第三方应用也不能由自身 UID 的播放器探针推导。

## 本轮验证

- Windows `AndroidAudioChecks --gemini-android-contract` 通过 79 项。覆盖新旧输出键、IPv6 和含冒号 token、v1–v3 档案及音频配置、未知字段、旧路由 ID、App 公钥守卫和 SRAP 样本。另通过 `--managed-connections`、`--pairing-fixes`、`--identity-review`、`--device-actions`、`--android-file-directory`、`--direct-adb` 的隔离回归。涉及配对、断开和移除的结果来自夹具或本机回环。
- Windows Native AOT Full/Lite x64 构建成功，各自在隔离数据目录启动并由控制通道正常退出。Lite 按原版型排除 Android 手机组件。
- Android `:core:check` 通过，其中 `:core:checkSrapVectors` 独立检查 11 条帧样本。此轮 Android 工程只增加协议测试任务及交接资料，已交付 APK 的字节未变。
- 构建与验证产物位于 `E:/kunkun/SonarSwitch/outputs/android-gemini-stage1-20261127`。本轮暂未在实体手机重新测试 Windows ADB 配对、蓝牙/有线/USB 出声及 App 局域网端到端联调。
