# SonicRoute 1.36 原生后台候选

Full 和 Lite 共用本项目。Windows 使用单个 WinExe、一个 STA 消息循环和隐藏的顶层窗口。桌面 UI、手机和 AirPlay 的完整产品入口继续由后续阶段接入。本阶段的托盘、配置、规则、音频和更新交接都运行在产品 EXE 内。

## 编译与启动

在 worktree 根使用 .NET 10 SDK、Windows SDK 和对应的 MSVC 工具。使用以下命令分别构建四个候选。ARM64 也可以向脚本传入阶段 1 已校验的 -ToolCache。两个版本使用独立的中间目录。

~~~powershell
./Native/Build-Product.ps1 -Edition Full -Architecture x64 -VsRoot 'C:/Program Files/Microsoft Visual Studio/2022/Community' -OutputRoot E:/scratch/sr-build
$exe = 'E:/scratch/sr-build/candidate/Full-x64/SonicRoute.exe'
$env:SONICROUTE_CANDIDATE_ROOT = 'E:/scratch/sr-profiles'
$env:SONICROUTE_CANDIDATE_ID = 'development'
Start-Process -FilePath $exe -WindowStyle Hidden
& $exe --control status | Out-String
& $exe --control devices render | Out-String
& $exe --run-rule '已保存的规则名称或 ID' | Out-String
& $exe --control exit | Out-String
~~~

默认数据在本用户的 SonicRoute.NativeCandidate 子目录。候选把 profile、版型、渠道和绝对根路径映射为互斥量、管道及激活窗口的隔离名称。在全新开发配置中，内置全局键和托盘滚轮初始关闭，避免与并行安装争抢输入。已有配置按实际绑定加载。命令测试使用单独的开发根，此外部接口和原有的规则管道独立。候选更新只接受位于本 profile 的 install 目录的目标。

## 服务调用

- ApplicationHost 拥有配置、音频、当前应用、自动化、截止时间、托盘及事务生命周期。可选视图接收 FeedbackState 和 ProductCurrentApp 的代际快照。UI 解绑时先提交 CoalescedVolumeWriter 的末值，后台音频所有者继续运行。
- Windows 端点与会话经 Native/Audio 访问。ProductAudio 控制音量、静音、设备及订阅。普通应用路由和显式多选通过 ProductRouting 进入原有 AppPlaybackOutputs 及 AppPlaybackRestore。产品中从未打开 UI 也能通过规则或管道使用这些服务。
- 读取命令包括 status、apps [offset]、devices render|capture、default <flow> <role>、volume <flow> <设备ID、default或PID>、route <PID> <flow>、outputs-get <PID>、mirror-status <PID>、output-state 及 automation-status。输入输出分别使用 capture 和 render。
- 绝对音量或静音写入使用 volume <flow> <target> level <0..100> 或 mute <true|false>。拖动产生的每应用音量写入使用 volume-queue <PID> <百分比>，volume-flush 等待已提交写入。route <PID> <flow> <设备ID或default> 切换应用路由。outputs-set <PID> <首选> [选中的各个ID] 表达显式多输出。
- 设置 SONICROUTE_CANDIDATE_METRICS=1 后，活动镜像记录固定容量的处理样本，退出后写 profile/last-exit.json。测量方法与范围见 [性能工具](../Measurements/README.md)。测试程序只作为客户端、事件发生源或读数方。
- 阶段 1 的 HostProbe、AudioProbe、FullProbe、GraphicsProbe 保留独立入口。旧 WPF Full/Lite 和 net48 保留作对照。发布流水线继续使用旧工程，最终替换须经过后续界面和发行验收。
