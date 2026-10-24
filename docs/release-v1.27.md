# 🎧 SonicRoute v1.27 正式版

> **发布时间**：2026-10-24 · **核心主题**：音频会话事件通知、快速面板统一与设置体验改进 · [📥 资产下载](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.27)

## ✨ 更新内容

### 音频监听与恢复

- **实时电平按会话通知发现和恢复**：监听会话创建、状态变化及设备通知，替代正常状态下每 2 秒完整重建会话列表。非活动会话电平归零后等待通知；活动会话即使瞬时为零仍保持采样，避免漏掉恢复的声音。通知注册或读取失败时保留必要的恢复检查。
- **复用短时缺失结果**：只有完整成功的扫描才缓存“目标应用没有会话”的结果，缓存有效期 200ms；状态变化会使缓存失效。失败或不完整扫描不会被当作可靠的无会话状态。极端时序下，新会话的发现可能增加约 200ms 等待。
- **麦克风故障恢复减少重复创建**：注册失败时只补齐缺失的通知，复用有效音频对象；真正读取或设备故障仍走原有恢复流程。
- **自动化保留 1 秒兜底**：进程通知异步初始化，订阅失败按 2、4、8 秒递增、最多 30 秒重试；这只调整订阅恢复，不放慢应用启动/退出的 1 秒快照检查。旧监听源及时清理，无相关规则时不保留专用监听。

### 快速面板与快捷键

- **统一为快速面板**：彻底移除旧的独立应用面板窗口、模式选择和专用代码；主窗口的“应用”页面继续保留。面板支持 350–850px 高度、位置保存和实时电平，旧配置可继续加载。
- **修复静音快捷键目标**：快速面板打开时，“当前应用静音”仍作用于应用播放会话，不再误静音顶部选中的系统输出设备；顶部设备静音按钮保持原有作用范围。
- **消除麦克风快捷键重复提示**：复用全局服务与面板读回，只显示一次 OSD。快速面板顶部直接调节设备音量不再弹出 OSD，快捷键等入口仍正常提示。
- **减少面板重复工作**：复用设备名称列表、行和既有刷新机制，清理旧双面板判断与重复初始化路径。全局快捷键动作跳过无关的当前应用探测，并修正右 Win 键配合鼠标的识别。

### 设置与外观

- **设备卡片优先显示自定义名称**：未命名时保留原来的标题，下方原始设备说明、提示和真实设备标识不变；改名沿用原有保存流程并同步现有标题。
- **接入统一滚动条**：使用圆角细滑块，悬停/拖动反馈与主题强调色；复用 WPF 原生滚动、拖动、分页及滚轮交互，支持浅色、深色与系统高对比度资源，不新增后台轮询或持续动画。
- **设备设置行按需复用**：全选/取消全选合并为一次保存，重复刷新按设备 ID 复用现有行，跳过已经完成的同状态展开/收起操作，保留离线设备和编辑状态。
- **麦克风选项保持默认开启**：新配置及缺省字段为开启，用户已经明确关闭的设置继续保留；这不强制开启“在快速面板中显示麦克风”。
- 九种语言同步清理过时面板文案，中文统一使用“快速面板”，高度提示与实际 850px 上限一致。

## 📋 升级与验证说明

- v1.25 / v1.26 便携版可在“设置 → 软件更新”检查、下载并确认升级；自动检查仍默认关闭，只匹配当前发行类型、运行时与架构。Microsoft Store 版继续由商店更新。
- v1.24 及更早版本请退出旧程序后手动更新；Legacy 必须完整解压 ZIP。用户设置、规则、主题及自定义图标仍使用原有数据目录，不随程序替换。
- 本轮已对 NET10、NET8、Legacy 进行音频通知、自动化/快捷键、持久化与面板定向检查。设置行的局部 A/B 显示重复同步和保存减少，但首次创建约增加 1–2ms；这不是整版 CPU/内存基准，不承诺零 CPU 或所有场景加速。
- 原生 ARM64、Windows 10 真机、物理多 DPI/触控及 Store 安装后的运行仍需环境验收。社区反馈的部分全屏游戏快捷键问题尚未复现，不宣称全部解决。

---

## 🚀 资产下载矩阵

| 发行形态 | 推荐目标人群 | 下载链接 | 运行环境要求 |
|---|---|---|---|
| ⭐ **Lite x64**（.NET 10 推荐主力） | 主流 64 位 PC 绿色单文件 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.27/SonicRoute-v1.27-Lite-x64.exe) | 需匹配的 x64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| ⚡ **Lite ARM64**（.NET 10 实验版） | Surface Pro / 骁龙 PC 原生高效 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.27/SonicRoute-v1.27-Lite-arm64.exe) | 需匹配的 ARM64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| ☕ **Lite NET8 x64**（.NET 8 兼容版） | 现有仅安装了 .NET 8 的机型 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.27/SonicRoute-v1.27-Lite-NET8-x64.exe) | 需匹配的 x64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| ☕ **Lite NET8 ARM64**（.NET 8 兼容版） | 现有仅安装了 .NET 8 的 ARM64 机型 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.27/SonicRoute-v1.27-Lite-NET8-arm64.exe) | 需匹配的 ARM64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| 🛡️ **Legacy x64**（免装运行库） | 公司办公电脑 / 受限机型 | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.27/SonicRoute-v1.27-Legacy-x64.zip) | 系统自带 .NET Framework 4.8（解压即用） |
| 🔄 **Microsoft Store** | 由商店提供更新，在架版本以商店页面为准 | [商店安装](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | x64 .NET 10 Desktop Runtime；MSIX 不内置运行时 |

> [!NOTE]
> - 支持 Windows 10 2004+ (Build 19041+) 及 Windows 11 系统。
> - .NET 10 默认文件名无附加后缀，.NET 8 兼容版带有 `-NET8` 后缀。依照规划，**2026 年 11 月之后将停止提供 .NET 8 新构建**，x64 用户可手动选择 .NET 10 或 Legacy，ARM64 用户请选择 .NET 10，不进行跨运行时自动替换。
> - 发布附件提供 `SHA256SUMS.txt` 校验清单与升级清单 `SonicRoute-update.json`（协议版本 2）。

---

## 🔄 升级与数据保留指引

- **v1.25 / v1.26 便携版用户**：
  打开 SonicRoute「主窗口 → 设置 → 软件更新」，点击「检查更新」，系统将自动匹配当前架构与运行时，下载并完成校验后点击「立即重启升级」，全程自动备份并平滑过渡。
- **v1.24 及更早版本用户（手动升级）**：
  请先在右下角系统托盘右键退出当前运行的旧版 SonicRoute，然后下载对应的新版 EXE（或将 Legacy ZIP 解压覆盖到原程序目录），双击启动即可。
- **保留独立存储的用户数据**：
  软件的所有用户设置、37 种自动化连招规则、全局快捷键绑定及自定义图标均独立保存在 `%LocalAppData%\SonicRoute\`，更新器不替换这些用户数据；建议在手动更新前备份该目录。

---

## 📚 相关链接

- [Wiki 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [自动化规则配置手册](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [常见问题排障](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)
- [📚 完整版本历史](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) · [🐛 提交建议与问题反馈](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose) · [💖 在爱发电支持我们](https://afdian.com/a/koukou021)
