# SonicRoute v1.32 正式版（2026-11-14）

`v1.32` 为 **Full** 版正式合入纯 C# 自研 Android 后端，彻底移除随包内嵌的 `adb.exe`、`scrcpy.exe`、FFmpeg 与 SDL2 等外部第三方运行时二进制，大幅缩小单文件体积；新增虚拟屏游戏手柄透传支持、手机“系统输出”自动跟随 Windows 默认播放设备、调整手机附窗面板间距（`12 → 6` DIP），以及自研管线六项关键修复与 SmartVolume 增益曲线调参。全版本（**Full / Lite / Legacy**）同步完成托盘长名称子菜单防重叠修复，以及图标、界面字典、OSD 窗口、ADB 缓冲与多媒体资源的按需加载和闲时内存优化。

> 发布构建遵循统一单源版本号 [`build/Version.props`](../build/Version.props)（`1.32` / `1.32.0.0`）。完整文件清单、构建校验哈希与更新元数据请以本版 Release 附件 [`SHA256SUMS.txt`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SHA256SUMS.txt) 与 [`SonicRoute-update.json`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SonicRoute-update.json) 为准。

---

## 核心更新

### 1. Full 版：纯 C# 自研 Android 后端与架构精简（仅 Full）

- **移除外部二进制依赖，单文件显著瘦身**：
  - 停止随程序嵌入 `adb.exe`、`AdbWinApi.dll`、`AdbWinUsbApi.dll`、`scrcpy.exe`、`scrcpy-server`、`scrcpy-server-video`、`SDL2.dll` 及 FFmpeg DLL（`avcodec-62.dll` / `avformat-62.dll` / `avutil-60.dll` / `swresample-6.dll`），仅保留配套轻量化 Android 端服务组件 `sonicroute-catalog.jar` 与图标资源。
  - ADB 通信与配对改由纯 C# 实现（`DirectAdbTransport`、`WinUsbAdbTransport`、`Spake25519`、`SspiPairingTls`），原生支持 TCP 与 WinUSB 连接及 TLS 1.3 配对；画面解码与虚拟屏渲染改用 Windows Media Foundation（`MediaFoundationH264Decoder`）、Direct3D 11（`D3D11VideoSurfaceRenderer`）与 Win32 原生窗口（`NativeVideoWindow` / `ScrcpyVideoProtocol` / `VideoStreamTransport` / `InProcessVirtualDisplayClient` / `NativeGamepadReader`），并配备自写 Java 服务 `SonicRouteServer`。
  - 从 `v1.32 Full` 起，不再依赖 `v1.30 Full` 与 `v1.31 Full` 时期外置运行时对应的第三方许可证分发清单（详见 [`packaging/AndroidLab/THIRD-PARTY-NOTICES.md`](../packaging/AndroidLab/THIRD-PARTY-NOTICES.md) 说明）；ARM64 `Full` 亦不再夹带任何外部 x64 辅助进程。
- **虚拟屏游戏手柄透传支持（`NativeGamepadReader` / `GamepadController`）**：
  - 虚拟屏与原生投屏窗口现已支持 Windows 游戏手柄直通：通过 `NativeGamepadReader` 同时采集 XInput（最多 `MaxSlots = 4` 个手柄槽位）与 Win32 RawInput HID 手柄/摇杆输入，经自研控制协议（`TYPE_INJECT_GAMEPAD_STATE`）由 Android 端 `GamepadController` 转换为标准 Android `InputDevice.SOURCE_GAMEPAD` / `SOURCE_JOYSTICK` 事件（支持左右摇杆轴、LT/RT 模拟扳机与迟滞触发数字键 `L2`/`R2`、D-Pad 十字键及 A/B/X/Y/L1/R1/Start/Select/Thumb/Mode 按键）。
  - 仅在虚拟屏窗口获焦且未最小化时激活（不注册 `RIDEV_INPUTSINK` 全局后台输入钩子，XInput 轮询严格以窗口焦点与在线手柄存在为门控），窗口失焦、最小化、关闭或手柄断开时自动归零并释放所有按键状态，避免卡键。
- **调整手机面板距离（`PhoneCompanionLayout`）**：
  - 将速控面板旁并排弹出的手机面板（`PhoneAudioWindow`）间距由原本 `12` DIP 调整收窄为 `6` DIP（`const double gap = 6`），与音量浮窗保持统一紧凑间距，同时保持工作区边缘自适应与跟随移动约束。
