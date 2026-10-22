# 🎧 SonicRoute v1.25 正式版

> **发布时间**：2026-10-22 · **核心主题**：拥抱 .NET 10、便携版一键安全更新与 37 种自动化连招 · [📥 资产下载](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.25) · [📖 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

---

## ✨ 核心更新亮点

- ⚡ **率先跃升 .NET 10 现代运行时**：主力 Lite 版本升级至微软最新的 .NET 10 平台，带来更强的底层执行效率；同时贴心提供带 `-NET8` 后缀的兼容版及免装环境的 Legacy 版（.NET 4.8），平滑全兼容。
- 🔄 **单文件便携版实现安全一键更新**：Lite 单文件版终于支持在线更新！在设置中一键检查、下载校验、自动备份并平滑重启升级，自带失败自动回滚保障，告别以往手动下包解压覆盖。
- 🎮 **自动化引擎大扩充（37 种操作）**：动作库从 20 项扩展至 37 项！新增指定设备音量/静音、默认麦克风切换、所有会话批量静音、全应用双向路由重置及快速面板显隐等 17 项强大控制能力。
- 🖼️ **应用图标直接拖拽导入**：在应用详情页支持直接将单张 PNG、JPEG 或 ICO 文件拖入窗口，瞬间为无图标或系统通用图标的程序完成个性化装扮。
- 🎛️ **面板体验与自适应拓展**：快速面板高度上限拓宽至 850px，更适合容纳超长应用列表；应用面板按钮布局紧凑重排；实验设置新增设备全称显示开关。
- 🍃 **底层事件解耦与极致轻量**：优化自动化调度与快照机制，杜绝重复触发；整理 WPF 资源与控件生命周期，界面关闭后彻底释放无用监听，待机闲时保持近乎 0% CPU 占用。

---

## 📋 详细演进说明

### 1. 跃升 .NET 10 时代，兼顾全环境平滑过渡
为了获得更现代的系统级优化与内存调度特性，SonicRoute 主力版本率先迁移至 **.NET 10 Desktop Runtime**。
- **Lite x64 / ARM64（推荐主力）**：基于最新的 .NET 10 构建，适合绝大多数 Windows 10/11 电脑及高通骁龙 / Surface Pro 设备；
- **Lite NET8 x64 / ARM64（兼容版）**：为当前仅安装了 .NET 8 运行时的设备保留，文件名带 `-NET8` 后缀，将持续维护至 2026 年 11 月；
- **Legacy x64（零依赖版）**：依赖系统内置的 .NET Framework 4.8，解压即用，适合受限办公机；
- **Microsoft Store（商店版）**：由微软应用商店负责后台静默自动更新。

### 2. 便携版（Lite）告别繁琐，支持全自动安全更新
以往单文件版用户每次更新都需要打开网页手动下载、关闭旧进程、再替换文件。从 v1.25 开始，便携版拥有了完整的原生更新工作流：
- **渠道严格隔离**：检查更新时会严格根据当前架构（x64 / ARM64）与运行时（.NET 10 / .NET 8 / Legacy）智能匹配对应的补丁包，绝不发生跨环境错升；
- **原子替换与灾备回滚**：更新包完成 SHA-256 校验后，升级代理会自动对原 EXE 进行备份；替换过程中若发生异常或被占用，会自动将原程序完整还原，确保数据零损坏；
- **克制无感**：自动更新检查默认保持关闭，绝不会在后台静默偷跑下载或强制弹窗打扰，把掌控权完全交给用户。

> [!TIP]
> **关于从 v1.24 升级**：由于自动更新机制始于 v1.25，已有 v1.24 版本的用户请先按以往方式手动退出旧版并运行新版 EXE；自 v1.25 起即可在软件内一键无感升级。

### 3. 自动化动作扩容至 37 项，玩法自由组合
本次更新将自动化操作库扩容了整整 17 项（编号 20~36），覆盖了更多发烧友与高级玩家的日常场景：
- **精准硬件设备调控**：支持直接指定某台播放设备设定音量、增减步进或反转静音；支持动态将指定麦克风设为系统默认录音设备；
- **会话级批量静音**：一键对所有正在发声的应用执行静音或音量重置；
- **全应用双向路由一键还原**：基于 Windows 原生机制安全重置所有应用的声音分流，完全不破坏系统混音器配置；
- **快速面板程序化呼出**：支持通过快捷键或外部脚本联动将快速面板显示或关闭，完美配合 Stream Deck 等外接控制台。

### 4. 界面交互与细节体验提升
- **图标拖拽导入**：无需在文件选择对话框中反复翻找路径，直接把桌面或文件夹里的图标文件（PNG/JPEG/ICO）拖入应用页面即可应用；
- **面板自适应扩容**：快捷面板可调高度由原本的 350~800px 提升为 **350~850px**，在高分屏或后台音频软件较多时能一屏呈现更多内容；
- **实验性设备名称显示**：在实验设置中可勾选开启“现代面板显示输出设备名称”，方便直观确认各应用当前绑定的目标设备。

---

## 🚀 资产下载矩阵

| 发行形态 | 推荐目标人群 | 下载链接 | 运行环境要求 |
|---|---|---|---|
| ⭐ **Lite x64**（.NET 10 推荐主力） | 主流 64 位 PC 绿色单文件 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.25/SonicRoute-v1.25-Lite-x64.exe) | 需匹配的 x64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| ⚡ **Lite ARM64**（.NET 10 实验） | Surface Pro / 骁龙 PC 原生高效 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.25/SonicRoute-v1.25-Lite-arm64.exe) | 需匹配的 ARM64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| ☕ **Lite NET8 x64**（.NET 8 兼容版） | 现有仅安装了 .NET 8 的机型 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.25/SonicRoute-v1.25-Lite-NET8-x64.exe) | 需匹配的 x64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| ☕ **Lite NET8 ARM64**（.NET 8 兼容版） | 现有仅安装了 .NET 8 的 ARM64 机型 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.25/SonicRoute-v1.25-Lite-NET8-arm64.exe) | 需匹配的 ARM64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| 🛡️ **Legacy x64**（免装运行库） | 公司办公电脑 / 受限机型 | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.25/SonicRoute-v1.25-Legacy-x64.zip) | 系统自带 .NET Framework 4.8（解压即用） |
| 🔄 **Microsoft Store** | 商店静默后台自动更新 | [商店安装](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | 适用于 64 位 Windows 10 2004+ / Windows 11 |

> [!NOTE]
> - 支持 Windows 10 2004+ / Windows 11 系统。
> - Lite 版本请确保安装了对应架构的 .NET 10（或 .NET 8）桌面运行时；Legacy 版本解压后直接运行 `SonicRoute.exe` 即可。
> - 资产完整性 SHA-256 校验和请查看发布附件 `SHA256SUMS.txt`。

---

## 🔄 升级与数据保留指引

- **从 v1.24 首次升级**：请先在任务栏右下角托盘图标右键选择「退出」，然后运行新版 EXE 或把 Legacy ZIP 解压覆盖到原程序目录。
- **数据完全保留**：您现有的应用分流规则、全局快捷键、自动化流水线及自定义图标均保存在 `%LocalAppData%\SonicRoute\`，新版本启动后会自动无损继承。
- **后续更新**：升级至 v1.25 后，便携版用户可直接在「设置 → 软件更新」中一键获取后续新版。

---

## 📚 相关链接

- [Wiki 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [自动化规则配置手册](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [常见问题排障](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)
- [提交建议或 Bug 反馈](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)
