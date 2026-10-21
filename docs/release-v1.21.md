# 🎧 SonicRoute v1.21 正式版

> **发布时间**：2026-10-09 · **核心主题**：事件驱动架构与 18 种自动化编排 · [📥 资产下载](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.21) · [📖 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

---

## ✨ 核心更新亮点

- ⚡ **零轮询事件驱动架构**：全面改用 Windows 前台应用切换与音频物理设备系统通知机制；关闭界面时彻底解绑动画与事件引用，实现极低系统闲时开销。
- 🤖 **自动化编排扩充至 18 种操作**：规则卡片直观显示触发条件与动作摘要；支持拖拽排序、搜索筛选、名称留空自动编号；新增「单步失败后停止」与「执行另一条规则」。
- 💻 **命令行 `sr` 深度联动**：支持一键复制规则执行命令并在 PowerShell / CMD 中秒级调用。
- 🎨 **全新设计语言与透明图标**：采用全新透明耳机轮廓托盘图标，随系统任务栏深浅自动适配；统一导航、卡片、Hex/RGB/HSL 色谱选色器与设备名重命名体验。
- 🛡️ **可选后台定时兜底**：实验设置中新增可选的后台定时巡检兜底开关，默认关闭，针对部分丢失 Windows 通知的老旧驱动提供可选保障。

---

## 📋 详细演进说明

### 1. 自动化规则体系全面成熟
- **可视化卡片与交互**：规则列表展示真实名称、操作动作与触发器摘要；支持按名称模糊搜索与类型筛选；拖拽即可自由调整展示排序；
- **容错与链路控制**：支持为每个步骤设置「失败后停止」，执行链路更加可控；执行结果明确标注 Success、Skipped 或 Stopped；
- **规则跨链调用**：新增「执行另一条规则」操作，支持将通用动作模块化复用。

### 2. 界面与主题美学重塑
- **统一视觉组件库**：重构导航栏、设置卡片、开关、数字输入框与下拉菜单，带来现代精细质感；
- **主题强调色系统**：内置色谱选色器，支持 Hex、RGB、HSL 自由输入与实时预览；
- **设备重命名**：支持为音频设备自定义命名，清空即可即时恢复系统默认物理名称。

### 3. 事件驱动与资源释放机制
- 界面关闭后自动解绑非必要的观察者监听，排队未处理的非关键界面刷新自动取消，并将空闲工作集移交操作系统调度；
- 状态更新完全交由系统通知触发，杜绝空闲时的无用周期轮询。

---

## 🚀 资产下载矩阵

| 发行形态 | 推荐目标人群 | 下载链接 | 运行环境要求 |
|---|---|---|---|
| ⭐ **Lite x64**（推荐主力） | 主流 64 位 PC 绿色单文件 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.21/SonicRoute-v1.21-Lite-x64.exe) | 需匹配的 x64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| ⚡ **Lite ARM64**（实验） | Surface Pro / 骁龙 PC 原生高效 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.21/SonicRoute-v1.21-Lite-arm64.exe) | 需匹配的 ARM64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| 🛡️ **Legacy x64**（免装运行库） | 公司办公电脑 / 受限机型 | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.21/SonicRoute-v1.21-Legacy-x64.zip) | 系统自带 .NET Framework 4.8（解压即用） |
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

- [Wiki 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [自动化规则指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [技术实现架构](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0)
- [提交建议或 Bug 反馈](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)
