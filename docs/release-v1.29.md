# 🎧 SonicRoute v1.29 正式版

> **发布时间**：2026-10-27 · **核心主题**：全部输出设备音量面板、按需加载与 Shift 滚轮系统音量 · [📥 资产下载](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.29) · [📖 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

---

## ✨ 核心更新亮点

- 🎚️ **展开控制全部输出设备**：快速面板顶部新增展开按钮，在额外面板中分别调整输出设备的主音量与静音，显示设备名称、默认标记和音量百分比。
- 📐 **自动选择展开位置与高度**：保留主面板原位置；额外面板优先向上展开，顶部空间不足时选择合适侧边。高度随设备数量变化，最高与主面板一致，空间不足时滚动。
- 💤 **按需加载和事件刷新**：收起时释放额外控件与监听；展开先显示已有设备列表，再异步确认状态。连续音量通知合并，只读取变化设备，快速收展与筛选不会被旧结果覆盖。
- 🖱️ **Shift＋滚轮调系统音量**：在原托盘响应区域按住 Shift 滚动，直接调节系统默认播放设备音量，并复用已有 OSD 反馈。

---

## 🛠️ 详细更新

### 1. 全部输出设备音量面板

- 点击快速面板设备选择器右侧的箭头展开或收起，记住用户选择，下次打开沿用；默认保持收起。
- 每个真实输出设备有独立的音量、静音、百分比和音量档位菜单；滚轮沿用现有音量步长。主面板原有应用音量列表保持原用法。
- 额外面板自动选择可用空间：通常向上；主面板位于左上角时放右侧、右上角时放左侧。展开和收起不改变主面板保存的位置与基础高度。
- 高度按实际显示设备数量计算，上限为当前主面板高度，同时受屏幕工作区限制；设备较少时缩短，较多时滚动，没有设备时显示紧凑提示。
- 展开箭头使用随明暗主题切换的背景。额外面板由该箭头统一收起，不增加重复关闭按钮。
- 在“快速面板更改系统默认设备”下方新增独立开关 **“额外面板只显示保留设备”**。开启后仅显示原“保留的设备”中保留的输出设备，默认关闭；该筛选不禁用主面板自己的设备音量控制。
- “快速面板更改系统默认设备”继续默认开启，保留用户明确保存的关闭选择。

### 2. 加载、同步与释放

- 额外设备行在展开时才创建，先复用主面板设备快照完成初始显示，再异步确认最新设备和音量，减少先显示空白小面板再扩高的过程。
- 输出端点分别监听变化；健康状态下，连续通知合并后只读取发生变化的设备。通知回调不执行音频读取或释放工作。
- 收起和关窗取消额外面板排队的刷新及读取等待，释放控件、端点与设备通知；重新展开使用新的加载范围，旧结果不能覆盖新界面。
- 修复读取过程中快速切换设备筛选后，新行可能缺少初始状态的问题；新建行主动补读，保留旧读回的版本保护。
- 拓扑变化与失败恢复继续核对完整状态。只有额外面板可见且通知注册或读取失败时才启用原恢复检查，成功或收起后停止；健康状态不新增后台轮询。
- 保留原音量合并写入队列和关窗 1.5 秒工作集缩减。

### 3. 托盘滚轮

| 操作 | 行为 |
|---|---|
| 普通滚轮 | 调节当前应用播放音量 |
| Shift＋滚轮 | 调节系统默认播放设备音量 |
| Alt＋滚轮 | 切换当前应用输出设备 |
| Alt＋Shift＋滚轮 | 切换系统默认播放设备 |

- 四种操作共用原托盘命中范围、暂停开关、步长、合并派发和快捷键优先级。Shift 系统音量复用现有快捷键音量处理与 OSD，不新增设置或轮询。
- 托盘命中复用已解析的反射元数据，修正原生返回值声明，删除不安全的 Toolbar 回退；保留 30 秒失败恢复并限定为同一图标身份，减少错误命中风险。

### 4. JSON、语言与电平路径

- NET10 执行历史写入采用紧凑的生成序列化合同；语言读写集中复用已有模块和写入选项，保持现有文件格式与按需初始化。
- 合并电平工作状态并减少字典查找，保留原采样节奏、平滑、量化、回零和通知恢复机制。
- 未保留缺少稳定收益的锁替换与读取试验。本版不宣称整版 CPU、内存或窗口打开耗时下降百分比。

---

## 📋 升级与验证说明

- 额外面板的最终定向检查覆盖 NET10、NET8 和 Framework x64：各 14 项观察器/恢复检查与 41 项真实产品私有桌面检查，验证加载取消、快速收展、筛选反转、订阅释放、尺寸上限和主音量可用性。之前的九语、深浅主题及托盘检查记录继续保留。
- 模拟 8 台设备时，同一台 100 次通知合并后只读取该台设备；这是定向读取次数检查，不等于整版 CPU 降幅。
- 真实音频检查仅执行读取，没有写入用户音量、静音或默认设备。物理鼠标、真实热插拔/驱动重启、混合 DPI 跨屏、Windows 10 真机、原生 ARM64 和 Store 安装后行为仍需对应环境实测。
- 支持独立更新器的便携版可在“设置 → 软件更新”检查、下载、校验并确认重启升级；自动检查仍默认关闭。更新匹配发行形态、运行时和架构，不自动跨运行时迁移。

---

## 🚀 资产下载矩阵

| 发行形态 | 下载 | 运行环境要求 |
|---|---|---|
| ⭐ **Lite x64**（默认主版本） | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-x64.exe) | x64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| ⚡ **Lite ARM64**（实验性） | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-arm64.exe) | ARM64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| ☕ **Lite NET8 x64**（兼容版） | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) | x64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| ☕ **Lite NET8 ARM64**（兼容版） | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | ARM64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| 🛡️ **Legacy x64** | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Legacy-x64.zip) | 系统 .NET Framework 4.8；完整解压后运行 |
| 🔄 **Microsoft Store** | [商店安装](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | x64 .NET 10 Desktop Runtime；在架版本以商店页面为准 |

> [!NOTE]
> - 支持 Windows 10 2004+（Build 19041+）与 Windows 11。
> - Lite 与 Store MSIX 为框架依赖部署，不内置 .NET Desktop Runtime。
> - 2026 年 11 月之后停止提供和维护 NET8 版。此后 x64 用户可手动选择 NET10 或 Legacy，ARM64 用户选择 NET10。
> - 七项 Release 附件还包含 [SHA256SUMS.txt](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SHA256SUMS.txt) 与 [SonicRoute-update.json](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-update.json)（Protocol 2）。SHA-256 校验完整性，不等于独立发布者签名。MSIX 单独构建，不包含在七项附件中；商店在架版本独立维护。

---

## 🔄 升级与数据保留

1. 使用软件内更新，或下载当前发行形态、运行时与架构对应的文件。v1.24 及更早版本请手动更新。
2. 手动升级前从托盘正常退出 SonicRoute，再替换 EXE 或完整解压 Legacy ZIP；不同发行形态的文件分别存放。
3. 更新器不替换原数据目录中的设置、自动化规则、主题及自定义图标。手动升级前可备份 `%LocalAppData%\SonicRoute\`；Store 版继续使用商店更新机制。

## 📚 相关链接

- [Wiki 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [快捷键](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) · [自动化规则](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)
- [完整版本历史](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) · [反馈问题](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose) · [支持作者](https://afdian.com/a/koukou021)
