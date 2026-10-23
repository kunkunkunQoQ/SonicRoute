# 🎧 SonicRoute v1.26 正式版

> **发布时间**：2026-10-23 · **核心主题**：高权限应用图标修复、按需音频读取与底层状态可靠性 · [📥 资产下载](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.26) · [📖 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

---

## ✨ 核心更新亮点

- 🖼️ **高权限与受保护应用图标智能修复**：彻底解决非管理员权限下网易 UU 远程、系统服务等高权限程序只显示文字占位符的问题；通过复用 Windows WASAPI 音频会话原生图标元数据与窗口回退机制，无感还原真实应用图标。
- 🎚️ **音频会话与策略对象深度复用**：重构底层 CoreAudio 查询流程，在单轮查询中端到端复用设备句柄与策略接口；合并音量与静音状态读取，快速面板在无活跃发声应用时立即跳过额外读取，大幅减少无效 COM 轮询与开销。
- 🎯 **默认设备角色与静音判定更精准**：严格解耦数据流向（Render / Capture）与设备角色（Console / Multimedia / Communications），彻底杜绝跨角色配置冲突；多会话应用合并静音状态更准确，操作遇阻时如实反馈，不再假定全部成功。
- ⚡ **运行时持久化定制加速（.NET 10 / Legacy）**：.NET 10 主力版引入源生成（Source Generator）预编译 JSON 上下文，彻底告别反射开销，提速配置读写；Legacy 版全面缓存元数据及程序路径，同时修复文件监视器退出时的锁等待问题。
- 🍃 **资源按需加载与无感生命周期管理**：图标元数据仅在界面实际展开可见时按需加载，面板关闭即刻暂停专用监听；继续严守克制原则，闲时保持接近 0% CPU 占用，不新增任何周期性后台轮询。
- 🔄 **便携版在软件内一键极速升级**：v1.25 便携版用户可直接在「设置 → 软件更新」中一键检测、校验并平滑重启升级至 v1.26，无需手动前往网页下载覆盖，无缝继承所有规则与配置。

---

## 📋 详细演进说明

### 1. 攻克高权限进程图标缺失，无侵入还原视觉体验
在 Windows 安全机制中，未以管理员身份运行的程序无法通过 Win32 API 读取高权限进程（如 SYSTEM 权限的网易 UU 远程服务、以管理员运行的游戏或工具）的可执行文件路径，此前往往只能回退显示首字母或通用占位图标。
- **WASAPI 会话元数据原生提取**：创新性地利用 Windows 原生音频会话接口（`IAudioSessionControl2.GetIconPath`），在无需提权、无注入的前提下直接获取应用创建音频流时声明的图标路径；
- **多层自愈与自定义优先**：建立「会话元数据 → 窗口图标（WM_GETICON）→ 进程模块」的多级回退链条；用户拖拽导入的自定义图标（PNG/JPEG/ICO）始终保持最高优先级；
- **动态就绪通知**：当后台受保护应用的音频流元数据延迟就绪时，界面对应行会自动刷新呈现，且全程异步加载，绝不阻塞 UI 渲染主线程。

### 2. 音频查询路径精简，空面板零开销
为了在频繁呼出面板或多设备切换时提供更极致的跟手感，v1.26 对核心音频服务（`AudioService`、`SessionVolumeService`）进行了系统级轻量化重构：
- **对象生命周期复用**：在单次应用扫描中，复用已获取的 `IMMDevice` 与策略接口实例，避免对系统混音器服务发起数十次重复的 COM 实例化与释放；
- **状态读取批量合并**：将原本分离的会话音量大小获取与静音状态查询合并为单次批量交互，减少一半的系统内核态切换开销；
- **空面板快速旁路**：当快速面板展开但当前没有任何应用在发声时，直接跳过全量会话遍历逻辑，实现空闲状态下的极低资源损耗。

### 3. 严格厘清设备角色，规避潜在配置串扰
Windows 底层将音频设备划分为数据流向（播放 Render / 录音 Capture）与业务角色（控制台/游戏 Console、多媒体 Multimedia、语音通讯 Communications）。
- **解耦角色枚举**：修正了策略客户端中的角色参数映射，保证设置默认播放设备或麦克风时，精确作用于用户指定的业务角色，杜绝以往在特定系统版本上可能发生的流向与角色混淆；
- **多会话复合应用静音校准**：部分复杂应用（如现代浏览器或大型游戏）会为不同音轨创建多个独立的音频播放会话。新版本能够准确聚合多个子会话的静音状态，并在部分会话操作失败时如实向上层反馈，确保自动化流水线与状态显示 100% 真实。

### 4. 平台针对性持久化调优，消灭退出锁等待
针对 SonicRoute 支持的多元化运行环境，分别量身定制优化方案：
- **.NET 10 源生成预编译**：通过 `PersistenceJsonContext` 在编译期完成 JSON 序列化契约的静态生成，彻底消除 `config.json` 与自动化规则在运行时的动态反射与泛型构造开销，规则保存与读取更加轻快；
- **Legacy 兼容版元数据复用**：在 .NET Framework 4.8 环境下全面缓存序列化元数据与可执行文件路径，保持旧机型上的流畅表现；
- **安全关停修复**：修复了配置与规则目录的 `FileSystemWatcher` 在程序退出时可能因多线程锁竞争导致短暂卡顿的问题，确保应用退出干净利落。

---

## 🚀 资产下载矩阵

| 发行形态 | 推荐目标人群 | 下载链接 | 运行环境要求 |
|---|---|---|---|
| ⭐ **Lite x64**（.NET 10 推荐主力） | 主流 64 位 PC 绿色单文件 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Lite-x64.exe) | 需匹配的 x64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| ⚡ **Lite ARM64**（.NET 10 实验版） | Surface Pro / 骁龙 PC 原生高效 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Lite-arm64.exe) | 需匹配的 ARM64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| ☕ **Lite NET8 x64**（.NET 8 兼容版） | 现有仅安装了 .NET 8 的机型 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Lite-NET8-x64.exe) | 需匹配的 x64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| ☕ **Lite NET8 ARM64**（.NET 8 兼容版） | 现有仅安装了 .NET 8 的 ARM64 机型 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Lite-NET8-arm64.exe) | 需匹配的 ARM64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| 🛡️ **Legacy x64**（免装运行库） | 公司办公电脑 / 受限机型 | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Legacy-x64.zip) | 系统自带 .NET Framework 4.8（解压即用） |
| 🔄 **Microsoft Store** | 商店静默后台自动更新 | [商店安装](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | 适用于 64 位 Windows 10 2004+ / Windows 11 |

> [!NOTE]
> - 支持 Windows 10 2004+ (Build 19041+) 及 Windows 11 系统。
> - .NET 10 默认文件名无附加后缀，.NET 8 兼容版带有 `-NET8` 后缀。依照规划，**2026 年 11 月之后将停止提供 .NET 8 新构建**，建议广大用户优先迁移至 .NET 10 或选用无需运行库的 Legacy 版本。
> - 所有便携版发行附件均附带 `SHA256SUMS.txt` 校验清单与升级清单 `SonicRoute-update.json`（协议版本 2）。

---

## 🔄 升级与数据保留指引

- **v1.25 便携版用户（推荐在软件内一键升级）**：
  打开 SonicRoute「主窗口 → 设置 → 软件更新」，点击「检查更新」，系统将自动匹配当前架构与运行时，下载并完成校验后点击「立即重启升级」，全程自动备份并平滑过渡。
- **v1.24 及更早版本用户（手动升级）**：
  请先在右下角系统托盘右键退出当前运行的旧版 SonicRoute，然后下载对应的新版 EXE（或将 Legacy ZIP 解压覆盖到原程序目录），双击启动即可。
- **数据与规则 100% 完整保留**：
  软件的所有用户设置、37 种自动化连招规则、全局快捷键绑定及自定义图标均独立保存在 `%LocalAppData%\SonicRoute\`，跨版本升级绝不丢失任何数据。

---

## 📚 相关链接

- [Wiki 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [自动化规则配置手册](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [常见问题排障](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)
- [📚 完整版本历史](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) · [🐛 提交建议与问题反馈](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)
