# 🎧 SonicRoute v1.23 正式版

> **发布时间**：2026-10-15 · **核心主题**：托盘右键音频控制与盲调分离 · [📥 资产下载](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.23) · [📖 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

---

## ✨ 核心更新亮点

- 🖱️ **托盘右键音频控制中枢**：右键菜单直接选择系统默认输出设备、调节当前应用音量预设/静音、分配应用输出端点及全部麦克风静音；操作期间锁定前台焦点。
- ⏸️ **托盘滚轮调音支持暂停**：支持在托盘右键菜单随时暂停/恢复托盘区滚轮调音，状态跨重启持久化，防止误触。
- 🎚️ **应用音量与系统音量独立键位**：默认快捷键清晰划分——应用音量 `Ctrl+Alt+滚轮`，系统默认音量 `Ctrl+Alt+Shift+滚轮`。
- ⚙️ **设置交互分层收拢**：输入静音监听与麦克风选项层级优化，按需创建页面与共用菜单控件，关闭后及时停止后台界面读取。
- 🌐 **九语国际化同步**：同步更新 9 种语言界面文案与 README 结构，表述更地道流畅。

---

## 📋 详细演进说明

### 1. 托盘右键全能调度
无需打开主窗口或弹出面板，在托盘右键菜单中即可完成核心音频操作：
- **系统默认输出选择**：一键切换桌面音箱与耳机；
- **当前应用音量档位**：支持 25%、50%、75%、100% 快速赋值与一键静音；
- **前台焦点锁定**：右键菜单呼出时会瞬间固定当前发声应用，避免因鼠标移动到菜单上而误把焦点切给其他进程。

### 2. 托盘区滚轮盲调防误触
在托盘右键菜单中新增「暂停滚轮调节音量」开关。对于习惯在任务栏右下角密集操作其他软件的用户，开启后托盘区域滚轮不再调节音量，且该状态会持久化保存至配置文件中；全局音量快捷键不受影响。

### 3. 应用音量与系统音量快捷键分离
- **前台应用音量**：默认分配为 `Ctrl+Alt+滚轮上/下`；
- **系统主音量**：默认分配为 `Ctrl+Alt+Shift+滚轮上/下`；
- 已有自定义键位的用户自动保留原有设置，也可随时在「快捷键」页面自由修改。

---

## 🚀 资产下载矩阵

| 发行形态 | 推荐目标人群 | 下载链接 | 运行环境要求 |
|---|---|---|---|
| ⭐ **Lite x64**（推荐主力） | 主流 64 位 PC 绿色单文件 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.23/SonicRoute-v1.23-Lite-x64.exe) | 需匹配的 x64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| ⚡ **Lite ARM64**（实验） | Surface Pro / 骁龙 PC 原生高效 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.23/SonicRoute-v1.23-Lite-arm64.exe) | 需匹配的 ARM64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| 🛡️ **Legacy x64**（免装运行库） | 公司办公电脑 / 受限机型 | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.23/SonicRoute-v1.23-Legacy-x64.zip) | 系统自带 .NET Framework 4.8（解压即用） |
| 🔄 **Microsoft Store** | 商店静默后台自动更新 | [商店安装](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | 适用于 64 位 Windows 10 2004+ / Windows 11 |

> [!NOTE]
> - 支持 Windows 10 2004+ / Windows 11 系统。
> - Lite 版本请确保安装了对应架构的 .NET 8 桌面运行时；Legacy 版本解压后直接运行 `SonicRoute.exe` 即可。
> - 资产完整性 SHA-256 校验和请查看发布附件 `SHA256SUMS.txt`。

---

## 🔄 升级与迁移指引

- **覆盖升级**：升级前请先在任务栏右下角托盘图标右键选择「退出」，然后运行新版 EXE 或解压覆盖 Legacy 文件夹。
- **数据保留**：已有的应用分流设置、快捷键与自动化规则（保存在 `%LocalAppData%\SonicRoute\`）会自动无损继承。
- **开机自启**：普通版与微软商店版各自使用独立自启项，互不干扰。

---

## 📚 相关链接

- [Wiki 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [快捷键设置手册](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) · [常见问题解答](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)
- [提交建议或 Bug 反馈](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)
