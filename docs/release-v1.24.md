# SonicRoute v1.24 正式版

2026-10-20 · [下载正式版](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.24)

## 更新内容

- **自动化起步模板**：点「新建规则」旁的示例按钮，选择场景后直接进入编辑器；补齐应用和设备，保存后再启用。
- **最近执行详情**：实验设置中可选开启，默认关闭；可设存储上限，查看规则与每一步的状态和耗时。
- **应用图标与名称**：应用页可导入图标或恢复默认；没有可用图标时显示名称首字，配置备份可带上导入的图标。
- **读取与界面改进**：历史按需读取并取消过时请求，配置传输和图标处理复用现有后台路径；应用详情布局同步调整。

- 修复部分高权限应用图标无法显示的问题（感谢 @Billy636 的 [PR #6](https://github.com/kunkunkunQoQ/SonicRoute/pull/6)）。

## 从示例开始创建规则

在「自动化」页，点击「＋ 新建规则」旁的小按钮，选择游戏使用耳机、会议使用指定麦克风、快捷键切换声音组合、启动常用工具、退出游戏后切回音箱、进入游戏时降低音乐音量或每晚降低系统音量。

选择示例只会打开未保存的草稿，不会执行操作。填写实际应用、设备、快捷键或程序路径后保存；模板创建的规则默认停用，确认参数后再启用。

## 查看最近执行详情

先在设置页点击作者名称五次，解锁并启用实验模式，再进入「实验设置」开启「最近执行详情」。默认关闭，关闭时不加载历史组件。存储上限可设为1–64MB，默认4MB；查看器显示最近200条有效记录，可查看每步结果与耗时，也可清空记录。不会补录此前未记录的执行。

## 应用图标

在「应用」页选中应用，使用顶部「导入图标…」选择PNG、JPEG或ICO，或点「恢复默认」。自动提取不到图标、或提取到系统通用图标时使用名称首字占位。导入图片会保存为受管小图，移动原文件不影响显示；界面导出配置会包含自定义图标，独立自动化规则文件仍需另行备份。

## 下载

| 版本 | 下载 | 运行要求 |
|---|---|---|
| Lite x64 | [单个EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-x64.exe) | x64 .NET 8 Desktop Runtime |
| Lite ARM64（实验） | [单个EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-arm64.exe) | ARM64 .NET 8 Desktop Runtime |
| Legacy x64 | [完整ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Legacy-x64.zip) | .NET Framework 4.8 |

支持Windows 10 2004+ / Windows 11。Lite需对应架构的.NET 8 Desktop Runtime，Legacy请完整解压后运行。校验值见附件SHA256SUMS.txt。商店版以[Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT)实际在架版本为准。

升级前从托盘退出旧版，再运行新版；已有自定义快捷键和设置继续保留。普通版与商店版的自启仍是两个独立选项。

详细操作见[Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)，旧版变化见[版本历史](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2)。
