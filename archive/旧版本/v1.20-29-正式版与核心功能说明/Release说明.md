## 🎧 音跃 SonicRoute v1.20 正式版

Windows 10/11 按应用音频快速切换工具：一键切单应用输出、音量和静音，不改系统默认设备。

v1.20 正式版现已发布。现有配置可继续使用，升级前建议保存配置副本。

**更新**
- 快速面板新增每应用实时音量电平，主题内可关闭；增强深浅模式可见度，修复加载失败与未知应用显示。
- 优化空闲轮询、界面复用、图标缓存及内存回收，改善 Legacy 设置、自动化切页和 OSD 响应。
- 改进更多选项、快速面板入场和设备栏展开动画；切换应用仍直接切换设备栏，简洁面板默认高度为 450px。
- OSD 新增主题内可关闭的音量进度条，音量数字显示在左侧；合并更新并减少连续调音量时的动画重启。
- OSD 文字采用主题强调色，主标题按深浅模式调整对比度；重新整理常驻、外观与位置设置。
- 自动化新增手动执行、规则与步骤复制、软件启动触发、明确静音、相对音量和全局麦克风控制。
- 新增短命令 `sr "规则名称或ID"`，从任意目录直接调用运行中的软件执行规则；保留完整 EXE 的 `--run-rule` / `-r` 调用。
- 自动化操作下拉按五组排列，步骤标题支持拖动、预览让位与边缘滚动；缩短上下换位所需拖动距离，松手时复用已有控件。
- 九种语言同步新增功能；Lite x64、Lite ARM64 与 Legacy x64 使用同一份主要界面源码。

**下载**：⚡ [Lite x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.20/SonicRoute-v1.20-Lite-x64.exe)（需 x64 .NET 8 Desktop Runtime）｜ 🧪 [Lite ARM64（实验）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.20/SonicRoute-v1.20-Lite-arm64.exe)（需 ARM64 .NET 8 Desktop Runtime）｜ 🪟 [Legacy x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.20/SonicRoute-v1.20-Legacy-x64.zip)（需系统已具备 .NET Framework 4.8）

- 支持 Windows 10 2004+ / Windows 11。Legacy 请解压完整目录后运行，保留所有 DLL 和配置文件。
- ARM64 版为实验性支持，尚未经过 ARM64 真机完整验证，如遇问题欢迎反馈。
- 首次使用短命令需正常启动新版一次，再重新打开终端；软件未运行时 `sr` 不会自动启动软件。`sr --help` 查看帮助，`sr --legacy "规则名"` 指定 Legacy。
- 三个版本已完成构建和发布包静态检查；本轮未启动软件进行界面、规则或 CPU/内存实测。
- Microsoft Store x64 Lite 的 1.20 MSIX 已在本地制作，尚未提交商店审核，商店仍以实际已上架版本为准。
- v1.20 起停止提供旧自包含版本；无需安装 .NET 8 的用户可使用 Legacy x64。
