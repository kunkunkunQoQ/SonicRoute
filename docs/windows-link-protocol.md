# Windows Link v1

## 连接与发现 / Connection and discovery

Windows Link 是 Full 版中的可选功能，需手动开启。连接和流传输代码位于 `SonicRoute.AndroidAudio/WindowsLink`，复用 Full 版自带的媒体组件。每一对设备通过一条双向认证的 TLS over TCP 连接同时收发数据。默认监听 TCP 46916 端口。IPv4 局域网发现使用 UDP 46917 端口，发现结果仅用于提示附近设备。手动输入 IP 地址时可指定端口。

Windows Link is opt-in in Full. The connection and stream code lives in `SonicRoute.AndroidAudio/WindowsLink` so it can reuse the media code that Full already ships. Each peer can send and receive at the same time. The protocol runs over one mutually authenticated TLS TCP connection per peer. The listener defaults to TCP 46916. IPv4 LAN discovery uses UDP 46917 and is a hint only. A manual IP can include a port.

每台设备都在受当前用户保护的本地存储中保存自己的证书和私钥。设备 ID 是对 DER 编码的叶证书计算得到的 SHA-256 摘要。TLS 握手验证对端持有对应的私钥。后续连接会核对已信任设备的证书 ID。首次配对时，双方先交换各自随机数的承诺值，再公开随机数。用户在两台电脑上核对由此生成的八位验证码，并分别确认。单凭广播地址无法建立信任。配对尝试与未认证连接均有数量和时限约束。如果两端同时发起连接，保留由 ID 较小的一端发起的连接。通常会沿用已建立的连接，只有连接尝试发生重叠时才需要仲裁。

Each peer has a private certificate in user-protected local state. Its ID is the SHA-256 of the leaf DER certificate. The TLS handshake verifies possession. Previously trusted IDs are pinned. New peers exchange randomness with a commitment before revelation, compare the resulting eight-digit code on both computers, and both explicitly confirm. An advertised address cannot establish trust. Pairing attempts and unauthenticated connections have bounded counts and deadlines. If both peers dial at once, the connection initiated by the lower ID wins. An established connection stays in use unless an overlapping dial must be resolved.

手动重连时，如果 TCP 直连失败，可以通过现有的 IPv4 发现端口请已保存的设备回连。请求包含接收方的证书 ID、发送方监听的 TCP 端口和签发时间，并由发送方使用现有身份私钥签名。接收方核对已固定的证书身份并验证签名，拒绝过期请求，也会按已保存的设备分别限制请求频率。验证通过后，接收方连接请求的局域网源地址，在双向 TLS 握手时再次核对同一身份。旧版设备会忽略这种请求。

Manual reconnect can use the existing IPv4 discovery port to ask a saved peer to dial back when direct TCP attempts fail. The request includes the recipient's certificate ID, the sender's listening TCP port and the issue time, signed by the sender's existing identity. The recipient checks its pin and the signature, rejects stale requests, and rate limits requests for each saved peer. It then dials the LAN source address and requires the same pinned identity over mutual TLS. Older peers ignore this request.

## 帧格式与流控制 / Framing and stream control

身份验证完成后，每帧以 1 字节的类型、按 .NET 字节序排列的 16 字节流 GUID，以及 4 字节的小端序载荷长度开头。视频按大小受限的分片发送，分片之间优先发送控制帧和音频帧。每台对端设备的队列和每条流的接收缓冲区都有上限。打开流的消息声明流类型、标题和发送方是否允许输入。流归创建方管理，连接断开或任一端主动停止时关闭。

Once authenticated, a frame starts with a one-byte type, a 16-byte stream GUID in .NET byte order, and a four-byte, little-endian payload length. Control and audio have priority between bounded video fragments. Per-peer queues and per-stream receiver buffers are bounded. An open message declares the stream kind, title, and whether the sender allows input. Streams are owned by their creator and close on disconnect or explicit stop by either side.

