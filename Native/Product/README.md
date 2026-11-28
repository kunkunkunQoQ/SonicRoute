# SonicRoute 2.00 Native AOT

Full 和 Lite 共用本项目。Full 包含 Android 和 AirPlay，Lite 提供电脑音频功能。桌面窗口、托盘、配置、规则、音频和更新交接均由本项目运行。使用仓库根目录的 `SonicRoute.Native.slnx` 开发。

## 编译与启动

在主工作区根目录使用 .NET 10 SDK、Windows SDK 和对应架构的 MSVC 工具构建。默认将候选 EXE 写到 `../outputs/native-product/candidate/<版型>-<架构>/`，版型和架构分别使用独立的中间目录。自定义目录用 `-OutputRoot`。ARM64 可通过 `-ToolCache` 指定匹配的 MSVC 缓存。

~~~powershell
& ./Native/Build-Product.ps1 -Edition Full -Architecture x64
& ./Native/Build-Product.ps1 -Edition Lite -Architecture x64
$exe = (Resolve-Path '../outputs/native-product/candidate/Full-x64/SonicRoute.exe').Path
$env:SONICROUTE_CANDIDATE_ROOT = (Resolve-Path '../outputs/native-product').Path
$env:SONICROUTE_CANDIDATE_ID = 'native-200-local'
$env:SONICROUTE_DISABLE_DEFAULT_HOTKEYS = '1'
Start-Process -FilePath $exe -WindowStyle Hidden
& $exe --control status | Out-String
& $exe --control devices render | Out-String
& $exe --run-rule '已保存的规则名称或 ID' | Out-String
& $exe --control exit | Out-String
~~~

VS Code 提供 Full 与 Lite 的 x64 构建任务，默认任务是 Full。需要带版本名的便携单文件时使用 `packaging/Build-Edition.ps1`，默认输出到 `../outputs/native-product/editions`。该脚本也支持已有的本地程序绑定，日常候选构建可直接使用上面的 Native 脚本。

完整便携候选用 `packaging/Updates/Build-PortableRelease.ps1 -OutputDirectory <新目录>` 构建。Full 和 Lite 的 x64、ARM64 EXE 内置 .NET 10 Native AOT 运行时。四个 EXE 与更新清单 `SonicRoute-update.json`、校验文件 `SHA256SUMS.txt` 共六项资产。普通推送只验证候选，正式发布使用显式手动流程。Full x64 的未签名商店候选单独打包。[发布操作清单](../../packaging/Updates/RELEASE-CHECKLIST.md) · [Store 构建说明](../../packaging/Store/README.md)。

按上述方式设置候选环境变量后，隔离数据写入候选根目录的 `SonicRoute.NativeCandidate` 子目录。普通便携版继续使用 `%LOCALAPPDATA%\SonicRoute`。候选把 profile、版型、渠道和绝对根路径映射为互斥量、管道及激活窗口的隔离名称。在全新开发配置中，内置全局键和托盘滚轮初始关闭，避免与并行安装争抢输入。已有配置按实际绑定加载。命令测试使用单独的开发根，此外部接口和原有的规则管道独立。候选更新只接受位于本 profile 的 install 目录的目标。

## 服务调用

- ApplicationHost 拥有配置、音频、当前应用、自动化、截止时间、托盘及事务生命周期。可选视图接收 FeedbackState 和 ProductCurrentApp 的代际快照。UI 解绑时先提交 CoalescedVolumeWriter 的末值，后台音频所有者继续运行。
- Windows 端点与会话经 Native/Audio 访问。ProductAudio 控制音量、静音、设备及订阅。普通应用路由和显式多选通过 ProductRouting 进入原有 AppPlaybackOutputs 及 AppPlaybackRestore。产品中从未打开 UI 也能通过规则或管道使用这些服务。
- 读取命令包括 status、apps [offset]、devices render|capture、default <flow> <role>、volume <flow> <设备ID、default或PID>、route <PID> <flow>、outputs-get <PID>、mirror-status <PID>、output-state 及 automation-status。输入输出分别使用 capture 和 render。
- 绝对音量或静音写入使用 volume <flow> <target> level <0..100> 或 mute <true|false>。拖动产生的每应用音量写入使用 volume-queue <PID> <百分比>，volume-flush 等待已提交写入。route <PID> <flow> <设备ID或default> 切换应用路由。outputs-set <PID> <首选> [选中的各个ID] 表达显式多输出。
- 设置 SONICROUTE_CANDIDATE_METRICS=1 后，活动镜像记录固定容量的处理样本，退出后写 profile/last-exit.json。测量方法与范围见 [性能工具](../Measurements/README.md)。测试程序只作为客户端、事件发生源或读数方。
- HostProbe、AudioProbe、FullProbe 和 GraphicsProbe 继续使用各自的项目入口。WPF 1.36 比较基准由本地 `v1.36` 标签固定，提交为 `3d9f9e1083b228f666e30d97e3c8edfce5e6654a`。
