# Рисует иконку ForgePad (рамка + три полосы) и собирает .ico из PNG-размеров.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$out = 'C:\Users\Admin\Documents\deepseek-harness\default-workspace\ForgePad\src\ForgePad.App\app.ico'
$sizes = @(16, 24, 32, 48, 64, 128, 256)

function New-LogoPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1, [int]($size * 0.04))
    $rect = New-Object System.Drawing.Rectangle $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)
    $radius = [int]($size * 0.26)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.ColorTranslator]::FromHtml('#171B2E')), ([System.Drawing.ColorTranslator]::FromHtml('#0C0E17')), 45
    $g.FillPath($bg, $path)

    $accent = [System.Drawing.ColorTranslator]::FromHtml('#5B8CFF')
    $barWidth = [Math]::Max(1, [single]($size * 0.11))
    $heights = @(0.34, 0.56, 0.42)
    $xs = @(0.30, 0.445, 0.59)

    for ($i = 0; $i -lt 3; $i++) {
        $w = $barWidth
        $h = [single]($size * $heights[$i])
        $x = [single]($size * $xs[$i])
        $y = [single](($size - $h) / 2)

        $barPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $br = [Math]::Max(1, [single]($w / 2))
        $barPath.AddArc($x, $y, $w, $w, 180, 180)
        $barPath.AddArc($x, $y + $h - $w, $w, $w, 0, 180)
        $barPath.CloseFigure()

        $barBrush = New-Object System.Drawing.SolidBrush $accent
        $g.FillPath($barBrush, $barPath)
        $barBrush.Dispose()
        $barPath.Dispose()
    }

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
    $bg.Dispose()
    $path.Dispose()

    return , $ms.ToArray()
}

$images = @()
foreach ($size in $sizes) { $images += , (New-LogoPng $size) }

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs

$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count

for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $bytes = $images[$i]

    $bw.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $bw.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$bytes.Length)
    $bw.Write([uint32]$offset)

    $offset += $bytes.Length
}

foreach ($bytes in $images) { $bw.Write($bytes) }

$bw.Flush()
$bw.Close()
$fs.Close()

Write-Output ("иконка: " + $out + " · " + (Get-Item $out).Length + " байт")
