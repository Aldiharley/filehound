<#
  Resizes generated art from assets-src/ into src/FileHound.App/Assets/ and builds app.ico.
  Usage: powershell -File tools/assets/process.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$src = Join-Path $root 'assets-src'
$dst = Join-Path $root 'src\FileHound.App\Assets'
New-Item -ItemType Directory -Force $dst | Out-Null

function Resize([string]$from, [string]$to, [int]$size) {
    $img = [System.Drawing.Image]::FromFile((Join-Path $src $from))
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($img, 0, 0, $size, $size)
    $g.Dispose(); $img.Dispose()
    $bmp.Save((Join-Path $dst $to), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

Resize 'hound-avatar.png' 'hound-avatar.png' 384
Resize 'hound-cut-cut.png' 'hound-hero.png' 560
Resize 'hound-sleep-cut.png' 'hound-sleep.png' 480
Resize 'hound-sniff-cut.png' 'hound-sniff.png' 480
Resize 'hound-shrug-cut.png' 'hound-shrug.png' 480
foreach ($icon in 'folder','document','image','video','audio','archive','app','code','drive','paw','clock','search','bolt','gear','star','home') {
    Resize "icon-$icon-cut.png" "icon-$icon.png" 160
}

# App icon: the hound's face on a peach rounded square, multi-size PNG-compressed ICO.
$avatar = [System.Drawing.Image]::FromFile((Join-Path $src 'hound-avatar-cut.png'))
$pngs = @()
foreach ($size in 256, 64, 48, 32, 16) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [math]::Max(3, [int]($size * 0.22)); $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($size - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d - 1, $size - $d - 1, $d, $d, 0, 90); $path.AddArc(0, $size - $d - 1, $d, $d, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(248, 205, 184))), $path)
    $g.SetClip($path)
    # Crop the head region of the avatar (upper-middle part of the portrait).
    $w = $avatar.Width; $crop = New-Object System.Drawing.Rectangle ([int]($w * 0.08)), ([int]($w * 0.02)), ([int]($w * 0.84)), ([int]($w * 0.84))
    $g.DrawImage($avatar, (New-Object System.Drawing.Rectangle 0, ([int]($size * 0.06)), $size, $size), $crop, 'Pixel')
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bmp.Save((Join-Path $dst 'app-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $pngs += ,@($size, $ms.ToArray())
    $bmp.Dispose()
}
$avatar.Dispose()

$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $s = $p[0]; $data = $p[1]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset); $offset += $data.Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p[1]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $dst 'app.ico'), $ico.ToArray())
Get-ChildItem $dst | ForEach-Object { "{0,-22} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB) }
