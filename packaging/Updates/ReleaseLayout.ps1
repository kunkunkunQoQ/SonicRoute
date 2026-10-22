# Shared release data and read-only artifact validation. No downloaded program is executed.
function Resolve-ReleaseDotnet([string]$DotnetPath) {
    $candidates=@()
    if($DotnetPath){$candidates=@($DotnetPath)}else{
        $command=Get-Command dotnet -ErrorAction SilentlyContinue
        if($command){$candidates+= $command.Source}
        if($env:LOCALAPPDATA){$candidates+= (Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet/dotnet.exe')}
    }
    foreach($candidate in ($candidates|Select-Object -Unique)){
        if(!(Test-Path -LiteralPath $candidate -PathType Leaf)){continue}
        $sdk=(& $candidate --list-sdks 2>$null|Out-String)
        if($LASTEXITCODE -eq 0 -and $sdk -match '(?m)^10\.0\.\d+ \['){return [IO.Path]::GetFullPath($candidate)}
    }
    throw 'A stable .NET 10 SDK is required for the five-target build. Install it or supply -DotnetPath; .NET 8 packages still target the .NET 8 runtime.'
}

function Get-ReleaseSourceState([string]$RepositoryRoot) {
    $commit=(& git -C $RepositoryRoot rev-parse HEAD|Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $commit -cnotmatch '^[a-f0-9]{40}$'){throw 'Cannot identify source commit.'}
    $tree=(& git -C $RepositoryRoot rev-parse 'HEAD^{tree}'|Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $tree -cnotmatch '^[a-f0-9]{40}$'){throw 'Cannot identify source tree.'}
    $status=@(& git -C $RepositoryRoot status --porcelain --untracked-files=all)
    if($LASTEXITCODE -ne 0){throw 'Cannot verify source status.'}
    [pscustomobject]@{Commit=$commit;Tree=$tree;Clean=($status.Count -eq 0);Status=$status}
}

function Get-ReleaseMatrix([string]$Version) {
    if ($Version -notmatch '^\d+\.\d+(\.\d+)?$') { throw 'Expected a stable numeric version.' }
    @(
        [pscustomobject]@{ Edition='Lite'; Runtime='net10'; Framework='net10.0-windows'; Architecture='x64'; Asset="SonicRoute-v$Version-Lite-x64.exe" }
        [pscustomobject]@{ Edition='Lite'; Runtime='net10'; Framework='net10.0-windows'; Architecture='arm64'; Asset="SonicRoute-v$Version-Lite-arm64.exe" }
        [pscustomobject]@{ Edition='Lite'; Runtime='net8'; Framework='net8.0-windows'; Architecture='x64'; Asset="SonicRoute-v$Version-Lite-NET8-x64.exe" }
        [pscustomobject]@{ Edition='Lite'; Runtime='net8'; Framework='net8.0-windows'; Architecture='arm64'; Asset="SonicRoute-v$Version-Lite-NET8-arm64.exe" }
        [pscustomobject]@{ Edition='Legacy'; Runtime='net48'; Framework='net48'; Architecture='x64'; Asset="SonicRoute-v$Version-Legacy-x64.zip" }
    )
}

function Get-LegacyReleaseFiles {
    @('LICENSE','Microsoft.Bcl.AsyncInterfaces.dll','SonicRoute.Core.dll','SonicRoute.exe','SonicRoute.exe.config',
      'System.Buffers.dll','System.Memory.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll',
      'System.Text.Encodings.Web.dll','System.Text.Json.dll','System.Threading.Tasks.Extensions.dll','System.ValueTuple.dll')
}

function Read-ReleaseRuntimeConfig([string]$Path) {
    if (!('SonicRouteReleaseBundle' -as [type])) {
        # .NET bundle v6: official HostModel Bundler/Manifest layout; only bounded metadata reads.
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
public static class SonicRouteReleaseBundle {
    static readonly byte[] Marker={0x8b,0x12,0x02,0xb9,0x6a,0x61,0x20,0x38,0x72,0x7b,0x93,0x02,0x14,0xd7,0xa0,0x32,0x13,0xf5,0xb9,0xe6,0xef,0xae,0x33,0x18,0xee,0x3b,0x2d,0xce,0x24,0xb3,0x6a,0xae};
    public static string Read(string path) {
        using(var stream=File.OpenRead(path)) using(var reader=new BinaryReader(stream)) {
            var prefix=reader.ReadBytes((int)Math.Min(stream.Length,1024*1024));
            long header=0;
            for(int i=8;i<=prefix.Length-Marker.Length;i++) {
                if(prefix[i]!=Marker[0]) continue;
                int j=1;while(j<Marker.Length&&prefix[i+j]==Marker[j])j++;
                if(j==Marker.Length) {if(header!=0)throw new InvalidDataException("Ambiguous bundle marker.");header=BitConverter.ToInt64(prefix,i-8);}
            }
            if(header<64||header>stream.Length-64)throw new InvalidDataException("Missing or invalid single-file bundle header.");
            stream.Position=header;
            if(reader.ReadUInt32()!=6||reader.ReadUInt32()!=0)throw new InvalidDataException("Unsupported bundle layout.");
            int count=reader.ReadInt32();if(count<1||count>256)throw new InvalidDataException("Invalid bundle file count.");
            int length=reader.ReadByte();if(length<1||length>128)throw new InvalidDataException("Invalid bundle ID.");
            if(reader.ReadBytes(length).Length!=length)throw new EndOfStreamException();
            reader.ReadInt64();reader.ReadInt64();
            long offset=reader.ReadInt64(),size=reader.ReadInt64();
            if(offset<64||size<1||size>128*1024||offset>stream.Length-size)throw new InvalidDataException("Invalid bundled runtime configuration.");
            stream.Position=offset;var bytes=reader.ReadBytes((int)size);
            if(bytes.Length!=size)throw new EndOfStreamException();
            return new UTF8Encoding(false,true).GetString(bytes);
        }
    }
}
'@
    }
    return ([SonicRouteReleaseBundle]::Read($Path) | ConvertFrom-Json)
}

function Assert-ReleaseExecutable([string]$Path, [string]$Architecture, [string]$Edition, [string]$Runtime, [string]$Version) {
    $parts=$Version.Split('.')
    $numeric=[version](($parts+(@('0')*(4-$parts.Count)))-join '.')
    $info=[Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $description="SonicRoute $Edition"+$(if($Runtime -eq 'net8'){' NET8'}else{''})
    if ([version]$info.FileVersion -ne $numeric -or $info.FileDescription -cne $description) { throw "Wrong product version/edition/runtime description: $Path" }
    $reader=[IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try {
        if($reader.BaseStream.Length -lt 256 -or $reader.ReadUInt16() -ne 0x5a4d){throw 'Not a PE executable.'}
        $reader.BaseStream.Position=0x3c;$offset=$reader.ReadInt32()
        if($offset -lt 64 -or $offset -gt $reader.BaseStream.Length-6){throw 'Invalid PE header.'}
        $reader.BaseStream.Position=$offset;$machine=if($Architecture -eq 'arm64'){0xaa64}else{0x8664}
        if($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne $machine){throw "Wrong PE architecture: $Path"}
    } finally {$reader.Dispose()}
    if($Runtime -eq 'net48') {
        # Windows PowerShell supplies the CLR reflection-only context even when invoked by pwsh.
        $powershell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
        $target=& $powershell -NoProfile -NonInteractive -File "$PSScriptRoot/Read-ManagedTarget.ps1" -Path $Path
        if($LASTEXITCODE -ne 0 -or $target -cne '.NETFramework,Version=v4.8'){throw "Wrong Legacy target framework: $Path"}
    } else {
        $config=Read-ReleaseRuntimeConfig $Path
        $expected=if($Runtime -eq 'net8'){'8.0'}elseif($Runtime -eq 'net10'){'10.0'}else{throw "Unsupported runtime $Runtime"}
        $options=$config.runtimeOptions
        if($options.tfm -cne "net$expected" -or $options.rollForward -in @('Major','LatestMajor')){throw "Wrong embedded target framework or roll-forward policy: $Path"}
        $frameworks=@($options.frameworks)
        foreach($name in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')){
            $match=@($frameworks|Where-Object{$_.name -ceq $name})
            if($match.Count -ne 1 -or ([version]$match[0].version).Major -ne ([version]$expected).Major){throw "Wrong $name runtime dependency: $Path"}
        }
        if($frameworks.Count -ne 2){throw "Unexpected runtime dependencies: $Path"}
    }
}

function Get-ReleasePackageFiles($Variant,[string]$AssetsDirectory,[string]$Version) {
    $path=Join-Path $AssetsDirectory $Variant.Asset
    $item=Get-Item -LiteralPath $path -ErrorAction Stop
    if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.Length -le 0 -or $item.Length -gt 128MB){throw "Invalid release package: $path"}
    if($Variant.Edition -eq 'Lite') {
        Assert-ReleaseExecutable $path $Variant.Architecture $Variant.Edition $Variant.Runtime $Version
        return @([ordered]@{path='SonicRoute.exe';size=$item.Length;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()})
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $allowed=@(Get-LegacyReleaseFiles);$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $archive=[IO.Compression.ZipFile]::OpenRead($path)
    $scratch=Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($AssetsDirectory))) ('SonicRoute-release-'+[guid]::NewGuid().ToString('N')+'.exe')
    $files=@();[long]$total=0
    try {
        foreach($entry in $archive.Entries){
            $total+=$entry.Length
            if($allowed -cnotcontains $entry.FullName -or !$seen.Add($entry.FullName) -or $entry.Length -le 0 -or $total -gt 128MB -or
               ($entry.ExternalAttributes -band 0x400) -ne 0 -or (($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000){throw "Unsafe or unknown Legacy ZIP entry: $($entry.FullName)"}
            $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
            try{$hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant()}finally{$stream.Dispose();$sha.Dispose()}
            $files+= [ordered]@{path=$entry.FullName;size=$entry.Length;sha256=$hash}
            if($entry.FullName -ceq 'SonicRoute.exe'){
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$scratch)
                Assert-ReleaseExecutable $scratch $Variant.Architecture $Variant.Edition $Variant.Runtime $Version
            }
        }
        if($seen.Count -ne $allowed.Count){throw 'Incomplete Legacy package.'}
        return $files
    } finally {$archive.Dispose();if(Test-Path -LiteralPath $scratch){Remove-Item -LiteralPath $scratch}}
}
