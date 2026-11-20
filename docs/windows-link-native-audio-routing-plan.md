# Windows Link 与手机系统音频流转兼容计划

**状态：待用户确认。** 2026-11-20 保存于 `windows-link-20261119` 工作树。本文记录设计和待验证事项。开发、构建和设备试验均待用户确认后启动。

## 目标与使用场景

第一优先级是在现有设备菜单中增加「跟随手机当前输出」。例如，把 KUN 上的哔哩哔哩拖到 OPPO 手机后，电脑只向手机发送一份音频。手机使用扬声器时从扬声器播放，用户切到蓝牙耳机后跟随切换。若 ColorOS 已开启双耳机共享，由手机同时向两副耳机播放。用户仍可明确指定「手机扬声器」或某副耳机。已保存的设备选择沿用原义。

第二个目标是让用户从手机原生媒体面板发起播放，直接选择 KUN。Windows 系统蓝牙接收、小米妙播、AirPlay 各有自己的设备发现、配对和传输方式，按入口分别验证。一个入口通过，仅代表已验证的方向、机型和播放方式。

## 各系统的接入依据

| 系统 | 已查到的能力 | 建议接入与待确认事项 |
|---|---|---|
| OPPO ColorOS 16 | OPPO 对双耳机共享标注的条件为 ColorOS 16.0.6 以上、开启蓝牙、最多两副耳机。耳机跨设备切换还取决于机型和耳机。 | 优先跟随手机当前输出，来电或用户手动切换后尊重系统选择。本次公开资料调研尚未找到允许普通 Windows 软件注册为 OPPO 互联专有音频接收端的接口，KUN 能否出现在该列表需单独验证。[OPPO 版本与设备条件](https://www.oppo.com/cn/intelligent-ecosystem/) · [耳机跨设备切换](https://www.coloros.com/instruction?id=886&version=ColorOS+13) |
| 小米 HyperOS | 妙播提供面向音频应用的发送端 SDK、设备选择器和投射状态回调。第三方项目也公开了 Windows 接收器。 | 优先验证在小米控制中心选择 KUN。将公开项目用作互操作对照，独立评估 Windows 接收实现、协议版本及许可。保留现有双向单应用链路。[小米妙播开发指南](https://dev.mi.com/xiaomihyperos/documentation/detail?pId=1944) · [Windows 接收器实例](https://github.com/loyoi/myplay) |
| 荣耀 MagicOS、vivo、三星 One UI | 荣耀有双耳机分享和信任环切换。部分 vivo 耳机支持多设备连接。三星可将指定应用分配到与主输出不同的设备。 | 分别验证一台手机向两副耳机播放、一副耳机切换音源、不同应用使用不同设备。SonicRoute 只修改用户选中的流，保留手机给导航、音乐等其他应用设置的输出。[荣耀音频分享](https://www.honor.com/cn/support/content/zh-cn15897142/) · [荣耀耳机共享](https://www.honor.com/cn/support/content/zh-cn15889114/) · [vivo 多设备耳机](https://www.vivo.com.cn/vivo/vivobudsclip/) · [三星独立应用声音](https://www.samsung.com/ca/support/mobile-devices/galaxy-phone-advanced-audio-settings/) |
| Pixel 与其他 Android | 支持的 Pixel 和 LE Audio 耳机可以共享音频。Android 的媒体路由接口允许适配的播放器发现接收端。 | 双耳机走「跟随手机」。后续可评估可选的媒体路由提供者，让兼容播放器选择 KUN。Google Cast 单独验证发送端、接收端及设备要求。[Pixel 音频共享](https://support.google.com/pixelphone/answer/16483797?hl=en) · [Android 媒体路由](https://developer.android.com/media/routing) · [Cast 接收端](https://developers.google.com/cast/docs/web_receiver) |
| 华为 HarmonyOS | 原生鸿蒙提供 AVSession、AVCastPicker 和媒体资源投播能力。 | 先评估已适配投播的媒体应用。接收端拿到媒体资源后播放的场景，与游戏、任意应用的实时音频采集分别验收。原生鸿蒙需独立于 Android ADB 助手接入。[华为投播开发指南](https://developer.huawei.com/consumer/cn/doc/harmonyos-Guides/distributed-playback-guide/) |
| 苹果 iOS / macOS | 用户可从系统媒体界面选择 AirPlay 输出。项目现有 AirPlay 接收器可作为电脑侧入口。 | 优先复用手机向电脑投送的方向。KUN 向 iPhone 扬声器发送音频需要单独的手机接收端设计。[苹果 AirPlay 输出说明](https://support.apple.com/en-mide/guide/iphone/iph315e0d58d/ios) · [项目 AirPlay 接收器](../SonicRoute.AndroidAudio/AirPlay/AirPlayReceiver.cs) |

## 面板交互与恢复规则

| 操作 | 行为 |
|---|---|
| 拖到目标设备上半区 | 仅目标播放。接收端就绪后再处理来源声音。若 KUN 只能通过临时静音共用扬声器实现独播，沿用现有明确提示，说明同一扬声器上的其他应用也会静音。 |
| 拖到下半区 | 来源和目标同时播放。例如手机应用保留 ColorOS 已选的双耳机，同时把一份音频送到 KUN。 |
| 单击、Ctrl 或 Shift 点选设备 | 单击切换，Ctrl 增减单项，Shift 按现有列表顺序连续选择。若「跟随手机」已包含所点耳机，合并重复目标。系统无法报告成员时，在同一手机的「跟随」与「指定设备」之间切换，避免重复播放。 |
| 耳机断开、来电、用户手动改输出 | 「跟随手机」采用手机的新路由。「指定耳机」先停止丢失的分支，避免意外外放。多选保留其他路，单选恢复上次可用选择。尊重来电暂停及用户后续选择。Windows 本机直连蓝牙继续使用 Windows 原生断开行为。 |
| 关闭捕获窗口 | 释放窗口自动建立的音频。若用户先把声音拖到主面板，再打开和关闭窗口，主面板继续播放。 |
| 手机从原生入口主动投送 | 仅能识别一条流时显示「小米 · 妙播」等真实来源。能确认应用身份后再显示应用名。 |

## 实现衔接

1. **区分跟随系统和指定硬件。** 当前 Android 播放桥枚举扬声器和蓝牙，并对播放轨指定目标；按设备名称去重可能合并两副同型号耳机。「跟随」让播放轨使用当前系统媒体路由，「指定」继续核实实际去向。硬件身份优先使用系统可区分的标识，无法区分时不恢复到猜测的设备。Android API 36 起可在播放期间用 `getRoutedDevices()` 查询多个实际输出。旧版或仅报告逻辑设备的系统显示「由手机管理」。[现有播放桥](../android-helper/AudioOutputBridge.java) · [现有设备标识](../SonicRoute.AndroidAudio/AndroidModels.cs) · [Android AudioRouting](https://developer.android.com/reference/android/media/AudioRouting)
2. **分别保存用户选择、播放就绪状态和系统实际输出。** 先准备新路，接收端就绪且指定设备验证通过后再提交切换。失败则保留旧选择，界面说明失败原因。复用现有 `AudioReady`、多输出历史和界面选择逻辑。若需要扩展能力声明，旧端只使用已协商的选项。[协议](windows-link-protocol.md) · [多输出切换与恢复](../SonicRoute.AndroidAudio/WindowsLink/LinkAudioOutputs.cs) · [面板选择逻辑](../SonicRoute/QuickPanelModernWindow.PlaybackSelection.cs)
3. **让电脑承担分发。** 手机应用同时发往本机电脑和 KUN 时只采集一次，在电脑端分流。KUN 发往手机的两副共享耳机时只向手机发送一路。路由、状态和 Windows Link 协议由现有 C# 代码管理，Android 端仅补播放和回报。设备变化走现有回调，面板可见或流活跃时才订阅所需事件。跨 PC 复用配对 TLS、VPN 直连和中继防回送逻辑；方案无需公共服务器、虚拟声卡或自定义驱动。[音频中继](../SonicRoute.AndroidAudio/WindowsLink/WindowsLinkAudioRelay.cs) · [多输出分发](../SonicRoute.AndroidAudio/PcmOutputMirrors.cs)
4. **原生入口单独启用。** Windows 蓝牙音频接收适合近距离的「手机当前声音到 KUN」。两端需要兼容蓝牙并完成系统配对，该入口无需手机 ADB。跨 VPN、按应用选择及反向转发走已有互联链路。Windows Link 完全信任、系统蓝牙配对和 AirPlay 投屏码分别生效。[Microsoft 蓝牙音频接收](https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/enable-remote-audio-playback)
5. **保留单应用隔离。** 现有 Android 采集使用已授权的 ADB 助手，按 ROM 验证音频策略。将来如增加普通 Android 应用模式，公开的播放采集 API 需要录音权限、用户批准 MediaProjection，并受来源应用的采集策略限制。指定应用拒绝采集或无法隔离时，提示原因并结束该次请求，避免意外发送整机声音。[现有应用采集](../android-helper/src/org/sonicroute/android/audio/PlaybackAudioCapture.java) · [Android 播放采集规则](https://developer.android.com/media/platform/av-capture)

## 实施顺序与验收条件（待确认）

| 阶段 | 范围 | 验收方法 |
|---|---|---|
| 1. ColorOS 与通用 Android | 「跟随手机」、实际输出回报、同名耳机识别、拖拽及多选。 | 用 KUN、支持共享的 ColorOS 16.0.6 以上机型及两副兼容耳机，双向验证应用音频。实听确认两副耳机均出声、独播时来源静音、其他应用没有被误传。验证断开一副耳机、来电、手动切换和窗口关闭。 |
| 2. 手机原生入口 | 分别验证 Windows 蓝牙音箱模式和小米妙播接收。 | 从手机原生面板选 KUN，完成播放、断开和重连。蓝牙与 Windows Link 同时可用时，同一声音只播放一次。旧版对端仍可使用原有扬声器、耳机和互联路由。 |
| 3. 扩大生态覆盖 | 验证三星、Pixel、荣耀等通用路径，再按公开接口和实机结果评估鸿蒙投播、更多播放器入口及 OPPO 专有接收端。 | 按机型、系统版本、外设和传输方向记录结果，分别标注单应用、双播、独播和系统入口。多个独立设备的同步须测实际出声时间差；只确认同时有声时使用「同时播放」的产品文案。 |

发布前以相同设备和音频场景对照当前版本，测量空闲唤醒、活动流 CPU、内存与切换耗时。本文未给出实机兼容性或性能结论。
