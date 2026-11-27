# Windows 阶段 2A 回执与 Gemini 阶段 2B 交接

2026-11-28，Windows Native AOT Full 在共用的「设备」页加入 SonicRoute App 连接方式。工作树以 `a60ffb4` 为基线并保留前序未提交改动。Windows 使用当前用户保护的 CNG P-256 安装身份，连接经固定叶公钥的 SslStream TLS/mTLS，复用 Android 设备协调器、档案、目录控件、菜单和操作端口。现有 ADB 协议及 Android 业务源码未作阶段 2A 修改。

| 入口 | 阶段 2A 行为 |
| --- | --- |
| 添加设备 → SonicRoute App | 按需搜索开放配对的 mDNS 候选。主动粘贴邀请后固定公钥，完成手机端确认，再建立认证会话。地址不参与身份判定。 |
| 设备管理 | App 档案经同一协调器解析。可信档案从启动、网络地址变化、mDNS 通知或会话结束自动尝试恢复。断开保留信任，删除归档档案，取消配对停止本地信任。 |
| 能力及操作 | 目录、输出枚举、系统媒体音量与自有测试音路由使用服务端快照及当前会话修订。测试音以 `audio.routed` 实际结果确认。PC 音频发送及捕获、媒体控制、文件、剪贴板和投屏留在其协议阶段。 |
| 档案与身份 | ADB 档案保持原 ID。阶段 2A 只创建已认证 App 档案。App 与既有 ADB 的同机合并由阶段 2B 的双通道证明启用。 |

协议规范见 [SRAP v1.0](protocol.md)，待实现扩展见 [阶段 2B 双通道绑定](srap-adb-alias-stage2b.md)。联调设备、APK 散列、失败项和产物见 [Windows 联调报告](../../outputs/android-srap-stage2a-20261128/实机联调报告.md)。

## Gemini 执行清单

1. 用独立 fixture 实现并核对 195 字节签名输入、大小端、角色、固定长度、版本取交集及标准错误码。给 Codex 提供两端可独立消费的字段和值。如需协议变更，先交付差异、兼容路径和验收向量。
2. 在现有 Android 接收器加入 `identity.alias.adb`、主会话关联、单窗口本地确认、临时回环 mTLS 和挑战单次消费。验证主会话中断、撤权、取消或超时都释放监听器和证明流。同一 Android 用户的同一安装密钥签署证书与证明。
3. 按新规范验证邀请生成器和解码器，收紧重复、未知字段及不安全地址。IPv6 链路本地的作用域由接收端发现解析。复制邀请时使用 Android 敏感剪贴板标记及限时清理。应用日志仅保留安全原因码。
4. 核对服务端撤销原因契约。只有真实删除对端信任使用 `permission_lost / peer_revoked`，接收停止及暂时换网使用可区分的会话结束事件。确保撤销报告与回读有一致顺序。若提出双向的远程撤销协议，单独提交接口评审，现有 1.0 客户端明确展示撤销位置。
5. 修复实测 APK 中远程 `volume.set(system_media)` 的线程访问。检查 `LanServerController.operationBridge` 直接调用 `LocalAudioController.setSystemMediaVolumePercent` 后，经 `publish()` 到 `SonicRouteRuntime.onAudioStateChanged` 和 Activity 的路径。确保按主线程约定更新界面，保留请求期限与写入回读语义，实际操作过但界面更新失败时禁止客户端盲目重放。复测远程写入前后两个不同系统音量档位、恢复原值和已信任电脑列表仍能撤销。Android 端修复后提交带固定散列的独立 APK、ROM/backend 矩阵和 C# 复跑记录。阶段 2B 仍由 Codex 负责 Windows 迁移事务、ADB forward 归属和跨端验收。
