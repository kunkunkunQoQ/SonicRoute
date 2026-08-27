# 🎧 音跃 SonicRoute

> Windows 10/11 按应用音频快速切换工具 —— 一键把单个应用的输出设备切到耳机、音箱、显示器或虚拟设备。

作者：[困困困](https://github.com/kunkunkunQoQ) ｜ 版本 **v1.0.6** ｜ 平台 **Windows 10 / 11 (x64)** ｜ 语言 **C# / .NET 8 / WPF**

底层完全基于 **EarTrumpet 已验证的 Per-App Audio Routing 机制** 实现（`IAudioPolicyConfigFactory` / `SetPersistedDefaultAudioEndpoint`），实测通过。

---

## 📑 目录

[🚀 快速开始](#quick-start) ｜ [✨ 功能特性](#features) ｜ [🖼 界面一览](#screenshots) ｜ [⌨️ 全局快捷键](#hotkeys) ｜ [🌐 多语言支持](#languages) ｜ [🎨 主题与自定义](#theme) ｜ [🛠 技术实现](#tech) ｜ [⚙️ 配置文件](#config) ｜ [🔨 本地构建](#build) ｜ [📦 发布产物](#release) ｜ [📋 更新日志](#changelog) ｜ [❓ 常见问题](#faq) ｜ [📌 版本规范](#versioning) ｜ [📄 许可证](#license)

<a id="quick-start"></a>
## 🚀 快速开始

1. 前往 [Releases](https://github.com/kunkunkunQoQ/SonicRoute/releases) 选择版本下载：
   - **绿色免安装版**（`SonicRoute-v1.0.6.exe` / `SonicRoute-v1.0.6.zip`）：无需任何环境，下载即用
   - **轻量版**（`SonicRoute-v1.0.6-Lite.exe` / `SonicRoute-v1.0.6-Lite.zip`）：体积极小，需已装 .NET 8 Desktop Runtime
2. 双击运行（绿色免安装版已内置运行时）
3. 程序驻留系统托盘：
   - **单击**托盘图标 → 快捷面板（当前应用切设备 / 调音量 / 静音 / 全局麦克风静音）
   - **双击**托盘图标 → 完整管理界面
   - **右键**托盘图标 → 打开完整界面 / 快速面板 / 退出
   - **滚轮**放在托盘右侧的任务栏区域上 → 调节当前应用音量

> 也可运行 `SonicRoute.exe --main` 直接打开完整界面。

<a id="features"></a>
## ✨ 功能特性

| 特性 | 说明 |
|---|---|
| 🎯 **按应用切设备 + 音量** | 为单个应用切输出设备、调音量、静音，音量合成器同步更新 |
| 🎤 **全局麦克风静音** | 一键静音/取消所有录音设备 |
| 🧩 **托盘快捷面板** | 单击托盘图标弹出，秒切设备、调音量、静音 |
| 🖥 **完整管理界面** | 双击打开，管理应用、设备、快捷键、主题、配置 |
| 📋 **设备筛选 + 改名** | 勾选常用设备只显示常用；可为设备自定义名称 |
| 🕵️ **当前应用自动检测** | 自动跟随最近使用/正在使用的应用；可对单个应用禁用 |
| ⌨️ **全局快捷键** | 切设备、调音量、静音、开面板，可自定义 |
| 🖱 **托盘滚轮调音量** | 任务栏区域滚轮调当前应用音量，OSD 实时提示 |
| 🌐 **多语言 / 🎨 主题** | 8 种语言跟随系统；深浅色 + RGB 强调色 + 透明度 |
| 🚀 **开机自启 / 解压即用** | 内置 .NET 运行时，无需安装环境 |

<a id="screenshots"></a>
## 🖼 界面一览

### 📌 快捷面板（单击托盘图标）

<img src="docs/images/quick-panel.png" width="320" alt="快捷面板">

单击托盘图标弹出小巧面板：顶部自动显示**当前应用**，下方切常用输出设备、调音量、静音/全局麦克风静音，点击空白自动关闭。

### 🖥 完整管理界面（双击托盘图标）

<img src="docs/images/app-settings.png" width="700" alt="设置页">

双击托盘图标打开完整界面，左侧导航（概览 / 应用 / 快捷键 / 主题 / 设置）：

- **概览**：同快捷面板——当前应用、快捷切输出设备、音量、静音/全局麦克风静音
- **应用**：列出有音频会话的应用，可切输出设备、调音量、改名、禁用自动切换
- **快捷键**：查看与重设全局快捷键（内联录音）
- **主题**：深浅色、强调色（预设 + RGB）、透明度
- **设置**：保留设备、改名、默认应用模式、语言、开机自启

### 🔔 OSD 通知（屏幕右上角）

<img src="docs/images/osb.png" width="300" alt="OSD 通知">

托盘滚轮、快捷键、切设备时右上角弹出轻量**OSD 通知**（应用名+音量/设备/状态），约 1 秒自动淡出，跟随主题，名称使用自定义名。

### ⌨️ 多种快捷键

<img src="docs/images/hotkeys.png" width="700" alt="多种快捷键">

在「快捷键」页点击「修改」按下新组合即可重新绑定。

> 💡 上图为**作者的个人快捷键设置**，并非默认快捷键；默认值见下方「全局快捷键」表格，均可自定义。

<a id="hotkeys"></a>
## ⌨️ 全局快捷键

| 功能 | 默认快捷键 |
|---|---|
| 增大当前应用音量 | `Ctrl + Alt + ↑` |
| 减小当前应用音量 | `Ctrl + Alt + ↓` |
| 静音当前应用 | `Ctrl + Shift + M` |
| 全局麦克风静音 | `Ctrl + Shift + N` |
| 切换当前应用快捷设备 | `Ctrl + Alt + D` |
| 打开快速面板 | `Ctrl + Alt + Space` |

> 在「设置 → 快捷键」中可点击「修改」重新绑定（免弹窗内联录音，`Esc` 取消）。若组合被其他程序占用会自动回退默认并标注 ⚠。

<a id="languages"></a>
## 🌐 多语言支持

| 中文 `zh-CN` | English `en-US` | 日本語 `ja-JP` | 한국어 `ko-KR` |
|---|---|---|---|
| Français `fr-FR` | Deutsch `de-DE` | Español `es-ES` | Русский `ru-RU` |

- **首次启动**自动跟随 Windows 系统语言（未匹配时默认英文）
- **切换方式**：「设置 → 语言」下拉选择，重启后生效

<a id="theme"></a>
## 🎨 主题与自定义

- 主题模式：跟随系统 / 浅色 / 深色
- 强调色：蓝色 / 绿色 / 粉色预设，或 **RGB 滑块自定义**任意颜色
- 背景透明度：60%–100%（默认 85%），快捷面板与完整界面统一生效

<a id="tech"></a>
## 🛠 技术实现

底层完全移植并调用 **EarTrumpet 已验证的 Per-App Audio Routing** 机制：

| 组件 | 说明 |
|---|---|
| `IAudioPolicyConfigFactory` | Windows 按应用音频路由的工厂接口 |
| `SetPersistedDefaultAudioEndpoint` | 按应用**持久化**输出设备（**不改系统默认设备**） |
| `GetPersistedDefaultAudioEndpoint` | 读取应用当前持久化设备 |
| `GenerateDeviceId` / `UnpackDeviceId` | 设备 ID 完整路径编解码 |

> 音量/静音独立走 **Audio Session API**（`ISimpleAudioVolume`），按 PID 聚合全部输出会话，绝不触碰系统主音量。

- **项目结构**：`SonicRoute`（WPF 主程序）/ `SonicRoute.Core`（核心库）/ `SonicRoute.Selftest`（回归自检）/ `Probe`（调试工具）

<a id="config"></a>
## ⚙️ 配置文件

- **位置**：`%LocalAppData%\SonicRoute\config.json`（JSON，改动即时保存，删除即重置默认）

<a id="build"></a>
## 🔨 本地构建

```bash
# Debug
dotnet build SonicRoute.sln -c Debug

# 发布版（自包含单文件）
dotnet publish SonicRoute\SonicRoute.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o dist\SonicRoute
```

<a id="release"></a>
## 📦 发布产物

每次 Release 提供**绿色免安装版**与**轻量版**两种，均含 exe 与 zip（**默认不制作安装包**，MSI 仅 v1.0.6 曾提供）：

| 版本 | 文件 | 体积 | 安装需求 | 优点 | 缺点 |
|---|---|---|---|---|---|
| 🟢 绿色免安装版（自包含） | `SonicRoute-v1.0.6.exe` / `SonicRoute-v1.0.6.zip` | ~156MB / ~67MB | **无**（内置 .NET 运行时） | 免安装免环境，下载即用；适合普通用户、装机环境不干净的用户 | 体积大，下载慢 |
| ⚡ 轻量版（框架依赖） | `SonicRoute-v1.0.6-Lite.exe` / `SonicRoute-v1.0.6-Lite.zip` | ~1.6MB | **需已装 .NET 8 Desktop Runtime (x64)**（未装会弹官方下载引导） | 体积极小，秒下秒开；适合已装运行时/开发者的用户 | 需先装 .NET 8 运行时，否则无法运行 |
| 📦 MSI 安装包（仅 v1.0.6 提供，此后不再制作） | `SonicRoute-v1.0.6.msi` | ~51MB | **无**（内置运行时） | 标准安装/卸载、开始菜单快捷方式、控制面板统一管理、标准用户可安装（无需管理员） | 体积较大；已停止制作 |
| 📦 MSI 轻量版（仅 v1.0.6 提供，此后不再制作） | `SonicRoute-v1.0.6-Lite.msi` | ~0.6MB | **需已装 .NET 8 Desktop Runtime** | 体积极小、标准安装/卸载、标准用户可安装（无需管理员） | 需先装 .NET 8 运行时；已停止制作 |

**轻量版运行时安装**：前往 https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0 选择 "Windows x64 → .NET Desktop Runtime 8.0.x" 安装。

<a id="changelog"></a>
## 📋 更新日志

> 本页只展示**最新版本**的改动。

**v1.0.6**（正式版）
- **新增：禁用自动切换当前应用**：应用页可为每个应用设置「禁用自动切换」，禁用后该应用不会被自动选为"当前应用"（前台跟随 / 最近使用全部跳过），但仍可手动选择；被禁用的应用在应用列表**图标右上角显示主题色 RGB 反色小圆点**，按钮风格与静音一致
- **修复：快捷键与任务栏滚轮应用解析一致**：静音/切设备/调音量的目标应用与"鼠标放任务栏滚轮调音量"统一走同一套解析规则，不再出现两者操作不同应用
- **修复：任务栏 OSD 应用名显示自定义名称**：设置自定义名的应用在任务栏调音量通知里显示自定义名（无则显示进程名），与快捷键/面板/通知一致
- **修复：粉色强调色下方 RGB 预览色块显示粉色**：主题页选"粉色"时下方 RGB 预览/滑块/十六进制值与实际强调色一致
- 版本号统一为 **v1.0.6**

<a id="faq"></a>
## ❓ 常见问题

**Q：为什么改了应用的输出设备，系统默认设备没变？**
A：设计如此——Per-App Audio Routing 只改变单个应用的输出，不动系统默认设备。

**Q：快捷面板里没看到我想用的设备？**
A：到「设置 → 保留的设备」勾选要显示的设备。

**Q：语言切换后界面没变？**
A：语言更改在**下次启动**时生效。

**Q：软件是否需要安装？**
A：不需要，解压即用，已内置 .NET 运行时。

**Q：如果我把软件删了，怎么改回应用的播放设备？**
A：任务栏声音图标**右键 → 打开"音量合成器"**，把对应应用改回即可（SonicRoute 改的就是音量合成器里应用那项，卸载后依然保留）。

<a id="versioning"></a>
## 📌 版本规范

本项目发布版本号采用 **主版本.次版本.修订 + 后缀** 命名：

| 后缀 | 含义 | 是否更新 GitHub |
|---|---|---|
| `a` | **测试版**（内部验证，可能有 bug，仅供自测） | ❌ **不上传 GitHub** |
| `r` | **修复版**（修复 bug 后的正式版本） | ✅ 上传 GitHub |

- 例：`v1.0.5a` = 内部测试版；`v1.0.5r` = 修复 bug 后的正式发布版（再次修复递增 `r2`、`r3`…）
- **默认不制作安装包，仅发布绿色免安装版（exe + zip）**；MSI 仅当明确要求时才制作，MSIX 一律不制作（曾于 v1.0.6 提供）

<a id="license"></a>
## 📄 许可证

本项目仅供学习交流使用。参考实现：[EarTrumpet](https://github.com/File-New-Project/EarTrumpet)（MIT License）。

---

**作者主页 ‖ [哔哩哔哩](https://b23.tv/TDqSAKM) ‖ [爱发电](https://www.ifdian.net/a/koukou021) ‖ [GitHub](https://github.com/kunkunkunQoQ/SonicRoute)**