v1 音频格式为 48 kHz 双声道、有符号小端序 PCM16。视频默认使用 H.264 Annex B。两端可根据各自独立声明的编码和解码能力协商使用 HEVC Annex B，视频分片携带小端序的微秒时间戳和分片标志。每条流可单独设置视频选项。

Audio v1 is stereo 48 kHz signed little-endian PCM16. Video defaults to H.264 Annex B. Both ends can negotiate HEVC Annex B using separate encode and decode capabilities, with a little-endian microsecond timestamp and fragment flags. Video options are per stream.

认证后的握手消息可声明 `SupportsNativeVideo`。请求原生分辨率时，接收方设置 `NativeResolution` 并将高度设为 0。省略高度的旧版请求仍按默认 1080p 处理。原生分辨率接收端要求宽和高均为偶数，每边最多 8192 像素，总计不超过 16,777,216 像素。新版设备之间可传输最大 8 MiB 的压缩帧，对旧版接收端仍使用 1 MiB 上限。如果原生分辨率 HEVC 流切换到编码器不支持的尺寸，发送端通过分片标志的第 3 位（bit 3）标明 H.264 数据，接收端从带标记的关键帧恢复解码。

The authenticated hello optionally advertises `SupportsNativeVideo`. A native-size pull sets `NativeResolution` and a zero height. Earlier requests with an omitted height keep the 1080p default. A native viewer accepts even frame dimensions up to 8192 pixels per side and 16,777,216 pixels in total. New peers can exchange compressed frames up to 8 MiB, with the original 1 MiB limit for older receivers. If a native HEVC stream changes to a size unsupported by the encoder, bit 3 of the fragment flags marks H.264 data. The viewer resumes decoding at a marked keyframe.

输入和音量消息只作用于对应的流。远程输入须由发送端授权。共享单个窗口时，输入发给该窗口。共享显示器时，指针坐标映射到这块显示器上可接受输入的应用，键盘输入发给远端用户选中的控件。

Input and volume messages apply only to the matching stream. Input requires permission on the sender. A window stream targets the shared window. A monitor stream maps pointer coordinates to an eligible application on that monitor and keeps keyboard input with the remotely selected control.

## 服务与媒体资源 / Service and media resources

`WindowsLinkService` 管理启停、设备发现、信任、连接和流状态。界面读取设备与流的状态快照，并在 UI 线程分发通知。媒体适配器只接收选定的本机进程 ID、窗口句柄、PCM 数据或视频帧，不依赖网络协议和界面。媒体资源仅在对应流活动时占用。传输优先级、取消操作和资源上限由连接层管理。

`WindowsLinkService` owns start, stop, discovery, trust, connection and stream state. UI code reads peer and stream snapshots and dispatches notifications onto the UI thread. Media adapters receive only selected local process IDs, window handles, PCM or video frames. They have no network protocol or UI dependencies. Media resources exist only while their streams are active. Transport priority, cancellation and limits live in the connection layer.

## 应用列表与采集 / App catalogs and capture

发送端使用缓存的 Windows 音频会话列表响应音频目录请求，跳过窗口枚举。应用是否有音频会话与它当前是否正在播放分别判断。在本机快捷面板中隐藏的应用也会从目录中排除。新会话建立或应用可见性变化时，发送端向兼容设备发送一条无载荷的目录失效通知。已打开的快捷面板会清除快照，仅重新加载当前可见的视图。

The audio catalog request uses the sender's cached Windows audio-session enumeration. It skips window discovery and reports whether an app has an audio session independently of whether it is playing. The sender excludes local quick-panel hidden apps from the catalogs. On a session creation or visibility change it sends an empty catalog invalidation to compatible peers. An open quick panel clears its snapshots and reloads only the visible view.

发送端使用现有的 WASAPI 进程回环采集。采集整机声音时排除 SonicRoute 自身的进程树，采集应用声音时仅包含选定的进程树。Windows Link 启动时，发送端使用实际的 PCM 采集格式探测此接口，并在设备发现和认证后的握手消息中声明结果。即使本机无法采集，也可以接收对方的音频。接收端复用现有的 PCM 环形缓冲区和 WASAPI 输出。

