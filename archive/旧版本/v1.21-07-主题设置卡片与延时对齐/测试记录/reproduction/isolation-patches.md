# 性能采样副本隔离核对

只读核对；未构建或运行。未读取或输出实际用户配置。

## Candidate 与当前生产源码

对 SonicRoute、SonicRoute.Core、SonicRoute.Legacy 使用 `rg --files` 取得相对路径清单，并逐文件比较 SHA-256。当前生产源码和 `candidate-harness/source` 都是 86 个文件；路径集合相同。候选与当前生产源码仅以下 6 个文件哈希不同，其余逐文件相同：

- `SonicRoute/App.xaml.cs`：增加 `SONICROUTE_PERF_ISOLATED=1` 时在 `base.OnStartup(e)` 之前返回。
- `SonicRoute.Core/ConfigService.cs`：隔离时把配置路径指向应用目录下 `test-data/config.json`。
- `SonicRoute.Core/AutoRuleStore.cs`：隔离时把规则目录指向 `test-data/Automation`。
- `SonicRoute/L10n.cs`：隔离时把外置语言目录指向 `test-data/Lang`。
- `SonicRoute/AutoRuleScheduler.cs`：隔离时把日志路径指向 `test-data/scheduler.log`。
- `SonicRoute/RuleCommandLine.cs`：隔离时把短命令目录指向 `test-data/Command`。

这些路径均仅在隔离环境变量为 `1` 时改向；未设置时仍走 LocalAppData。`AutoRuleService.cs` 与当前生产源码哈希一致，且不直接使用 LocalAppData 路径；规则与日志访问经 `AutoRuleStore` / `AutoRuleScheduler`。

## Baseline 来源

`v1.20^{commit}` 解析为 `de1de5a1b9003071bd67a1934f038e1965fdebbd`。该提交三个项目文件的版本均为 1.20。Baseline 的 74 个项目路径与该 Git 提交下三个项目的路径清单一致。逐路径比较 Git blob 后，68 个文件完全一致；仅以下 6 个文件不同，且差异均为测试隔离补丁：

- `SonicRoute/App.xaml.cs`：环境变量 `SONICROUTE_PERF_ISOLATED=1` 时，在 `base.OnStartup(e)` 前提前返回。
- `SonicRoute.Core/ConfigService.cs`：配置路径改到应用目录下的 `test-data/config.json`。
- `SonicRoute.Core/AutoRuleStore.cs`：规则目录改到应用目录下的 `test-data/Automation`。
- `SonicRoute/L10n.cs`：外置语言目录改到应用目录下的 `test-data/Lang`。
- `SonicRoute/AutoRuleScheduler.cs`：调度日志路径改到应用目录下的 `test-data/scheduler.log`。
- `SonicRoute/RuleCommandLine.cs`：短命令目录改到应用目录下的 `test-data/Command`。

对上述 6 个文件逐行核对后，未发现隔离补丁以外的内容差异。因此 baseline 生产源码可确认来自 `v1.20`，其唯一区别为以上隔离保护与路径重定向。

Baseline 的配置、规则、外置语言、短命令和调度日志路径在隔离副本中硬编码到应用目录下 `test-data`。Candidate 则以环境变量条件选择隔离路径，以便非隔离时保留生产路径。

## 测试启动与测量边界

两个生产 `SonicRoute.csproj` 的 `OutputType` 均为 `WinExe`；性能 driver 项目为 `Exe`。批次脚本以 `dotnet exec` 启动 `PerfReview.dll`，并传入生产输出的 `SonicRoute.deps.json` 和 `SonicRoute.runtimeconfig.json`。

Driver 在创建窗口前设置隔离变量、注入内存中的配置缓存和空/合成规则缓存，再调用 `App.InitializeComponent()`，手动设置语言与主题并直接创建、显示 `MainWindow`。隔离保护在 `App.OnStartup` 内、`base.OnStartup` 之前返回，所以资源数据代表 UI 负载，不包含完整生产启动负载。它跳过 WPF `Startup` / `StartupUri` 启动路径，以及生产代码中的命令行解析、单实例互斥和激活窗口、读取/迁移真实配置与首次语言写入、短命令登记、自启修复、托盘与系统偏好监听、热键、自动化调度/启动规则、托盘滚轮、麦克风轮询、前台监听、音频预热、命令服务和空闲回收定时器。Driver 手动提供主题资源与窗口，并用自己的 Dispatcher 采样。

因此该数据不应解释为生产进程完整后台服务的 CPU / 私有内存占用。报告仅核验源文件和启动边界，没有触碰实际 LocalAppData 配置或规则文件。
