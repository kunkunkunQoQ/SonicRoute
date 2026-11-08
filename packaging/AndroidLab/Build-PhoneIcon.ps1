$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne [Threading.ApartmentState]::STA) {
    throw 'Run this build-only vector export with pwsh -STA.'
}
Add-Type -AssemblyName PresentationCore,WindowsBase
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$source = [IO.File]::ReadAllText((Join-Path $repo 'SonicRoute\AutomationIcons.cs'))
$match = [regex]::Match($source, 'Geometry Phone\s*\{\s*get;\s*\}\s*=\s*Frozen\("([^"]+)"\)')
if (!$match.Success) { throw 'Shared phone geometry not found.' }
$geometry = [Windows.Media.Geometry]::Parse($match.Groups[1].Value)
$pen = [Windows.Media.Pen]::new([Windows.Media.SolidColorBrush]::new([Windows.Media.Color]::FromRgb(128,128,128)),1.5)
# A white phone face keeps the gray outline legible on dark/inactive native captions;
# the area outside the shared geometry remains transparent.
$drawing = [Windows.Media.GeometryDrawing]::new([Windows.Media.Brushes]::White,$pen,$geometry)
$drawing.Freeze()
$image = [Windows.Media.DrawingImage]::new($drawing)
$image.Freeze()
function Export-PhonePng([int]$Size) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $height = $Size * 0.84
    $width = $height * $drawing.Bounds.Width / $drawing.Bounds.Height
    $context.DrawImage($image,[Windows.Rect]::new(($Size-$width)/2,($Size-$height)/2,$width,$height))
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($Size,$Size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); return ,$stream.ToArray() }
    finally { $stream.Dispose() }
}
$sizes = @(16,20,24,32,40,48,64,256)
$frames = @($sizes | ForEach-Object { ,(Export-PhonePng $_) })
$png = Join-Path $repo 'AndroidRuntime\VirtualDisplay\phone-icon.png'
[IO.File]::WriteAllBytes($png,$frames[-1])
$ico = Join-Path $PSScriptRoot 'native\phone_icon.ico'
$stream = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i=0; $i -lt $sizes.Count; ++$i) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    $writer.Flush(); [IO.File]::WriteAllBytes($ico,$stream.ToArray())
} finally { $writer.Dispose(); $stream.Dispose() }
Get-FileHash -LiteralPath $png,$ico | Format-List Path,Hash
