# SRAP 阶段 2B · App 与 ADB 双通道身份绑定

本规范冻结阶段 2B 的互操作约定。两端公布 `identity.alias.adb` 且状态为 `available` 后方可启用。SRAP 1.0 早期客户端忽略新增能力。阶段 2A 对两个连接保留各自的档案，直到用户选定对象并完成下面的共存证明。

## 控制流

1. Windows 设备页由用户选择现有 **ADB 档案** 和 **已配对 App**。取当前已验证的 ADB 附件、进程内附件代次和档案版本。为本次操作保留 16 字节随机 `attachment` 标记，映射到该附件和代次。计算 `profileRef = SHA256(ASCII("SRAP1/PROFILE") || 00 || UTF8(profile.Id))`。主通道使用双方固定公钥的当前已认证 LAN 会话。请求 `alias.bind.begin`，体为 `{profileRef, attachment}`，沿用 `request`、能力修订和 `id`。每个 App 安装同时只允许一个绑定窗口。
2. Android 通过本地界面确认当前已配对电脑及绑定动作。等待确认最多 60 秒，拒绝或超时通过同请求返回错误。已确认时为本次授权开独占临时 **IPv4 127.0.0.1** TLS 动态端口，生成 `bindId` 16 字节、`challenge` 32 字节。其主会话响应为 `status:"done", result:{bindId, challenge, port, expiresInMs:30000}`。开始签发后仅开放 30 秒，主会话断开、信任撤销、用户取消或本地接收结束立即清理。主通道从始至终保持同一代，重连不能延用窗口。
3. Windows 通过已选 ADB 附件申请自己的临时 `tcp:0 -> tcp:<port>` forward。必须核实管理器仍持有原附件和同代授权，选择能证明该附件的 transport ID 或进程内附件句柄。禁止全局选机、模糊序列匹配、复用旧 forward、LAN 回退。PC 端仅连接本机回环并确认 forward 的所有权和对应远端端口。外部 ADB 下无法保证本机回环绑定或精确清理的实现关闭此能力。
4. Forward 上独立做固定公钥 mTLS，身份必须等于主会话角标两端；复用 SRAP `hello` 及版本确认，禁止签发业务会话。唯一后续帧为顶层 `alias.bind.prove`、`id`、`body={bindId, profileRef, attachment, parentSession, signature}`，无顶层 `session`。Windows 对下面定义的 transcript 以角色 1 签名。Android 以窗口冻结值及连接公钥重新计算，逐字段验证、检查双方仍受信、消费挑战并关闭监听，对相同 `bindId` 拒绝再次签发。证明件只经主会话事件 `event.body={name:"alias.bind.proof", bindId, requestId, profileRef, attachment, signature}` 返回，`requestId` 为步骤 1 的请求 `id`。Android 以角色 2 签名。回环证明错误可用 `alias.bind.result`、原 `id`、`body={status:"error",error}` 返回后关闭，不返回其他业务数据。
5. Windows 在相同主会话、主会话代次及 `seq` 校验后验签，再关闭独立 TLS、移除自己持有的 forward，检查已清理并回读原 ADB 附件代次、App 会话代次、档案状态和归属。只有全部有效且挑战在本地截止前才原子提交。成功后采用保留 ADB 档案 ID 的新代次，终止旧 App-only 档案的在途业务和重连任务。失败只报告错误并释放资源。

## 签名输入与字段

ECDSA P-256/SHA-256，DER 签名，经无填充 Base64url 传输。下表按顺序拼接恰好 195 字节。除端口和版本外固定长度字节字段均为无填充 Base64url。主会话 ID 在已有 SRAP 信封和 `parentSession` 字段中按 32 位小写十六进制编码，进入 transcript 前解码。端口、版本为 16 位无符号大端整数。

| 字节数 | 值 |
| --- | --- |
| 12 | ASCII `SRAP1/ALIAS` 加一个零字节 |
| 1 | 角色 1 Windows 请求、角色 2 Android 回证 |
| 16 + 32 | Android 生成的 `bindId`、`challenge` |
| 32 + 32 | Android、Windows 叶公钥 SPKI 的 SHA-256 |
| 32 + 16 | `profileRef`、`attachment` |
| 16 | 主会话 ID 解码原值 |
| 2 + 2 + 2 | 回环监听端口、主协议版本、协商的次版本 |

公开发现、邀请、绑定报文和诊断中不得发送 ADB 序列、ADB 密钥、设备 MAC、Android 硬件标识或 Windows 本地档案路径。绑定证明授权身份归属，业务授权仍通过当前传输的能力快照逐次校验。

两端可使用 [固定签名输入向量](protocol-fixtures/srap-alias-v1.txt) 核对字节序和角色，再各自生成和交叉验证 DER 签名。

## 冲突、资源与验收

- 两个不同 ADB 档案、旧归档归属占用、App 公钥已归属其他 ADB 档案、目标档案已有不同 App 公钥、相同会话出现证据矛盾，均返回 `identity_conflict`。仅允许迁移该次认证的 App 独立档案，并要求用户明确选择合并。保留 ADB 档案 ID、输出 ID、原配置和名称。可无歧义追加的 App 固定项采用稳定键合并，同时存在冲突设置时要求明确选择。以同一原子档案事务和既有备份、回滚机制提交，绝不静默改绑或复活其它归档。
- 操作、档案、主会话、ADB 附件任何代次变化返回 `stale_session`。权限或信任失效返回 `permission_lost`，用户拒绝和取消返回 `canceled`，超时返回 `timeout`。远端错误和重复回证不得触发合并。重连、同地址同名和同型号均不能作为共存证据。
- Windows 的失败及退出顺序为标记取消、撤销本地关联、关闭证明 TLS、按原 transport/端口移除本次 forward，再向仍在线的主会话发送按原 `requestId` 的 `cancel`。Android 收到取消、窗口失效或主会话结束立即使 nonce 失效并关闭证明流和监听。清理出错时保留可重试的本次 forward 所有权信息，禁止执行全局 `forward --remove-all`，不得误删后来重用的规则。Windows 在失败路径仍保留用户原档案和信任。
- Gemini 用回环 mTLS 测试向量核对签名输入字节、双方公钥匹配、单次消费、权限撤销、过期和主会话代次切换。Codex 实机核验可控的外部/进程内 ADB、USB 和无线 forward 生命周期，测试两台同型号、伪端口、USB/无线切换、同时连接与清理失败。只有两个业务传输各自保持认证且选定传输实际提供能力时才可发布可用。
