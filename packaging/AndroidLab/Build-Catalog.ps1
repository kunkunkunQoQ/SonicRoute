param([string]$ReferenceDirectory, [string]$JdkBinDirectory)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$labRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$reference = if ($ReferenceDirectory) { [IO.Path]::GetFullPath($ReferenceDirectory) } else { Join-Path $labRoot 'android-audio-test' }
$sdkFiles = @('tools\sdk\platforms\android-36\android.jar','tools\sdk\build-tools\36.0.0\lib\d8.jar')
$copied = @()
foreach ($relative in $sdkFiles) {
    $destination = Join-Path $reference $relative
    if (!(Test-Path -LiteralPath $destination)) { throw ('Missing read-only SDK input: ' + $destination) }
    $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    $copied += @{Path=$relative;SHA256=$hash}
}
$work = Join-Path $labRoot ('outputs\catalog-build-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
$classes = Join-Path $work 'classes'
$stubs = Join-Path $work 'stubs'
$dex = Join-Path $work 'dex'
$runtime = Join-Path $repositoryRoot 'AndroidRuntime'
New-Item -ItemType Directory -Path $classes,$stubs,$dex,$runtime -Force | Out-Null
$jdkBin = if ($JdkBinDirectory) { [IO.Path]::GetFullPath($JdkBinDirectory) } elseif ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin' } else { 'C:\Program Files\BellSoft\LibericaJDK-21\bin' }
$androidJar = Join-Path $reference $sdkFiles[0]
$stubRoot = Join-Path $repositoryRoot 'android-helper\compile-stubs'
$classpath = $androidJar
$d8Args = @('--min-api', '31', '--lib', $androidJar, '--output', $dex)
if (Test-Path -LiteralPath $stubRoot) {
    $stubSources = @(Get-ChildItem -LiteralPath $stubRoot -Filter '*.java' -File -Recurse | ForEach-Object FullName)
    if ($stubSources.Count -gt 0) {
        & (Join-Path $jdkBin 'javac.exe') '-J-Duser.language=en' '-J-Duser.country=US' --release 8 -encoding UTF-8 -cp $androidJar -d $stubs @stubSources
        if ($LASTEXITCODE -ne 0) { throw 'Catalogue stub compilation failed' }
        $stubJar = Join-Path $work 'compile-stubs.jar'
        & (Join-Path $jdkBin 'jar.exe') cf $stubJar -C $stubs .
        if ($LASTEXITCODE -ne 0) { throw 'Catalogue stub archive failed' }
        $classpath = $androidJar + [IO.Path]::PathSeparator + $stubs
        $d8Args = @('--min-api', '31', '--lib', $androidJar, '--classpath', $stubJar, '--output', $dex)
    }
}
$helperRoot = Join-Path $repositoryRoot 'android-helper'
$helperTopSources = @(Get-ChildItem -LiteralPath $helperRoot -Filter '*.java' -File | ForEach-Object FullName)
$helperSrcRoot = Join-Path $helperRoot 'src'
$helperPkgSources = if (Test-Path -LiteralPath $helperSrcRoot) {
    @(Get-ChildItem -LiteralPath $helperSrcRoot -Filter '*.java' -File -Recurse | ForEach-Object FullName)
} else {
    @()
}
$helperSources = @($helperTopSources + $helperPkgSources)
& (Join-Path $jdkBin 'javac.exe') '-J-Duser.language=en' '-J-Duser.country=US' --release 8 -encoding UTF-8 -cp $classpath -d $classes @helperSources
if ($LASTEXITCODE -ne 0) { throw 'Catalogue compilation failed' }
$helperClasses = @(Get-ChildItem -LiteralPath $classes -Filter '*.class' -File -Recurse | ForEach-Object FullName)
& (Join-Path $jdkBin 'java.exe') -cp (Join-Path $reference $sdkFiles[1]) com.android.tools.r8.D8 @d8Args @helperClasses
if ($LASTEXITCODE -ne 0) { throw 'Catalogue dex failed' }
& (Join-Path $jdkBin 'jar.exe') --create --date=2026-01-01T00:00:00Z --file (Join-Path $runtime 'sonicroute-catalog.jar') -C $dex classes.dex
if ($LASTEXITCODE -ne 0) { throw 'Catalogue archive failed' }
$copied | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'catalog-sdk-manifest.json') -Encoding utf8
