# SonicRoute Benchmark Lab (性能基准测试与版本对比实验室)

**SonicRoute Benchmark Lab** 是一款专为 Windows 桌面软件（优先针对 SonicRoute）设计的独立、可复用、可扩展的专业性能基准测试与 A/B 版本对比工具。

支持被 Codex、Gemini 等 AI 编程代理直接调用，通过简洁的命令行接口完成高精度的性能测量、版本对比、性能回归检测，并**以极少的 Token 消耗（零外部 API、纯本地生成）直接获取结构化性能结论**。

---

## 🌟 核心设计与特性

1. **通用进程性能监控 (`ProcessMonitor`)**：
   - 基于原生 Win32 APIs (`GetProcessTimes`, `GetProcessMemoryInfo`, `GetProcessIoCounters`, `GetProcessHandleCount`)，监控工具自身 CPU 开销极低（实测 `< 0.05%`，自动校准记录）。
   - **严格区分累计 CPU 时间与瞬时 CPU 使用率**：累计 CPU 时间作为单调递增的基准真值，避免采样抖动和核心数换算误判。
   - 采集工作集（Working Set）、私有内存（Private Bytes）、内存峰值、线程数、句柄数、I/O 读写统计、进程启动耗时（毫秒级）。
   - 不支持直接获取托管 GC 堆指标时明确标注为未测量，严禁伪造数据。

2. **进程安全与完全沙盒隔离**：
   - **Windows Job Object 自动管理**：使用 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`，测试进程意外退出或测试工具被终止时，Windows 内核自动清理所有测试子进程，绝无僵尸进程残留。
   - **隔离环境与双环境变量约束**：使用独立的临时目录 `SONICROUTE_DATA_DIRECTORY` 与独立的实例标识 `SONICROUTE_INSTANCE_ID`，测试实例拥有完全独立的配置、规则目录、互斥体（Mutex）与命名管道，**绝不影响、不修改用户真实的音频设备、系统音量、运行中实例、生产配置或全局快捷键**。
   - **绝不误杀无关进程**：通过精准的进程句柄与创建时间戳绑定，防范 PID 复用。

3. **双层报告机制与零 Token 极简 AI 总结**：
   - **第一级（极简 AI 报告）**：自动生成 `ai-summary.md` 与 `ai-summary.json`，字符数严格控制在约 300–500 字，自动合并微小波动指标，置顶突出超阈值退化项，输出结论、退化定位与详细报告路径。零外部 API，100% 本地统计规则驱动。
   - **第二级（完整证据报告）**：自动生成 `report.json`（完整结构化数据与每轮统计）与 `report.html`（现代化自包含可视化图表与各场景指标比对表）。

4. **自动化多轮交替 A/B 对比 (`ComparisonRunner`)**：
   - 支持双版本奇偶轮次交替运行（例如第 1 轮 A→B，第 2 轮 B→A，第 3 轮 A→B），消除系统散热降频、后台任务波动与冷启动缓存影响。
   - 噪声抑制机制：设置绝对时间/内存底噪阈值（如 CPU 时间 `< 30ms`），微小波动不被错误宣称为优化或退化。

5. **历史基线存储与复用 (`BaselineStore`)**：
   - 能够将基准版本测试结果持久化为命名基线（如 `v1.25-baseline`），后续开发测试可直接与历史基线对比，无需重复运行旧版二进制。
   - 环境兼容性校验：与历史基线对比前自动比对 CPU 核心数、操作系统与架构，不兼容时主动告警。

---

## 🛠️ 命令参考

### 1. 快速验收自检 (`verify`)
自动化运行内置的 6 项核心验收标准测试（正常报告、CPU 退化定位、内存退化定位、噪声抑制、Token 控制、真实 SonicRoute 采集）：
```powershell
dotnet run --project tools/SonicRoute.BenchmarkLab -c Release -- verify
```

### 2. A/B 版本对比 (`compare` 或 `ab`)
对比两个可执行文件，或将候选版本与已保存的历史基线对比：
```powershell
# 对比两个 EXE（执行 3 轮交替测试，输出 AI 极简摘要）
dotnet run --project tools/SonicRoute.BenchmarkLab -c Release -- compare `
  --baseline "dist/experiments/v1.24/SonicRoute.exe" `
  --candidate "SonicRoute/bin/Release/net8.0-windows/win-x64/SonicRoute.exe" `
  --suite standard `
  --rounds 3 `
  --quiet

# 对比已保存的历史基线
dotnet run --project tools/SonicRoute.BenchmarkLab -c Release -- compare `
  --baseline v1.25-quick-baseline `
  --candidate "SonicRoute/bin/Release/net8.0-windows/win-x64/SonicRoute.exe" `
  --suite quick `
  --rounds 2 `
  --quiet
