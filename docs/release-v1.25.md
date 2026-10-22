# 🎧 SonicRoute v1.25 正式版

2026-10-22 · [下载正式版](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.25) · [使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

## 本次更新

- **.NET 10 主版本与 NET8 兼容版**：默认 Lite 使用 .NET 10，文件名保持不变；NET8 兼容版带 `-NET8` 后缀，Legacy 继续使用 .NET Framework 4.8。各版均需匹配自身架构与运行环境。
- **完整的便携版更新流程**：在设置的“软件更新”中检查、查看更新内容、确认下载与校验；下载完成后可立即重启安装或稍后处理。安装采用备份、重启确认与失败恢复机制。自动检查默认关闭，不会静默强制安装。
- **更新渠道严格隔离**：NET10、NET8、Legacy 以及 x64、ARM64 分别匹配更新资源；缺少匹配资源时回到官方下载页，不跨运行时或架构替换。Microsoft Store 版继续使用商店更新。
- **自动化与快捷键改进**：增加指定设备、默认录音设备和应用会话的音量/静音控制，全部应用路由重置及快速面板显示/隐藏等操作；完善动作名称与说明。检测和执行解耦，修复快照异常、定时旧回调及无关修改反复注册快捷键的问题，保留必要的 1 秒进程检测兜底。
- **面板与设置调整**：应用面板按钮布局和文字精简，快速面板高度上限调整为 850；设备名称下拉入口可在实验设置中按需开启，默认关闭。图标导入支持拖入单张 PNG/JPEG/ICO，复用原有导入流程。
- **WPF 资源与生命周期整理**：复用主题资源、绑定与按需菜单，关闭窗口后释放相关界面工作；保留原有滑块模板、触控与触控笔支持。不承诺未经确认的 CPU 或内存提升比例。

## 运行时与维护说明

- 默认 Lite 需对应架构的 **.NET 10 Desktop Runtime**；NET8 兼容版需 **.NET 8 Desktop Runtime**，不是仅安装通用 .NET Runtime。
- **2026 年 11 月之后停止提供和维护 NET8 版**。x64 用户可在官方下载页手动选择 .NET 10 或 Legacy 4.8；ARM64 用户选择 .NET 10 ARM64，不会自动跨运行时迁移。
- Microsoft Store 的在架版本以商店页面为准。本次准备的 NET10 商店包与 Lite x64 复用同一框架依赖 EXE，需 x64 .NET 10 Desktop Runtime；MSIX 不内置或自动安装该运行时。本次 GitHub Release 不包含 MSIX，也不代表商店已同步上线。
- ARM64 保持实验性支持。

## 🚀 资产下载矩阵

| 发行形态 | 推荐目标人群 | 下载链接 | 运行环境要求 |
|---|---|---|---|
| ⭐ **Lite x64**（.NET 10 推荐主力） | 主流 64 位 PC 绿色单文件 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.25/SonicRoute-v1.25-Lite-x64.exe) | 需匹配的 x64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| ⚡ **Lite ARM64**（.NET 10 实验） | Surface Pro / 骁龙 PC 原生高效 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.25/SonicRoute-v1.25-Lite-arm64.exe) | 需匹配的 ARM64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| ☕ **Lite NET8 x64**（.NET 8 兼容版） | 现有仅安装 .NET 8 的机型 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.25/SonicRoute-v1.25-Lite-NET8-x64.exe) | 需匹配的 x64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| ☕ **Lite NET8 ARM64**（.NET 8 兼容版）| 现有仅安装 .NET 8 的 ARM64 机型 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.25/SonicRoute-v1.25-Lite-NET8-arm64.exe) | 需匹配的 ARM64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| 🛡️ **Legacy x64**（免装运行库） | 公司办公电脑 / 受限机型 | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.25/SonicRoute-v1.25-Legacy-x64.zip) | 系统自带 .NET Framework 4.8（解压即用） |
| 🔄 **Microsoft Store** | 商店静默后台自动更新 | [商店安装](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | 适用于 64 位 Windows 10 2004+ / Windows 11 |

> [!NOTE]
> - 支持 Windows 10 2004+ / Windows 11 系统。
> - Lite 版本请确保安装了对应架构的 .NET 10（或 .NET 8）桌面运行时；Legacy 版本解压后直接运行 `SonicRoute.exe` 即可。
> - 资产完整性 SHA-256 校验和请查看发布附件 `SHA256SUMS.txt`。

---

## 升级与数据保留

- **从 1.24 首次升级**：1.24 的检查更新仅打开官方下载页，请先从托盘菜单退出旧版，再手动运行新版 Lite EXE，或把 Legacy ZIP 解压到原程序目录。1.25 起提供完整的便携版更新流程。
- 用户配置、自动化规则、主题和图标仍使用原有数据目录；更新包不包含这些用户文件。重要配置可先通过软件的备份功能保存。
- 不需要额外安装运行库时可选择 Legacy x64（需系统具有 .NET Framework 4.8）。保持原有手动下载、替换和运行方式可用。
- Release 附带 `SonicRoute-update.json` 和 `SHA256SUMS.txt`。SHA-256 用于文件完整性校验，不等同于独立发布者签名。

## 相关链接

[使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [自动化规则](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [问题反馈](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)
