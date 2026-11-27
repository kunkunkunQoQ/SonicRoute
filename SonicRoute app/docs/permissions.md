# Android 权限能力矩阵

当前两端实现、实测设备和逐后端结果见[2026-11-28 权限与原生音频矩阵](../../integration-docs/DUAL-END-20261128-PRIVILEGES.md)。下文的阶段 0 表格保存开发初期的目标与限制。

## 已建立的后端

| 后端 | 检测和授权 | 失效和释放 |
| --- | --- | --- |
| 普通权限 `standard` | 原生进程 UID，从首页启动起可用。后续运行时权限和 MediaProjection 同意按单项功能取得。 | 当前前台会话关闭时结束观察。后续各功能独立处理权限撤回和会话停止。 |
| Shizuku Shell `shizuku.shell` | 用户单独安装并启动 Shizuku。官方 Binder 报告 UID 2000，读取当前授权，用户显式发起授权。 | Binder 死亡、换代、授权失败或撤回时回到普通权限。结束会话移除收到、死亡和授权回调。 |
| Shizuku Root `shizuku.root` | 同一官方接入，服务器必须报告 UID 0，独立显示状态。 | 与 Shell 共用失效步骤。Shell 和 Root 切换视作新 Binder/新授权检查。 |
| 直接 Root `root.direct` | 用户选择后执行固定的 UID 探针。手机上应用输出时另行启动本 APK 的固定路由入口，两端核对本地内核凭据、安装包签名与单次质询。 | UID 探针限 15 秒。实际路由有独立的 10 秒建立期限和最长 5 分钟授权，切换后端、手动释放、播放结束或失联时关闭并注销策略。 |

普通权限是初始和回退项。可用的高级后端必须由用户选择。重新出现、过期回调、旧 Binder 或上一前台会话无法自动启用高级后端。Shizuku 兼容最低服务协议 v11，检查使用其官方 AIDL 读取当前授权，避免便利 API 的已授权缓存误报。Binder 存活状态由通知更新，应用回前台时重查权限。阶段 0 无特权业务，因此不存在特权业务续用。在后续版本中，调用失败及权威回读也要触发失效；仅靠前台刷新无法保证全局即时发现授权变更。 [Shizuku 官方接入文档](https://github.com/RikkaApps/Shizuku-API)

## 目标能力

下表描述可行路径和需要验证的上限。阶段 0 的所有业务能力均为待实现。每个后端即使通过身份核验，也要逐项发布真实能力。Windows 的 ADB 权限由现有路径实测，和 App 后端分别计算。

| 能力 | 普通权限 | Shizuku Shell | Shizuku Root | 直接 Root |
| --- | --- | --- | --- | --- |
| 可信电脑连接、认证、局域网文件 | 系统网络能力及用户同意。文件通过 SAF，仅授权目录。 | 同普通，权限后端不扩大远端范围。 | 同普通。 | 同普通。 |
| 应用、媒体目录与手机音量 | 受包可见性限制。媒体控制需可访问的会话或通知访问授权。允许的系统音量受 AudioManager/免打扰约束。 | 可探测 shell 用户范围与系统服务。 | 可探测 root 用户范围与系统服务。 | 逐操作读取实际系统能力。 |
| 整机/应用捕获 | API 29+ MediaProjection + AudioPlaybackCapture；仅同用户且使用允许用途、来源允许捕获。应用范围按 UID 匹配。 | 已有 ADB shell 实现为功能参考；逐 ROM 探测，Shell UID 本身不保证权限。 | 逐 ROM 验证 AudioPolicy 及 SELinux。 | 同类候选，分别核验 UID、SELinux 与回执。 |
| 独占捕获、只在目标输出 | 普通捕获可保留来源播放；独占控制没有通用公开接口。 | 系统策略可行性和恢复需验证。 | 逐系统功能验证，缺少可靠恢复时不可用。 | 同类候选，设置变更须有恢复和回读。 |
| PC 到 Android、自有轨道音量和输出 | 可用 AudioTrack、设备回调和首选设备；系统最终输出必须回读。轨道独立增益仅作用于 SonicRoute 自有轨道。 | 受测系统适配器可增强实际可选输出。 | 同左，仍受 ROM 与硬件限制。 | 同左，逐操作核验。 |
| 其他应用的路由和独立音量 | 无普通公开的通用逐应用控制。 | 受 shell 特权和 OEM AudioPolicy 支持约束，未验证前不可用。 | 需验证真实策略、范围、恢复及无副作用。 | 需验证真实策略、范围、恢复及无副作用。 |
| 蓝牙、USB、有线、扬声器等 | 音频设备回调枚举。蓝牙权限按版本与动作取得，配对/连接优先用系统 UI，普通 API 无法保证控制所有 profile。 | 额外控制逐 profile 和 ROM 核验。 | 同左，结果以真实设备状态为准。 | 同左，结果以真实设备状态为准。 |
| 投屏、独立屏、输入 | 用户每次同意的 MediaProjection。App 自有虚拟显示与跨应用独立显示分开。普通模式无任意跨应用输入注入。 | 按设备和 display 范围验证。 | 逐 ROM 验证拥有的任务、显示和输入权限。 | 同左，验证清理与输入限制。 |
| 剪贴板 | 普通前台路径受系统隐私与当前焦点约束，明确开关。 | 逐 ROM 核验事件访问，禁止回环。 | 需经相同开关和范围限制。 | 同左。 |
| 自动化 | 用户启用规则后调用可用能力，触发事件受系统限制。 | 仅运行已获授权的动作。 | 同左。 | 同左，Root 失效暂停受影响规则。 |

[Android 音频捕获](https://developer.android.com/media/platform/av-capture) · [AudioRouting](https://developer.android.com/reference/android/media/AudioRouting) · [蓝牙权限](https://developer.android.com/develop/connectivity/bluetooth/bt-permissions) · [MediaProjection](https://developer.android.com/media/grow/media-projection)

后续探测结果绑定操作范围、Android 用户、ROM、版本、运行中后端及授权代次。任何操作的可用状态都需要真实平台证据。系统设置写入、播放路由、保护内容和其它应用权限均有独立约束，UID 0 本身不构成成功证据。
