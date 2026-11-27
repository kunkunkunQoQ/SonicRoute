# SonicRoute Android 架构

## 当前模块

| 模块 | 职责 | 依赖边界 |
| --- | --- | --- |
| `:core` | 权限状态、后端与能力模型、四层连接生命周期状态机（`ConnectionLifecycle.kt`）、本地音频域模型、自研严格 JSON 与 SRAP v1.0 协议/TLS 状态机。纯 Kotlin/JVM，可脱离 Android 执行全部校验。 | Kotlin/JVM 工具链和标准库 |
| `:platform` | `SonicRouteRuntime` 进程级四层协调器、`SonicRouteConnectionService`（按需 `connectedDevice\|mediaPlayback` 前台服务）、`LanServerController`（Android Keystore + NSD + `WIFI\|ETHERNET` 网络恢复）、`LocalAudioController`（功耗门控音频/蓝牙/自有音轨路由回读）、`PermissionSession` 及 Shizuku/Root 探针。 | `:core`、Android SDK、官方 Shizuku API |
| `:app` | 纯 UI 观察者 `MainActivity`、窗口 Insets、系统视图、主题、双语字符串；不持有后台线程、Socket 或特权对象。 | `:platform` 运行时快照与命令入口、`:core` 状态 |

当前应用可离开电脑独立打开并测试本地音频与权限探测；电脑连接默认关闭，不自动启动前台服务。

## 后续业务边界

| 边界 | 所有者与职责 |
| --- | --- |
| 设备会话 | Windows 的共享连接协调器拥有档案、实时附件和连接代次。Android 本地控制器拥有安装身份、已配对电脑及本地功能。连接策略、权限快照和操作派发共用，ADB 和 App 是两个实现端口。 |
| 能力与授权 | Android 后端负责系统级授权和会话资源。业务从统一快照查询能力，执行时领取带代次的短期租约。能力按 Android 版本、用户、设备、ROM、请求范围和真实回执计算。设备连接权限、Android 权限和远端操作范围分别核验。 |
| 音频 | 一个控制器接收本地 UI、可信电脑和未来自动化的同一组类型化命令。分别管理系统音量、SonicRoute 自有轨道、捕获来源及第三方应用路由。独立增益只调对应音轨，查询和值回读报告各自作用范围。 |
| 设备 | 使用 Android 音频设备、蓝牙和网络的系统回调。输出使用稳定的逻辑键，运行时按当前快照解析实际端口，设备重新出现后重新核验。不可达的保存目标留在设置，活动流使用明确的回退策略。 |
| 媒体与数据 | 每路视频、音频、文件及剪贴板各自持有取消令牌、资源和数据上限。Windows 画布、设备页、伴随窗与本地 Android UI 只读取快照和提交命令。 |
| 自动化 | Android 原生事件触发，规则文件和启停独立存储。规则只调用业务命令，权限失效时暂停受影响动作。规则支持去抖、幂等、避免循环和可见执行记录。需要延后执行时才使用平台调度。 |
| 保存与信任 | Windows 使用原档案/音频/剪贴板存储，App 配对只扩展别名与安全元数据。Android 本地规则和音量由 Android 持有，Windows 端点和工作区由 Windows 持有；双方共享与当前会话相关的只读快照。按字段确定权威方，写操作使用修订戳，避免配置双向覆盖。 |

接口先在同一模块按职责形成边界，媒体模块或特权进程只有在存在真实的独立所有权、平台要求或编译依赖时才拆出。Shizuku 框架和 Root 探针永远属于平台适配层。业务、序列化和 UI 不直接使用 Binder 或 `su`。

## 生命周期和空闲资源

1. **四层独立生命周期**（`ConnectionLifecycle.kt` + `SonicRouteRuntime.kt`）：
   - **界面生命周期（UI）**：`MainActivity.onStart` 附着观察者，`onResume` 刷新状态，`onStop` 脱离界面、取消 UI 探针、自动关闭未完成配对窗口并停止仅限本地 UI 的测试音；若用户已启用电脑连接，不中断后台服务与活跃 mTLS 会话。
   - **网络与前台服务生命周期（Network / FGS）**：仅在用户显式点击「开启接收 / 开启配对」后由前台拉起 `SonicRouteConnectionService`（`START_NOT_STICKY`、低优先级静默通知附「停止连接」动作）。日常声明 `connectedDevice`，仅在活跃音频播放期间就地叠加 `mediaPlayback`，止播即时降回。`LanServerController` 仅监听 `TRANSPORT_WIFI | TRANSPORT_ETHERNET`：切离家庭 Wi-Fi 到移动网络时立即关闭监听端口与 NSD 进入 `WAITING_FOR_LAN`，回连家庭 Wi-Fi 后自动重建 TLS 与 `pair=0` NSD 以允许可信电脑免重新配对重连。用户主动关闭连接后清空全部资源并写入 `USER_STOPPED`，严格禁止自动恢复；若因系统回收进程中断，重启后安全降级至普通权限并展示回收提示，不自动重启 FGS、保留可信电脑列表。
   - **特权后端生命周期（Privileged Backend）**：`PermissionSession` 仅在 UI 可见或 LAN 已启用时存活；权限撤销、Shizuku Binder 死亡或进程回收时立即推进代次、向活跃会话单调推送能力变化并安全回退普通权限。
   - **音视频会话生命周期（Audio / Media）**：`LocalAudioController` 采用功耗门控（仅在 UI 可见或至少有 1 台已连 PC 时注册 `AudioDeviceCallback`、音量广播与蓝牙 Profile 代理；后台 0 PC 空闲监听时自动撤回）；每条音频流在断线、撤销权限或用户停止时立即释放 `AudioTrack`。
2. **零无意义保活与低功耗约束**：全程零应用层轮询、零自定义 `WakeLock`、零开机自启广播、零静默全网扫描；长连接依赖系统级 TCP `SO_KEEPALIVE` 与平台回调，配对窗口限定 120 秒一次性过期。
3. 每个会话分别拥有网络监听、Binder/UserService、租约、文件临时对象、MediaProjection 和音视频资源。撤销授权、Binder 死亡、网络丢失或用户停止时先使代次失效、取消旧工作，再关闭资源与恢复仍由当前租约拥有的系统状态。新连接只能开启新代次，旧帧、回调及操作不能跨代进入。
4. 音视频使用原生 `AudioRecord`、`AudioTrack`、`MediaCodec` 等。只给活跃流分配有界队列和缓冲。停止或权限撤销时释放。能否捕获、监听本机、切换输出或改变外部应用路由逐项目核验。

## 原生 UI

首页采用 Activity、原生控件、XML、系统浅色/深色主题和共享间距样式。应用名固定为 SonicRoute，其他文案用字符串资源。后续页面沿用同一组列表行、开关、操作按钮、状态提示和空状态，使用主题属性表达选中、不可用、错误和焦点。状态包含具体权限、设备或版本原因，同时支持大字体、TalkBack、键盘和系统边缘留白。

后续 Android 导航包括本地路由、音频设备、自动化、电脑连接和权限设置。Windows 延用现有设备页和操作位置，配对入口根据方式提供不同提示；从进入已绑定设备档案起使用同一个设备详情、画布、应用条目、伴随窗、配置编辑及错误格式。能力可用时相同动作需要相同语义。阶段 0 中的样式仅用于检查项目和权限状态，最终视觉待产品阶段。
