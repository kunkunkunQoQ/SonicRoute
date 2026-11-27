# SRAP 1.0 可选音频与媒体会话扩展

两项扩展沿用已认证 TLS 连接、16 字节帧头、能力修订及原请求 ID。旧手机只提供测试音。电脑以能力表区分接收 PCM、测试音及媒体控制，使用前检查当前档案、会话和授权。

## 电脑 PCM 播放到手机

手机通过 `audio.play` 的 `scope=owned_track`、`limits.pcm=s16le-48000-stereo-v1` 声明接收能力。电脑向 `audio.play.start` 提交奇数 `streamId`、`targetKey` 和同值的 `encoding`。成功响应回显编码和流 ID，并给出 `credit=4`。省略编码沿用 2A 自有测试音语义。新客户端在发送第一份音频前核对响应。每个接收器同时接收一路电脑音轨。

| DATA 负载偏移 | 含义 |
| --- | --- |
| 0–3 | 大端无符号包序号，从 0 开始并逐包加 1 |
| 4–11 | 大端无符号起始采样帧位置，从 0 开始并按上一包的立体声帧数递增 |
| 12 起 | 1–960 个 48 kHz 立体声帧，每帧左、右各 16 位有符号小端样本 |

负载长 16–3852 字节。每个包必须属于当前会话协商的流。手机在整包写入 AudioTrack 后发 `audio.credit {streamId, nextSeq}`，其中 `nextSeq` 是累计写入的包数。允许合并通知。电脑的已发送包数最多超出该值 4，等待额度的写入随会话或流结束取消。两个固定 DATA 向量包含静音、带符号样本和连续的采样帧位置。[PCM 黄金向量](protocol-fixtures/srap-pcm-v1.txt)

显式输出以 `audio.route.set` 和 `audio.routed` 对实际硬件的回读为准。手机用静音填充启动 AudioTrack，让电脑先确认输出。输入包、AudioTrack 写入和硬件听音是三种不同证据。失败、撤权或失去目标时应用释放所属流。正常停止使用 `stream.stop {streamId}`，回读 `{streamId,stopped}`，取消可用原 SRAP `cancel`。

## 手机媒体会话

用户在手机系统授予媒体会话访问，并在 SonicRoute 设置开启电脑媒体共享后，手机可声明 `media.control`、`scope=active_media`。本地授权和共享开关均由手机用户管理。

- `media.get {}` 返回 `{players:[...],truncated}`，最多 16 个会话。每项有 `sessionKey,package,title,artist,playing,actions,position,duration,speed`。位置和时长单位为毫秒，动作位与 Android PlaybackState 一致。临时 UUID 只标识此次观察到的播放器实例。
- 手机声明 `limits.packageState="1"` 时，可用 `media.get {packageName,fields:"state"}` 单次读取指定包。包名完整、区分大小写，每段以 ASCII 字母开头，仅包含字母、数字或下划线，以点分隔。恰好一个活动会话时只回 `{packageName,playing}`，其中 `playing` 取自该系统 MediaController 的播放状态。无匹配或无状态返回 `target_unavailable`，多个会话返回 `busy`。此请求与 `watchId` 互斥，标题、作者、会话键和其他包均不进入回执。查询复核与完整媒体列表相同的手机共享开关、通知访问和受信会话。
- Kotlin 和 Windows 共用播放和暂停两条固定的单包状态请求与结果。[单包媒体样本](protocol-fixtures/srap-media-state-v1.txt)
- `media.get {watchId}` 在读取时订阅无正文的 `media.changed` 事件；收到通知后用普通读取更新。watchId 使用小写 UUID，每条受信连接最多 8 个，重复注册同一 ID 幂等。关闭视图调用 `media.unwatch {watchId}`。断开、撤销共享或权限失效会清理订阅。
- `media.control {sessionKey,action,position?}` 仅接受 play、pause、next、previous、seek。只有 seek 可带 position。手机按本次可见的令牌和动作位校验，最多处理一条在途远端命令/连接。确认后的响应为 `{player:实际快照}`。拒绝、过期、超时、无法核实则返回对应错误。取消按原请求 ID 处理。媒体指令不使用自动重试。
# Android 应用声音到 Windows

系统音量沿用 SRAP 的 `volume.device`。设备声明 `limits.indexWrite="1"` 时，`volume.set {scope:"system_media",streamIndex:N}` 可写真实系统档位，成功回应带有平台重新读取的 `streamIndex/minIndex/maxIndex/percent`。`percent` 与 `streamIndex` 必须且只能提供一个，未扩展的对端继续使用百分比。可选 `isMuted` 表示系统对 STREAM_MUSIC 的实际静音状态。

本地用户在电脑页选择一个已连接的电脑、一个当前用户可启动且 UID 唯一的来源包。Android 当次录音权限与 MediaProjection 系统同意返回后，SonicRoute 将授权绑定到该公钥和确切会话，90 秒内等待接收方启动。设备断开、操作停止、系统撤回投影或授予过期都释放 AudioRecord 和投影前台服务类型。音频来源允许公开播放捕获时，手机保留原播放，Windows 可使用原有输出、音量和电平显示。来源播放策略限制的轨道可能持续产生零值。第三方音轨的实际设备与原生独立音量均需另行验证。[Android 播放捕获](https://developer.android.com/media/platform/av-capture)

* 能力 `audio.capture.app` 的作用域为 `other_app:<package>`。选定会话可见 `limits.pcm=s16le-48000-stereo-v1`、`limits.mode=playback_capture` 和随授权变更的 `grant`。其他会话仅收到需要本机授权的状态。
* `audio.capture.start` 接受 `{appKey,encoding,keepPhoneAudio:true}`。源需为手机本地选定的包；手机创建并启动 AudioRecord 后回复 `{streamId,encoding,credit:4}`，流号采用 Android 分配的偶数。
* Windows 建立对应 DATA 接收器后发送 `audio.capture.credit {streamId,consumedPackets:0}` 开始取音。每个数据包复用同一 PCM 固定向量。每个方向的序号和帧位置独立从零开始。Windows 回报已经写入本机 PCM 源的累计包数，手机回复两字段的实值。DATA 与额度回复可交错，序号严格递增，待消费数据最多四包。
* 八秒没有消费进展时中止。Windows 使用 `stream.stop` 或 `cancel{streamId}`，手机撤回发送 `stream.canceled`。重连需重新取得手机投影同意。非独占播放与应用级捕获遵守平台策略。[Android 投影生命周期](https://developer.android.com/media/grow/media-projection)
