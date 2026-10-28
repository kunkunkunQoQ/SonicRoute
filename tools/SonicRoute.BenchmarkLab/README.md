# SonicRoute BenchmarkLab 1.2

独立的 Windows 进程测量与版本对比工具。保留 compare/ab、run、baseline、scenarios、verify 命令及原报告文件名；不向 SonicRoute 加入探针或后台服务。默认工具使用 .NET 8 x64，可以测量 NET10、NET8 和 Legacy 目标。

运行工具本身需要 .NET 8 x64 Runtime（Desktop Runtime 也包含所需运行时）；仅装 .NET 10 不会自动满足此要求。工具与目标软件的运行时需求分别检查。

## 1.2 新增

最终细化版：专项默认每侧 5 轮 × 30 秒、预热 5 秒；全量默认 7 轮 × 60 秒、预热 5 秒；轻量为 1 轮 × 3 秒、预热 2 秒。可用 --warmup / --rounds / --duration 覆盖并记录协议。历史基线默认 7 天有效且同名不可覆盖。AI 先读 [BENCHMARK_POLICY.md](BENCHMARK_POLICY.md) 固定计划，报告的推荐协议完成度与统计判定分别查看。

性能观察室新增可点击场景总览、协议完成度、逐轮 CPU 分布 / 中位数 / 极差 / CV，以及置信区间对照门限图；旧报告缺区间时明确显示缺失，单轮不作区间推断。

- `plan` 在运行前预览场景、轮次、顺序、桌面影响和预计时长，不启动目标。`--scenario idle_empty,target_absent` 按需选择多场景。
- 同一场景的 A/B 相邻测量，逐轮交换顺序；资源均值按实际采样时间加权，采样延迟可追查。统计区间额外保存结构化方法、标准误、自由度和门限。
- Schema 3 历史基线绑定有效时长、预热、间隔、场景与 generic 参数；旧基线可读，跨协议不判通过。SonicRoute 多文件包也复核相邻 DLL。
- **性能观察室**：打开 `Viewer/index.html` 导入数据。支持版本、场景、轮次筛选，CPU / 私有内存 / 工作集曲线，逐轮表格及采样质量；数据只在页面内处理。示例始终注明合成。
- compare 和 run 均生成自带数据的离线 `report.html`，无需联网或网页服务。`导出整理数据` 可保存并再次导入同一份完整数据。
- 测试规范、产物位置与清理规范分别见 [TESTING.md](TESTING.md)、[ARTIFACTS.md](ARTIFACTS.md)、[CLEANUP.md](CLEANUP.md)；升级设计见 [UPGRADE-v1.2.md](UPGRADE-v1.2.md)。
- **AI 执行规范**：[AI_TESTING.md](AI_TESTING.md) 是 AI 的选择与执行入口，包含产品各功能专项索引。工具验证支持 `-Mode Targeted|Light|Full`；性能测量支持 `--mode targeted|light|full`，两者分别留证。

## 本次修正

