# 后台候选与冻结 1.36 的测量

在目标 Windows 的 64 位 PowerShell 7 中运行。WPF 测量取 1.36 冻结发布的 x64 Full/Lite，脚本先验证发布 SHA-256，再复制到新建的独立实例目录。两者都使用 IdleProfile.json，托盘启动、禁用自动启动修复与自动检查、空规则、Hotkeys={} 继承原有默认键盘与鼠标快捷键、暂停托盘滚轮、使用 last 模式。此配置仍允许原产品依配置监测麦克风变化。Full 没有手机配置。脚本只在子进程环境中设置根目录、实例标识和启动钩子。基线无更新标记和历史事务、不启用监听端口，旧版短命令注册遇自定义根目录直接返回。候选配置和安装路径由 CandidateEnvironment 派生，无需包注册。

~~~powershell
& $dotnet10 build Native/Measurements/WpfSnapshot/WpfSnapshot.csproj -c Release -o $tools
Native/Measurements/Measure-Product.ps1 -Mode Wpf -Edition Full -Executable $frozenExe -WpfHook "$tools/WpfSnapshot.dll" -OutputDirectory $newWpfRun
Native/Measurements/Measure-Product.ps1 -Mode Native -Edition Full -Executable $candidate -OutputDirectory $newNativeRun
~~~

附加的 Measure-Private.ps1 使用同一隔离流程，在未切换的私有桌面运行。其 Idle-UnboundProfile.json 为默认动作逐个指定空字符串，解除对应快捷键并保持托盘滚轮暂停，供区分交互桌面和输入监听所产生的活动。私有桌面可能没有 Explorer 通知区域，实托盘验证以互动桌面门禁为准。

两边启动真实产品并等待隐藏窗口，预热采样、静置 4 秒后开始 8 秒观察。旧版启动钩子只加载在允许的发布文件副本上，复用该 PID 的专属激活 HWND，按请求读取 GC 和 WPF 消息计数，结束时让同一副本通过 Application.Shutdown 正常退出。原生版通过其已有产品 CLI 读取 status 并 exit。没有主动触发 GC。初次运行使用的测量宿主已安装 SDK，工作集包含本机共享页面行为。WPF 旧版测试附带一个测量程序集；原生版模块列表来自原始 AOT EXE 自身。

PSAPI 读取 PrivateUsage、PagefileUsage、PrivateWorkingSetSize 和 SharedCommitUsage；前两者都是进程私有提交，不能累加。PDH 双快照收集 Thread 原始上下文切换计数，并分别记录只在任一边出现的线程。该数字是可观察的调度活动，无法区分抢占、主动阻塞和硬件唤醒。GC Heap 分别报告当前估计与上次 GC 结束值，分配速率包含两次采样协议在区间中的开销；额外一次紧邻采样给出参考开销。尚未 GC 时 LastGcHeap 可为 0。WPF 消息统计仅计算 ComponentDispatcher.ThreadFilterMessage 可见的消息，原生计数来自 HWND，两者覆盖范围不同。完整模块名与 GPU 实例数组随每次采样写入原始 JSON。GPU 空数组代表没有该 PID 的实例。Windows API 定义见 [进程内存统计](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex2) 和 [GDI/USER 对象](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getguiresources)。