Existing WASAPI process loopback supplies system audio by excluding SonicRoute's process tree and app audio by including the selected process tree. At Windows Link startup, the sender probes the actual process-loopback interface with the PCM capture format and advertises its result in discovery and the authenticated hello. A peer can still receive audio when its own capture interface is unavailable. Receive-side playback reuses the existing PCM ring and WASAPI output.

支持显示器采集（能力位 `0x2000`）的设备在响应窗口目录请求时，也会列出活动显示器。每项包含显示器编号、尺寸、主显示器标志和实例标识。发送端在打开流时核对实例标识。窗口和显示器均通过 Windows Graphics Capture 采集。发送端和接收端使用 Media Foundation 处理 H.264，有兼容的 Windows HEVC 编解码组件时也可使用 H.265。HEVC 协商消息包含起始画面尺寸，接收端据此选择解码组件。旧版设备继续使用 H.264。

Peers with monitor capture capability (0x2000) also include active monitors in the requested windows catalog. A monitor entry carries its display number, size, primary status and instance identity. The sender verifies that identity when opening the stream. Both windows and monitors use Windows Graphics Capture. The sender and viewer use Media Foundation H.264 or, when a compatible Windows HEVC transform is available, H.265. HEVC offers include the initial dimensions. The viewer selects its transform from the actual offer. Old peers continue to use H.264.

## 播放确认与音频路由 / Playback readiness and audio routing

如果两端都声明支持播放就绪确认，接收端会在本机播放设备第二次请求渲染缓冲区后回复，即使源应用处于暂停状态也能确认。若播放初始化失败，接收端会报告失败。发送端收到就绪回复后才将路由标为活动。

When both peers advertise audio readiness, the receiver replies after the local playback endpoint requests a second render buffer, including when the source app is paused, or reports an initialization failure. The sender publishes the active route after that reply.

仅在目标设备播放应用声音时，可以使用发送端上空闲的备用音频输出。发送端先保存该应用原有的多媒体和控制台音频路由，按需将备用输出静音，并核实应用已切换到该输出。播放结束后，发送端恢复仍由它管理的改动。如果所有活动输出都已由用户静音，发送端只监测这一状态，保留原有路由和静音设置。

A destination-only app stream can use an unused secondary render endpoint. The sender saves the app's multimedia and console routes, mutes the spare endpoint if needed, verifies the app moved, and restores the changes it still owns when playback ends. If every active output is already muted by the user, the sender monitors that condition without changing any route or mute setting.

没有专用输出时，发送端可在应用目录项中声明 `CanMuteSourceSpeaker`。接收端请求仅在目标设备播放时，可通过 `AllowSpeakerMute` 明确允许发送端在收到 `AudioReady` 后临时静音应用当前使用的输出。在此期间，共用该扬声器的其他应用在发送端也会静音。两端同时播放以及旧版客户端发来的请求仍按原有方式处理。

When there is no dedicated output, a sender can advertise `CanMuteSourceSpeaker` in an app catalog entry. A destination-only app pull can then explicitly request `AllowSpeakerMute` to temporarily mute the app’s current render endpoint after AudioReady. Other apps using that speaker are silent locally for the duration. Shared playback and requests from older clients retain their existing behavior.

支持窗口音频（能力位 `0x4000`）的设备可将音频请求关联到正在传输的视频流。共享应用窗口时使用该应用的进程音频，共享显示器时使用发送端的系统混音。最后一个查看窗口关闭时，随画面自动开启的音频会释放。用户单独建立的音频路由继续播放。

Peers with window audio capability (0x4000) can associate an audio pull with a live video stream. A window uses its application's process audio. A monitor uses the sender's system mix. The final viewer releases automatically opened audio, while an explicit audio route remains active.

<a id="file-transfers"></a>

## 文件传输 / File transfers

