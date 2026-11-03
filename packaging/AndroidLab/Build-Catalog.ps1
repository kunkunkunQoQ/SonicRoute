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
$dex = Join-Path $work 'dex'
$runtime = Join-Path $repositoryRoot 'AndroidRuntime'
New-Item -ItemType Directory -Path $classes,$dex,$runtime -Force | Out-Null
$jdkBin = if ($JdkBinDirectory) { [IO.Path]::GetFullPath($JdkBinDirectory) } elseif ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin' } else { 'C:\Program Files\BellSoft\LibericaJDK-21\bin' }
$androidJar = Join-Path $reference $sdkFiles[0]
$helperSources = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'android-helper') -Filter '*.java' -File | ForEach-Object FullName)
& (Join-Path $jdkBin 'javac.exe') '-J-Duser.language=en' '-J-Duser.country=US' --release 8 -encoding UTF-8 -cp $androidJar -d $classes @helperSources
if ($LASTEXITCODE -ne 0) { throw 'Catalogue compilation failed' }
$helperClasses = @(Get-ChildItem -LiteralPath $classes -Filter '*.class' -File -Recurse | ForEach-Object FullName)
& (Join-Path $jdkBin 'java.exe') -cp (Join-Path $reference $sdkFiles[1]) com.android.tools.r8.D8 --min-api 31 --lib $androidJar --output $dex @helperClasses
if ($LASTEXITCODE -ne 0) { throw 'Catalogue dex failed' }
& (Join-Path $jdkBin 'jar.exe') cf (Join-Path $runtime 'sonicroute-catalog.jar') -C $dex classes.dex
if ($LASTEXITCODE -ne 0) { throw 'Catalogue archive failed' }
foreach ($name in @('adb.exe','AdbWinApi.dll','AdbWinUsbApi.dll','scrcpy-server')) {
    Copy-Item -LiteralPath (Join-Path $reference ('dist\scrcpy-runtime\' + $name)) -Destination (Join-Path $runtime $name) -Force
}
Copy-Item -LiteralPath (Join-Path $reference 'tools\platform-tools\NOTICE.txt') -Destination (Join-Path $runtime 'ADB-NOTICE.txt') -Force
Copy-Item -LiteralPath (Join-Path $reference 'third_party\scrcpy-experimental\LICENSE') -Destination (Join-Path $runtime 'SCRCPY-LICENSE.txt') -Force
$copied | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'catalog-sdk-manifest.json') -Encoding utf8
