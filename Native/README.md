# SonicRoute 阶段 1 验证目标

1.37 的开发和 Full/Lite 构建从 [产品入口](Product/README.md) 开始。以下命令用于独立探针和基准验证。

阶段 1 以 `3d9f9e1` 为基准。探针使用 .NET 10 SDK、Windows SDK 与对应架构的 MSVC linker/CRT，Native AOT 编译器和运行时包统一为 10.0.15。桌面程序、WPF 对照夹具和 Native AOT 探针分别使用各自的入口。

| 入口 | 验证内容与资源归属 |
|---|---|
| Audio / AudioProbe | 设备、会话、通知、策略、进程回环和原生 PCM。音频库可被正式程序集直接引用，探针持有测试会话和实测断言。 |
| Graphics / GraphicsProbe | Win32/D2D/DWrite 独立资源和四个按需窗口。交互与私有桌面方式详见 [GraphicsProbe 说明](GraphicsProbe/README.md)。 |
| GraphicsProbe/Foreground/ForegroundProbe | 直接链接现有前台 WinEvent 监听。在私有桌面向测试 HWND 注入通知，验证安装、注销和重注册。 |
| Monitoring / HostProbe | MTA 原生 WMI 进程订阅、取消与状态故障；宿主按需调用。验证 Core 真实数据、规则文件通知、共享管道帧/结果、互斥量和更新计划。Store 模式只验证启动与身份。 |
| FullProbe | 引用本树 AndroidAudio 实现，运行本地 ADB、编解码、AirPlay/图形/声音样本。ViewLifetimeChecks 为开发机行为夹具，独立于发布体。 |
| UpdateBridgeCheck | 开发机上编译**未修改的 1.36 更新器源码**，与当前候选做真实文件事务。设置 BaselineSource 指向原始 1.36 检出。 |

## 独立发布

在 worktree 根 PowerShell 下执行。示例的 dotnet 应解析为 10.x SDK。本机使用用户目录下的 SDK。ARM64 需要相应的 VS 工具/CRT；脚本还支持指定经核验的本地 VS 包提取目录，不需修改系统 PATH。

~~~powershell
. ./Native/Set-Phase1Toolchain.ps1 x64 -VsRoot 'C:/Program Files/Microsoft Visual Studio/2022/Community'
$dotnet10 = Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet/dotnet.exe'
& $dotnet10 publish Native/HostProbe/HostProbe.csproj -c Release -r win-x64 -p:SonicRouteEdition=Full -p:IlcUseEnvironmentalTools=true -o ../repro/Full-x64
~~~

对 Lite 使用 `-p:SonicRouteEdition=Lite`。对 ARM64 同时改变脚本 Architecture 与 publish RID，使用独立输出目录。多个 RID/版型并行构建时，再给每次调用独立的绝对 `-p:ArtifactsPath=...`，避免 NuGet assets/投影互相覆盖。AudioProbe、FullProbe 和 GraphicsProbe 同样从各自项目发布。

HostProbe 便携模式使用 `--check <新的隔离数据目录> <真实1.36便携发布的SonicRoute-update.json>`。清单引用的 EXE 必须在其旁边。使用隔离临时数据、独立命名管道与短命子进程。探针实测不需要安装服务或驱动。

`Build-Phase1Package.ps1` 用 Full manifest 和 native HostProbe 创建并回读未签名 MSIX；开发注册时必须另用 `-TestIdentity` 产生隔离包布局。`Test-Phase1Package.ps1` 仅对新建的测试身份执行注册、激活和解除注册。正式包身份不参与安装，历史 Store Lite 身份继续保留。开发注册需要系统已有相应支持。

`Measure-Phase1Process.ps1` 只读采样指定 PID。GPU 结果用实例列表表示，空列表表示没有匹配的计数器实例。消息分发时延和真实音频/绘制延迟使用不同口径。
