param([Parameter(Mandatory)][string]$DotnetPath, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$lab = Split-Path -Parent $repo
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (!$output.StartsWith($lab + '\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)) {
    throw 'Choose a new fixture directory inside the Lab.'
}
New-Item -ItemType Directory -Path $output | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $lab 'outputs/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $lab 'outputs/nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Join-Path $repo 'SonicRoute/SonicRoute.csproj'
$results = [Collections.Generic.List[string]]::new()
function Check([bool]$Success,[string]$Message) {
    if (!$Success) { throw ('FAIL ' + $Message) }
    $results.Add($Message)
    Write-Output ('PASS ' + $Message)
}
$log = Join-Path $output 'invalid-edition.log'
& $DotnetPath msbuild $project -t:ValidateSonicRouteEdition -p:SonicRouteEdition=Unknown -p:TargetFramework=net10.0-windows *> $log
Check ($LASTEXITCODE -ne 0 -and (Get-Content -LiteralPath $log -Raw).Contains('SonicRouteEdition must be Full or Lite.')) 'unknown editions fail before compilation'

$publish = Join-Path $output 'mixed-publish'
New-Item -ItemType Directory -Path (Join-Path $publish 'AndroidRuntime') | Out-Null
$owned = Join-Path $publish 'AndroidRuntime/fixture.txt'
'Keep this fixture' | Set-Content -LiteralPath $owned
$before = (Get-FileHash -LiteralPath $owned).Hash
$log = Join-Path $output 'mixed-publish.log'
& $DotnetPath msbuild $project -t:RejectPhoneFilesInLiteOutput -p:SonicRouteEdition=Lite -p:TargetFramework=net10.0-windows ('-p:PublishDir=' + $publish + '\') *> $log
Check ($LASTEXITCODE -ne 0 -and (Get-Content -LiteralPath $log -Raw).Contains('Lite output contains phone files') -and
    (Get-FileHash -LiteralPath $owned).Hash -ceq $before) 'Lite rejects a phone-containing output without deleting existing files'

# Both ends of this link are isolated fixtures in the authorized Lab. Leave it as test evidence.
$target = Join-Path $output 'junction-target'
New-Item -ItemType Directory -Path $target | Out-Null
$link = Join-Path $output 'linked-build-root'
New-Item -ItemType Junction -Path $link -Target $target | Out-Null
$rejected = $false
try { & (Join-Path $repo 'packaging/Build-Edition.ps1') -Edition Lite -OutputRoot $link }
catch { $rejected = $_.Exception.Message -eq 'Edition output paths cannot contain links or junctions.' }
Check ($rejected -and @(Get-ChildItem -LiteralPath $target -Force).Count -eq 0) 'linked output roots are rejected before any build or output write'

$rejected = $false
try { & (Join-Path $repo 'packaging/Build-Edition.ps1') -Edition Lite -OutputRoot (Split-Path -Parent $lab) }
catch { $rejected = $_.Exception.Message -eq 'Edition artifacts must stay inside the Lab.' }
Check $rejected 'output roots outside the authorized workspace are rejected'

[ordered]@{Created=(Get-Date -Format o);Assertions=$results.Count;Results=$results;DeviceAccess=$false;
    ProductLaunched=$false;JunctionFixture=$link;JunctionTarget=$target} |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
