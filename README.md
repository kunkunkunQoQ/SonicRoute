# 🎧 音跃 SonicRoute

> Windows 11 按应用音频快速切换工具 · 一键把单个应用的输出/输入设备切到耳机、音箱、显示器或虚拟设备

作者：[困困困](https://github.com/kunkunkunQoQ) · 基于 **EarTrumpet 的 Per-App Audio Routing 机制** 实现，实测通过。

---

## ✨ 功能

- **应用列表**：自动显示当前有音频会话的应用（Chrome / 游戏 / Discord / QQ / 哔哩哔哩 …），含进程名与 PID
- **一键切换设备**：为单个应用设置输出设备 / 输入设备，改完立即生效，Windows 音量合成器同步更新
- **快捷面板**：单击托盘图标弹出，当前应用秒切设备、调音量、静音，适合游戏时快速操作
- **完整管理界面**：双击托盘图标打开，管理应用、设备、快捷键、主题、配置
- **设备筛选**：设置里勾选"保留的设备"，快捷面板只显示你常用的设备；输出/输入独立设置
- **设备改名**：为设备自定义显示名称，中文输入流畅
- **当前应用自动检测**：跟随最近打开/上次操作/指定应用，自动匹配前台应用
- **单独应用音量**：滑块实时调节当前应用自己的 Session 音量，不影响系统主音量和其他应用；支持静音
- **麦克风静音**：一键静音/取消静音当前应用的麦克风（输入会话），面板/概览/快捷键均可操作
- **全局快捷键**：切设备（输出/输入）、调音量、静音（扬声器/麦克风）、打开面板，全部可自定义
- **托盘滚轮调音量**：鼠标放在托盘图标上滚轮即可调节当前应用音量，OSD 提示
- **界面主题**：深色/浅色跟随系统，多种强调色（含 RGB 自定义）、背景透明度可调
- **中英文双语**：设置里一键切换
- **开机自启**、**解压即用**（内置 .NET 运行时，无需安装任何环境）

## 🛠 技术实现

底层完全移植并调用 **EarTrumpet 已验证的 Per-App Audio Routing** 机制：

| 组件 | 说明 |
|---|---|
| `IAudioPolicyConfigFactory` | Windows 按应用音频路由的工厂接口 |
| `SetPersistedDefaultAudioEndpoint` | 按应用**持久化**输出/输入设备（不改系统默认设备） |
| `GetPersistedDefaultAudioEndpoint` | 读取应用当前持久化设备 |
| `GenerateDeviceId` / `UnpackDeviceId` | 设备 ID 完整路径编解码 |

> 音量/静音独立走 **Audio Session API**（`ISimpleAudioVolume`），按 PID 聚合全部输出会话，绝不触碰系统主音量。

- **语言/框架**：C# · .NET 8 · WPF · Windows 10/11 · x64
- **项目结构**：`SonicRoute`（WPF 主程序）/ `SonicRoute.Core`（核心库）/ `SonicRoute.Selftest`（回归自检）/ `Probe`（调试工具）

## 📦 下载与安装

前往 [Releases](https://github.com/kunkunkunQoQ/SonicRoute/releases) 下载最新版 zip（如 `SonicRoute-v1.0.2.zip`，**解压即用**）：

1. 解压到任意目录
2. 双击 `SonicRoute.exe` 运行（已内置运行时，绿色免安装）
3. 程序驻留系统托盘，开始使用

> 也可直接运行 `SonicRoute.exe --main` 打开完整界面。

## ⌨️ 全局快捷键

| 功能 | 默认快捷键 |
|---|---|
| 增大当前应用音量 | `Ctrl + Alt + ↑` |
| 减小当前应用音量 | `Ctrl + Alt + ↓` |
| 静音当前应用 | `Ctrl + Shift + M` |
| 麦克风静音当前应用 | `Ctrl + Shift + N` |
| 切换当前应用快捷设备 | `Ctrl + Alt + D` |
| 打开快速面板 | `Ctrl + Alt + Space` |

> 在「设置 → 快捷键」中可自定义；若组合被其他程序占用会自动回退默认并提示 ⚠。

## ⚙️ 配置文件

- **位置**：`%LocalAppData%\SonicRoute\config.json`
- **格式**：JSON 纯文本（UTF-8），改动即时保存
- **内容**：保留的设备、设备自定义名称、快捷键、界面语言、主题/强调色/透明度、默认应用模式、开机自启等
- **重置**：删除该文件即可恢复全部默认设置

## 🔨 本地构建

```bash
# Debug
dotnet build SonicRoute.sln -c Debug

# 发布版（自包含单文件）
dotnet publish SonicRoute\SonicRoute.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o dist\SonicRoute
```

## 📄 许可证

本项目仅供学习交流使用。参考实现：EarTrumpet（MIT License）。

---

**作者主页 ‖ [哔哩哔哩](https://b23.tv/TDqSAKM) ‖ [爱发电](https://www.ifdian.net/a/koukou021) ‖ [GitHub](https://github.com/kunkunkunQoQ/SonicRoute)**
