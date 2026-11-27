# Windows Android 源码审查与对应关系

审查基线为 `E:/kunkun/SonarSwitch/当前项目` 的 Native AOT Full，HEAD `31c2ef4f676ba6c8912673319c62a1dcf99167e5`。Windows 工作树另有本地 UI 修改，审查时以实际所见入口为准。下文的源码链接指向该工作区。WPF 1.36 仅用作旧界面与配置兼容参考。源码显示当前已进入 2.00 后续开发，容器入口文档的 1.37 是较早记录。

Windows 设备页的配置、连接、设备 Dock 与刷新在 `PhoneDevicePageView` 分部类。它和画布、手机伴随窗通过 `PhonePreview` 汇总档案、在线状态、音频、文件、剪贴板和显示。Full 构建才引入 `SonicRoute.AndroidAudio`。AirPlay 属于同页独立接收器，不进入 Android 设备协议。Windows 旧 ADB 实现及数据保持作为兼容路径，以下对应项均为后续接入计划。 [设备页](../../当前项目/Native/Product/Views/PhoneDevicePageView.cs) · [连接页](../../当前项目/Native/Product/Views/PhoneDevicePageView.Connection.cs) · [协调入口](../../当前项目/Native/Product/Views/PhonePreview.cs) · [Full 条件](../../当前项目/Native/Product/Product.csproj)

## 发现、配对和状态

| 已有入口与可观察行为 | 当前协议、配置或实现 | App 接入后共用处 |
| --- | --- | --- |
| 扫手机无线调试二维码 | Windows 生成 `WIFI:T:ADB;S:studio-…;P:…;;`，查匹配 `_adb-tls-pairing._tcp` 后发起 ADB 配对，结束后查 `_adb-tls-connect._tcp` 的实际连接端口。页面切换、重发及过时代次取消原任务。 | 共用「添加设备」流程和忙碌、取消、重试状态。邀请格式由传输决定。 |
| 六位码、手工地址、搜索保存设备 | 六位码配对要求 Android 配对页端口；手工直连用连接端口和已建立信任。地址解析校验 IP 和端口。mDNS 查询分离配对与连接，处理接口、IPv4、IPv6 地址及同名服务。搜索保存档案时可探测候选 TCP，可达只是提示，仍需验证连接及设备身份。 | 相同配对或重连协调器。App 安全邀请带高熵密钥及指纹，发现候选和在线是不同状态。 |
| ADB 选择与数据通道 | 外部 ADB 服务在线时优先走 TCP 5037 smart-socket，必要时走进程内 Wi-Fi ADB/WinUSB。直连按 ADB `CNXN/AUTH/STLS` 和 multiplex 流处理 `shell_v2`、`localabstract:`、`sync:`，保留 transport ID/代次，限制包队列。旧直连 ADB 的认证与新版 TLS 路径由原适配器处理；无线配对内置 SSPI TLS、TLS 导出密钥、SPAKE2/PeerInfo/AES-GCM，使用 ADB RSA 客户端身份。 | 产品操作端口接在 ADB 命令之上。App 使用同一有类型的操作模型和独立 LAN 协议。旧 ADB 认证、报文和组件留在 ADB 适配器。 |
| USB 切换、复原原设备、重连 | Windows USB 通知、外部设备跟踪、直连传输事件和已保存设备的 mDNS/网络事件。旧仅有 USB 的档案在显式重连时可利用稳定身份查 Wi-Fi。无线地址可能更换，重连复用档案，实际匹配已授权的新传输，再按当前代次提交。 | 传输统一发布发现、已授权和丢失事件。用档案协调 USB、ADB Wi-Fi 及 App，用户不用针对不同传输重建路由。 |
| 在线、离线、未授权、冲突、手动断开、移除 | `AndroidConnectionManager` 管理可用附件与版本。档案为 `AndroidPhoneProfile.Id`。ADB 主要证据为稳定 `device:` key、`ro.serialno`、mDNS/transport 当次证据；使用 shell 读取的 Android ID 哈希区别同序列号，缓存按传输。IP、端口、型号和重用 transport ID 均不足以认定设备。 | App 公钥为可信别名，档案 ID 保持原值。绑定 ADB/App 别名需要共存证明和明确选择。能力不足不等同于离线。 |
| 改名、断开、取消配对、移除 | 改名写入档案，断开收尾会话并记载主动断开，USB 场景可停止接收。取消配对由绑定到实际认证密钥的传输执行，用相应 key 指纹撤销当前 ADB 授权。单独移除设备则关闭相关任务、清理路由并记录移除历史，保留手机配对授权。 | 共用原动作和状态机。移除保留授权及禁止自动重加的记录；取消配对显示实际已撤销与待对端撤销的凭据。 |

