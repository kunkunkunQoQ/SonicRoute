param([string]$RepositoryRoot = (Join-Path $PSScriptRoot '../..'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$exclude = (& git -C $root rev-parse --git-path info/exclude | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or !$exclude) { throw 'Cannot locate checkout-local Git exclusions.' }
if (![IO.Path]::IsPathRooted($exclude)) { $exclude = Join-Path $root $exclude }
$existing = if (Test-Path -LiteralPath $exclude) { [IO.File]::ReadAllText($exclude) } else { '' }
$lines = @($existing -split '\r?\n')
# The repository intentionally has no tracked .gitignore. Exclude only known
# build output in this checkout; source and unrelated untracked files stay visible.
$missing = @(@('**/bin/', '**/obj/', '/build-artifacts/') | Where-Object { $_ -cnotin $lines })
if ($missing.Count) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($exclude)) | Out-Null
    [IO.File]::AppendAllText($exclude, "`n# Release checkout build output`n" + ($missing -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
}
