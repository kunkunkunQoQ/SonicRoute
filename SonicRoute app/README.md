# SonicRoute for Android

安卓端后续源码、安装说明和协议文档已迁至 [SonicRoute-App](https://github.com/kunkunkunQoQ/SonicRoute-App)。此目录保留双端工程最初合入时的源码。

本版可独立管理手机音频，也可通过已配对的局域网电脑使用音频、媒体、文件和屏幕共享。实现、实机验收范围和已知限制见[双端交接与续测说明](docs/verified-handoff-20261128.md)。

## 早期版本沿革

SonicRoute 的独立 Android 工程当前已完成阶段 0、阶段 3 本地公开音频基线、阶段 2A SRAP v1.0 局域网安全协议以及后台连接生命周期与功耗优化：首页支持普通权限、Shizuku Shell、Shizuku Root 与直接 Root 四后端核验，提供默认关闭的 Android Keystore TLS 1.3/1.2 服务端、NSD `_sonicroute._tcp` 发现、120 秒一次性 ECDSA P-256 配对、持久化信任管理与动态能力同步；通过按需 `connectedDevice|mediaPlayback` 前台服务（`START_NOT_STICKY`、零 WakeLock）、四层生命周期隔离、Wi-Fi/以太网专属网络恢复及音频观察者功耗门控，实现退后台/切应用/锁屏持续可用、主动关闭彻底释放与进程回收安全降级，同时支持本地音频输出与蓝牙监测、自有音轨路由真实回读、三类音量作用域隔离及跨后端音频可行性探针。

## 外观与语言

「设置 → 外观」可选择跟随系统、浅色或深色主题，以及系统默认、简体中文或 English。语言同时用于手机界面和局域网连接通知。Android 13 及以上也可在系统的应用语言设置中修改。Android 10 至 12 将选择保存在 SonicRoute 内。[Android 应用语言设置](https://developer.android.com/guide/topics/resources/app-languages)

测试 APK 的 `LanInteropRunner` 支持 `-e mode appearance`。它从设置页实选深浅主题和两种语言，检查页签与通知资源并恢复原偏好。

## 构建与启动

使用 JDK 17 或 21、Android SDK Platform 35、Build Tools 35.0.1，以及 Platform Tools。最低支持 Android 10（API 29），编译和目标 SDK 为 35。当前应用 ID 为 `org.sonicroute.app`，版本为 `0.1.0`。

```powershell
.\gradlew.bat :core:check :app:assembleDebug :app:lintDebug
adb -s <serial> install -r app/build/outputs/apk/debug/app-debug.apk
adb -s <serial> shell am start -W -n org.sonicroute.app/.MainActivity
```

macOS/Linux 使用 `./gradlew`。Shizuku 需测试者单独安装并启动。所有运行依赖仅包含 Kotlin 标准库与官方 Shizuku 接入（`api` / `provider` 13.1.5）及其必要传递依赖。

## 文档

- [Windows 源码审查与功能映射](docs/windows-android-audit.md)
- [架构与原生 UI](docs/architecture.md)
- [协议和接口规范（SRAP v1.0）](docs/protocol.md)
- [SRAP v1.0 黄金测试样本](docs/protocol-fixtures/srap-v1.txt)
- [权限能力矩阵](docs/permissions.md)
- [构建与运行依赖](docs/dependencies.md)
- [分阶段开发和验收](docs/roadmap.md)
- [本版开发交接](docs/verified-handoff-20261128.md)
- [Codex 阶段 1 接收回执](docs/codex-windows-stage1-response.md)
- [Codex 阶段 2A 联调与阶段 2B 配合清单](docs/codex-handoff-stage2a.md)
