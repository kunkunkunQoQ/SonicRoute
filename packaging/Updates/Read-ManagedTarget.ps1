param([Parameter(Mandatory=$true)][string]$Path)
$ErrorActionPreference='Stop'
try {
    $assembly=[Reflection.Assembly]::ReflectionOnlyLoad([IO.File]::ReadAllBytes([IO.Path]::GetFullPath($Path)))
    $attributes=@([Reflection.CustomAttributeData]::GetCustomAttributes($assembly)|Where-Object{$_.AttributeType.FullName -ceq 'System.Runtime.Versioning.TargetFrameworkAttribute'})
    if($attributes.Count -ne 1 -or $attributes[0].ConstructorArguments.Count -ne 1){throw 'Missing target framework metadata.'}
    Write-Output $attributes[0].ConstructorArguments[0].Value
} catch {Write-Error $_;exit 1}
