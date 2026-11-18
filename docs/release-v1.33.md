# 🎧 SonicRoute v1.33 正式版

> **发布日期**：2026-11-18 · **主题**：自研 C# AirPlay 音画投屏、H.265 与声画解码器选择、统一配对与无线重连、独立麦克风 OSD 与全端轻量/稳定性优化 · [📥 发布页](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.33) · [📖 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

---

## ✨ 更新亮点

- 🍎 **自研 C# AirPlay 音频与屏幕镜像接收（仅 Full）**：无需额外运行库或外部软件，支持 PCM / ALAC / AAC-LC 与 C# 双声道 AAC-ELD 音频，支持等比投屏窗口（置顶 / `F11` 全屏）、可选 4 位投屏码、媒体曲名/歌手/封面展示，以及在快速面板中拖拽或一键转发至电脑设备或已连接的 Android 扬声器/蓝牙设备。
- 🎞️ **H.265（HEVC）与声画解码器选择（仅 Full）**：Android 与 AirPlay 新增 H.265 支持（Android 编码启动失败在同一连接自动回退 H.264，含独立关键帧恢复与损坏帧防护）；Android 设备设置新增声音/画面解码器选择器与可见设备页按需编码器查询（按机缓存最多 4 台），并将游戏手柄严格门控至获焦前台视频窗口。
- 📱 **统一手机配对卡片、无线重连刷新与配置恢复（仅 Full）**：将二维码（2 分钟自动换码、点击主动刷新）与配对码整合进同一张卡片且互不阻塞；设备设置重连、快捷设备面板刷新、应用页侧栏刷新统一验证当前设备连接，筛掉过期端口并按已保存身份找回新 IP/端口（无可用候选或失败时切回手动输入并保留草稿）；支持移除重加按稳定身份自动恢复设置与 USB 显式恢复，并加固配对身份与期限控制。
- 🎛️ **独立麦克风 OSD 开关、配置卡片与轻量性能优化（全版本）**：主题页独立显示并保存「所有麦克风静音时 OSD 常驻」与「默认录音设备静音时 OSD 常驻」；实验设置整合单行「配置文件管理」卡片（导出/导入/清理）；超过 20 个应用行启用虚拟化容器，合并 OSD 过渡并释放已解码 PNG 原始字节与闲置视频纹理，Full 取消关窗显式主动 GC 请求（交由运行时自然回收）。
- 🛡️ **可靠性加固、Store Lite 独立包与 GPL-3.0-only 迁移**：新增 `config.json.bak` 原子备份与损坏保护、单实例重启交接、鼠标多键仲裁与按需错误诊断日志；支持独立 Microsoft Store **Lite** MSIX 打包（`9N37LQ8SMLF6`），并于 2026-11-17 起迁移至 `GPL-3.0-only`（全版本设置页内置离线查看）。

---

## 🛠️ 详细更新

### 1. Full 版：自研 AirPlay 音画投屏、H.265 与手机互联体验升级
- **自研 C# AirPlay 接收与输出转发**：从「添加设备 → AirPlay」或手机面板底部开启接收，广播电脑名称并加入可用 IPv4 接口；支持媒体标题/歌手/按需封面解码、可选 4 位投屏码、投屏窗口置顶与 `F11` / `Esc` 等比全屏；AirPlay 音量行共用电脑设备顺序与别名规则，可将声音切至电脑输出或已连接的 Android 扬声器/蓝牙（目标失效自动回退 Windows 系统输出），并支持图标拖拽切换或断开当前串流。
- **H.265（HEVC）、解码器选择与串流稳定性**：Android 设备页「声音」「画面」行新增解码器选择（默认自动，可选 C# `Concentus Opus` 与 `Windows D3D11 优先 / Windows 软件解码`），仅在当前设备设置页可见时后台查询编码器并缓存；H.265 切换编码配置时保留上一帧并等待独立帧再提交画面、跳过损坏帧；游戏手柄仅在当前前台未最小化窗口生效，手机 PCM16 转换启用 SIMD 加速（保留标量回退）。
- **合并配对卡片、无线端口验证重连与身份加固**：连接页左侧二维码自动开启并到期刷新，右侧保留手动配对与直接连接；打开安卓连接页或点击「搜索」发起一次性 `_adb-tls-connect._tcp.local` 局域网发现并验证候选端口，无可用候选或失败时切回手动地址并保留用户输入草稿；设备设置重连、快捷设备面板刷新、应用页侧栏刷新统一验证当前设备连接，先确认现存 ADB 身份响应，失效时以一次性 mDNS 查找与短 TCP 探测筛掉过期端口、按已保存身份找回新地址并在连接核对身份后才覆盖保存；移除设备保留配置档案，无线显式重连或 USB 显式恢复时按稳定身份还原，不再被同 IP 旧记录覆盖；统一 `20s` 自动连接 / `30s` 手动配对期限并将取消配对绑定当前真实认证身份。

