# 手机本机应用音频输出

在声音页打开「应用音频输出」。选择当前 Android 用户的媒体应用和已连接的物理输出。播放开始后点「应用输出」。播放器需要持续使用同一条活动 MEDIA 轨道，界面会列出原设备和当前设备 ID。点「恢复输出」会注销本次策略，然后重新查询仍在播放的原轨道。停止播放、拔出设备、目标包移除或后端死亡会结束本次路由。

普通权限可管理 SonicRoute 自己的音轨。第三方路由需要用户在手机本地选择并授权 Shizuku Shell、Shizuku Root 或直接 Root。服务逐次校验实际权限 `MODIFY_AUDIO_ROUTING`、当前用户下的唯一包 UID、现有 MEDIA 播放器及输出的设备 ID、类型和地址。策略固定为 UID 与 MEDIA 的交集，只指向一个物理输出。获取到原播放器的真实设备 ID 后才能报告切换或恢复。系统拒绝调用或缺少读回时，面板会报告原因并释放资源。该入口只供手机本地 UI 使用。[Android 播放器及设备读回](https://android.googlesource.com/platform/frameworks/base/+/master/media/java/android/media/AudioPlaybackConfiguration.java)

直接 Root 通过已授权的系统 `su` 启动自带的固定 `app_process` 类。启动参数只含系统 PackageManager 提供并校验过的标准安装 APK 路径与常量。应用创建一次性本机 Unix socket，随机质询经子进程私有标准输入发送。双方核对内核对端 UID、已安装包与所加载 APK 的当前签名、质询回执及唯一调用应用。每个子进程只接收一次应用/输出选择及一次可选恢复。私有通道关闭或最长五分钟到期即清理。Root 授权管理器如未主动终止已经运行的进程，手机可通过「恢复输出」或切换后端立即释放。

「记住此应用与输出」在本机保存包名、安装签名、Android 用户 serial 及稳定的输出标识。内建听筒和扬声器要求设备类型唯一。蓝牙要求实际输出地址与已配对设备吻合，保存配对地址的摘要及设备类型。运行时重新求出包 UID 和硬件设备 ID。「本次授权期间使用首选项」在该后端代次监听播放变化，并复用设备接入回调；仅在关联应用与物理输出同时存在时尝试。断开、停止播放、切换后端、删除首选项或主动停止会释放当前资源。进程重启后须在手机重新启用。

某些老播放器的 `AudioPlaybackConfiguration` 报 `USAGE_UNKNOWN`。服务按用户选中包的实际 UID、活动播放器及 session 做判断。如有真实 AudioFlinger 轨道，要求活动输出线程、`ST=3` 与可确认的实际用途。原生用途为 MEDIA 时匹配 UID 和 MEDIA。原生用途仍为 UNKNOWN 或只能由平台属性映射到 `STREAM_MUSIC` 时，策略还限定本次实际媒体 session。若框架无法提供设备 ID，可按该 session 的原生输出类型与唯一在场的物理设备交叉确认。已从混音器确认的基线须在结果处重新确认，歧义、拒权或缺少证据时释放策略。系统多设备策略的作用范围是音频 strategy，E5 的本机实验也没有从 UID loopback 得到可用 PCM。单应用原生多输出需另有同一轨道在多个物理设备同时播放的证据。[Android 组合设备路由](https://source.android.com/docs/core/audio/combined-audio-routing)

小米支持的系统版本可从面板打开系统「声音与振动」。在「声音助手」或「更多设置」启用分应用音量，由系统面板控制和显示数值。产品未取得厂商提供给第三方的可靠数值设置、回读及恢复接口。本机旧 E4 实测中 `PlayerProxy.setVolume` 连续衰减，回设 100% 无法恢复原增益，现行诊断仅读取接口存在性。[小米声音助手](https://www.mi.com/global/support/article/KA-08490/) · [小米分应用音量设置](https://www.mi.com/global/support/faq/details/KA-507167/)

## 验证

- JVM 运行 `:core:checkNativeAppRouteEvidence`。覆盖原播放器身份、单/多设备读回、来源证明、严格字段、启动路径及恢复拒绝条件。
- 2026-11-28 小米 13、Shizuku Root、MT 管理器播放 WAV，独立 AudioService 和 AudioFlinger 三次确认同一活动播放器及 session 在 A2DP 端口 23887→听筒端口 2→A2DP 端口 23887。端口按当次枚举。[真机结果](../../outputs/dual-end-20261128/physical-media/route-06.json) · [跨厂商与权限审计](../../integration-docs/DUAL-END-20261128-PRIVILEGES.md)
- 真机使用获准的实际第三方播放器与不同的物理输出。记录切换前、切换后和恢复后相同 UID、播放器/会话及实际输出。可用系统 `media.audio_flinger` 的活动轨道及其输出线程交叉验证。历史波点音乐案例发生在 2026-11-07，当时的数值只属于那次观测。[历史 E2](../../sonic%20route%20mobile/实验模块/reports/E2-应用路由.md) · [历史 E4](../../sonic%20route%20mobile/实验模块/reports/E4-独立音量.md)
- 独立无声夹具仅用于调试轨道生命周期。显式运行 `gradlew.bat -ProuteFixture :routefixture:assembleDebug` 生成单独 APK。夹具无需音频焦点、网络或录音权限，最多播放 90 秒。
