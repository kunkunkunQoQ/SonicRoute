param([string]$Version,[switch]$VerifyOnly)
$ErrorActionPreference='Stop'
$backupRoot=[IO.Path]::GetFullPath($PSScriptRoot)
function Resolve-BackupPath([string]$relative){
    if([IO.Path]::IsPathRooted($relative)){throw 'Absolute manifest paths are not allowed.'}
    $resolved=[IO.Path]::GetFullPath((Join-Path $backupRoot $relative))
    if(-not $resolved.StartsWith($backupRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Manifest path escapes the backup directory.'}
    return $resolved
}
$entries=@(Get-Content -LiteralPath (Join-Path $backupRoot 'backup-manifest.json') -Raw|ConvertFrom-Json)
if($Version){$entries=@($entries|Where-Object Version -eq $Version);if(-not $entries.Count){throw ('Unknown backup version: '+$Version)}}
$checked=0
$restored=0
$buffer=[byte[]]::new(1MB)
foreach($entry in $entries){
    $target=Resolve-BackupPath $entry.File
    if($entry.Storage -eq 'Direct'){
        if((Get-Item -LiteralPath $target).Length -ne $entry.Bytes -or (Get-FileHash -LiteralPath $target).Hash -ne $entry.SHA256){throw ('Backup verification failed: '+$entry.File)}
    }elseif($entry.Storage -eq 'Parts'){
        $fullHash=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
        $temporary=$target+'.restore-tmp'
        $outputStream=$null
        $complete=$false
        $createdTemporary=$false
        try{
            if(-not $VerifyOnly){
                if(Test-Path -LiteralPath $target){
                    if((Get-Item -LiteralPath $target).Length -ne $entry.Bytes -or (Get-FileHash -LiteralPath $target).Hash -ne $entry.SHA256){throw ('An existing different file must not be overwritten: '+$entry.File)}
                    $checked++
                    continue
                }
                if(Test-Path -LiteralPath $temporary){throw ('An existing temporary file must not be overwritten: '+$entry.File)}
                New-Item -ItemType Directory -Path (Split-Path $target) -Force|Out-Null
                $outputStream=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write)
                $createdTemporary=$true
            }
            $total=0L
            foreach($part in $entry.Parts){
                $partPath=Resolve-BackupPath $part.File
                if((Get-Item -LiteralPath $partPath).Length -ne $part.Bytes){throw ('Part size mismatch: '+$part.File)}
                $partHash=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
                $inputStream=[IO.File]::OpenRead($partPath)
                try{
                    while(($read=$inputStream.Read($buffer,0,$buffer.Length)) -gt 0){
                        $partHash.AppendData($buffer,0,$read)
                        $fullHash.AppendData($buffer,0,$read)
                        if($null -ne $outputStream){$outputStream.Write($buffer,0,$read)}
                        $total+=$read
                    }
                    if([Convert]::ToHexString($partHash.GetHashAndReset()) -ne $part.SHA256){throw ('Part hash mismatch: '+$part.File)}
                }finally{$inputStream.Dispose();$partHash.Dispose()}
            }
            if($total -ne $entry.Bytes -or [Convert]::ToHexString($fullHash.GetHashAndReset()) -ne $entry.SHA256){throw ('Restored file hash mismatch: '+$entry.File)}
            if($null -ne $outputStream){$outputStream.Dispose();$outputStream=$null;Move-Item -LiteralPath $temporary -Destination $target;$restored++}
            $complete=$true
        }finally{
            if($null -ne $outputStream){$outputStream.Dispose()}
            $fullHash.Dispose()
            # Remove only the single temporary file created in this call.
            if($createdTemporary -and -not $complete -and (Test-Path -LiteralPath $temporary)){Remove-Item -LiteralPath $temporary}
        }
    }else{throw 'Unknown storage mode.'}
    $checked++
}
[pscustomobject]@{Version=$Version;VerifiedFiles=$checked;RestoredLargeFiles=$restored;VerifyOnly=[bool]$VerifyOnly;AllSHA256Matched=$true}|ConvertTo-Json
