# 🎧 音跃 SonicRoute · Windows 音频枢纽

> Windows 10/11 **音频控制中心**：**当前应用 / 全局应用 / 系统默认**三档音频路由、按应用音量与静音、设备管理，一个托盘入口全盘掌控。底层实现方式参考 [EarTrumpet](https://github.com/File-New-Project/EarTrumpet) 已验证的 Per-App Audio Routing（`IAudioPolicyConfigFactory` / `SetPersistedDefaultAudioEndpoint`），代码为自行重新实现。

作者：[困困困](https://github.com/kunkunkunQoQ) ｜ **v1.13** ｜ Win10/11 x64 ｜ C# / .NET 8 / WPF

---

## 🚀 快速开始

🛍 **推荐**：从 [微软商店](https://apps.microsoft.com/detail/9NQZGRTPM1NT) 安装（自动更新）；或 [Releases](https://github.com/kunkunkunQoQ/SonicRoute/releases) 下载**含运行环境版本**（解压即用）/ 轻量版（需 .NET 8）→ 驻留托盘：**单击**快捷面板、**双击**完整界面、**任务栏滚轮**调当前应用音量。

## ✨ 核心功能

- 🎯 **三档音频路由**：当前应用 / 全局应用 / 系统默认，互不影响可组合；一键还原全部应用（含已退出）
- 🧩 **托盘快捷面板**：简洁（Win11 音量飞出式，全部应用逐行音量）/ 经典双面板，单击秒切
- 🖥 **完整管理界面**：概览 / 应用 / 设备 / 快捷键 / 主题 / 设置一站式管理
- ⌨️ **快捷键中枢**：11 项动作全可自定义（F区 / 单键 / 鼠标键 / 滚轮 / Esc 清除）
- 🎤 **麦克风枢纽**：按应用切输入 + 全局麦克风静音 + 麦克风静音 OSD 常驻
- 📋 **设备管理**：保留设备勾选 / 自定义名称 / 系统默认也可配置，快捷键只在保留设备间循环
- 🔇 **应用音量 + OSD**：按 Session 独立调音量，不动系统主音量；任务栏滚轮即调；OSD 不闪不跳、可拖拽定位 / 调大小
- 🧹 **内存优化 + 多语言主题**：Lite 后台 ~36MB 自动回收；9 语言、RGB 强调色、透明度

## 📦 下载

| 版本 | 文件 | 体积 | 需求 |
|---|---|---|---|
| 🛍 微软商店（推荐） | [Store 搜索 SonicRoute](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | — | 自动更新 |
| 🟢 含运行环境版本 | `SonicRoute-v1.13.exe` / `.zip` | ~237MB / ~85MB | 内置运行时 |
| ⚡ 轻量版 | `SonicRoute-v1.13-Lite.exe` / `.zip` | ~27MB / ~6.7MB | 需 .NET 8 |

## 📚 完整文档 → [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

[📖 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)（安装 / 面板 / 完整界面 / 场景教程）｜ [✨ 功能特性](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) ｜ [⌨️ 快捷键](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE)（完整默认键表 + 绑定规则）｜ [❓ 常见问题](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) ｜ [🛠 技术实现](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) ｜ [📌 版本相关](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3)（版本规则 + 更新日志）

## 📌 版本相关

| 后缀 | 含义 | 上传 GitHub |
|---|---|---|
| `r` | 修复版（修复 bug 后正式发布） | ✅ |

发布形态：绿色免安装（含运行环境版本 + 轻量版）上传 Releases；微软商店版（Lite）自动更新、**优先推荐**；更新日志只展示最新版本，完整版本历史见 [Wiki · 版本相关](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3)。

### 🔮 未来版本方向

- 🎨 UI 更简洁
- ⚡ 性能表现优化
- 🛠 使用体验优化
- 暂不考虑自动化相关

---

**作者主页 ‖ [哔哩哔哩](https://b23.tv/TDqSAKM) ‖ [爱发电](https://www.ifdian.net/a/koukou021) ‖ [GitHub](https://github.com/kunkunkunQoQ/SonicRoute)**

---

## 💖 支持项目

[![支持 爱发电](https://img.shields.io/badge/支持-爱发电-FF7A2F)](https://www.ifdian.net/a/koukou021)

喜欢这个项目？点击上方按钮或到 [爱发电](https://www.ifdian.net/a/koukou021) 支持项目～ 仓库页右侧的 **Sponsor** 按钮同样直达爱发电。