源码入口分别为 [无线连接与恢复](../../当前项目/SonicRoute.AndroidAudio/AndroidWirelessConnection.cs)、[mDNS](../../当前项目/SonicRoute.AndroidAudio/AndroidMdnsDiscovery.cs)、[直连与仲裁](../../当前项目/SonicRoute.AndroidAudio/DirectAdbTransport.cs)、[ADB 层接口](../../当前项目/SonicRoute.AndroidAudio/IAdbTransport.cs)、[报文层](../../当前项目/SonicRoute.AndroidAudio/AdbWireProtocol.cs)、[配对 TLS](../../当前项目/SonicRoute.AndroidAudio/SspiPairingTls.cs)、[配对报文](../../当前项目/SonicRoute.AndroidAudio/Spake25519.cs)、[连接管理](../../当前项目/SonicRoute.AndroidAudio/AndroidConnectionManager.cs)、[设备快照](../../当前项目/SonicRoute.AndroidAudio/AndroidDeviceDiscovery.cs)、[身份富化](../../当前项目/SonicRoute.AndroidAudio/AdbClient.cs)及[离线音频恢复](../../当前项目/Native/Product/Views/PhonePreview.AudioRecovery.cs)。

## 业务功能

| Windows 行为 | 已有接口与数据 | App 端能力映射 |
| --- | --- | --- |
| 设备详情选人、固定应用、目录、搜索、图标、分类、刷新、智能音量例外 | `AdbClient.AppsAsync/ResolveUidAsync/EncodersAsync` 调用 `app_process` Catalog/EncoderCatalog，按需读取并缓存；固定与快捷面板记录归档案。包名展示和配置可稳定保存，实际 Android UID 在使用时解析，含多用户、分身、隔离 UID 情况。 | `catalog.apps`，目录项带用户范围及稳定应用键。现有固定应用、搜索、图标缓存和刷新策略共用。编码器目录只显示实际组合。 |
| 媒体状态、封面、上首/下首、播放/暂停、拖时间轴、连接质量与手机媒体音量 | 独立的 shell MediaSession helper 双向。JSON 行快照，文本命令及 `ping/pong`；根据 Android 可控会话启用控制项。整机音量和应用音量不同。 | `media.control`、`volume.device`。App 默认从用户可授权的会话访问和公开音量 API 提供能力。 |
| 手机整机或指定应用音频送电脑，电脑默认/指定端点、多输出、保留手机发声或仅电脑发声、独立增益、智能音量、掉线恢复 | 整机使用 `sonicroute:device-audio`。按包名/UID 用 helper 捕获与隔离，处理远程子混音、`audio_dup`、ROM 探测、线性增益及会话冲突。旧链路在 ADB 本地抽象套接字使用 Raw PCM 或 Opus，含可选 dummy、big-endian codec ID 以及 12 字节帧头（PTS/标记+长度）。Windows 接收与渲染用 WASAPI，显式规则可以恢复。 | `audio.capture.device/app/exclusive`。普通 MediaProjection 受同用户、用途和来源应用政策限制。保留本机发声与独占分别探测，接收确认前不改变来源播放。 |
| Windows 单应用送手机当前输出、扬声器或蓝牙；仅手机或同时播放 | `PcToPhoneAudioManager` 负责 WASAPI 捕获、路由、静音租约和恢复；`AudioOutputBridge` 用 `AudioTrack` 并读取实际路由。ADB 本地抽象套接字承载 48 kHz 双声道 PCM，shell 行协议发送 `target`、`mute`、`quit` 并读取 JSON 路由/输出回执。Windows 用 `android-out:<phoneKey>:<sinkKey>` 标识 `speaker`、`current`、`bt:…`。 | `audio.play`、`audio.devices`、`audio.route.owned`。配置沿用同一组输出标识，由后端报告端点或能力变化。更广的第三方应用路由/独立音量是新增可选能力。 |
| 镜像、应用虚拟显示、窗口尺寸与恢复、应用迁移、键鼠/触摸/滚轮、Unicode、游戏手柄、屏幕电源和系统面板 | Java 服务提供 video/audio/control 的有序 `localabstract` 流。H.264/H.265，包含 codec 尺寸头和帧 PTS/标记/长度，自定义了 resize、gamepad、请求关键帧等控制码。Windows 现有解码、渲染、沉浸窗口、显示大小与资源恢复按档案和应用管理。 | `display.mirror`、`display.virtual`、`display.input`。分别协商，不用镜像能力假定可注入。现有 Windows 渲染与交互继续使用，App 不带入 Android helper 或媒体编解码库。 |
| 手机目录浏览、接收目录、上传/下载、进度、队列取消 | `AndroidFileTransfer(s)` 使用 ADB `sync:`，按 feature 选 `LIS2/LIST`。默认接收位置为 `/sdcard/Download`，上传先写入临时文件，完成时保留同名文件并原子改名。UI 关闭时取消目录浏览，已经确认的传输独立继续。 | `files.transfer`，App 用 SAF 的授权根及逻辑相对路径。共用 Windows 文件卡、选择器、队列、进度与错误。 |
| 每设备剪贴板开关、状态、双向文本、防回环 | `PhoneClipboard` 使用 Windows 剪贴板事件和 Android helper，JSON 行修订与回显抑制，单条文本 64 KiB。进程/观察按当前启用设备切换，离线可以保留开关。 | `clipboard.sync`，Android 普通模式按前台与系统隐私限制，需用户开启；有修订序号、无回环和冲突处理。 |

