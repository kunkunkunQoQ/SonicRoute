# 🎧 SonicRoute v1.28 正式版

> **发布时间**：2026-10-26 · **核心主题**：应用卡片、清晰的状态标记与托盘设备切换 · [📥 资产下载](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.28) · [📖 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

---

## ✨ 核心更新亮点

- 🪟 **应用页面重新整理**：应用信息、音频路由、音量和快捷控制分组展示；名称旁点击铅笔即可原位改名，导入图标与恢复默认放在右上角。
- 🎯 **状态标记各有含义**：列表右侧反色点表示播放静音，正常点表示音频会话活动；不在快速面板显示的应用，在图标左上角单独标记。
- 🖱️ **托盘滚轮切设备**：Alt＋滚轮切换当前应用的输出设备；Alt＋Shift＋滚轮切换系统默认播放设备。普通滚轮继续调节当前应用播放音量，已有设备切换快捷键保留。
- 📦 **发布与升级流程完善**：正式说明优先使用人工核对的 UTF-8 文本；加强候选文件、运行时隔离、下载回验与更新助手恢复检查。

---

## 🛠️ 详细更新

### 1. 应用卡片与操作

- **原位编辑应用名称**：名称旁的铅笔会在原位置展开输入框，自动选中当前名称。输入沿用原有自动保存；回车或移开焦点后收起，留空恢复默认名称。仅打开编辑器不会把默认名称额外保存成自定义名称。
- **输入与输出共用路由卡片**：“音频路由”标题、输出设备和输入设备放在同一张卡片内。关闭麦克风选项后隐藏输入区，输出选择器铺满整行。
- **快捷控制更紧凑**：静音、禁用自动切换、在快速面板显示各自保留开关，开关靠右居中；“在快速面板显示”采用开启即显示的正向含义，旧配置保持兼容。
- **减少重复说明**：去掉路由、音量和快捷控制卡片的冗余辅助文字。名称编辑的留空与保存提示保留在悬停提示中；失败反馈复用原有提示方式。
- **避免旧结果串到新应用**：切换选中应用或关闭窗口后，延迟返回的音量、路由及静音结果不会更新错误的目标；未选中应用时清空并禁用对应操作。

### 2. 列表状态标记

- **右侧反色点：播放静音**。静音与活动状态同时存在时，静音标记优先。
- **右侧正常点：音频会话活动**。未静音且存在 Active 会话时显示；它表示会话状态，不等同于实时电平、实际可听声音或没有其他静音因素。
- **图标左上角反色点：不在快速面板显示**。这个标记独立于播放静音，可以同时显示。
- 静音读取复用现有会话缓存，在进入应用页、手动刷新和原有操作读回时更新；无需先逐一选中应用。不增加为这些标记服务的定时轮询，外部音量合成器改动按原有刷新时机读回。

### 3. 托盘与默认设置

- **普通滚轮**调节当前应用播放音量；**Alt＋滚轮**在当前应用的可用输出设备间切换；**Alt＋Shift＋滚轮**切换系统默认播放设备。上滚选择上一个，下滚选择下一个。
- 这些操作共用原有托盘命中区域、设备筛选与切换实现。暂停托盘滚轮时一并暂停；已匹配全局滚轮快捷键时，保留快捷键优先级。
- **新配置默认仅在 SonicRoute 托盘图标上响应滚轮**。“托盘区域滚轮”开启后扩展到托盘通知区域，说明文字同步简化。
- **新配置默认开启快速面板更改系统默认设备**。这两个默认值均保留用户已经保存的选择，不强制覆盖旧偏好。
- 已撤回上一／下一设备独立快捷键的试验，现有设备切换动作及绑定继续使用原来的方式；NET10 Lite 继续采用单文件 EXE。

### 4. 发布维护

- 修正稳定版日志基线选择，排除草稿与预发布，不因 GitHub 列表顺序或前几条结果漏掉更新记录；历史读取失败会明确停止。
- 改进发布后 Latest 指针延迟的重试与审计时间记录；发布前继续核对源码收据、五个程序包、更新清单与哈希，并从远端下载回验。
- 更新 README、AI 代码索引与 BenchmarkLab 测量工具。测试工具改进不作为应用本身的性能收益承诺；本版不宣称整版 CPU／内存加速。

---

## 📋 升级与验证说明

- 支持独立更新器的便携版可在“设置 → 软件更新”检查、下载、校验并确认重启升级；自动检查仍默认关闭。更新严格匹配 Lite／Legacy、NET10／NET8／NET48 及 x64／ARM64，不自动跨运行时迁移。
- v1.24 及更早版本请正常退出后手动替换。Legacy 必须完整解压；设置、自动化规则、主题及自定义图标继续使用原有数据目录。
- 应用页定向验证覆盖九种语言、浅深主题、不同窗口宽度、改名保存、标记优先级、只读缓存回读与关闭清理。发行流程另行校验五包七资产、运行时隔离及更新助手失败恢复。
- 离屏界面与隔离夹具检查不等于真实音频写入、物理输入或完整性能基准。原生 ARM64、Windows 10 真机、多显示器 DPI、真实中文输入法及 Store 安装后行为仍需对应环境实测。

---

## 🚀 资产下载矩阵

| 发行形态 | 下载 | 运行环境要求 |
|---|---|---|
| ⭐ **Lite x64**（默认主版本） | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.28/SonicRoute-v1.28-Lite-x64.exe) | x64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| ⚡ **Lite ARM64**（实验性） | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.28/SonicRoute-v1.28-Lite-arm64.exe) | ARM64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| ☕ **Lite NET8 x64**（兼容版） | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.28/SonicRoute-v1.28-Lite-NET8-x64.exe) | x64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| ☕ **Lite NET8 ARM64**（兼容版） | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.28/SonicRoute-v1.28-Lite-NET8-arm64.exe) | ARM64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| 🛡️ **Legacy x64** | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.28/SonicRoute-v1.28-Legacy-x64.zip) | 系统 .NET Framework 4.8；完整解压后运行 |
| 🔄 **Microsoft Store** | [商店安装](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | x64 .NET 10 Desktop Runtime；在架版本以商店页面为准 |

> [!NOTE]
> - 支持 Windows 10 2004+（Build 19041+）与 Windows 11。
> - Lite 单文件和 Store MSIX 均为框架依赖部署，不内置 .NET Desktop Runtime；Microsoft Store 安装不代表系统一定已具备运行时。
> - 2026 年 11 月之后停止提供和维护 NET8 版。x64 用户可手动选择 NET10 或 Legacy，ARM64 用户请选择 NET10。
> - 七项 Release 附件还包含 [SHA256SUMS.txt](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.28/SHA256SUMS.txt) 与 [SonicRoute-update.json](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.28/SonicRoute-update.json)（Protocol 2）。SHA-256 验证完整性，不等同于独立发布者签名。

---

## 🔄 升级与数据保留

1. 使用软件内更新，或从上表下载当前运行时与架构对应的文件。
2. 手动升级时先从托盘正常退出 SonicRoute，再替换 EXE 或完整解压 Legacy ZIP。不要将多个发行形态的程序文件混放。
3. 更新器不替换 `%LocalAppData%\SonicRoute\` 中的设置、规则、主题和自定义图标；手动更新前仍可自行备份该目录。Store 版继续由商店更新。

## 📚 相关链接

- [Wiki 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [快捷键](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) · [自动化规则](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)
- [完整版本历史](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) · [反馈问题](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose) · [支持作者](https://afdian.com/a/koukou021)
