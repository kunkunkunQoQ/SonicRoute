# SRAP 前台纯文本剪贴板

双方使用当前已认证 SRAP 会话。Android 本地启用共享、Activity 可见并实际持有窗口焦点、已连接可信电脑时公布 `clipboard.sync` 为 `available`，作用域为 `focused_text`。Windows 为选定的手机开启剪贴板同步后才注册订阅。监听器由系统剪贴板变化驱动，最后一个订阅关闭时释放。

| 操作或事件 | 参数与行为 |
| --- | --- |
| `clipboard.watch` | 新建当前会话独占订阅，参数 `{watchId}`。使用小写带连字符 UUID；同一会话已有订阅返回 `busy`。返回 `done {revision}`，以接下来用户的复制操作启动同步。 |
| `clipboard.unwatch` | 以 `{watchId}` 结束对应订阅。撤权后仍可发送，重复结束返回 `done {}`。旧 watchId 无法关掉后来建立的订阅。 |
| `clipboard.get` | 按当前 `{watchId}` 主动读取。返回 `done {revision}`，随后按此次读取结果发送下述事件。 |
| `clipboard.set` | 参数 `{watchId,streamId,byteLength,expectedRevision}`。Windows 分配本会话的新奇数流 ID，正文限 0–65536 字节。非空正文先等同一请求的 `pending`，再以一个 SRAP DATA 帧发送严格 UTF-8。空文本在控制请求中声明零长度，无 DATA。 |
| 写入回执 | 手机重新核验授权、焦点、订阅、数据长度和剪贴板修订。写入带本次来源标记的普通文本，立即读回标记与文本，再返回同一请求的 `done {revision}`。修订冲突返回 `stale_session`；写入结果未知时返回 `io_failure`。客户端不会自动重放写入。等待正文上限 6 秒。 |
| `clipboard.changed` | 只有本机可分享文本才发送。主连接的有序事件体为 `{name,watchId,revision,streamId,byteLength}`。非空文本使用 Android 新分配的正偶数流，同一发送锁下紧接一帧 DATA。Windows 等待 6 秒后释放该流并结束订阅。空文本使用流 0 和零长度，省略 DATA。 |
| `clipboard.revision` | 体为 `{name,watchId,revision}`。当内容被排除、来自远端写入或无法作为普通文本读取时，只通知修订。 |

剪贴板修订独立于能力修订。系统变化更新手机侧修订，客户端写入带上它在桌面复制事件时观察到的修订。更换会话或重新订阅时重新建立基线。UTF-8 不携带 BOM 前缀，拒绝坏编码、未配对代理项、内嵌 NUL 和 SonicRoute 配对邀请。手机依据剪贴板描述、敏感标志和来源标记过滤，仅接受一个 `text/plain` 项目。Windows 检查原生格式、敏感标记、剪贴板所有者及内容序号。由对端写入的内容不回传给来源或同一手机的其他电脑。

数据与其订阅和认证会话绑定。会话关闭、撤销、失焦、关闭本地开关、取消请求或超时会释放监听、流占用及待处理写入。操作系统剪贴板没有跨应用原子比较并写入接口，双方在写入前复查版本、写后回读，对竞态返回可观察结果。

固定 DATA 帧样本由两端独立读取。[前台剪贴板协议向量](protocol-fixtures/srap-clipboard-v1.txt)

真机检查使用 Windows `AndroidAudioChecks --srap-live-clipboard <配对目录>` 与测试 APK。互通脚本的 `-ClipboardChecks` 参数连接两端，手机必须保持焦点并由用户打开前台共享。检查只在系统报告手机剪贴板为空时写入带每次运行随机标识的文本。Android 原生读回后发送第二条文本，Windows 在内存验证摘要，确认后清除本次测试文本。手机原有内容非空则记录跳过。