### 2. 全版本（Full / Lite / Legacy）：桌面功能、性能优化与可靠性加固
- **桌面交互与配置整理**：两项麦克风静音 OSD 常驻开关独立保存、共享同一次事件驱动录音读取（无新增轮询）；实验设置将导出、导入（保留拖入）与清理合并至单行「配置文件管理」卡片。
- **按需渲染与轻量资源管理**：应用列表超过 20 行时切换 WPF 可回收容器并延后创建控件（20 行内维持预建路径）；OSD 合并同目标连续输入并按硬件/软件渲染限帧；通过 `ImageDataStream` 释放解码后不再持有的原始 PNG 字节，Full 切换软硬解码时即时释放退役 GPU 纹理并取消关窗显式主动 GC 请求（交由运行时自然回收，Lite / Legacy 保持关窗后单次限频工作集收缩）。
- **配置安全、稳定性与发行规范**：写入/导入/重置前原子保留 `config.json.bak` 并在损坏时保护原件；单实例重启等待新进程资源就绪后再退出旧实例；精确仲裁鼠标组合键冲突、隔离坏图标，并在异常时按需写入 `Logs/diagnostics.log`；新增独立 Microsoft Store **Lite** MSIX 清单支持与设置页内置离线 `GPL-3.0` 查看器。完整逐项改动与验证边界请参阅 [Wiki 版本历史（09-版本历史）](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2)。

---

## 📦 资产下载与校验

| 版本 | 下载 | 说明 |
|---|---|---|
| ⭐ **Full**（全功能全量版） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-x64.exe) / [ARM64（实验）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-arm64.exe) | 完整版（纯 C# Android 互联、自研 AirPlay 音画与电脑音频路由），单文件 EXE（需匹配架构的 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)） |
| 🪶 **Lite**（纯 PC 轻量版） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Lite-x64.exe) / [ARM64（实验）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Lite-arm64.exe) | 轻量版（仅保留 Windows 本地音频路由），单文件 EXE（需匹配架构的 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)） |
| 🛡️ **Legacy x64**（免装运行库） | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Legacy-x64.zip) | 兼容版（.NET Framework 4.8），多文件绿色免安装包 |
| 🔄 **Microsoft Store** | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | 商店版（提供独立 Full / Lite；在架版本以商店页面为准，需 x64 .NET 10 Desktop Runtime） |
| 📋 **更新清单** | [`SonicRoute-update.json`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-update.json) | 客户端自动更新检测清单 |
| 🔒 **校验散列** | [`SHA256SUMS.txt`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SHA256SUMS.txt) | SHA-256 完整性校验文件 |

---

## 📜 许可证说明（GPL-3.0-only）

- **当前发行协议（GPL-3.0-only）**：自 **2026 年 11 月 17 日**起（提交 `46cf001`），完整 SonicRoute 源码与 `v1.33` 及后续发行物（在版权所有者有权授权的范围内）采用 GNU 通用公共许可证第 3 版，仅限该版本（`GPL-3.0-only`），允许商用、销售与收费分发，须遵守 GPL 条款；分发所涵盖的修改作品或二进制文件时，须继续使用 GPL 第 3 版并按 GPL 第 6 条提供完整对应源码与必要声明，详见仓库 [LICENSE](https://github.com/kunkunkunQoQ/SonicRoute/blob/master/LICENSE) 与 [LICENSING.md](https://github.com/kunkunkunQoQ/SonicRoute/blob/master/docs/LICENSING.md)（新版本设置页亦可点击 `GPL-3.0` 离线查看）。
- **既有发行物与第三方许可保留**：此前已授予的 MIT 权限（含原始 `v1.32` 标签 `4e3138f` 及其既有下载资产）及 2026-11-14 期间的旧自定义许可权限继续有效；本次仓库协议迁移不撤销先前授予的权限，亦不改写既有标签或已发布的旧二进制资产，第三方组件保留各自原有许可（见 [THIRD-PARTY-NOTICES.md](https://github.com/kunkunkunQoQ/SonicRoute/blob/master/packaging/AndroidLab/THIRD-PARTY-NOTICES.md) 与 [AirPlay 兼容声明](https://github.com/kunkunkunQoQ/SonicRoute/blob/master/SonicRoute.AndroidAudio/AirPlay/Compatibility/NOTICE.md)）。