文件发送请求、接收确认和取消操作均复用现有的双向认证连接。支持文件传输（能力位 `0x1000`）时，每个对端一次传输一个文件，多选文件按顺序发送。接收方可将文件保存到已配置的文件夹（初始为 Windows 的“下载”已知文件夹），也可指定其他位置。双方已认证并开启完全信任时，文件可直接送达配置的文件夹。若该文件夹已有同名文件，最终移入时会为新文件名追加数字后缀。

File offers, acceptance and cancellation use the existing mutually authenticated connection. With the file capability (0x1000), each peer can transfer one file at a time. Multiple selections are sent sequentially. Receivers can accept to their configured folder (initially the Windows Downloads known folder) or choose an explicit destination. An authenticated peer with mutual full trust can deliver directly to the configured folder. A collision in that folder adds a numbered suffix during the final move.

一次磁盘读取最多对应四个 12 KiB 的传输帧。写入连接时，文件块之间优先发送音频帧和控制帧。接收端每收到 96 KiB 数据确认一次，先写入临时文件，最后移入目标位置，保留已有文件。发送端在收到最终写入确认后才报告完成。断开连接或取消会结束传输并清理未完成的数据。支持空文件，单文件最大 16 GiB。

A disk read groups up to four 12 KiB wire frames. The writer gives audio and control frames priority between file chunks. The receiver acknowledges each 96 KiB window, writes to a temporary file, and commits without replacing an existing file. The sender reports completion after the receiver acknowledges that commit. Disconnect and cancellation retire the transfer and clean up partial data. Empty files are supported. The limit is 16 GiB per file.

## 完全信任 / Full trust

完全信任在首次配对之外单独协商。用户在两台电脑上分别生成一个随机八位验证码。每台电脑通过已固定对端身份的 TLS 连接发送质询标识，并核验用户在另一台电脑上输入的验证码。双方必须在同一轮协商中都验证成功。三分钟超时、连接断开、用户取消或同一个验证码输错三次，都会结束本轮协商。两端保存这次授权的登记标识，每次新建连接时都必须出示匹配的授权记录。撤销信任会清除该记录及由此获得的权限。完全信任下接收的文件保存到磁盘，远程输入的范围仍限于共享目标。

Full trust is negotiated separately from initial pairing. Each PC explicitly generates its own random eight-digit code. It sends a challenge identifier over the existing pinned TLS connection and verifies the code typed on the other PC. Both verifications must succeed in the same exchange. The exchange expires after three minutes, disconnect, cancellation or three incorrect attempts against a code. Both peers save the resulting enrollment identifier and must present matching grants on each new connection. Revoking trust clears the grant and permissions derived from it. Files accepted through full trust are saved to disk. Window input remains scoped to the shared target.

## 实现参考 / Implementation references

C# 实现为大批量数据设置独立的逻辑队列。每个对端复用一条 TLS 连接，文件缓冲区按需分配。LocalSend 协议描述了先发送元数据、获得同意后再上传文件内容的流程。KDE Connect 则将文件载荷传输任务与控制消息分开调度。

The C# implementation uses a separate logical queue for bulk data. There is one TLS connection per peer and file buffers are allocated on demand. LocalSend documents offering metadata for approval before uploading binary data. KDE Connect schedules file payload jobs independently of its control messages. [LocalSend protocol](https://github.com/localsend/protocol/blob/main/README.md) · [KDE Connect LAN implementation](https://github.com/KDE/kdeconnect-kde/blob/master/core/backends/lan/landevicelink.cpp)

Windows 进程回环 API 可以采集选定进程树的声音，其采集接口独立于物理输出设备。是否在本机播放与采集哪些声音可以分别决定，OBS 的应用音频采集指南也采用了这一做法。

The Windows process-loopback API captures a selected process tree. Its capture interface is independent of the physical output endpoint. Local monitoring and capture remain separate routing decisions, as in OBS's application capture guidance. [Microsoft ApplicationLoopback sample](https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/ApplicationLoopback) · [OBS application audio capture guide](https://obsproject.com/kb/application-audio-capture-guide)
