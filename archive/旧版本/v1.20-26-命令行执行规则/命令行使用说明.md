# 命令行直接执行自动化规则

适用于 v1.20 阶段 26 及后续构建的 Lite 和 Legacy。

## 基本用法

在 SonicRoute.exe 所在目录执行：

```powershell
.\SonicRoute.exe --run-rule "规则名称"
.\SonicRoute.exe --run-rule "规则ID"
.\SonicRoute.exe --help
```

参数使用规则的完整名称或 JSON 内的 `Id`，忽略大小写。Id 优先匹配，也支持 GUID 的带连字符形式。名称重复时拒绝执行，请改用 Id。名称含空格时必须加引号。一次调用一条规则，不能与 `--main` / `--panel` 混用。

这是直接执行入口，无需满足快捷键、应用、定时或启动条件；关闭自动触发的规则也可以执行。保留全部步骤、顺序和延时，失败后继续后续步骤，不修改规则的启用状态、快捷键或定时执行记录。

软件已经运行时，命令交给同一用户、同一登录会话、同一版本的现有实例；不会打开主窗口。Lite 与 Legacy 分别接收自己的命令。

软件未运行时，临时初始化音频与 OSD，执行完成后退出；不会启动托盘、快捷键、自动触发、定时调度或常驻监听，也不会顺带运行其他“SonicRoute 启动时”规则。最后一条 OSD 会按原显示时间和淡出完成后退出；麦克风常驻提示在此模式下临时显示，长期常驻需要正常启动软件。

同一规则正在执行时，重复请求返回忙碌，不排队、不重复运行。不同规则可并发。临时执行进程退出前等待已经接收的其他请求完成。

## 脚本中等待完成

SonicRoute 是 Windows GUI 程序，PowerShell 直接调用可能不会自动等待。需要退出码时使用：

```powershell
$process = Start-Process -FilePath '.\SonicRoute.exe' `
    -ArgumentList '--run-rule "规则名称"' -Wait -PassThru
$process.ExitCode
```

如需读取结果 JSON：

```powershell
$resultFile = Join-Path $env:TEMP 'SonicRoute-rule-result.json'
$process = Start-Process -FilePath '.\SonicRoute.exe' `
    -ArgumentList '--run-rule "规则名称"' -Wait -PassThru `
    -RedirectStandardOutput $resultFile
$result = Get-Content -LiteralPath $resultFile -Encoding UTF8 -Raw | ConvertFrom-Json
$result
$process.ExitCode
```

输出示例：

```json
{"ExitCode":0,"Code":"ok","RuleId":"示例ID","RuleName":"规则名称","Succeeded":2,"Failed":0}
```

输出的字段和 `Code` 是固定协议；`--help` 跟随软件语言。无控制台的快捷方式调用仍可执行，但不会弹出命令行窗口。

| 退出码 | Code | 含义 |
|---|---|---|
| 0 | ok | 全部步骤成功 |
| 1 | step_failed | 至少一个步骤失败，仍执行后续步骤 |
| 2 | invalid_arguments | 参数缺失、混用或格式错误 |
| 3 | rule_not_found | 未找到规则 |
| 4 | ambiguous_rule | 名称或 Id 匹配多条规则 |
| 5 | rule_busy | 同一规则正在执行 |
| 6 | server_unavailable | 现有实例无法连接、通道断开或无法取得结果 |
| 7 | canceled | 程序退出导致执行取消 |
| 8 | internal_error | 执行器内部异常 |

连接正在启动的实例最多等待 5 秒；规则执行没有固定超时，以支持长延时。通道失败不会自动重试或另起进程执行：断开时可能已有步骤执行，调用者应先确认结果再决定是否再次调用。旧版运行实例不支持此通道，需先正常退出旧实例并启动本构建。

规则中的启动程序 / PowerShell 沿用原语义：成功表示已提交启动，不等待所启动程序或脚本自身运行完毕。
