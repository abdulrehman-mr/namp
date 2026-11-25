# Builds assets\namp.ico (multi-size) and assets\namp-logo.png from the NAMP artwork (assets\namp-source.png)
Add-Type -AssemblyName System.Drawing
$Root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $Root 'assets\namp-source.png'
$out = Join-Path $Root 'assets\namp.ico'
$logo = Join-Path $Root 'assets\namp-logo.png'

$img = [System.Drawing.Image]::FromFile($src)
$corner = ([System.Drawing.Bitmap]$img).GetPixel(2, 2)
Write-Host "Source $($img.Width)x$($img.Height), corner alpha=$($corner.A)"

function Resize([System.Drawing.Image]$im, [int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.SmoothingMode = 'HighQuality'
    $g.PixelOffsetMode = 'HighQuality'
    $g.CompositingQuality = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($im, 0, 0, $s, $s)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = Resize $img $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,$ms.ToArray()
    $bmp.Dispose()
}
$l = Resize $img 96
$l.Save($logo, [System.Drawing.Imaging.ImageFormat]::Png)
$l.Dispose()
$img.Dispose()

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Host "Icon written: $out"
