param(
    [ValidateSet('lite','legacy')][string[]]$Variants = @('lite','legacy'),
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '../..')
)
$ErrorActionPreference = 'Stop'
$taskRepoRoot = [IO.Path]::GetFullPath($RepositoryRoot)
foreach ($taskVariant in $Variants) {
    if ($taskVariant -eq 'lite') {
        $taskOutput = Join-Path $taskRepoRoot 'dist/SonicRoute-v1.21-Lite-x64'
        & dotnet publish (Join-Path $taskRepoRoot 'SonicRoute/SonicRoute.csproj') -c Release -r win-x64 --self-contained false -p:PublishProfile=win-x64 "-p:PublishDir=$taskOutput/" --nologo -v minimal 2>&1 |
            Tee-Object -FilePath (Join-Path $PSScriptRoot 'build-lite.log')
    } else {
        $taskOutput = Join-Path $taskRepoRoot 'dist/SonicRoute-v1.21-Legacy-x64'
        & dotnet build (Join-Path $taskRepoRoot 'SonicRoute.Legacy/SonicRoute.Legacy.csproj') -c Release -o $taskOutput --nologo -v minimal 2>&1 |
            Tee-Object -FilePath (Join-Path $PSScriptRoot 'build-legacy.log')
    }
    if ($LASTEXITCODE -ne 0) { throw "Development build failed: $taskVariant" }
    $taskExecutable = Join-Path $taskOutput 'SonicRoute.exe'
    $taskVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($taskExecutable).ProductVersion
    if ($taskVersion -notmatch '^1\.21(?:\.|\+|$)') { throw "Unexpected executable version: $taskVersion" }
    Write-Output "Development build: $taskExecutable [$taskVersion]"
}