参照 [ADB 客户端](../../当前项目/SonicRoute.AndroidAudio/AdbClient.cs)、[流参数](../../当前项目/SonicRoute.AndroidAudio/AndroidStreamSettings.cs)、[媒体控制](../../当前项目/SonicRoute.AndroidAudio/AndroidMediaSessionClient.cs)、[接收源管理](../../当前项目/SonicRoute.AndroidAudio/AndroidAudioManager.cs)、[ADB 音频流](../../当前项目/SonicRoute.AndroidAudio/ScrcpyAdbTransport.cs)、[音频帧](../../当前项目/SonicRoute.AndroidAudio/ScrcpyRawPcmReceiver.cs)、[PC 到手机](../../当前项目/SonicRoute.AndroidAudio/PcToPhoneAudioManager.cs)、[手机播放助手](../../当前项目/android-helper/AudioOutputBridge.java)、[Android 音频采集](../../当前项目/android-helper/src/org/sonicroute/android/audio/PlaybackAudioCapture.java)、[画面与输入](../../当前项目/SonicRoute.AndroidAudio/ScrcpyVideoProtocol.cs)、[手机流通道](../../当前项目/android-helper/src/org/sonicroute/android/io/StreamSession.java)、[虚拟显示管理](../../当前项目/SonicRoute.AndroidAudio/AndroidVirtualDisplayManager.cs)、[文件同步](../../当前项目/SonicRoute.AndroidAudio/AndroidFileTransfer.cs)、[剪贴板客户端](../../当前项目/SonicRoute.AndroidAudio/AndroidClipboardClient.cs)及[Native 剪贴板](../../当前项目/Native/Product/Views/PhoneClipboard.cs)。

## 配置与操作兼容规则

| 现有保存项 | 接入规则 |
| --- | --- |
| `android-phone.json`，旧 Native 候选存在 `android-phones.json` 导入路径 | 保留 v1 单档案、v2/v3 兼容、原档案 ID、选择、名称、固定应用、快捷应用、智能音量例外、输出、历史地址、接收目录、主动断开、移除历史和扩展字段。App 身份绑定通过原存储管理，写入采用原子替换和可回滚备份。 |
| `android-audio.json` | 沿用 v1–v3 的每设备/包名音量（0–100）、输出、恢复意愿、虚拟显示/窗口大小和每设备流参数。音频编码器、自动/Concentus Opus/Raw PCM、16–512 kbps、超低/均衡/稳定延迟，默认 Opus 128 kbps/均衡。视频含 H.264/HEVC 编码器和自动、H.264 D3D11、H.264 CPU、HEVC MFT 选择，支持 0.1–50 Mbps、1–120 FPS，默认 4 Mbps/30 FPS。在新会话应用参数，Raw PCM 跳过音频编码器和码率。App 使用协商结果呈现实际可用组合。 |
| `phone-clipboard.json`、`android-file-receive-folder.txt` | 沿用每档案剪贴板开关及存档记录。Android 接收目录归手机档案，Windows 文本配置保留 Windows 接收目录，缺省使用系统 Downloads。Android 本地权限和规则保存在 Android。 |
| `phone-app-catalog.json`、`PhoneRuntime`、诊断、密钥 | 运行时和目录缓存遵循原生命周期。ADB 主机密钥、App 私钥与档案偏好分离，发现缓存仅做提示。日志留错误码和阶段，省略配对密钥、剪贴板正文、真实标识及本地路径。 |

参照 [手机档案](../../当前项目/SonicRoute.AndroidAudio/AndroidPhoneStore.cs)、[音频配置](../../当前项目/SonicRoute.AndroidAudio/AndroidAudioSettingsStore.cs)、[外部音频恢复配置](../../当前项目/SonicRoute.AndroidAudio/AndroidAudioSettingsStore.ExternalAudio.cs)、[键生成与匹配](../../当前项目/SonicRoute.AndroidAudio/AndroidModels.cs)及[历史兼容条目](../../当前项目/Native/Product/Parity/R10-Compatibility.md)。

接入时先保证原 ADB 实现经新业务端口走完同一组检查。App 连接仅在相同用例、错误、恢复和存储路径通过后接入。本文是阶段 0 源码审查，涉及真机行为的条目需按路线图验收。
