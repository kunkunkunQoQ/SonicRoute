# 隔离构建与产物位置规范

所有下列仓库相对路径以实际公开业务树为根。当前是 `E:/kunkun/SonarSwitch-public-v1.27-release`；不得以同名旧根 `E:/kunkun/SonarSwitch` 替代。

| 类型 | 位置 | 生命周期 |
| --- | --- | --- |
| 工具源码、网站源码、规范 | `tools/SonicRoute.BenchmarkLab/` | 跟随源码审阅 |
| 本轮临时构建 / obj / bin | `build-artifacts/benchmarklab/<RunId>/compiled/` | 验证和交付提取后删除 |
| 临时配置、音频隔离实例、探针 | `build-artifacts/benchmarklab/<RunId>/work/sandboxes/` | 场景 Dispose 清理，外层脚本再核查 |
| 自检临时伴随文件 | `build-artifacts/benchmarklab/<RunId>/work/verification/` | 仅复制必要报告留证后删除 |
| 浏览器临时配置 / 导入夹具 | `build-artifacts/benchmarklab/<RunId>/work/browser/` | 先退出自建浏览器，删除配置；外层清理其余 |
| 长期验证证据 | `maintenance/plans/benchmarklab-runs/<RunId>/` | 保留；不自动删除或覆盖 |
| 最终本机工具与网站 | `dist/tools/benchmarklab-<version>-<date>/` | 保留；不覆盖旧包 |
| 最终 ZIP | `dist/tools/SonicRoute.BenchmarkLab-v<version>-x64-<date>.zip` | 保留，与交付哈希对应 |
| 产品实验目标 | 原 `dist/experiments/<实验标识>/` | 只读输入；不由工具清理 |
| 正式版 / 商店包 / Gemini 冻结输入 | 原各自目录 | 只读保护，不自动删除、重建或覆盖 |

每个 RunId 为单段名称，包含日期和随机后缀；临时目录与证据目录均须全新。脚本在临时目录写入独占归属标记；未核验归属、绝对路径和父目录前不得递归清理。禁止把仓库根、驱动器根、用户目录、共享 dist 或整个临时目录作为删除目标。

## 统一入口

```powershell
# 按本次改动选择；自动隔离构建、留证和清理
./tools/SonicRoute.BenchmarkLab/Invoke-IsolatedChecks.ps1 -Scope Tool
./tools/SonicRoute.BenchmarkLab/Invoke-IsolatedChecks.ps1 -Scope Viewer -Browser
# 工具 + 网站改动；显式追加一个已知产品测试目标的隔离冒烟，并提取本机交付
./tools/SonicRoute.BenchmarkLab/Invoke-IsolatedChecks.ps1 -Scope All -Browser -Target E:/releases/current-test.exe -DeliveryName benchmarklab-v1.2-20261028
```

脚本将完整日志和 `receipt.json` 留在证据目录；清理失败会返回失败并记录残留的准确路径。`-Browser` 使用已安装 Edge 的独立无头配置，需 Node 22+；不使用个人浏览器配置。没有浏览器时保持数据检查，明确页面验证尚未执行，不伪造通过。

工具只需 .NET 8 SDK / x64 Runtime；产品工程另有 .NET 10 SDK 约束。脚本从临时目录调用独立工具构建，并用 `--artifacts-path` 把所有工具编译缓存放进本轮目录；不修改产品 `global.json`，也不在源码旁生成新的 bin/obj。

`SONICROUTE_BENCHMARK_TEMP_ROOT` 仅控制工具创建的临时目录父级。脚本只在自身进程中设定并在 finally 恢复；工具仍创建随机命名的直属子目录，不清理父目录。未设置时使用系统临时目录下的 `SonicRouteBenchmark`。

## 留存边界

证据至少包含构建 / 自检 / 数据与浏览器检查日志、协议预览、必要报告和截图、最终清理收据；交付时附逐文件 SHA256。不得把浏览器缓存、完整编译树、机器秘密或用户数据混入最终包。网站交付仅需 index.html、style.css、data.js、app.js；测试脚本留在源码中。

初次升级前已经存在的 bin/obj、历史证据和冻结工具不归新入口所有，不得为了整齐自动清理。删除历史材料必须另有明确授权和逐项范围。
