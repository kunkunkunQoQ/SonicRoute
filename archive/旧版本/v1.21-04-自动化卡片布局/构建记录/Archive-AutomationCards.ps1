$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskEncoding = [Text.UTF8Encoding]::new($false)
$taskName = 'v1.21-04-自动化卡片布局'
$taskArchive = Join-Path $taskRoot ('SonicRoute源码/' + $taskName)
if (Test-Path -LiteralPath (Join-Path $taskArchive '留档清单.json')) { throw '保留已有完整备份。' }
$taskProjects = @(& rg --files -- SonicRoute SonicRoute.Core SonicRoute.Legacy)
if ($LASTEXITCODE -ne 0) { throw '无法列出项目源码。' }
$taskSources = @($taskProjects) + @('.gitignore', 'SonicRoute.sln', 'LICENSE', 'README.md', 'README.en.md',
    'docs/automation-command-line.md', 'docs/automation-convenience.md')
$taskSources = @($taskSources | Sort-Object -Unique)
$taskNote = @"

### 2026-10-08 v1.21 自动化卡片布局（阶段39，v1.21-04）

- 用户要求按参考图重排自动化界面，并明确图中“1”是真实规则名称。标题及说明左置，新建右置；搜索图标/占位提示、状态、触发方式、列表选项同一行；去掉包裹整个列表的卡片，每条规则独立圆角卡片。
- 规则行左侧手柄及动作矢量图标，规则名加粗独立显示，摘要展示动作/静音状态/音量/随机启动模式；中间触发胶囊展示类型及应用/时间/快捷键；右侧启用开关、运行/编辑/复制/删除/更多图标。长文字省略及全文提示，图标有提示和辅助名称；运行/编辑/悬停高亮。保留仅一次已执行状态点及快捷键冲突提示。
- 开关复用AutoToggle_Click/AutoRuleStore.Save，保存失败恢复先前开关状态；只停自动触发，不禁止手动运行。每操作的失败停止仍默认未勾选，规则执行/单飞/延时/调用链逻辑未改。
- 手柄支持真实规则排序：鼠标捕获、最小拖动距离、Dispatcher合并落点预览，同一落点跳过重复画刷/边框写入，松手一次更新顺序；Esc/失焦/离页/关闭/数据刷新取消。边缘滚动随拖动输入发生，无常驻计时器或Rendering订阅。筛选中移动可见项，不移动其他规则的相对顺序。
- 新增配置AutomationRuleOrder按Id保存展示顺序，缺字段/清空按规则名称；新增规则排在已保存顺序后。只改变UI，不改变触发执行顺序和步骤排序。列表选项可重置筛选、恢复名称排序、打开原脚本文件夹。新建/编辑后按需滚入编辑区。
- 新增MainWindow.AutomationLayout.cs与AutomationIcons.cs，替换旧文字按钮行，复用按Id行缓存、原执行/复制/编辑/删除/更多入口；Geometry一次解析冻结，动态主题资源共用，无新增依赖/逐行阴影/循环动画。保持现有主窗口导航与尺寸，两版共享同一源码。
- 九语言各新增4键，当前均423键；当前资源仍为Resources/Lang/*.json，未照历史翻译规范误改为L10n硬编码。双语README、开发功能指南、AI_CONTEXT及本地主规范/改动记录/留档同步。
- Lite x64单文件开发发布、Legacy x64开发构建、ARM64编译均0错误；保留既有可空性警告。没有添加或运行功能/界面/性能测试，未启动用户程序、未读取或改写实际规则/配置/音量；手工检查步骤见docs/automation-convenience.md。不能据编译成功宣称视觉及交互已验收或CPU/内存下降。
- 修改前基线v1.21-03-步骤失败策略与筛选布局及Git 077b6e2261fa85c3fa1b69c2fcbfe4161b997ecc保留；本阶段备份$taskName含$($taskProjects.Count)项目文件/$($taskSources.Count)完整源码文档、构建记录、开发程序、用户参考图及SHA256。版本仍1.21，未提交/上传GitHub、未发布，原.workbuddy-ai/及动画/保持。
- 开发程序：dist/SonicRoute-v1.21-Lite-x64-automation-cards/与dist/SonicRoute-v1.21-Legacy-x64-automation-cards/，Legacy需保留整目录；请退出旧实例后打开新版查看界面。
"@
foreach ($taskDocument in @('AI_CONTEXT.md', 'SonicRoute源码/README.md', 'SonicRoute源码/改动记录.md', 'SonicRoute源码/留档.txt')) {
    $taskPath = Join-Path $taskRoot $taskDocument
    # 私有主规范只在内存检查标记后追加，不输出或复制其中的签名信息。
    if (-not [IO.File]::ReadAllText($taskPath).Contains('2026-10-08 v1.21 自动化卡片布局')) {
        [IO.File]::AppendAllText($taskPath, "`r`n" + $taskNote + "`r`n", $taskEncoding)
    }
}
New-Item -ItemType Directory -Path $taskArchive -Force | Out-Null
foreach ($taskRelative in $taskSources) {
    $taskDestination = Join-Path $taskArchive ('源码快照/' + $taskRelative)
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot $taskRelative) -Destination $taskDestination
}
$taskArtifacts = @()
foreach ($taskBuild in @('SonicRoute-v1.21-Lite-x64-automation-cards', 'SonicRoute-v1.21-Legacy-x64-automation-cards')) {
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
foreach ($taskFile in @('publish-lite.log','build-legacy.log','build-arm64.log','artifact-metadata.json','Archive-AutomationCards.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskFile) -Destination (Join-Path $taskLogs $taskFile)
}
$taskReference = 'C:/Users/20905/AppData/Local/Temp/codex-clipboard-0a8d1fad-2bcb-4abd-abca-b2aa129aa255.png'
if (Test-Path -LiteralPath $taskReference) {
    Copy-Item -LiteralPath $taskReference -Destination (Join-Path $taskArchive '设计参考.png')
}
[IO.File]::WriteAllText((Join-Path $taskArchive 'README.md'), ("# $taskName`r`n" + $taskNote + "`r`n`r`n源码快照保留项目结构，不含bin/obj及用户数据。构建是未签名开发产物，ARM64仅编译。原始参考图供设计追溯；未执行运行时界面验证。SHA256清单不包含自身。本归档不含私有签名规范。`r`n"), $taskEncoding)
Copy-Item -LiteralPath (Join-Path $taskRoot 'AI_CONTEXT.md') -Destination (Join-Path $taskArchive 'AI_CONTEXT.md')
$taskFiles = foreach ($taskFile in Get-ChildItem -LiteralPath $taskArchive -Recurse -File) {
    [pscustomobject]@{Path=[IO.Path]::GetRelativePath($taskArchive, $taskFile.FullName).Replace('\','/');Bytes=$taskFile.Length;
        SHA256=(Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash}
}
$taskManifest = [ordered]@{Stage=39;Version='1.21';Date='2026-10-08';BaselineArchive='v1.21-03-步骤失败策略与筛选布局';
    BaselineCommit='077b6e2261fa85c3fa1b69c2fcbfe4161b997ecc';ProjectFiles=$taskProjects.Count;SourceFiles=$taskSources.Count;
    RuntimeTests='not run';Files=@($taskFiles | Sort-Object Path)}
[IO.File]::WriteAllText((Join-Path $taskArchive '留档清单.json'), ($taskManifest | ConvertTo-Json -Depth 6), $taskEncoding)
[pscustomobject]@{Archive=$taskArchive;SourceFiles=$taskSources.Count;ArchivedFiles=$taskFiles.Count+1} | ConvertTo-Json
