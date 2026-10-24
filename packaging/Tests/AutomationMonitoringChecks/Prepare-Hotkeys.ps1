param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$app = [IO.File]::ReadAllText((Join-Path $root 'SonicRoute/App.xaml.cs'))
$start = $app.IndexOf('private async Task ExecuteHotkeyCoreAsync', [StringComparison]::Ordinal)
$end = $app.IndexOf('private async Task AdjustDefaultOutputVolumeAsync', $start, [StringComparison]::Ordinal)
if ($start -lt 0 -or $end -le $start) { throw 'Hotkey method boundaries changed; inspect the fixture.' }
$method = $app.Substring($start, $end - $start)
$source = "using SonicRoute.Core; using SonicRoute.Core.Compat; using SonicRoute.Core.Interop; using SonicRoute.Core.Models; namespace SonicRoute { public sealed partial class App { $method } }"
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'HotkeyDispatch.cs'), $source, [Text.UTF8Encoding]::new($false))
$mouse = [IO.File]::ReadAllText((Join-Path $root 'SonicRoute/MouseHotkeyHook.cs'))
# OS key-state boundary only; compile actual modifier matching unchanged.
$mouse = $mouse.Replace('(GetAsyncKeyState(vk) & 0x8000) != 0', 'SonicRoute.KeyBoundary.Down.Contains(vk)')
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'MouseHotkeyHook.cs'), $mouse, [Text.UTF8Encoding]::new($false))
