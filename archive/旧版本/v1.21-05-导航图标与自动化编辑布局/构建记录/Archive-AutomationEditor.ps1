$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskEncoding = [Text.UTF8Encoding]::new($false)
$taskName = 'v1.21-05-导航图标与自动化编辑布局'
$taskArchive = Join-Path $taskRoot ('SonicRoute源码/' + $taskName)
if (Test-Path -LiteralPath (Join-Path $taskArchive '留档清单.json')) { throw '保留已有完整备份。' }
$taskProjects = @(& rg --files -- SonicRoute SonicRoute.Core SonicRoute.Legacy)
if ($LASTEXITCODE -ne 0) { throw '无法列出源码。' }
$taskSources = @($taskProjects) + @('.gitignore', 'SonicRoute.sln', 'LICENSE', 'README.md', 'README.en.md',
    'docs/automation-command-line.md', 'docs/automation-convenience.md')
$taskSources = @($taskSources | Sort-Object -Unique)
$taskNote = @"
### 2026-10-08 v1.21 导航图标与自动化编辑布局（阶段40，v1.21-05）

- 用户要求给左侧增加图标，右侧按第二张参考图修改并尽量复用；补充有问题及时告知。保持1.21版本、现有主窗口与七项导航，实验设置显示条件不变。概览/应用/快捷键/主题/设置/自动化/实验设置共用NavItem模板与冻结Geometry图标，选中图标用主题强调色。
- 右侧编辑标题及说明、基本信息、操作分区带圆形图标及分隔线；规则名称输入框保留（参考图未画，但真实规则仍需要名称）。快捷键、应用、定时等六类触发沿用原控件及事件。
- 新建/编辑时AutoListPanel隐藏，编辑区独占右侧并滚至顶部；保存/取消恢复列表及先前滚动位置，筛选状态和规则行缓存保留。隐藏编辑区统一终止规则/步骤拖动及快捷键录制。
- MainWindow.AutomationEditor.cs封装步骤卡片：点状手柄、动作图标与原分类下拉同排，复制图标、红色删除按钮，内层参数、延时及每步失败停止（默认不勾选）。复用原步骤拖动、复制/删除、数值验证、字段写回及保存入口，执行引擎和持久化格式未改。
- OSD主副标题同排弹性宽度；启动模式/列表/逐项打开方式保留，列表不再固定520px，路径栏增加文件图标。AutoAppItemTemplate共用于应用/打开方式下拉，无图标时共用矢量占位，不新增Shell枚举入口。
- 切换操作通过步骤视图引用只替换该卡片参数，旧应用/设备下拉从慢刷新集合移除，修复无效控件残留。四类分类颜色×深浅模式画刷按需缓存、冻结，回到自动化页更新配色不重建编辑器；矢量图标Geometry继续共享，无新增依赖/常驻计时器/循环动画/逐卡阴影。实际CPU/内存变化未测量。
- 九语言新增5键，当前均428键；双语README、开发指南、AI_CONTEXT及本地规范/改动记录/留档同步。历史阶段39所述编辑区滚入视图方案在当前阶段替换为独占编辑区，原阶段记录和备份保留。
- Lite x64单文件开发发布、Legacy x64构建、ARM64编译均成功0错误；Legacy45、ARM649个既有可空性警告。没有添加或运行界面/功能/性能测试，未启动或退出用户进程，未修改真实规则/配置/音量。手工检查建议见docs/automation-convenience.md，不能据编译成功宣称视觉或性能已验收。
- 修改前基线SonicRoute源码/v1.21-04-自动化卡片布局保留；当前Git基线仍077b6e2261fa85c3fa1b69c2fcbfe4161b997ecc。本阶段备份$($taskProjects.Count)项目文件/$($taskSources.Count)完整源码文档、开发程序、构建日志、参考图和SHA256；不含私有签名规范及用户数据。未上传GitHub、未发布，.workbuddy-ai/和动画/保持。
- 当前开发程序dist/SonicRoute-v1.21-Lite-x64-automation-editor/和dist/SonicRoute-v1.21-Legacy-x64-automation-editor/，Legacy保留整目录。用户检查新版前需退出旧实例，避免单实例入口激活旧程序。
"@
foreach ($taskDocument in @('AI_CONTEXT.md', 'SonicRoute源码/README.md', 'SonicRoute源码/改动记录.md', 'SonicRoute源码/留档.txt')) {
    $taskPath = Join-Path $taskRoot $taskDocument
    # 私有主规范只检查标记后追加，不输出、不复制原内容。
    if (-not [IO.File]::ReadAllText($taskPath).Contains('2026-10-08 v1.21 导航图标与自动化编辑布局')) {
        [IO.File]::AppendAllText($taskPath, "`r`n`r`n" + $taskNote + "`r`n", $taskEncoding)
    }
}
New-Item -ItemType Directory -Path $taskArchive -Force | Out-Null
foreach ($taskRelative in $taskSources) {
    $taskDestination = Join-Path $taskArchive ('源码快照/' + $taskRelative)
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot $taskRelative) -Destination $taskDestination
}
$taskArtifacts = @()
foreach ($taskBuild in @('SonicRoute-v1.21-Lite-x64-automation-editor', 'SonicRoute-v1.21-Legacy-x64-automation-editor')) {
    $taskTarget = Join-Path $taskArchive ('本地构建/' + $taskBuild)
    New-Item -ItemType Directory -Path $taskTarget -Force | Out-Null
    foreach ($taskFile in Get-ChildItem -LiteralPath (Join-Path $taskRoot ('dist/' + $taskBuild)) -File) {
        Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskTarget $taskFile.Name)
        $taskArtifacts += [pscustomobject]@{Path='dist/' + $taskBuild + '/' + $taskFile.Name;Bytes=$taskFile.Length;
            Version=$taskFile.VersionInfo.FileVersion;SHA256=(Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash}
    }
}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'artifact-metadata.json'), ($taskArtifacts | ConvertTo-Json -Depth 4), $taskEncoding)
$taskLogs = Join-Path $taskArchive '构建记录'
New-Item -ItemType Directory -Path $taskLogs -Force | Out-Null
foreach ($taskFile in @('publish-lite.log','build-legacy.log','build-arm64.log','artifact-metadata.json','Archive-AutomationEditor.ps1','language-additions.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskFile) -Destination (Join-Path $taskLogs $taskFile)
}
$taskReference = 'C:/Users/20905/AppData/Local/Temp/codex-clipboard-4ca57def-52fd-4034-89f7-5944e3d40009.png'
if (Test-Path -LiteralPath $taskReference) { Copy-Item -LiteralPath $taskReference -Destination (Join-Path $taskArchive '设计参考.png') }
[IO.File]::WriteAllText((Join-Path $taskArchive 'README.md'), ("# $taskName`r`n`r`n" + $taskNote + "`r`n`r`nSHA256清单不包含自身；源码不含bin/obj，程序为本地开发产物，尚未运行界面验证。`r`n"), $taskEncoding)
Copy-Item -LiteralPath (Join-Path $taskRoot 'AI_CONTEXT.md') -Destination (Join-Path $taskArchive 'AI_CONTEXT.md')
$taskFiles = foreach ($taskFile in Get-ChildItem -LiteralPath $taskArchive -Recurse -File) {
    [pscustomobject]@{Path=[IO.Path]::GetRelativePath($taskArchive, $taskFile.FullName).Replace('\','/');Bytes=$taskFile.Length;
        SHA256=(Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash}
}
$taskManifest = [ordered]@{Stage=40;Version='1.21';Date='2026-10-08';BaselineArchive='v1.21-04-自动化卡片布局';
    BaselineCommit='077b6e2261fa85c3fa1b69c2fcbfe4161b997ecc';ProjectFiles=$taskProjects.Count;SourceFiles=$taskSources.Count;
    RuntimeTests='not run';Files=@($taskFiles | Sort-Object Path)}
[IO.File]::WriteAllText((Join-Path $taskArchive '留档清单.json'), ($taskManifest | ConvertTo-Json -Depth 6), $taskEncoding)
[pscustomobject]@{Archive=$taskArchive;SourceFiles=$taskSources.Count;ArchivedFiles=$taskFiles.Count+1} | ConvertTo-Json
