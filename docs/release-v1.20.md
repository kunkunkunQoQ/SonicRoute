# 🎧 SonicRoute v1.20 正式版

> **发布时间**：2026-10-02 · **核心主题**：自动化编排初登场与极客命令行联动 · [📥 资产下载](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.20) · [📖 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

---

## ✨ 核心更新亮点

- 🤖 **自动化编排引擎初登场**：引入可视化规则编排体系，支持 5 组 17 种操作（切换系统/应用端点、相对/绝对音量调节、明确静音、麦克风控制与外部脚本启动）。
- ⚡ **极客命令行客户端 `sr`**：引入本地命名管道 (IPC) 通信机制，在 PowerShell / CMD 中输入 `sr "规则名称"` 即可秒级触发运行实例。
- 📊 **实时电平音频活动指示**：快捷面板新增实时电平采样反馈，发声状态一目了然；主题内支持随时开启或关闭。
- 🪟 **四大多元分发形态确立**：正式形成 Lite x64 单文件、Lite ARM64 实验版、Legacy x64 (免额外运行库) 与 Microsoft Store 四大并行的分发矩阵。
- 🖥 **OSD 浮窗体验进化**：支持关闭音量条、数字左置、强调色与标题深浅适配，合并高频调音动画。

---

## 📋 详细演进说明

### 1. 自动化编排引擎与五组十七种操作
- **系统音频控制**：切换默认播放设备、切换默认录音设备、调整系统音量、相对调整系统音量、系统静音/解除静音；
- **应用定向分流**：切换指定应用播放设备、调整应用音量、相对调整应用音量、指定应用静音/解除静音；
- **麦克风控制**：全局麦克风硬件级静音、解除静音、反转静音状态；
- **外部联动**：启动指定外部程序、执行 PowerShell 脚本、延时等待；
- **编排体验**：支持规则复制、步骤复制、步骤拖动与序号预览。

### 2. 本地命名管道与短命令 `sr`
内置极简的 Windows 命名管道客户端，用户在终端只需键入：
```bash
sr "游戏开黑"
```
无需调出主窗口即可静默触发后台常驻的 SonicRoute 执行既定动作链，无缝衔接 Stream Deck、AutoHotkey 或批处理脚本。

### 3. 实时电平采样与面板改进
- 简洁面板引入非侵入式实时电平反馈，准确感知各应用是否正在推流发声；
- 适配 Windows 深色与浅色模式，自动排除被隐藏或已静音的应用；
- 优化面板滑入动画与默认高度（450px），展开/收拢更加稳固流畅。

---

## 🚀 资产下载矩阵

| 发行形态 | 推荐目标人群 | 下载链接 | 运行环境要求 |
|---|---|---|---|
| ⭐ **Lite x64**（推荐主力） | 主流 64 位 PC 绿色单文件 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.20/SonicRoute-v1.20-Lite-x64.exe) | 需匹配的 x64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| ⚡ **Lite ARM64**（实验） | Surface Pro / 骁龙 PC 原生高效 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.20/SonicRoute-v1.20-Lite-arm64.exe) | 需匹配的 ARM64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| 🛡️ **Legacy x64**（免装运行库） | 公司办公电脑 / 受限机型 | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.20/SonicRoute-v1.20-Legacy-x64.zip) | 系统自带 .NET Framework 4.8（解压即用） |
| 🔄 **Microsoft Store** | 商店静默后台自动更新 | [商店安装](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | 适用于 64 位 Windows 10 2004+ / Windows 11 |

> [!NOTE]
> - 支持 Windows 10 2004+ / Windows 11 系统。
> - Lite 版本请确保安装了对应架构的 .NET 8 桌面运行时；Legacy 版本解压后直接运行 `SonicRoute.exe` 即可。
> - 资产完整性 SHA-256 校验和请查看发布附件 `SHA256SUMS.txt`。

---

## 🔄 升级与迁移指引

- **覆盖升级**：升级前请先在任务栏右下角托盘图标右键选择「退出」，然后运行新版 EXE 或解压覆盖 Legacy 文件夹。
- **数据保留**：已有的应用分流设置与快捷键配置会自动迁移至新版本配置目录。
- **开机自启**：普通版与微软商店版各自使用独立自启项，互不干扰。

---

## 📚 相关链接

- [Wiki 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [命令行执行规则](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) · [常见问题解答](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)
- [提交建议或 Bug 反馈](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)
