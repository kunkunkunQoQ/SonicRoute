# Full 媒体与互联的 Native AOT 验证

本工程引用隔离树内真正的 SonicRoute.AndroidAudio 和 QRCoder，入口为独立的验收程序。正式 UI 的程序逻辑仍在原项目，Windows Link Beta 独立。ViewLifetimeChecks 是仅在开发机上运行的 WPF 行为基线；FullProbe.csproj 已将它从 AOT 程序的编译项剔除。

## 可重放步骤

在隔离源码根以 PowerShell 运行。每个 RID 使用自己的输出和中间目录，避免不同架构共享静态库。dotnet 路径取本机已安装的 .NET 10 SDK，Set-Phase1Toolchain.ps1 初始化 VC/Windows SDK 的编译和链接环境。每次运行探针时提供新的私有数据目录。

```powershell
$stageRoot = [IO.Path]::GetFullPath('../stage/full')
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet/dotnet.exe'
foreach ($arch in @('x64', 'arm64')) {
    . ./Native/Set-Phase1Toolchain.ps1 $arch -VsRoot 'C:/Program Files/Microsoft Visual Studio/2022/Community'
    & $dotnet publish Native/FullProbe/FullProbe.csproj -c Release -r "win-$arch" -p:IlcUseEnvironmentalTools=true "-p:ArtifactsPath=$stageRoot/build-$arch" -o "$stageRoot/$arch"
    if ($LASTEXITCODE) { throw "Publish failed for $arch" }
}
& "$stageRoot/x64/FullProbe.exe" "$stageRoot/check-$([guid]::NewGuid())"
if ($LASTEXITCODE) { throw 'Full x64 probe failed' }
& $dotnet run --project Native/FullProbe/ViewLifetimeChecks/ViewLifetimeChecks.csproj -c Release "-p:ArtifactsPath=$stageRoot/build-views" -- "$stageRoot/view-$([guid]::NewGuid())"
```

## 证据与边界

| 路径 | 实际操作 | 证据边界 |
|---|---|---|
| 安卓 ABI 与数据 | 发布体展开原嵌入 JAR 并核 SHA-256，回写/重开手机与音频配置；真实 AdbSocketClient 对独占回环端口执行 SYNC RECV，检查字节、原子提交、取消和所有权 | 协议对端模拟安卓。TLS 另用真实 Windows SChannel 的 TLS 1.3 和 SPAKE2 完成回环双端交换，独立临时身份在结束时删除。真机 USB、无线调试、系统/蓝牙播放仍需设备。 |
| 确认后的文件任务 | 旧页仅挂/卸 Changed 观察，按持久 profile.Id 恢复进度。AndroidAudioHost.StartFileTransfer 核验当前设备，挂接管理令牌；AndroidFileTransfers 持有已开始的批次、逐项检查传输并处理用户取消/设备吊销/退出。主线程无需等待拷贝 | AOT 运行同一协调器；独立 JIT/WPF 夹具在同一条拿到确认的接收任务上真实关闭、重开设备页面所在 HWND，检查进行中和完成后的界面状态并核对接收内容。最终原生管理控件尚需接入。 |
| QRCoder / WIC | 生成模块矩阵及 PNG，经 WIC 解到 BGRA，校验尺寸/像素；另一条入口把真实手机 PNG 转 HICON 后销毁 | 未在本轮用真实手机相机扫描。 |
| Concentus / AAC-ELD | 使用产品 ScrcpyOpusReceiver 解一个 20 ms DTX/静音包，验证 960 帧共 1920 个采样值；两个 ELD 合成夹具各 16 个访问单元输出非静音 PCM | Opus 此向量检查时长和解码路径；实际内容、丢包和多端点时延另验。 |
| AirPlay | 真 AirPlayReceiver 在私有回环 socket 回答 RTSP OPTIONS；AAC-LC/ALAC 系统 MFT 解码独立生成音调；ALAC 每个访问单元验证长度、全部 24576 样本与原音调逐样本比较 | 协议握手和解码各自验过。真实 Apple 发送、加密媒体完整接收、同步和镜像属于上机验收。 |
| Windows 媒体、声音 | 真 Win32 视频 HWND、MF H.264 Baseline 解码成 64×64 NV12、D3D11 呈现并归还对象计数；在默认输出端点注册/注销通知，打开产品浮点 WASAPI 共享事件播放器，静音样本两次取数确认，停播、再启再停 | D3D11 使用实际驱动选路，样片强制走软件解码分支输送纹理。未据此认证 HEVC、硬件解码、真屏视觉品质、实际听音或所有端点。 |
| RID | 两个 PE 包分别核对 Machine、无 CLR 目录、原生依赖、SHA-256 及 AOT 发布诊断；本台机器 x64 实跑 | ARM64 交叉发布须另在 ARM64 Windows 实跑。当前执行机装有开发运行时；干净系统脱离运行时需另验证。 |

