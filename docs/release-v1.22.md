# 🎧 SonicRoute v1.22 正式版

> **发布时间**：2026-10-11 · **核心主题**：开机自启隔离与轻量化加载 · [📥 资产下载](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.22) · [📖 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

---

## ✨ 核心更新亮点

- 🔄 **双版本自启彻底隔离**：普通版与商店版分别注册独立自启项，消除窗口激活冲突与系统状态同步差异。
- 📦 **精简依赖与体积优化**：重构商店自启动接口，Lite 单文件程序体积进一步精简缩小。
- ⚡ **自动化页面按需加载**：自动化规则界面采用延迟初始化，首次进入时构建，后续切页无缝复用。
- 🎨 **菜单视觉规范统一**：托盘、自动化与音量预设菜单采用统一样式，移除默认灰色边缘与图标留白。
- 🛠 **短命令复制健壮性提升**：修复“复制执行命令”偶发卡顿，增加后台重试机制与失败友好提示。
- 🌐 **多语言用词微调**：修正 9 种内置语言的用词、语法与标点，使跨语言表述更加自然地道。

---

## 📋 详细演进说明

### 1. 商店版与普通版自启隔离
- 微软商店版依托 Windows `StartupTask` 规范管理后台自启；
- 普通版（Lite 与 Legacy）依托注册表当前用户 Run 项进行自启管理；
- 本次更新将两套逻辑完全解耦，彻底修复此前两版本在同一机器切换时窗口激活混淆与状态判断失效的问题。

### 2. 界面加载与内存控制
- 自动化规则管理页面改用按需加载机制，在用户首次点击进入时才实例化重量级组件，降低常驻启动开销；
- 统一样式菜单，全面移除 Windows 原生 ContextMenu 遗留的多余灰色内边距与图标占位槽，界面更加精致紧凑。

### 3. 命令行交互稳定性
- 针对极端情况下剪贴板占用引发的“复制执行命令”短暂停顿，增加异步后台保护与异常回退提示，杜绝主界面线程受阻塞。

---

## 🚀 资产下载矩阵

| 发行形态 | 推荐目标人群 | 下载链接 | 运行环境要求 |
|---|---|---|---|
| ⭐ **Lite x64**（推荐主力） | 主流 64 位 PC 绿色单文件 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.22/SonicRoute-v1.22-Lite-x64.exe) | 需匹配的 x64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| ⚡ **Lite ARM64**（实验） | Surface Pro / 骁龙 PC 原生高效 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.22/SonicRoute-v1.22-Lite-arm64.exe) | 需匹配的 ARM64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| 🛡️ **Legacy x64**（免装运行库） | 公司办公电脑 / 受限机型 | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.22/SonicRoute-v1.22-Legacy-x64.zip) | 系统自带 .NET Framework 4.8（解压即用） |
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

- [Wiki 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [命令行执行规则](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) · [常见问题解答](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)
- [提交建议或 Bug 反馈](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)