- CPU 保留原始累计时间，按实际窗口长度比较；总体 CPU 均值由端点差值/时长计算，初始无区间样本不参与瞬时百分位。GetProcessTimes 的 100ns 是表示单位，不保证同等精度。
- 原有 CPU 30ms 绝对保护值与 5% 相对阈值保留。非零变化低于绝对保护值标记 INCONCLUSIVE，不能解释成零开销或稳定通过。
- 至少三轮才建立重复性判断。A/B 使用交替轮次差值的描述性 t 区间，并检查区间是否跨越原门限；历史基线使用独立样本区间，不冒称配对。区间用于测量诊断，不证明代码因果关系。
- 完整报告保留每轮成功/失败、异常、时间序列、观察与负载证据。读取失败、早退、缺失场景、不可比基线不会变成 PASS；单目标失败不保存基线。
- 线程采样先 Refresh，停止时取消并等待采样任务再释放资源。可取消异步等待替代 Thread.Sleep，结束无需等满采样周期；Attach 的进程禁止通过 Kill 结束。Job 分配失败则结束自建目标并报错。
- 规则目标去掉错误的 .exe；进程负载使用唯一命名自建探针，存活超过一秒；执行确认读取实际 JSONL，长延时必须出现对应记录。修改规则保持 ID 并原子替换；热键负载采用四个不同绑定，定时负载具有有效且变化的未来计划。
- SonicRoute 场景通过独立命令管道确认就绪并核验服务端 PID。v1.24 及更早版本没有这个协议，不能确认时保留失败，不假报自动化场景成功。
- UI 场景验证目标进程实际出现原生主窗口，分别保留托盘/窗口样本阶段；原生窗口可见不等于 WPF 首帧、数据就绪或关闭后资源回收。
- HTML、AI 和 JSON 区分 PASS / REGRESSION / INCONCLUSIVE；JSON-only 不混入进度。检查参数及输出路径，已有报告目录不覆盖。
- 默认每 200ms 采样一次，可用 --interval 20..1000 调整；仅测量期间采样，结束即取消等待。相较旧默认 100ms，周期采样次数减半，不代表 CPU 开销必然减半；累计 CPU 仍由端点读取。
- 报告记录目标 EXE、托管入口及运行时配置的 SHA256，Legacy 同时记录本地 DLL；结束后复核，文件变化即失去可比性。单目标 run_metadata.json 保留测量协议，旧基线可读但不会冒充新协议的有效对照。

## 隔离构建与自检

```powershell
./tools/SonicRoute.BenchmarkLab/Invoke-IsolatedChecks.ps1 -Mode Targeted -Feature sampling,protocol
# 轻量：工具自检和纯数据检查，不默认启动浏览器 / 产品
./tools/SonicRoute.BenchmarkLab/Invoke-IsolatedChecks.ps1 -Mode Light
# 页面变化时才追加浏览器验证；需要已安装 Edge 与 Node 22+
./tools/SonicRoute.BenchmarkLab/Invoke-IsolatedChecks.ps1 -Mode Targeted -Feature reports,viewer-ui
# 全量：全部工具 / 网站检查 + 自建进程 A/A 流程冒烟
./tools/SonicRoute.BenchmarkLab/Invoke-IsolatedChecks.ps1 -Mode Full
# 显式实际目标冒烟与交付提取；默认不自动寻找或启动用户软件
./tools/SonicRoute.BenchmarkLab/Invoke-IsolatedChecks.ps1 -Scope All -Browser -Target E:/releases/current.exe -DeliveryName benchmarklab-v1.2-20261028
```

自检包含计算、报告、JSONL、规则身份及真实自建 Windows 子进程；实际软件检查需要显式 --target。

统一入口把构建与临时材料放进 `build-artifacts/benchmarklab/<RunId>`，结束清理并保留 `maintenance/plans/benchmarklab-runs/<RunId>/receipt.json`。直接使用已构建工具时，`verify --output <新目录>` 可保留自检报告；省略时自检临时材料在结束删除。不要在旧根或冻结输入目录中构建工具。

自动 SonicRoute 场景使用独立数据目录及实例身份，关闭自动更新、开机启动和托盘滚轮，避免操作用户音频。这里的空闲结果对应这套受控配置；需要测量托盘滚轮等功能时，应另建明确启用该功能的专项场景。

## 常用命令

先让机器尽量安静，用同一 EXE 做 A/A，再做 A/B。工具不会暂停系统任务或自动消除环境噪声；缓存、调度和后台负载仍需控制。