## 有界队列验收

| 路径 | 资源上限与慢端行为 | 实跑断言 |
|---|---|---|
| ADB 物理连接的发送队列 | FIFO 最多 32 项，另有一个正在写的包。每连接最多 8 个出站负载租约，先取得许可再复制，每包有效载荷最多取对端协商大小和 1 MiB 的较小值。每物理连接的 32 个逻辑流许可限制等待入队的生产者，CLSE 入队后才归还对应许可。单个流最多 4 个进行中或等待的调用。同步 Span 入口逐包取同一池，整次操作以 30 秒为阻塞截止。 | 私有链路暂停写端，队列达 32；第九个写等待租约。取消时仅关闭已取信用的受影响流，31 个其他 WRTE 的 ACK 保序。同一逻辑写的三段 Span 先于后续写，最大实际租借请求 65,536 B（本夹具协商值）。4 MiB 入站高水位降至 2 MiB 时，已消费数据的 ACK 随流的生命周期异步发送，取消本次读仍能取得下一段正确数据。销毁连接后所有出站租约归还。 |
| 虚拟屏输入控制 | 每显示会话 256 项 FIFO；单包最多 305 B，另有一次在途写。可合并的鼠标移动只更新尚未投递的队尾坐标。无法接纳关键转换时立即报告故障，使用同一会话停止逻辑取消写端、视频及控制通道；控制子流独立写失败也会通知会话。 | 阻塞发送时装满队列，下一条入队明确失败。另一显示会话继续按下→4000 次移动合为最后坐标→抬起顺序。原定向夹具另核对 64 次快速点击、拖动起止、取消/断开和关闭。 |

`stage/full/full-process-metrics.json` 记录独立进程对一次最终 x64 FullProbe 全路径运行的采样。私有字节取最大采样值，CPU 时间取整个子进程，工作集取运行期间观察到的 Windows 峰值计数。该次负载含样本传输、解码、设备操作和夹具；采样器属于验收工具。

## 样本来源与修正

`frame64.h264` 来自本仓库 `checks/AndroidAudioChecks/VideoStage2Tests.cs` 的 `BuildTestBaselineH264_64x64`，将其配置 NAL 和 IDR NAL 顺序连接。`aac.packets` 和 `alac.packets` 用本仓库 `checks/AndroidAudioChecks/airplay_simulator.py` 原有 `tone_packets` 函数与 PyAV 19.0.2 生成，44100 Hz 立体声、437 Hz 正弦、振幅 0.42。每个访问单元前置四字节小端长度。ELD 两组直接引用仓库夹具，JAR 来自现行产品资源。PyAV 仅用于制作样本。

首轮 x64 原生执行在 WasapiDeviceLease.Unique 命中不支持的 RCW，链接器也报告了 AirPlay 动态 COM 激活/NAudio MFT 警告。在隔离树中，Full 播放器/通知改走静态 ABI。Native/Audio/SharedAudioBuffers.cs 统一两个播放器的 IAudioClient 初始化、事件和 PCM 缓冲申请/归还；Full 保留原浮点来源、延迟候选、共享模式格式转换、事件等待和两次读确认。AAC/ALAC 的 IMFTransform 改用静态 ABI，保留格式协商、采样/缓冲复用和 flush。实测 ALAC MFT 可追加或替换调用方提供的样本缓冲；每次处理输出前重新附上已清空的原缓冲，避免重播前一帧。Android 的 manifest、设置、目录和剪贴板发送改用源生成为现有字段提供 metadata。

固定测试工作树 `stage/full` 中的发布、运行、PE 日志和收据记录本次实际结果；`baseline-interop-x64.log`、`run-before.log` 用于复核修正前证据。阶段 1 结论限于达到入口并实跑的路径，其他现存 Core/NAudio 调用及真机行为仍需要按后续阶段迁移和验收。