- **手机“系统输出”自动跟随 Windows 默认播放设备（`DefaultOutputNotifications`）**：
  - 当手机整机音频或单应用音频输出设置为 **系统输出**（`OutputId=null`）时，Windows 默认播放设备切换后会自动仅重建电脑端 WASAPI 播放器，完整保留原有手机捕获、解码、PCM 状态以及音量、静音与智能音量设置，无需手动切换后再切回。
  - 复用 `WasapiDeviceLease` 的 COM 边界并按实际使用的 `Render` / `Multimedia` 角色过滤，重复及快速通知在现有串行操作中合并；切出默认输出或最后一个相关会话停止后立即取消原生订阅，不新增后台轮询。新设备暂不可用时保留当前播放器并在下一次真实变化时重试；显式指定设备与回传手机扬声器/蓝牙保持原有行为。
- **C# 自研管线六项缺陷修复与 SmartVolume 调参入库**：
  - **ADB 流并发安全**：单条 ADB 流接收与关闭共用 flow gate，避免关闭与接收竞态。
  - **TLS 1.3 与后握手上下文**：补全 TLS 后握手上下文延续，并使用 Windows `SCH_CREDENTIALS` 真正启用 TLS 1.3 握手。
  - **USB 失败槽位状态收敛**：USB 连接失败槽位维持 `offline` 并在显式刷新时重试，设备通知事件不再重复盲目重试。
  - **虚拟屏创建取消与句柄清理**：取消创建虚拟屏窗口时同步中止创建意图，滞后到达的 `HWND` 交由所属线程销毁，消除窗口句柄与线程泄漏。
  - **最大化窗口尺寸保护**：虚拟屏最大化期间仍适配当前远端画面，但跳过持久化写入 `WindowSize` 与 `DisplaySize`，关闭后滞后尺寸帧同样不回写配置；还原窗口后手动调整尺寸正常保存。
  - **退出整机接管后恢复单应用音频**：开启整机音频接管时会先保存此前独立应用的轻量接管意图及音量/静音/智能音量/输出/手机保留声音偏好并释放旧策略；当用户正常退出整机接管时按序恢复原有应用接管，若已显式归还或关闭对应应用屏则不恢复，断开连接、停止全部或退出应用时亦不触发意外恢复。
  - **SmartVolume 曲线同步入库**：正式提交 `AndroidPcmSource` 中既有的手机智能音量曲线调参（滑块曲线 `volume * (0.12f + 0.88f * volume)` 与基于包络阈值 `0.15f`、区间 `[0.08f, 0.25f]` 的动态范围因子），保持源码与发布构建一致。

### 2. 全版本（Full / Lite / Legacy）：桌面交互修复与闲时资源优化

- **托盘“系统默认播放”长名称子菜单防重叠**（`ConvenienceMenus`）：
  - 修复异步读取设备后默认播放设备长名称在屏幕右边缘使一级菜单向左展宽、导致已打开二级子菜单错位遮挡的问题；在子菜单展开或菜单项尺寸变化后仅派发一次布局后 `PlacementRectangle` 重定位，不引入定时轮询。
- **界面资源、图标与窗口按需加载及闲时释放**：
  - `AppIconService` 与 `CustomAppIconStore` 对应用 PNG 图标按目标显示尺寸限制解码大小；`IconFactory` 按尺寸解码托盘图标并即时清理 `HICON` 句柄。
  - `OsdService` 在无新提示 8 秒（`IdleReleaseSeconds = 8`）后单次释放闲置 OSD 窗口，下次触发再按需重建。
  - 将 `ThemeControls.xaml` 与 `SettingsControls.xaml` 从全局 `App.xaml` 移至 `MainWindow.xaml` 随主窗口按需加载，`ThemeColorPickerWindow` 改为延迟创建取色器资源；通过 `HasActiveUiWindows` 统一判断界面窗口活跃状态。
  - `Lite` 与 `Legacy` 保持既有关窗后单次工作集收缩策略，不新增后台周期清理轮询。
- **Full 专用 ADB / 音视频与缓存闲时优化**：
  - `AdbWireProtocol` 采用 `ArrayPool<byte>.Shared` 池化 ADB 读写缓冲，`WinUsbAdbTransport` 使用 64 KiB（`TransferStageSize`）固定传输缓冲；`DirectAdbTransport` / `AdbClient` 增加轻量 `AdbDevicePresence` USB 接口探测与 `ReleaseIdleConnectionsAsync` 闲时连接释放。
  - `D3D11VideoSurfaceRenderer` 内置离线预编译着色器并在会话停止后释放 GPU 资源；`MediaFoundationH264Decoder`、`PcToPhoneAudioManager` 与 `ComInteropHelpers` 在 MTA COM 空闲清理时调用 `CoFreeUnusedLibrariesEx`。
  - `AndroidAppCatalogCache`（8 MiB 预算）支持 `TrimForIdle()` 与 `IconPng` 引用复用，同时对媒体封面执行按需缩放解码。
  - `PhoneClipboardSync` 结合 `WM_DEVICECHANGE` 事件防抖在 USB 设备重连后恢复订阅，不增加后台轮询；Full 版保护活动手机音视频、虚拟屏与剪贴板任务，不做全进程工作集裁剪，仅在完全无活动界面与手机任务时执行一次 LOH 压缩。

