param(
    [Parameter(Mandatory=$true)][string]$SourceImage,
    [string]$OutputDirectory=(Join-Path $PSScriptRoot '../SonicRoute')
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$inputPath=[IO.Path]::GetFullPath($SourceImage)
$output=[IO.Path]::GetFullPath($OutputDirectory)
$pngPath=Join-Path $output 'SonicRoute.png'
$icoPath=Join-Path $output 'SonicRoute.ico'
if($inputPath -eq $pngPath -or $inputPath -eq $icoPath){throw 'Use a separate input image.'}
New-Item -ItemType Directory -Path $output -Force|Out-Null
$source=[Drawing.Image]::FromFile($inputPath)
function New-IconBitmap([int]$size){
    $bitmap=[Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    try{
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $scale=[Math]::Min($size/$source.Width,$size/$source.Height)
        $width=$source.Width*$scale; $height=$source.Height*$scale
        $graphics.DrawImage($source,[float](($size-$width)/2),[float](($size-$height)/2),[float]$width,[float]$height)
        return $bitmap
    } catch { $bitmap.Dispose(); throw }
    finally { $graphics.Dispose() }
}
try{
    $png=New-IconBitmap 256
    try{$png.Save($pngPath,[Drawing.Imaging.ImageFormat]::Png)}finally{$png.Dispose()}
    $frames=foreach($size in @(16,20,24,32,48,64,96,128,256)){
        $bitmap=New-IconBitmap $size
        $stream=[IO.MemoryStream]::new()
        try{
            $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
            [pscustomobject]@{Size=$size;Bytes=$stream.ToArray()}
        }finally{$stream.Dispose();$bitmap.Dispose()}
    }
    $file=[IO.File]::Create($icoPath)
    $writer=[IO.BinaryWriter]::new($file)
    try{
        $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$frames.Count)
        $offset=6+16*$frames.Count
        foreach($frame in $frames){
            $dimension=if($frame.Size -eq 256){0}else{$frame.Size}
            $writer.Write([byte]$dimension);$writer.Write([byte]$dimension)
            $writer.Write([byte]0);$writer.Write([byte]0)
            $writer.Write([uint16]1);$writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Bytes.Length);$writer.Write([uint32]$offset)
            $offset+=$frame.Bytes.Length
        }
        foreach($frame in $frames){$writer.Write([byte[]]$frame.Bytes)}
    }finally{$writer.Dispose();$file.Dispose()}
    Write-Output $pngPath
    Write-Output $icoPath
}finally{$source.Dispose()}
