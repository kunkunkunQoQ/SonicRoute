# 共享文件夹与文件传输

手机在「设置 → 共享文件夹」打开 Android 系统目录选择器。只有所选树及其后代可供已配对电脑访问。选择「停止共享」或更换目录会使已发出的目录游标、上传和下载失效。Android 系统保存持久 URI 授权，SonicRoute 的私有偏好保存选中树的 URI。线上协议只传逻辑名称。

Android Storage Access Framework 支持对不同提供者单独授予目录访问权。SonicRoute 可浏览可读的文档树。接收文件的首个适配器使用 Android 系统本地存储提供者，并检查目录创建及新文档的写入、删除、重命名能力。其他提供者显示只读共享。Windows 文件卡显示当前传输可用的选项。[Android 文档与目录授权](https://developer.android.com/training/data-storage/shared/documents-files) [Android 文档操作](https://developer.android.com/reference/android/provider/DocumentsContract) [AOSP 本地文件提供者](https://github.com/aosp-mirror/platform_frameworks_base/blob/master/core/java/com/android/internal/content/FileSystemProvider.java)

Activity 返回前台时复核系统权限。进程重启后用同一棵持久树重新查询根目录、写入能力和残留暂存记录。提供者暂时不可读时保留本机选择，界面可重选或停止共享，再次进入前台会重试。系统撤回读权限后清除选择。剩余只读授权按只读重新公布。同一 Android 用户、包名及兼容签名的覆盖更新保留应用数据和系统持久授权。清除应用数据、完整卸载或所选目录移动后需要重新授权。[Android 覆盖安装](https://developer.android.com/tools/adb#pm) [AOSP 包替换和 URI 授权处理](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/android14-release/services/core/java/com/android/server/am/ActivityManagerService.java#14685)

## 已认证会话

能力为 `files.transfer`，作用域为 `saf_tree`。限制字段均为字符串。`grant` 标识本地本次目录授权，`write` 是 `true` 或 `false`，`chunk` 是 `32768`，`maxFile` 是 `536870912`。能力启用后复用当前双向 TLS 会话的 SRAP 控制和 DATA 帧。每会话最多一项文件操作同时运行。Windows 用现有流号分配器申请非零奇数，上传和下载都使用同一已登记的流号。关流回收对应所有权。

| 请求 | 参数 | 完成回执 |
| --- | --- | --- |
| `file.list` | `parent: string[]`，可附上一页的 `cursor` | `entries: [{name, directory, size?, modified}]`，还有下一页时返回 `cursor`。修改时间为 Unix 毫秒。 |
| `file.offer` | `streamId, parent, name, size, sha256` | 手机前台展示可信电脑名称、完整指纹、文件名、大小和目录。用户接受后才返回 `{streamId,size}`。60 秒超时，拒绝或离开界面取消。 |
| `file.open` | `streamId, parent, name` | 下载所选项目。返回 `{streamId,size}`。大小未知或超限的文件报告不可用。 |
| `file.chunk` | `streamId, offset, count` | `{offset,count}`。上传方先发送一个 DATA，下载时手机先发送一个 DATA，然后确认对应控制请求。 |
| `file.finish` | `streamId` | `{size,sha256,name}`。手机接收时还要核对提供者回读；Windows 接收时核对本地摘要后才移动到下载目录。 |
| `cancel` | 标准信封的 `body.streamId` 或 `body.requestId` | 释放本次文件、单块缓冲、确认窗口和注册的 DATA 接收端。 |

根目录为 `parent=[]`。每个逻辑目录名精确匹配一个直接子项，最多 16 层。含路径分隔符、点路径、Windows 保留名或控制字符的项目不参与传输。目录有重复的可见名称时返回冲突。每个目录最多 4096 项，每页最多 24 项；后续页读取同一会话、同一授权下的目录快照。

空文件直接结束。其他文件每块 1 至 32768 字节，`offset` 必须等于已确认字节数；下一块必须等上块收据。上传方先计算完整 SHA-256，手机逐块写入私有暂存并校验。手机随后把已校验内容写入所选目录内的随机临时文档，从提供者再次读取实际大小和 SHA-256，核对后按请求名提交。已有同名项目时返回 `busy/name_exists`。恢复日志只记录应用自己创建的临时文档，冷启动恢复同一授权后清理。Windows 下载使用目标目录下的独占临时文件和现有无覆盖移动操作，重名时自动添加序号。

## 可复跑验证

Android 只对系统内置外部存储提供者开放接收。提交前后都枚举选中父目录的直接子项，核对随机暂存名、文档身份、目标名和实际大小。并发提交在最终重命名时串行。提供者返回未知的最终身份时会保留恢复记录并报错；恢复只清理与暂存记录相符的私有随机名。会话取消时独立关闭已打开的提供者流，可以解除正在等待文件数据的读取。

在 Android 工程运行 `gradlew.bat :core:checkSrapFiles`。该 JVM 用例使用内存提供者，覆盖手机确认前拒收数据、拒绝、跨块双向传送、摘要错误、乱序、授权更换、目录分页和阻塞读取的取消。

Windows `AndroidAudioChecks --srap-live-files <配对目录>` 保持同一可信连接，等待手机用户选定空白、可写的测试目录。选择后请回到手机 App 接受命名为 `SonicRoute-transfer-<随机标识>.bin` 的本次收件。该检查上传 1 MiB 加 17 字节，再下载同一项目并独立复算 SHA-256。输出手机保留的文件名、本地回读位置、上传总时长、用户同意后的传输时长及两向吞吐量。
