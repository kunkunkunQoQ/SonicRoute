# 🎧 音跃 SonicRoute

> Windows 10/11 按应用音频快速切换工具：一键切单应用输出/音量/静音，不改系统默认设备。基于 EarTrumpet 已验证的 Per-App Audio Routing（`IAudioPolicyConfigFactory` / `SetPersistedDefaultAudioEndpoint`）。

作者：[困困困](https://github.com/kunkunkunQoQ) ｜ **v1.11** ｜ Win10/11 x64 ｜ C# / .NET 8 / WPF

---

[🚀 快速开始](#quick-start) ｜ [✨ 功能](#features) ｜ [🖼 界面](#screenshots) ｜ [⌨️ 快捷键](#hotkeys) ｜ [🎨 自定义](#customize) ｜ [🛠 技术](#tech) ｜ [📦 下载](#release) ｜ [📋 更新日志](#changelog) ｜ [❓ FAQ](#faq) ｜ [📌 版本规范](#versioning)

<a id="quick-start"></a>
## 🚀 快速开始

1. [Releases](https://github.com/kunkunkunQoQ/SonicRoute/releases) 下载（绿色版解压即用 / 轻量版需 .NET 8），或 [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT)
2. 驻留托盘：**单击**→快捷面板（切设备/音量/静音）｜**双击**→完整界面｜**任务栏滚轮**→调当前应用音量（OSD 提示）

<a id="features"></a>
## ✨ 功能

| 特性 | 说明 | 特性 | 说明 |
|---|---|---|---|
| 🎯 按应用切设备+音量 | 单应用切输出/音量/静音，音量合成器同步 | 🎤 全局麦克风静音 | 一键静音所有录音设备 |
| 🧩 托盘快捷面板 | 单击秒切设备/音量/静音，点空白自动关闭 | 🖥 完整管理界面 | 应用/设备/快捷键/主题/设置一站式管理 |
| 📋 设备筛选+改名 | 只显示常用设备，设备/应用可自定义名称 | 🕵️ 当前应用自动检测 | 自动跟随最近使用，可单应用禁用 |
| ⌨️ 全局快捷键 | 切设备/音量/静音/面板，全部可自定义 | 🧹 内存优化 | 关闭 UI 自动释放+换出，占用可降至 ~4MB |

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

**🌐 多语言**：中文/English/日本語/한국어/Français/Deutsch/Español/Русский，跟随系统，切换后重启生效 ｜ **🎨 主题**：深浅色跟随系统、RGB 强调色、透明度 60–100%（默认 85%）

<a id="tech"></a>
## 🛠 技术实现

| 组件 | 作用 |
|---|---|
| `IAudioPolicyConfigFactory` | Windows 按应用音频路由工厂接口 |
| `SetPersistedDefaultAudioEndpoint` | 按应用持久化输出设备（不改系统默认） |
| `ISimpleAudioVolume` | 按 PID 聚合会话，独立调音量/静音 |

项目结构：`SonicRoute`（WPF）/ `SonicRoute.Core`（核心）/ `SonicRoute.Selftest`（自检）/ `Probe`（调试）。配置：`%LocalAppData%\SonicRoute\config.json`。

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
A：设计如此——只改单个应用，不动系统默认。

**Q：面板里没看到想用的设备？**
A：设置 → 保留的设备 里勾选。

**Q：删了软件怎么改回应用播放设备？**
A：任务栏声音图标右键 → 音量合成器，把对应应用改回即可。

<a id="versioning"></a>
## 📌 版本规范

| 后缀 | 含义 | 上传 GitHub |
|---|---|---|
| `a` | 测试版（内部验证） | ❌ |
| `r` | 修复版（bug 修复后正式发布） | ✅ |

例：`v1.0.5a`=测试版；`v1.0.5r`=修复版（再次修复递增 r2/r3…）。默认仅发布绿色免安装版（exe+zip）。

---

**作者主页 ‖ [哔哩哔哩](https://b23.tv/TDqSAKM) ‖ [爱发电](https://www.ifdian.net/a/koukou021) ‖ [GitHub](https://github.com/kunkunkunQoQ/SonicRoute)**