### 3. 文档与项目规范同步

- 完善多语言 README 首屏排版与轻量拖拽演示动图（`docs/images/phone-audio-drag-demo.gif`），同步更新 `THIRD-PARTY-NOTICES.md` 适用范围说明（仅限 `v1.30 Full` 与 `v1.31 Full`）。

---

## 下载选择与安装说明

> **运行库说明**：**Full** 与 **Lite** 基于 **.NET 10**，需预先安装与下载包架构一致的 Microsoft [.NET 10 Desktop Runtime（桌面运行时）](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)（Console / ASP.NET Core 运行时不包含 WPF 桌面组件）。**Legacy x64** 基于系统自带 .NET Framework 4.8，无需额外安装运行库；**NET8 兼容版已自 v1.30 起停止提供**。

| 版本 | 适用人群与主要特点 | 文件名 / 获取方式 | 运行库要求 |
| :--- | :--- | :--- | :--- |
| ⭐ **Full x64**（推荐主力） | 完整 PC 音频分流、自动化 + Android 手机音画投屏、双向剪贴板与纯 C# 自研后端 | [`SonicRoute-v1.32-x64.exe`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SonicRoute-v1.32-x64.exe) | [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0) |
| ⭐ **Lite x64** | 专注 PC 本地音频分流、滚轮控音与自动化规则；不含手机模块，单文件更轻巧 | [`SonicRoute-v1.32-Lite-x64.exe`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SonicRoute-v1.32-Lite-x64.exe) | [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0) |
| 🛡️ **Legacy x64** | 解压即用、免安装额外运行库，适合办公或受限环境；不含手机模块 | [`SonicRoute-v1.32-Legacy-x64.zip`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SonicRoute-v1.32-Legacy-x64.zip) | Windows 内置 .NET Framework 4.8 |
| 🧪 **Full ARM64**（实验性） | 面向 Windows on ARM64 原生单文件全功能包（已无外部 x64 `adb`/`scrcpy` 进程；未在真机验收） | [`SonicRoute-v1.32-arm64.exe`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SonicRoute-v1.32-arm64.exe) | [.NET 10 Desktop Runtime (ARM64)](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0) |
| 🧪 **Lite ARM64**（实验性） | 面向 Windows on ARM64 原生纯 PC 音频轻量包（未在真机验收） | [`SonicRoute-v1.32-Lite-arm64.exe`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SonicRoute-v1.32-Lite-arm64.exe) | [.NET 10 Desktop Runtime (ARM64)](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0) |
| 🔄 **Microsoft Store** | 商店版与托管自动更新用户（具体在架版本以微软商店审核同步进度为准） | [微软商店页面](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | x64 `.NET 10 Desktop Runtime` |
| 📋 **校验与更新清单** | 校验发布包完整性与供应用内更新器解析下载 | [`SHA256SUMS.txt`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SHA256SUMS.txt) / [`SonicRoute-update.json`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SonicRoute-update.json) | — |

### 升级与数据保留说明

- **从旧版本升级**：关闭正在运行的旧版程序后，便携单文件版（Full / Lite）直接替换为对应版本的新 `.exe`，Legacy 版将新 `.zip` 解压覆盖原文件夹即可；配置、自动化规则、自定义图标与历史缓存均完整保留在数据目录。
- **应用内更新**：在 **设置 → 软件更新** 中触发检查与升级时，更新器会严格按照当前包的版型（`Full` / `Lite` / `Legacy`）、运行时（`net10` / `net48`）与架构（`x64` / `arm64`）匹配下载并核对 SHA-256，不会跨版型或跨架构自动替换。

---

## 验证范围与已知限制

- **已完成校验范围**：涵盖完整五目标发布包构建与单 EXE 载体校验（Full payload、Lite `net10.0-windows` 与 `net48` 运行时/版型检查、独立更新器检查）、默认输出跟随与自研管线六项修复隔离/回环验证、手机面板布局间距（`PhonePlacement`）与托盘子菜单定位桌面检查、闲时内存隔离验证，以及十语 README 模板生成一致性校验。
- **明确边界**：
  - **ARM64 原生硬件尚未实测**：`arm64` 构建产物在 x64 环境完成编译与打包校验，实际运行仍标记为实验性，需在原生 ARM64 设备上进一步实机确认。
  - **隔离验证与真实体验区分**：离线夹具、本地回环及单模块内存裁剪检查用于核对逻辑与释放路径，不等同于长时间高负载下的真实整版内存/CPU 基准或全机型 Android 真机听感测试。
