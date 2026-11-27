# 手机画面共享

手机在「电脑」页点「共享手机画面」，选择电脑的名称和公钥指纹，再通过 Android 的系统窗口授权。本次共享绑定当前 mTLS 会话。Android 14 及以上按系统提供的选项选择单个应用或整个显示屏。Windows Full 的设备页出现「打开手机共享画面」，打开后使用既有 Native 视频窗口。手机可以随时停止。系统保护安全窗口，窗口输入事件留在 Windows 本地。

手机在同意后进入 mediaProjection 前台服务。等待电脑打开的期限为 60 秒。系统撤销、LAN 停用、对端撤销、会话结束或两端任一方停止共享都会释放投影；重新连接需要新的系统授权。应用声音共享与画面共享各自申请系统同意，同一时刻使用一个投影。Windows 收到并实际呈现首帧才显示窗口；20 秒未呈现首帧结束本次打开。

## 协议

现有 SRAP 会话发布 `display.mirror`，范围为 `one_projection`，可用时 `limits` 包括字符串 `codec=h264`、`maxUnit=1048576` 和授权代号 `grant`。其他会话收到 `needs_authorization`。每次操作检查配对状态、具体会话、公钥、后端代次和本次授权。授权与 DATA 不带画面输入能力。

| 操作 | 参数 | 完成回执 |
| --- | --- | --- |
| `display.start` | `{codec:"h264"}` | `{streamId,width,height,codec:"h264"}`。手机分配本会话唯一的非零偶数流号，一次授权只打开一次。 |
| `display.next` | `{streamId,seq}`。从 0 开始逐帧递增，每次只有一个待处理请求。 | 先发一个或多个同一流号的 DATA，再回 `{streamId,seq,size,ptsUs,config,key}`。最后两个字段均为布尔值。 |
| `stream.stop` | `{streamId}` | `{streamId,stopped}`。停止后不再发送该流的数据。标准 `cancel` 也可按请求或流号结束。 |

每个访问单元为 H.264 Annex B，DATA 块上限 32 KiB，单元上限 1 MiB。配置单元含 SPS/PPS，上限 64 KiB；其后的第一个画面是关键帧。电脑先注册流号再发送 `display.next`，收到完整单元并解码后才请求下一帧。手机在编码回调交付画面时唤醒发送线程。静止画面的等待可以持续到取消。手机只缓存至多四个待发单元、编码配置和当前发送中的单元。慢速接收方积压时，手机丢弃待发预测帧并请求关键帧恢复。电脑放弃请求下一帧时，手机在约 30 至 60 秒回收停滞的会话。

编码固定画布保持授权时的显示比例，长边至多 1280，短边至多 720，16 像素对齐，目标 20 fps / 2 Mbit/s。Android 根据系统能力把应用窗口、旋转后的内容缩放到画布。重新授权可以按当前方向创建画布。Windows 通过 Media Foundation 解码、D3D11 显示硬件或软件 NV12 输出，窗口缩放保留画面比例。[Android MediaProjection](https://developer.android.com/media/grow/media-projection)

## 独立应用窗口与输入

目前随安装包交付的 SRAP 画面范围由 Android 系统捕获窗口决定。MediaProjection 回调提供尺寸、可见性和停止通知，没有提供可用于输入派发的目标应用 UID 与独立显示所有权。服务把 `display.virtual` 和 `display.input` 报告为 `unsupported`。Windows 共享窗口只绑定关闭、绘制和缩放回调。[MediaProjection.Callback](https://developer.android.com/reference/android/media/projection/MediaProjection.Callback)

Android 的跨设备独立应用、虚拟键盘和触摸设备方案依赖 OEM 预装特权应用取得 `COMPANION_DEVICE_APP_STREAMING` 角色。普通应用自己创建的私有显示还有 Activity 嵌入与启动限制。Shizuku 或 Root 音频后端的就绪读数不构成该角色、目标窗口或每次输入的 UID 证明。[Android 应用流式传输](https://source.android.com/docs/core/permissions/app-streaming) [独立显示活动启动规则](https://source.android.com/docs/core/display/multi_display/activity-launch)

Windows Full 的已授权 ADB 功能使用工程自己的 `VirtualDisplaySource` 创建独立显示，`OwnedAppMonitor` 监听目标应用任务，既有 Native 应用窗口提供键鼠和触摸。此 helper 的输入接口只指定显示号，任务变化又由异步回调合并，不能复用为具有逐次 UID 约束的 SRAP 输入端口。已完成双端签名绑定的档案在同机 ADB 真实在线时提供「切换到 ADB 以使用独立应用窗口」；切换会结束当前 App 共享，打开独立窗口使用已有 ADB 页面。App 单通道、ADB 身份冲突、ADB 已离线或信任撤销时隐藏入口。

## 可复跑检查

Android 的 `:core:checkSrapDisplay` 验证会话隔离、偶数流、无内容时等待、跨块顺序、慢接收方的关键帧恢复和取消。Windows 的 `AndroidAudioChecks --srap-screen-frames` 验证有界分块和帧格式。Windows 的 `AndroidAudioChecks --srap-live-screen <配对目录>` 复用已配对连接，等待手机本机授权，打开正式解码窗口、读取实际呈现尺寸和帧数，关闭窗口后检查系统授权状态及 mTLS 会话。硬件通过以该运行输出和实际观察到的画面为准。
