<#
.SYNOPSIS
  Builds the Windows app icons from shared/assets/icon-1024.png.

.DESCRIPTION
  The Windows counterpart of mac/Scripts/make-icon.swift's AppIcon export.
  Writes three .ico files to windows/Hearsay.App/Assets/, each with the
  sizes 16, 24, 32, 48, 64, 128 and 256 px:

    Hearsay.ico            the app icon, also the idle tray icon
    Hearsay-recording.ico  a red dot in the lower right corner (tray while recording)
    Hearsay-paused.ico     an amber dot with two pause bars (tray while paused)

  The source keeps macOS's transparent margin around the rounded square;
  it is cropped away so the icon fills the Windows icon grid. The Mac's
  menu bar uses monochrome template symbols; the Windows notification area
  shows full-colour icons, so the status variants are the app icon with a
  badge. Sizes below 256 are stored as 32-bit DIBs (every loader reads
  them), 256 as PNG, as Windows' own icons do.

  Run from anywhere with Windows PowerShell 5.1 or PowerShell 7:
    powershell -ExecutionPolicy Bypass -File windows\scripts\make-icon.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$source = Join-Path $repo 'shared\assets\icon-1024.png'
$outDir = Join-Path $repo 'windows\Hearsay.App\Assets'
$sizes = 16, 24, 32, 48, 64, 128, 256

# The rounded square in icon-1024.png spans about 100..924; keep a little of
# its shadow.
$crop = New-Object System.Drawing.Rectangle 92, 92, 840, 840

function New-Frame([System.Drawing.Image] $image, [int] $size, [string] $badge) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $destination = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $g.DrawImage($image, $destination, $crop, [System.Drawing.GraphicsUnit]::Pixel)

    if ($badge) {
        # The dot covers the lower right 56% at 16 px and 44% from 64 px up,
        # so it stays readable in the notification area.
        $fraction = if ($size -le 24) { 0.56 } elseif ($size -le 48) { 0.5 } else { 0.44 }
        $diameter = [Math]::Round($size * $fraction)
        $x = $size - $diameter
        $y = $size - $diameter
        $ring = [Math]::Max(1.0, $size / 16.0)
        $fill = if ($badge -eq 'recording') {
            [System.Drawing.Color]::FromArgb(255, 232, 17, 35)    # Windows' red
        } else {
            [System.Drawing.Color]::FromArgb(255, 247, 168, 0)    # amber
        }
        $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
        $brush = New-Object System.Drawing.SolidBrush $fill
        $g.FillEllipse($white, [single]$x, [single]$y, [single]$diameter, [single]$diameter)
        $g.FillEllipse($brush, [single]($x + $ring), [single]($y + $ring),
            [single]($diameter - 2 * $ring), [single]($diameter - 2 * $ring))
        if ($badge -eq 'paused' -and $diameter -ge 12) {
            # Two white bars, each a sixth of the dot wide, half its height.
            $barWidth = $diameter / 6.0
            $barHeight = $diameter / 2.0
            $top = $y + ($diameter - $barHeight) / 2.0
            $center = $x + $diameter / 2.0
            $g.FillRectangle($white, [single]($center - 1.5 * $barWidth), [single]$top, [single]$barWidth, [single]$barHeight)
            $g.FillRectangle($white, [single]($center + 0.5 * $barWidth), [single]$top, [single]$barWidth, [single]$barHeight)
        }
        $white.Dispose()
        $brush.Dispose()
    }
    $g.Dispose()
    return $bitmap
}

# A 32-bit BGRA DIB (BITMAPINFOHEADER, bottom-up, height doubled for the
# AND mask) followed by an all-zero AND mask: alpha carries transparency.
function Get-DibBytes([System.Drawing.Bitmap] $bitmap) {
    $size = $bitmap.Width
    $stream = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $stream
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2))
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]0)
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $w.Write([int]($size * $size * 4 + $maskStride * $size))
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = New-Object byte[] ($size * 4)
    for ($y = $size - 1; $y -ge 0; $y--) {
        [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $row, 0, $row.Length)
        $w.Write($row)
    }
    $bitmap.UnlockBits($data)
    $w.Write((New-Object byte[] ($maskStride * $size)))
    $w.Flush()
    return $stream.ToArray()
}

function Get-PngBytes([System.Drawing.Bitmap] $bitmap) {
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return $stream.ToArray()
}

function Write-Icon([string] $path, [string] $badge) {
    $image = [System.Drawing.Image]::FromFile($source)
    try {
        $entries = @()
        foreach ($size in $sizes) {
            $frame = New-Frame $image $size $badge
            try {
                $bytes = if ($size -ge 256) { Get-PngBytes $frame } else { Get-DibBytes $frame }
            } finally {
                $frame.Dispose()
            }
            $entries += , @($size, $bytes)
        }
    } finally {
        $image.Dispose()
    }

    $stream = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $stream
    $w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($entry in $entries) {
        $size = $entry[0]; $bytes = $entry[1]
        $dimension = if ($size -ge 256) { 0 } else { $size }
        $w.Write([byte]$dimension); $w.Write([byte]$dimension)
        $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([int16]1); $w.Write([int16]32)
        $w.Write([int]$bytes.Length); $w.Write([int]$offset)
        $offset += $bytes.Length
    }
    foreach ($entry in $entries) { $w.Write([byte[]]$entry[1]) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes($path, $stream.ToArray())
    Write-Host "wrote $path"
}

New-Item -ItemType Directory -Force $outDir | Out-Null
Write-Icon (Join-Path $outDir 'Hearsay.ico') $null
Write-Icon (Join-Path $outDir 'Hearsay-recording.ico') 'recording'
Write-Icon (Join-Path $outDir 'Hearsay-paused.ico') 'paused'
