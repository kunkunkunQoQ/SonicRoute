# 🎧 音跃 SonicRoute · Windows 音频枢纽

> Windows 10/11 **音频控制中心**：不止切换单个应用——**当前应用 / 全局应用 / 系统默认**三档音频路由、按应用音量与静音、设备管理，一个托盘入口全盘掌控。基于 EarTrumpet 已验证的 Per-App Audio Routing（`IAudioPolicyConfigFactory` / `SetPersistedDefaultAudioEndpoint`）。

作者：[困困困](https://github.com/kunkunkunQoQ) ｜ **v1.12r** ｜ Win10/11 x64 ｜ C# / .NET 8 / WPF

> 🙏 **特别感谢 [EarTrumpet](https://github.com/File-New-Project/EarTrumpet)**：本项目的按应用音频路由（Per-App Audio Routing）底层实现参考其开源代码移植而来。

---

[🚀 快速开始](#quick-start) ｜ [✨ 功能](#features) ｜ [🖼 界面](#screenshots) ｜ [⌨️ 快捷键](#hotkeys) ｜ [🎨 自定义](#customize) ｜ [🛠 技术](#tech) ｜ [📦 下载](#release) ｜ [📋 更新日志](#changelog) ｜ [❓ FAQ](#faq) ｜ [📌 版本规范](#versioning)

<a id="quick-start"></a>
## 🚀 快速开始

1. [Releases](https://github.com/kunkunkunQoQ/SonicRoute/releases) 下载（绿色版解压即用 / 建议轻量版环境不全则绿色版），或 [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT)
2. 驻留托盘：**单击**→快捷面板｜**双击**→完整界面｜**任务栏滚轮**→调当前应用音量（OSD 提示）
3. 完整体验：[Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)（功能详解 / 分步教程 / 常见问题）

<a id="features"></a>
## ✨ 功能

| 特性 | 说明 | 特性 | 说明 |
|---|---|---|---|
| 🎯 三档音频路由 | 当前应用 / 全局应用 / **系统默认设备**，随时切换 | 🎤 麦克风枢纽 | 按应用切输入 + 全局麦克风静音 |
| 🧩 托盘快捷面板 | 简洁/经典双面板，单击秒切设备/音量/静音 | 🖥 完整管理界面 | 应用/设备/快捷键/主题/设置一站式管理 |
| 📋 设备管理中枢 | 输出/输入筛选、改名、虚拟声卡一目了然 | 🕵️ 当前应用自动检测 | 自动跟随最近使用，可单应用禁用 |
| ⌨️ 快捷键中枢 | 12 项动作全可自定义（F区/单键/鼠标/滚轮） | ♻️ 一键还原 | 全部应用（含已退出）恢复默认设备 |
| 🧹 内存优化 | 关闭 UI 自动释放+换出，占用可降至 ~4MB | 🌐 多语言+主题 | 8 种语言、RGB 强调色、透明度 |

**🎯 组合场景**（三档路由互不影响，先想**影响范围**再选档）：

| 想影响… | 用哪档 | 入口 / 快捷键 | 实际更改 |
|---|---|---|---|
| 只一个应用（如游戏） | 当前应用档 | 面板点设备 / `Ctrl+Alt+D` 循环 | 该应用的输出设备 |
| 所有「未单独设置」的应用 | 系统默认档 | `Ctrl+Alt+O/I` | 系统默认输出/输入设备 |
| 所有运行中应用 | 全局档 | `Ctrl+Alt+Shift+O/I` | 全部运行应用的输出/输入设备 |
| 游戏静音 | 静音 | `Ctrl+Shift+M`（游戏成为当前应用后） | 游戏音量静音/恢复 |
| 全局麦克风静音 | 静音 | `Ctrl+Shift+N` | 全部麦克风静音/恢复 |
| 全部恢复默认 | 一键还原 | `Ctrl+Alt+Shift+R` / 音量合成器「重置」 | 全部应用（含已退出）路由重置为默认 |

> **决策**：只改一个 → 当前应用档；让未设置的默认应用一起换 → 系统默认档；临时全体切（虚拟声卡）→ 全局档；收尾 → `Ctrl+Alt+Shift+R`。
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
| 音量+ / 音量- | Ctrl+Alt+↑/↓ | 静音当前应用 / 全局麦克风静音 | Ctrl+Shift+M/N |
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

项目结构：`SonicRoute`（WPF）/ `SonicRoute.Core`（核心）/ `SonicRoute.Selftest`（自检）/ `Probe`（调试）。配置：`%LocalAppData%\SonicRoute\config.json`。完整实现细节见 [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0)。

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
| 🟢 绿色免安装 | `SonicRoute-v1.12r.exe` / `.zip` | ~237MB / ~89MB | 内置运行时 |
| ⚡ 轻量版 | `SonicRoute-v1.12r-Lite.exe` / `.zip` | ~25.9MB / ~6.8MB | 需 .NET 8 |
| 🛍 微软商店 | [Store 搜索 SonicRoute](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | — | 自动安装更新 |

轻量版需 .NET 8 Desktop Runtime：[下载](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0)

<a id="changelog"></a>
## 📋 更新日志

**v1.12**
- 全新简洁快速面板（默认，Win11 音量飞出式）：逐应用音量列表 + 每行独立滑块/图标静音/折叠设设备 + 全局输出/麦克风静音；经典面板可在设置切换，新旧共用快捷键与托盘
- 面板反馈改右上角 OSD 通知；静音/禁用自动切换/快速面板显示等按钮状态用强调色表达，文本固定不切换
- 静音快捷键改为控制系统设备；应用列表新增「禁用在快速面板显示」独立开关
- OSD 位置调整移入主题页（拖拽定位 + 一键还原）
- 新增繁体中文（共 9 语言）；新增翻译规范
- 内存优化落地：删除实验内存释放入口，启动 15s 提前回收，占用更低
- 开机自启：新增清理自启项按钮、商店版独立自启存储、启动自检修复
- UI 收纳：清理自启项与麦克风子选项收进「显示更多选项」折叠区

**v1.12r**（修复版）
- OSD 全面重构：连续操作零闪烁零跳动、视觉树长期复用、内存不随操作增长、防定位任务堆积
- 修复 OSD 不显示与一键还原位置错误（坐标单位混用根因）；清理九宫格死代码
- OSD 支持自由调整大小（宽度/字号滑条）+ 拖拽定位 + 多屏 DPI 正确
- 修复长设备名 OSD 闪烁 / 双屏闪现（固定宽度 + 定位 Clamp + 光标所在屏）
- 托盘图标深浅色自动跟随任务栏主题
- 新增音量步进设置（1-20%，托盘滚轮/面板滚轮/音量快捷键共用）
- 简洁面板底部按钮布局微调；概览「全局麦克风静音」文案简化
<a id="faq"></a>
## ❓ FAQ

**Q：改了应用输出设备，系统默认设备没变？**
A：设计如此——按应用路由只改单个应用；要改系统默认请用「切换系统默认输出/输入」快捷键或完整界面。

**Q：面板里没看到想用的设备？**
A：设置 → 保留的设备 里勾选，输出和输入分开勾选。

**Q：删了软件怎么改回应用播放设备？**
A：任务栏声音图标右键 → 音量合成器，把对应应用改回即可。

**Q：更多问题？**
A：见 [Wiki 常见问题](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)（20+ 条分类解答）。

<a id="versioning"></a>
## 📌 版本规范

| 后缀 | 含义 | 上传 GitHub |
|---|---|---|
| `a` | 测试版（内部验证） | ❌ |
| `r` | 修复版（bug 修复后正式发布） | ✅ |

例：`v1.0.5a`=测试版；`v1.0.5r`=修复版（再次修复递增 r2/r3…）。默认仅发布绿色免安装版（exe+zip）。

---

**作者主页 ‖ [哔哩哔哩](https://b23.tv/TDqSAKM) ‖ [爱发电](https://www.ifdian.net/a/koukou021) ‖ [GitHub](https://github.com/kunkunkunQoQ/SonicRoute)**
