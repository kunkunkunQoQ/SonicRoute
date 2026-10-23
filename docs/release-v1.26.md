# 🎧 SonicRoute v1.26 正式版

> **发布时间**：2026-10-23 · **核心主题**：高权限应用图标修复、按需音频读取与状态可靠性 · [📥 资产下载](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.26) · [📖 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

---

## ✨ 核心更新亮点

- 🖼️ **修复部分高权限应用图标缺失**：普通权限下无法读取进程路径时，复用音频会话提供的图标路径，并保留窗口图标等回退方式，改善网易 UU 远程等应用只显示文字占位的问题。
- 🎚️ **减少重复音频读取**：同一轮查询复用设备和策略对象，合并会话音量与静音读取；快速面板没有可见应用行时，跳过只为这些行执行的额外查询。
- ✅ **静音与默认设备控制更准确**：应用拥有多个播放会话时正确合并静音状态，操作部分失败时如实反馈；修正默认设备角色的区分，避免混淆播放/录音方向与 Console、Multimedia、Communications 角色。
- ⚙️ **分别优化运行时常用路径**：.NET 10 配置与规则使用预生成的 JSON 元数据；Legacy 复用序列化元数据及已确认的程序路径，保持原有配置格式和迁移能力。
- 🍃 **按需加载与关闭清理**：图标元数据随界面需求读取，保留缓存复用；修复配置/规则监听器退出时的锁等待问题，关闭面板后停止其专用刷新工作。

## 📋 使用与兼容性

- 保留现有 37 种自动化操作、步骤顺序、失败策略及配置格式；应用启动/退出检测继续保留 **1 秒兜底**，没有新增常驻更新轮询。
- 更新检查默认关闭。v1.25 便携版可在「设置 → 软件更新」中检查、下载并确认重启升级；只匹配当前发行类型、运行时和架构。Microsoft Store 版继续由商店更新。
- v1.24 及更早版本仍需退出旧程序后手动运行新版；Legacy 请解压完整 ZIP，保留所有随附文件。
- 设置、自动化规则和自定义图标继续使用 `%LocalAppData%\SonicRoute\`。手动更新前可备份该目录；不要删除用户数据。

---

## 🚀 资产下载矩阵

| 发行形态 | 下载 | 运行环境 |
|---|---|---|
| ⭐ **Lite x64**（.NET 10 默认版） | [单文件 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Lite-x64.exe) | x64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| **Lite ARM64**（.NET 10 实验版） | [单文件 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Lite-arm64.exe) | ARM64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| **Lite NET8 x64**（兼容版） | [单文件 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Lite-NET8-x64.exe) | x64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| **Lite NET8 ARM64**（实验兼容版） | [单文件 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Lite-NET8-arm64.exe) | ARM64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| **Legacy x64** | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Legacy-x64.zip) | .NET Framework 4.8；解压后运行 `SonicRoute.exe` |
| **Microsoft Store** | [商店页面](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | x64 .NET 10 Desktop Runtime；商店版本以页面实际审核上线为准 |

> 支持 Windows 10 2004+ / Windows 11。.NET 10 默认文件名没有 `NET10` 后缀，.NET 8 兼容版带 `NET8` 后缀。**2026 年 11 月之后不再提供和维护 .NET 8 新版本**；x64 用户可手动选择 .NET 10 或 Legacy，ARM64 用户请选择 .NET 10，不进行跨运行时自动替换。
>
> Lite 与 Store 版均为框架依赖部署，需先安装对应的 Desktop Runtime；不假定商店会自动安装 .NET 10。ARM64 保持实验性支持。

## 🔒 校验与升级

- 发布附件包含 `SonicRoute-update.json`（Protocol 2）和 `SHA256SUMS.txt`，用于匹配发行形态并验证文件完整性。
- 更新器保留下载取消、安装备份、失败恢复及启动确认机制。SHA-256 是完整性校验，不等于独立的发布者数字签名；请始终从本仓库正式 Release 下载。
- 需要手动升级时，先通过托盘菜单退出 SonicRoute，再替换对应版本文件。不要在运行中直接覆盖 EXE，也不要用其他运行时或架构的文件混合替换。

---

[📚 完整版本历史](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) · [🐛 问题反馈](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)
