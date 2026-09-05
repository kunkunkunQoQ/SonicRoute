# 🎧 音跃 SonicRoute · Windows 音频枢纽

> Windows 10/11 **音频控制中心**：不止切换单个应用——**当前应用 / 全局应用 / 系统默认**三档音频路由、按应用音量与静音、设备管理，一个托盘入口全盘掌控。基于 EarTrumpet 已验证的 Per-App Audio Routing（`IAudioPolicyConfigFactory` / `SetPersistedDefaultAudioEndpoint`）。

作者：[困困困](https://github.com/kunkunkunQoQ) ｜ **v1.11** ｜ Win10/11 x64 ｜ C# / .NET 8 / WPF

---

[🚀 快速开始](#quick-start) ｜ [✨ 功能](#features) ｜ [🖼 界面](#screenshots) ｜ [⌨️ 快捷键](#hotkeys) ｜ [🎨 自定义](#customize) ｜ [🛠 技术](#tech) ｜ [📦 下载](#release) ｜ [📋 更新日志](#changelog) ｜ [❓ FAQ](#faq) ｜ [📌 版本规范](#versioning)

<a id="quick-start"></a>
## 🚀 快速开始

1. [Releases](https://github.com/kunkunkunQoQ/SonicRoute/releases) 下载（绿色版解压即用 / 轻量版需 .NET 8），或 [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT)
2. 驻留托盘：**单击**→快捷面板｜**双击**→完整界面｜**任务栏滚轮**→调当前应用音量（OSD 提示）
3. 完整体验：[Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)（功能详解 / 分步教程 / 常见问题）

<a id="features"></a>
## ✨ 功能

| 特性 | 说明 | 特性 | 说明 |
|---|---|---|---|
| 🎯 三档音频路由 | 当前应用 / 全局应用 / **系统默认设备**，随时切换 | 🎤 麦克风枢纽 | 按应用切输入 + 全局麦克风静音 |
| 🧩 托盘快捷面板 | 单击秒切设备/音量/静音，点空白自动关闭 | 🖥 完整管理界面 | 应用/设备/快捷键/主题/设置一站式管理 |
| 📋 设备管理中枢 | 输出/输入筛选、改名、虚拟声卡一目了然 | 🕵️ 当前应用自动检测 | 自动跟随最近使用，可单应用禁用 |
| ⌨️ 快捷键中枢 | 12 项动作全可自定义（F区/单键/鼠标/滚轮） | ♻️ 一键还原 | 全部应用（含已退出）恢复默认设备 |
| 🧹 内存优化 | 关闭 UI 自动释放+换出，占用可降至 ~4MB | 🌐 多语言+主题 | 8 种语言、RGB 强调色、透明度 |

<a id="screenshots"></a>
## 🖼 界面

| 快捷面板（单击托盘） | 完整界面（双击托盘） |
|---|---|
| <img src="docs/images/quick-panel.png" width="320"> 顶部自动显示当前应用，下方切设备/调音量/静音 | <img src="docs/images/app-settings.png" width="380"> 左侧导航，管理应用/设备/快捷键/主题/设置 |
| **OSD 通知**（右上角弹出，1秒淡出，跟随主题） | **快捷键设置**（内联录音，点击即改，Esc 取消） |
| <img src="docs/images/osb.png" width="320"> | <img src="docs/images/hotkeys.png" width="380"> |

> 快捷键截图为作者个人设置，非默认值。

<a id="hotkeys"></a>
## ⌨️ 全局快捷键

| 功能 | 默认 | 功能 | 默认 |
|---|---|---|---|
| 音量+ / 音量- | `Ctrl+Alt+↑/↓` | 静音当前应用 / 全局麦克风静音 | `Ctrl+Shift+M/N` |
| 切换当前应用快捷设备 | `Ctrl+Alt+D` | 切换当前应用麦克风 | `Ctrl+Alt+Shift+D` |
| 切换全局应用输出 / 输入 | `Ctrl+Alt+Shift+O/I` | 切换系统默认输出 / 输入 | `Ctrl+Alt+O/I` |
| 还原全部应用默认设备 | `Ctrl+Alt+Shift+R` | 打开快速面板 | `Ctrl+Alt+Space` |

支持 F1-F24、无修饰单键、鼠标键/滚轮绑定（可组合修饰键），按键设置按「音量 / 当前应用 / 全局与系统 / 界面」分组。

<a id="customize"></a>
## 🎨 自定义

**🌐 多语言**：中文/English/日本語/한국어/Français/Deutsch/Español/Русский，跟随系统，切换后重启生效 ｜ **🎨 主题**：深浅色跟随系统、RGB 强调色、透明度 60–100%（默认 85%） ｜ **📦 形态**：绿色免安装 / 轻量版 / 微软商店

<a id="tech"></a>
## 🛠 技术实现

| 组件 | 作用 |
|---|---|
| `IAudioPolicyConfigFactory` | Windows 按应用音频路由工厂接口 |
| `SetPersistedDefaultAudioEndpoint` | 按应用持久化输出/输入设备（不改系统默认） |
| `IPolicyConfig.SetDefaultEndpoint` | 切换系统默认输出/输入设备 |
| `ClearAllPersistedApplicationDefaultEndpoints` | Win11 22H2+「音量合成器重置」底层，一键还原 |
| `ISimpleAudioVolume` | 按 PID 聚合会话，独立调音量/静音 |

项目结构：`SonicRoute`（WPF）/ `SonicRoute.Core`（核心）/ `SonicRoute.Selftest`（自检）/ `Probe`（调试）。配置：`%LocalAppData%\SonicRoute\config.json`。完整实现细节见 [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki/%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0)。

```bash
# 构建
dotnet build SonicRoute.sln -c Debug
# 发布（自包含单文件；不加压缩参数 → 低内存，与 Lite 一致）
dotnet publish SonicRoute\SonicRoute.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

<a id="release"></a>
## 📦 下载

| 版本 | 文件 | 体积 | 需求 |
|---|---|---|---|
| 🟢 绿色免安装 | `SonicRoute-v1.11.exe` / `.zip` | ~180MB / ~69MB | 内置运行时 |
| ⚡ 轻量版 | `SonicRoute-v1.11-Lite.exe` / `.zip` | ~25MB / ~6.5MB | 需 .NET 8 |
| 🛍 微软商店 | [Store 搜索 SonicRoute](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | — | 自动安装更新 |

轻量版需 .NET 8 Desktop Runtime：[下载](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0)

<a id="changelog"></a>
## 📋 更新日志

**v1.11**
- 快捷键大升级：支持 F1-F24 / 无修饰单键 / 鼠标键+滚轮绑定 / Esc 取消，设置页按功能分组
- 新增快捷键：切换系统默认输出/输入（Ctrl+Alt+O/I）、切换全局应用输出/输入（Ctrl+Alt+Shift+O/I）、还原全部应用默认设备（Ctrl+Alt+Shift+R）
- 麦克风选项移至设置页常驻，不再需要实验模式

<a id="faq"></a>
## ❓ FAQ

**Q：改了应用输出设备，系统默认设备没变？**
A：设计如此——按应用路由只改单个应用；要改系统默认请用「切换系统默认输出/输入」快捷键或完整界面。

**Q：面板里没看到想用的设备？**
A：设置 → 保留的设备 里勾选，输出和输入分开勾选。

**Q：删了软件怎么改回应用播放设备？**
A：任务栏声音图标右键 → 音量合成器，把对应应用改回即可。

**Q：更多问题？**
A：见 [Wiki 常见问题](https://github.com/kunkunkunQoQ/SonicRoute/wiki/%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)（20+ 条分类解答）。

<a id="versioning"></a>
## 📌 版本规范

| 后缀 | 含义 | 上传 GitHub |
|---|---|---|
| `a` | 测试版（内部验证） | ❌ |
| `r` | 修复版（bug 修复后正式发布） | ✅ |

例：`v1.0.5a`=测试版；`v1.0.5r`=修复版（再次修复递增 r2/r3…）。默认仅发布绿色免安装版（exe+zip）。

---

**作者主页 ‖ [哔哩哔哩](https://b23.tv/TDqSAKM) ‖ [爱发电](https://www.ifdian.net/a/koukou021) ‖ [GitHub](https://github.com/kunkunkunQoQ/SonicRoute)**