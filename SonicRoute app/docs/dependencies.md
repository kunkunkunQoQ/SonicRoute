# 构建与运行依赖

| 范围 | 名称 | 用途 |
| --- | --- | --- |
| 构建 | Gradle Wrapper 8.9、Android Gradle Plugin 8.7.3、Kotlin Gradle Plugin 2.0.21 | 构建、打包、AIDL 编译与 Lint。Wrapper 固定发行包的 SHA-256。 |
| 构建 | JDK 17 或 21；SDK Platform 35；Build Tools 35.0.1 | JVM 17 字节码目标及 Android APK。Platform Tools 用于本地装包和启动检查。 |
| 运行 | Android SDK，最低 API 29、目标 API 35；`kotlin-stdlib:2.0.21` 与其 `org.jetbrains:annotations:13.0` | Activity、原生视图、JSSE TLS、Android Keystore、NSD、系统音频/蓝牙/包管理回调、AIDL 和 Kotlin 标准库。 |
| 运行 | `dev.rikka.shizuku:api:13.1.5`、`dev.rikka.shizuku:provider:13.1.5` | 官方 API、受系统权限保护的 Provider 与按需绑定的本地白名单 UserService。传递依赖为该版本的 `dev.rikka.shizuku:aidl`、`shared` 以及 `androidx.annotation:annotation:1.3.0`。均由平台模块引入。 |
| 可选外部应用 | Shizuku，用户独立安装、启动并授权 | 仅选择 Shizuku Shell 或 Root 时使用。SonicRoute 安装包不附带 Shizuku 管理器或服务。 |
| 可选系统工具 | 用户设备已有的 `su` | 用户在本机选择直接 Root 后执行身份检查。如需第三方原生路由，应用按需启动有生命周期上限、固定入口和已认证对端的本机服务。 |

Android 侧源码为当前项目的独立实现。没有 AndroidX 功能组件、Compose、网络库、TLS/证书库、媒体库、二维码库、Root 框架、JSON 库、虚拟声卡、云服务或第三方功能源码。AndroidX 注解仅来自官方 Shizuku 的依赖链。Kotlin 是语言及标准库。

## 合并 Manifest 与按需授权

- `android.permission.INTERNET`：用户显式开启局域网接收器后监听本地 TLS 1.3/1.2 端口。
- `android.permission.ACCESS_NETWORK_STATE`：注册 `TRANSPORT_WIFI | TRANSPORT_ETHERNET` 专属 `ConnectivityManager.NetworkCallback`，在离开 Wi-Fi 时挂起服务、回到家庭 Wi-Fi 时自动恢复监听与 NSD。
- `android.permission.CHANGE_NETWORK_STATE`：安装期普通权限，满足 Android 14–16 `connectedDevice` 前台服务的合法前置权限要求，确保用户未授予蓝牙权限时仍可正常使用局域网电脑连接。
- `android.permission.FOREGROUND_SERVICE`、`android.permission.FOREGROUND_SERVICE_CONNECTED_DEVICE`、`android.permission.FOREGROUND_SERVICE_MEDIA_PLAYBACK`：仅在用户显式启用电脑连接时启动 `SonicRouteConnectionService`（`START_NOT_STICKY`）；日常保持 `connectedDevice`，仅在活跃播音期间按需叠加 `mediaPlayback`，用户主动停止立即销毁服务。
- `android.permission.POST_NOTIFICATIONS`：Android 13+（API 33+）按需运行时申请，用于在系统通知栏呈现连接状态与「停止连接」按钮；即使用户拒绝，前台服务仍按系统规则正常运行。
- `android.permission.BLUETOOTH`（`maxSdkVersion="30"`）：Android 10–11 蓝牙适配器状态读取。
- `android.permission.BLUETOOTH_CONNECT`：Android 12+（API 31+）按需运行时申请，用于读取已配对及 A2DP / LE Audio 连接状态；拒绝时降级仅用系统 `AudioManager` 回调与系统蓝牙设置入口。
- `moe.shizuku.manager.permission.API_V23`：来自官方 Shizuku SDK。
- `<queries>`：仅声明 `ACTION_MAIN` + `CATEGORY_LAUNCHER`，遵守 Android 11+ 包可见性约束，**不申请** `QUERY_ALL_PACKAGES`。
- `RECORD_AUDIO` 用于用户在前台选定第三方应用、可信电脑并经系统确认的播放捕获。录制源是 `AudioPlaybackCaptureConfiguration`。投屏与捕获需要 `FOREGROUND_SERVICE_MEDIA_PROJECTION`，每次执行还需要新的系统投影授权。[Android 播放音频捕获](https://developer.android.com/media/platform/av-capture)
- 文件访问使用系统文件夹选择器发放的持久 URI 授权。应用无全盘存储权限。Manifest 没有 `WAKE_LOCK`、`RECEIVE_BOOT_COMPLETED` 或 `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`；明文传输和应用数据备份均已关闭。

依赖检查命令为 `.\gradlew.bat :app:dependencies --configuration debugRuntimeClasspath`。

[AGP 8.7 兼容表](https://developer.android.com/build/agp-8-7-0-release-notes) · [Shizuku API 与许可](https://github.com/RikkaApps/Shizuku-API)