```powershell
# 先预览；不要求目标存在，也不启动或修改目标
SonicRoute.BenchmarkLab plan --mode targeted --feature idle,rules --aa --json
SonicRoute.BenchmarkLab plan --mode light --aa --json
SonicRoute.BenchmarkLab plan --mode full --aa --json
SonicRoute.BenchmarkLab plan --aa --scenario idle_empty,target_absent --suite quick --rounds 3 --duration 30 --json
# A/A 重复性对照；不能作为软件版本优化结论
SonicRoute.BenchmarkLab compare --aa --candidate E:/releases/current.exe --suite quick --rounds 3 --duration 30 --output E:/benchmarks/aa-new --json
# 双版本交替，后台0/10规则和目标不存在
SonicRoute.BenchmarkLab compare --baseline E:/releases/old.exe --candidate E:/releases/current.exe --suite quick --rounds 3 --duration 30 --output E:/benchmarks/ab-new --quiet
# 100条规则专项
SonicRoute.BenchmarkLab compare --baseline E:/releases/old.exe --candidate E:/releases/current.exe --scenario rules_scaling_100 --rounds 3 --duration 30 --json
# 保存历史基线
SonicRoute.BenchmarkLab run --target E:/releases/current.exe --suite quick --rounds 3 --duration 30 --save-baseline current-quick --output E:/benchmarks/baseline-new
# 通用进程，参数直接传给目标，不经 shell 解释
SonicRoute.BenchmarkLab run --target E:/tests/example.exe --scenario generic_process --args "--test-mode" --duration 10
# UI、前台、全局热键场景需显式允许，避免测试时干扰工作
SonicRoute.BenchmarkLab compare --baseline E:/releases/old.exe --candidate E:/releases/current.exe --suite standard --allow-desktop-interaction --rounds 3
SonicRoute.BenchmarkLab scenarios
SonicRoute.BenchmarkLab baseline list
SonicRoute.BenchmarkLab baseline show current-quick
SonicRoute.BenchmarkLab baseline delete current-quick
```

--duration 使用小数点，接受有限秒数，最低0.5秒；短窗口用于冒烟，不足以证明低负载性能。默认 CPU 5%、内存10% 门限不变。--rounds 必须为正整数；一轮可留证，但不能作为最终门禁。

## 场景范围

| 套件/场景 | 范围 |
| --- | --- |
| quick | idle_empty、rules_scaling_10、target_absent；后台场景 |
| standard | 另含1/100规则、进程负载、主窗口；需允许桌面交互 |
| automation / optimization | 进程、前台、长延时、规则保存和定时更新；部分需允许桌面交互 |
| endurance | 后台、100规则和资源趋势；短期斜率不等于证实泄漏 |
| generic_process | 通用进程；遵循目标自身启动行为，不承诺目标软件的数据或音频副作用隔离 |
| app_lifecycle_churn / long_delay_step | 自建负载并查实际执行；安全操作针对随机唯一的不存在应用 |
| rule_save_hotkeys / schedule_rule_churn | 修改规则并确认最新名称已载入；不等于已验证物理热键、冲突或定时到期 |
| fast_foreground_switch | 必须实际切换成功且记录规则执行；被系统拦截则失败 |
| window_tray_transitions | 目标 PID 的可见原生主窗口与两段资源；未测首帧或关窗回收 |

SonicRoute 场景使用独立数据与实例 ID，禁用更新、自启动和普通快捷键。仅明确选择交互场景时产生窗口/前台/规则热键负载。规则只尝试控制随机唯一的不存在应用，不写用户音量、默认设备或路由。全局键位和桌面活动仍受 Windows 及正在运行的软件影响。

## 报告与退出码

- report.json：来源、逐轮指标、样本、失败和负载证据；观察器 CPU 是工具全进程在测量期间的 CPU，包含负载生成，不是纯采样器开销。
- report.html：离线展示判定、原始 CPU/时长、门限与限制；不确定状态不显示绿色通过。
- ai-summary.md/json：快速摘要；完整波动与证据见 report.json。
- run_summary.json / run_evidence.json / run_metadata.json：单目标汇总、逐轮证据、协议与身份；同目录 report.html 内嵌这些数据。Schema 3 保存完整场景协议；旧基线仍可读，缺协议证据时不作为有效门禁。

| 码 | 含义 |
| --- | --- |
| 0 | 比较未检出超阈值退化；run 仅表示测量完成 |
| 1 | 检出有重复性证据的退化 |
| 2 | 参数、路径或输出目录错误 |
| 3 | 场景负载、启动、采样或执行失败 |
| 4 | 证据不足、波动或基线不可比 |
| 130 | 用户取消，清理自建进程 |

工作集峰值来自进程生命周期，包含预热；托管 GC、硬件功能、首帧及未执行场景不伪造结果。短测和工具自检不等于整版 SonicRoute 性能提升。

参考：[GetProcessTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes)、[WaitForInputIdle](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-waitforinputidle)、[Process.Refresh](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.refresh)。