```

**核心选项**：
- `--baseline <path|name>`: 基线 EXE 路径或已存储基线名称（必填）。
- `--candidate <path>`: 待测候选版本 EXE 路径（必填）。
- `--suite <quick|standard|endurance>`: 测试等级预设（默认 `standard`）。
- `--scenario <id>`: 仅运行单个指定场景（如 `idle_empty`）。
- `--module <all|automation|hotkey|ui|general>`: 按受影响模块过滤场景。
- `--rounds <n>`: 交替测试轮次（默认 `3`）。
- `--duration <seconds>`: 自定义每阶段测量秒数。
- `--threshold-cpu <percent>`: CPU 退化报警阈值百分比（默认 `5.0%`）。
- `--threshold-mem <percent>`: 内存退化报警阈值百分比（默认 `10.0%`）。
- `--output <dir>`: 报告输出目录（默认 `./benchmarks/<run_id>/`）。
- `--quiet, -q`: 安静模式，控制台仅输出面向 AI 的极简摘要。
- `--json`: 控制台仅输出 `ai-summary.json` 内容。

### 3. 单目标基准测量与保存基线 (`run`)
运行测试套件并可选择保存为基线：
```powershell
dotnet run --project tools/SonicRoute.BenchmarkLab -c Release -- run `
  --target "SonicRoute/bin/Release/net8.0-windows/win-x64/SonicRoute.exe" `
  --suite quick `
  --rounds 2 `
  --save-baseline v1.25-quick-baseline
```

### 4. 历史基线管理 (`baseline`)
```powershell
# 列出所有存储的基线
dotnet run --project tools/SonicRoute.BenchmarkLab -c Release -- baseline list

# 查看指定基线详情
dotnet run --project tools/SonicRoute.BenchmarkLab -c Release -- baseline show v1.25-quick-baseline

# 删除指定基线
dotnet run --project tools/SonicRoute.BenchmarkLab -c Release -- baseline delete v1.25-quick-baseline
```

### 5. 列出支持的场景 (`scenarios`)
```powershell
dotnet run --project tools/SonicRoute.BenchmarkLab -c Release -- scenarios
```

---

## 📋 预置测试场景清单

| 场景 ID | 归属模块 | 默认时长(标配) | 测量目标与模拟负载 |
| :--- | :--- | :--- | :--- |
| `idle_empty` | `general` | 10.0s | 无自动化规则时的后台常驻托盘闲置资源消耗。 |
| `rules_scaling_1` | `automation` | 10.0s | 载入 1 条自动化规则时的资源变化与调度开销。 |
| `rules_scaling_10` | `automation` | 10.0s | 载入 10 条自动化规则时的内存增长与调度开销。 |
| `rules_scaling_100`| `automation` | 10.0s | 载入 100 条规则时内存线性扩张与文件索引效率。 |
| `target_absent` | `automation` | 10.0s | 规则目标进程长期不存在时的后台轮询/WMI 闲时消耗。 |
| `app_lifecycle_churn`| `automation` | 10.0s | 外部进程频繁启动与退出（高吞吐进程事件）时的调度性能。 |
| `fast_foreground_switch`| `automation` | 10.0s | 快速切换前台应用时的窗口事件追踪与匹配开销。 |
| `long_delay_step` | `automation` | 10.0s | 包含长延时执行步骤（如 5000ms 计时器）时的挂起稳定性。 |
| `rule_save_hotkeys`| `hotkey` | 10.0s | 大量规则动态写入与重新加载时的热键注销/重绑性能。 |
| `window_tray_transitions`| `ui` | 10.0s | 托盘常驻状态与主界面打开（`--main`）之间的资源阶跃变化。 |
| `endurance_leak_trend`| `general` | 25.0s | 长时间运行下的句柄、线程及私有内存斜率（泄漏趋势分析）。 |
| `generic_process` | `general` | 10.0s | 通用进程基准测试，可适用于任何其他 Windows 应用程序。 |

---

## 🚦 进程退出码规范

| 退出码 | 状态 | 说明 |
| :---: | :--- | :--- |
| `0` | **PASS** | 性能测试通过，所有关键指标变化均在预设阈值以内。 |
| `1` | **REGRESSION** | 检测到性能显著退化（某项指标超出设定退化阈值）。 |
| `2` | **INVALID_ARGUMENTS** | 命令行参数格式错误、目标文件不存在或配置异常。 |
| `3` | **EXECUTION_ERROR** | 目标进程在测试期间异常崩溃、发生超时或环境中断。 |

---

## 🤖 面向 AI 的输出样例 (`ai-summary.md`)

```markdown
### SonicRoute 性能对比结论: 🟢 PASS (无退化)

- **对比目标**: `v1.25-quick-baseline (1.25.0.0)` ➔ `SonicRoute.exe` (2 轮交替)
- **宿主环境**: 13th Gen Intel(R) Core(TM) i7-13620H (16 逻辑核) | Microsoft Windows 10.0.26300

#### 📊 稳定指标概要:
- 18 项常规指标（CPU时间、工作集、私有内存、句柄等）在预设噪声容限内保持平稳

#### ℹ️ 测试限制与说明:
- Executed 2 round(s); 3+ rounds recommended for production regression gating to minimize background OS noise.

#### 📁 完整报告与证据:
- 结构化数据: `file:///E:/kunkun/SonarSwitch/benchmarks/compare_20261021_105137/report.json`
- 可视化图表: `file:///E:/kunkun/SonarSwitch/benchmarks/compare_20261021_105137/report.html`
```
