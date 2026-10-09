param(
    [string]$SourceImage=(Join-Path $PSScriptRoot '../../SonicRoute/SonicRoute.png'),
    [Parameter(Mandatory=$true)][string]$AssetsDirectory
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $AssetsDirectory -Force|Out-Null
$source=[Drawing.Image]::FromFile([IO.Path]::GetFullPath($SourceImage))
function New-Logo([string]$name,[int]$width,[int]$height,[bool]$white=$false){
    $bitmap=[Drawing.Bitmap]::new($width,$height,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    try{
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $scale=[Math]::Min($width/$source.Width,$height/$source.Height)
        $drawWidth=$source.Width*$scale; $drawHeight=$source.Height*$scale
        $graphics.DrawImage($source,[float](($width-$drawWidth)/2),[float](($height-$drawHeight)/2),[float]$drawWidth,[float]$drawHeight)
        if($white){
            # 深色 Shell 背景使用白色轮廓，alpha 与原图一致；浅色背景保留原色。
            for($y=0;$y -lt $height;$y++){
                for($x=0;$x -lt $width;$x++){
                    $pixel=$bitmap.GetPixel($x,$y)
                    if($pixel.A -gt 0){$bitmap.SetPixel($x,$y,[Drawing.Color]::FromArgb($pixel.A,255,255,255))}
                }
            }
        }
        $bitmap.Save((Join-Path $AssetsDirectory $name),[Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
try{
    New-Logo 'Square44x44Logo.png' 44 44
    New-Logo 'Square150x150Logo.png' 150 150
    New-Logo 'Square310x310Logo.png' 310 310
    New-Logo 'StoreLogo.png' 50 50
    New-Logo 'Wide310x150Logo.png' 310 150
    # 三套 targetsize 都需要存在；缺少主题变体时 Shell 会加底板。
    foreach($size in @(16,20,24,30,32,36,40,48,60,64,72,80,96,256)){
        $name='Square44x44Logo.targetsize-'+$size+'.png'
        New-Logo $name $size $size
        New-Logo ('Square44x44Logo.targetsize-'+$size+'_altform-unplated.png') $size $size $true
        Copy-Item -LiteralPath (Join-Path $AssetsDirectory $name) -Destination (Join-Path $AssetsDirectory ('Square44x44Logo.targetsize-'+$size+'_altform-lightunplated.png')) -Force
    }
} finally { $source.Dispose() }
