# SonicRoute Android 连接接口 · SRAP v1.0

前台剪贴板的能力、控制信封、数据流、修订校验和双方样本见[前台纯文本剪贴板](srap-clipboard.md)。

SRAP 1.0 定义帧头、控制信封、音频作用域、能力词表和 App 局域网连接。[黄金样本](protocol-fixtures/srap-v1.txt)覆盖正常帧及边界。[双通道协议](srap-adb-alias-stage2b.md)定义 App 与 ADB 档案绑定和启用条件。文件和屏幕分别使用经能力协商的[文件传输扩展](files-transport.md)和[屏幕共享扩展](screen-transport.md)。

Codex 根据 Gemini 提交的跨端需求审查并维护本规范。接口变更需核对旧版本、共享设备档案、功能能力和现有 ADB 行为，并把决策与互操作结果写入 [开发交接](../HANDOFF.md#后续分工与跨端交接)。

## 共同的业务端口

电脑音频 DATA 流的窗口、序号和信用回执，以及本地授权的播放器控制与变更通知，见[音频与媒体会话扩展](media-transport.md)。

| 接口 | 输入与结果 |
| --- | --- |
| `PhoneTransportDiscovery` | 产生 `Candidate`、`VerifiedAttachment`、`AttachmentLost`。发现结果仅有传输类型、地址与到期时间。只有出示当前已信任凭据并通过身份归属校验才能发布 `VerifiedAttachment`。 |
| `PhoneConnectionCoordinator` | `pair`、`reconnect(profileId)`、`disconnect(profileId)`、`unpair(profileId)`、`forget(profileId)`、`resolve(profileId)`。维护每档案独立代次、已选择传输、设备/能力快照和取消范围。相同设备的有效连接可并存，只有当前选中的传输接收同一业务域的指令。 |
| `PhoneOperationPort` | 输入 `OperationContext(profileId, attachmentGeneration, expectedCapabilityRevision, cancellation)` 及类型化参数。查询目录、媒体、音量和输出，启动/停止媒体、文件或剪贴板会话，回读操作结果。App adapter 用协议，ADB adapter 以既有 helper 和数据通道完成相同调用。 |
| `PermissionBackend` | Android 本地端 `observe`、显式 `requestAuthorization`、`capabilities`、`acquire(operation, scope, generation)`、`invalidate`、`close`。租约提供白名单业务调用及 cancel/close，操作前复查身份、授权、作用对象和系统状态。普通用户授权 UI 在 Android 本地进行。 |

业务请求按能力与作用域协商。目录和音频使用 `catalog.list`、`volume.get` / `volume.set`、`audio.devices.list`、`audio.capture.start`、`audio.play.start`、`audio.route.set`。媒体使用 `media.get`、`media.control` 和 `media.unwatch`。画面使用 `display.start`、`display.next`。文件使用 `file.list`、`file.offer`、`file.open`、`file.chunk`、`file.finish`。剪贴板使用 `clipboard.watch`、`clipboard.get`、`clipboard.set`、`clipboard.unwatch`。各扩展规定 `stream.stop` 或 `cancel` 的作用范围。写操作只在同一代次且具备幂等键时重试，结果不明先回读。输入、剪贴板、媒体启动和文件确认不跨代重放。协议只接受具名业务调用和授权范围内的逻辑文件名。

共用错误包含 `not_implemented`、`permission_required`、`permission_lost`、`unsupported`、`target_unavailable`、`identity_conflict`、`not_paired`、`version_mismatch`、`stale_session`、`invalid_argument`、`busy`、`timeout`、`canceled` 和 `io_failure`。允许携带安全的原因码和短期可重试标记，UI 使用本地文案。返回成功必须有对应 API 或被操作对象的确认；仅接受任务时返回进行中状态。

## 发现和配对

1. 默认不监听。用户开启「连接电脑」后，App 在所选本地接口和动态端口创建监听，限时公布 `_sonicroute._tcp`。NSD TXT 只包含主要版本、随机短期实例标志和是否开放配对，禁止可用于永久跟踪的标识、密钥或真实用户数据。手工导入邀请与发现同样核验身份。配对窗口初始拟定 120 秒，一次性邀请、最多五次错误尝试及少量并发；暂停或离开页面时丢弃未完成邀请。
2. Android 生成高熵一次性邀请（至少 128 bit 随机秘密），内容包含协议、地址提示、加密身份公钥 SHA-256 指纹和秘密。采用可复制文本；二维码只编码同一邀请。已有 ADB 的六位码和 `WIFI:T:ADB` 仅用于 ADB。用户在 Android 本地确认 Windows 名称与指纹。
3. 使用 Android JSSE 和 Windows SslStream 的 TLS。TLS 1.3 优先，若受支持 Windows 版本需要 TLS 1.2，则只允许经验证的 ECDHE+AEAD 套件，并验证协商矩阵。首次 TLS 必须匹配邀请中的 Android 公钥指纹后才能递交秘密。Android 首配窗口仅允许固定配对消息，只有秘密、Windows 持钥证明和本地确认全通过时提交信任。Windows 持钥证明绑定双方公钥、邀请 ID、版本和服务器随机挑战，采用平台 ECDSA P-256/SHA-256。私钥通过 Android Keystore 与 Windows 系统保护存储。配对确认丢失时允许用同一已验证的请求恢复收据，禁止重消费邀请。
4. 后续连接使用双向 TLS 固定双方公钥。失败、键变化、证书更新不匹配、过期或身份冲突时停止会话，按密钥轮转或重新配对流程处理。Android 主动撤销发送认证会话内的 `session.error(permission_lost)` 并关流。Windows 收到该事件或经公钥匹配的握手收到明确 `not_paired` 后关闭自动恢复，保留档案供用户重新配对。Windows 本地撤销关闭通道并删除可用信任状态。SRAP 1.0 未提供远程取消配对命令，手机端的信任通过手机列表管理。配对过程和日志不包含 ADB 密钥、设备序列及剪贴板正文。[Android Keystore](https://developer.android.com/privacy-and-security/keystore)

### 邀请 URI

规范形式为 `sonicroute://pair?v=1&id=<inviteId>&s=<secret>&fp=<SHA256>&h=<ip>&p=<port>`。可省略整组 `h` 和 `p`，此时从当前发现记录选择候选。候选只提供拨号地址，信任始终来自邀请公钥和双方的认证。广播服务标签为 `sr-<8 位小写十六进制>`，TXT 的 `v=1`、`inst=<16 位小写十六进制>`、`pair=0|1` 是临时提示。

| 字段 | 编码和校验 |
| --- | --- |
| `v` | 唯一支持的字面值 `1`。 |
| `id` | 正好 16 字节，RFC 4648 无填充、规范 Base64url。 |
| `s` | 16–32 字节，同一 Base64url 格式。2A Android 生成 16 字节。拒绝非零尾位、空白、`=`、`+`、`/` 和超过上限的长度。 |
| `fp` | SHA-256 的 64 个 ASCII 十六进制字符。生成时写大写，读取允许大小写，档案归一化大写。 |
| `h` | IPv4 使用四段规范十进制。IPv6 接受裸字面量或一层方括号，禁止 IPv4-mapped IPv6。IPv6 链路本地地址需要接收方本地数值接口索引，例如 `[fe80::1%2512]` 中的 `12`。Android 接口名、数字索引在另一台设备上通常不可复用，Android 生成器应优先提供 IPv4/非链路本地 IPv6，或省略地址，由 mDNS 按本地接口补作用域。禁止 DNS 主机名、通配、组播和环回。 |
| `p` | 1–65535 的十进制端口，禁止符号、空白和前导零。 |

URI 长度上限 1024 个字符，允许去除整串首尾空白。参数名小写且只出现一次，顺序无关。拒绝未知字段、片段、路径、空字段和重复字段。除 IPv6 接口分隔符 `%25` 外禁止百分号转义，避免多重解码和分隔符歧义。可复制文本与二维码承载相同字节。Windows 当前通过主动粘贴导入邀请文本，支持粘贴扫描器解码的 URI。邀请为一次性凭据，禁止用于日志、诊断、命令行参数、档案和同步数据。只在匹配 Android 公钥且协商密码套件合规后发送秘密。复制到系统剪贴板时，Android 应设置敏感标记并尽可能限时清除。Windows 导入界面不回显邀请，不主动改写用户的剪贴板。

Windows 用当前用户的命名 CNG P-256 私钥，按本地应用数据目录区分身份。Schannel 按系统策略协商，客户端在发送 SRAP 数据之前检查 TLS 1.3 AEAD 或 TLS 1.2 ECDHE-ECDSA AEAD 白名单，并校验证书期限和叶证书 SPKI。客户端证书选择也要求远端公钥匹配。禁用明文降级、TLS 恢复和重新协商。配对确认收据丢失后，使用同一身份建立新的固定公钥 mTLS，只有收到已认证会话的首份能力快照才视为已配对。自动重连以网络地址、mDNS 通知和本地会话关闭驱动，先尝试上次认证端点，再验证短期发现记录。手动断开、移除或撤销后停止该档案自动重连。[Microsoft SslStream TLS 指南](https://learn.microsoft.com/dotnet/core/extensions/sslstream-best-practices)

## 统一档案与别名

- Windows 已有 `AndroidPhoneProfile.Id` 是产品档案 ID。ADB 的现存身份材料是 ADB 别名证据。App 一侧公开的身份是安装密钥的公钥哈希，正常模式不读硬件序列、MAC 或 shell 的 Android ID。安装重置、密钥丢失和 Android 用户切换按身份变化处理。
- 阶段 2A 通过明确配对登记 App 档案，使用同一设备协调器、档案仓储和业务入口。阶段 2B 才通过 [双通道绑定协议](srap-adb-alias-stage2b.md) 将 App 归入现有 ADB 档案。
- 两个同时在线设备、端口重用、同型号、重复序列或名称相同均不能自动合并。缺少共存证据时保持待绑定，可等原连接恢复，或由用户明确选择建立新档案。冲突、代次切换、移除或用户取消中止提交。App 和 ADB 独立保存与撤销认证，档案级断开和移除仍由一个协调器发出。
- 迁移以旧档案 ID 为准，避免重写 Windows 输出 ID 或旧配置键。写入前备份，保留未知字段，检测旧文件损坏时回退。重连不会绕过手动断开，也不会为了执行 App 端缺失功能自动切换到更高权限。切换传输前验证同一档案，用新代次重建所需会话。
- Windows 档案 v3 的可选字段 `AppPublicKeySha256` 存储大写 SHA-256，`LastAppAddress` 只记录上次经认证的端点，`AppTrustRevoked` 关闭当前凭据的使用。旧 ADB 档案绑定字段的写入需要经过共存证明的同代附件。已归属另一 ADB 档案（含已移除档案）的密钥、隐式密钥替换和旧代次都拒绝绑定。首版每档案存一把 App 安装公钥，使用确认流程轮转。
- 档案「断开」保留信任和设置；「移除」关闭会话、清除该档案路由并阻止自动恢复，保留已有授权；「取消配对」才撤销选定档案的信任。未确认的远端撤销记录在案，配对侧及时撤销本地访问。

## 首版网络编码

TCP/TLS 连接只公开本协议。首个完整帧须为 `hello`，校验协议版本后进行信任或配对流程；后续命令只在已验证会话生效。App 方式使用以下帧，旧 ADB 继续运行现有报文。

| 偏移 | 固定首部字段，均为网络字节序 |
| --- | --- |
| 0 | 4 字节 ASCII `SRAP` |
| 4 | 1 字节帧类型，1＝控制 JSON，2＝二进制流 |
| 5 | 1 字节标志，首版固定 0 |
| 6 | 2 字节保留值，首版固定 0 |
| 8 | 4 字节无符号 streamId，控制帧固定 0，双方分配奇数/偶数 ID（Windows 奇数） |
| 12 | 4 字节无符号 payload 长度，读取前完成上限检查 |

两类帧的负载长度为 1–65536 字节。控制帧是严格 UTF-8 JSON 对象，必含 `type`（最多 128 个字符）、`body`（对象）。关联请求的消息使用顶层 `id`（带连字符的小写 UUID）；已认证连接使用顶层 `session`（服务端生成的 16 字节随机值，编码为 32 位小写十六进制）。事件使用顶层 `seq`（会话内单调递增的非负 64 位整数）。握手时省略 `session`，受信的客户端从第一条 `capabilities` 获取会话标识。JSON 拒绝重复键、无效数字、深度超过 16 的值；各字段还需检查类型和长度。帧类型、标志、流 ID、认证和消息顺序通过校验后才派发。

- `hello` 体为 `{protocol:"sonicroute.android", major, minorMin, minorMax, appVersion?}`，版本字段为 0–65535 的整数。当前值为主版 1、次版范围 0–0。主版必须相同，次版取交集最大值。无交集时发送握手事件 `body={name:"session.error", error:"version_mismatch"}`，随后关闭。次版可添加可忽略字段或协商后的能力。
- `pair.begin` 体为 `{inviteId}`。`pair.challenge` 回复同一 `id`，体为 `{inviteId, challenge}`。固定长度二进制字段使用无填充 base64url。`inviteId` 为 16 字节，`challenge` 为 32 字节。客户端提交 `pair.prove`，含 `{inviteId, secret, publicKey, signature, displayName}`，其中公钥为 DER SPKI。签名输入依次拼接 ASCII `SRAP1/PAIR`、一个零字节、原始 inviteId、原始 challenge、Android 和 Windows 的 SPKI SHA-256（各 32 字节），以及大端 16 位 major/minor。签名使用 ECDSA P-256/SHA-256，签名采用 DER 编码。`pair.result` 按原请求 `id` 返回 `pending`、`done` 或错误。用户确认并持久化后才能返回 `done`。操作会话随后通过双向 TLS 建立。
- `capabilities` 是完整快照，体为 `{capabilityRevision, entries:[{id,state,reason?,scope?,limits?}]}`。修订为非负 64 位整数，在同一会话内变更时递增。`reason`、`scope` 为字符串，`limits` 为字符串到字符串的对象。状态为 `not_implemented`、`needs_authorization`、`unsupported`、`unavailable` 或 `available`。忽略未知能力及状态，未宣告的能力不可用。请求声明当时的修订，服务端重新校验实际授权、对象与作用域。回退、撤权或资源变动时发新快照并取消受影响操作。
- `request` 体为 `{op, capabilityRevision, params, idempotencyKey?}`。`response` 使用原请求 `id`，体为 `{status:"done"|"pending"|"error", result?, error?, reason?, retryable?}`。`error` 使用前述 14 个错误 ID。`cancel` 的体为 `{requestId}` 或 `{streamId}`，取消和关闭幂等。`event` 的体为 `{name, ...eventFields}`。
- 二进制媒体按协商过的 codec、PTS、上限、序号和分片规则重组。每路流拥有有界窗口，控制与取消优先，慢消费者背压。文件由用户确认大小、逻辑名称、目标、摘要，校验后提交。剪贴板最多 65536 个 UTF-8 字节，正文走数据帧，控制请求使用 `{streamId, byteLength, revision}`，通过独立修订防回环。

### 音量、输出键及路由验收

| `volume.get` / `volume.set` 的 `params.scope` | 寻址、效果与回读 |
| --- | --- |
| `system_media` | 需要 `volume.device`。按 Android 当前用户的 `STREAM_MUSIC` 操作，写入 `percent`（整数 0–100），回读含 `streamIndex`、`minIndex`、`maxIndex`、`percent`。固定音量或系统拒绝时返回具体原因。 |
| `owned_track` | 需要 `audio.play`。以已认证会话拥有的 `streamId` 寻址。写 `trackGainPercent`（整数 0–100）、`trackMuted`（布尔）之一或同时写，回读两项。只作用于 SonicRoute 的目标音轨。 |
| `other_app` | 需要 `audio.volume.other_app`。目标为当前会话目录中的 `appKey`，服务端解析用户与实际 UID，外部请求不可直接指定 UID。写 `percent`（整数 0–100），结果标明经回读的作用范围。播放器变化后的重应用须以同一授权为条件。 |

能力词表为 `catalog.apps`、`media.control`、`volume.device`、`audio.capture.device`、`audio.capture.app`、`audio.capture.exclusive`、`audio.play`、`audio.devices`、`audio.route.owned`、`audio.route.other_app`、`audio.volume.other_app`、`bluetooth.control`、`display.mirror`、`display.virtual`、`display.input`、`files.transfer`、`clipboard.sync`、`automation.rules`。

输出逻辑键为 `current`、`speaker`、`bt:<token>`、`wired:<token>` 和 `usb:<token>`。各 token 是不透明字符串，可包含冒号。Windows 合成 ID `android-out:<phoneKey>:<sinkKey>`，既有档案和输出键原样兼容。只有当前后端实际枚举并可操作的端点进入可选列表。

`audio.route.owned`、`audio.route.other_app`、`audio.volume.other_app`、`audio.capture.exclusive` 独立协商。注册 AudioPolicy、能枚举播放器或在自身 UID 探针中通过，仅属可行性证据。第三方应用仍需真实目标回读才能发布 `available`。独占回环要验证实际采集和来源播放策略，测试有信号时以听音和数据验收，运行时允许合法的静音内容。

`audio.route.set` 接受任务时返回 `pending`。随后在事件体用 `requestId` 关联原请求。只有同会话、同目标 `targetKey`、明确 `actualKey` 且 `verified=true` 才提交界面结果；显式端点要求 `actualKey == targetKey`。`current` 可解析为另一个实际端点键。端上核验预算为 2000 ms，网络等待另受会话时限约束。失败或撤销时释放该操作的租约。旧调用可生成 App 自有测试音。宣告 PCM 能力的双方可选择电脑发送真实音频，手机自有 AudioTrack 依选择的物理输出播放。第三方应用本机原生路由的选择与回读见[本地应用路由说明](native-app-routing.md)。

协议验收必须覆盖半帧、乱序、负载上限、未知能力、授权更新、断线、重新协商、拒绝无效密钥和同时多设备。可还原的成功条件以真实目标回读为准。

当前黄金文件每行依次给出名称、帧类型、流 ID、读取分片大小、预期结果、UTF-8 负载和完整帧十六进制。C# 检查消费同一文件并验证帧编解码、JSON、能力表和版本协商。Kotlin `:core:checkSrapVectors` 独立验证帧字节、拆包、截断和尺寸边界。Android 的实际 JSON 派发和双端 TLS 会话在阶段 2A 接入时使用同一文件验收。
