# 🎧 SonicRoute v1.24 正式版

> **发布时间**：2026-10-20 · **核心主题**：场景起步模板与个性化应用图标 · [📥 资产下载](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.24) · [📖 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

---

## ✨ 核心更新亮点

- 🤖 **自动化场景起步模板**：新建规则旁一键选择开黑独占、会议闭麦、游戏降噪等 7 套典型场景，生成未保存草稿，降低规则编写门槛。
- 📊 **最近执行详情日志**：实验设置可选开启（默认关闭、可设 1–64MB 存储上限），提供按需两段滚动与每一步状态、耗时精细排查。
- 🎨 **逐应用自定义图标与首字标识**：支持导入 PNG、JPEG、ICO 格式图标或恢复默认，缺失图标时自动提取首字生成专属强调色标识；导出配置自动打包内嵌。
- ⚡ **按需读取与性能优化**：执行历史按需加载并自动取消过时请求，配置传输与图标复用后台路径。
- 🐛 **修复高权限应用图标**：解决部分以管理员身份运行的应用图标无法在列表中显示的问题（感谢 @Billy636 的 [PR #6](https://github.com/kunkunkunQoQ/SonicRoute/pull/6)）。

---

## 📋 详细演进说明

### 1. 自动化场景起步模板
在「自动化」页面，点击「＋ 新建规则」旁边的示例菜单，即可快速选择：
- 🎮 **游戏使用耳机**：前台为游戏时，声音自动切往耳机；
- 🎙️ **会议使用指定麦克风**：打开飞书/腾讯会议时，自动指定高品质麦克风；
- ⚡ **快捷键切换声音组合**：一键在「耳机+语音」与「音箱+静音麦」之间快速轮转；
- 🎵 **进入游戏降低音乐音量**：游戏启动时背景音乐压低至 30%；
- 🌙 **每晚降低系统音量**：定时防扰民；
- 🚪 **退出游戏切回音箱**：关闭游戏后自愈还原。

> [!TIP]
> 选择示例仅会在界面中生成一份未保存的草稿，不会立刻生效。您可以按需修改应用名、设备与音量参数；所有模板创建的规则默认保持停用，确认无误后手动启用即可。

### 2. 最近执行详情与毫秒级耗时
- 在设置页点击作者头像/名称 5 次解锁「实验模式」，开启「最近执行详情」开关；
- 查看器支持存储最近 200 条执行记录（可自定义 1–64MB 存储上限，默认 4MB）；
- 展开每条记录可深入查看规则内各个步骤的执行结果、状态（成功、跳过、失败）与毫秒级耗时；
- 默认保持关闭，关闭状态下完全不占用内存与磁盘 I/O。

### 3. 应用图标管理与首字强调色
- 在「应用」页选中任意发声程序，点击顶部「导入图标…」即可自定义设定个性化头像；
- 当软件未提取到图标或为系统通用图标时，系统会自动采用应用首字生成美观的强调色标识块；
- 在实验设置导出完整配置时，自定义导入的图标会自动内嵌打包，换机导入无缝还原。

---

## 🚀 资产下载矩阵

| 发行形态 | 推荐目标人群 | 下载链接 | 运行环境要求 |
|---|---|---|---|
| ⭐ **Lite x64**（推荐主力） | 主流 64 位 PC 绿色单文件 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-x64.exe) | 需匹配的 x64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| ⚡ **Lite ARM64**（实验） | Surface Pro / 骁龙 PC 原生高效 | [单个 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-arm64.exe) | 需匹配的 ARM64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| 🛡️ **Legacy x64**（免装运行库） | 公司办公电脑 / 受限机型 | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Legacy-x64.zip) | 系统自带 .NET Framework 4.8（解压即用） |
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

- [Wiki 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [自动化规则配置](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [常见问题解答](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)
- [提交建议或 Bug 反馈](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)
