$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'AIIsland/Assets'
$source = [Drawing.Bitmap]::FromFile((Join-Path $assets 'logo.png'))
try {
    # Remove the transparent outer margin while keeping the complete supplied logo.
    $left = $source.Width; $top = $source.Height; $right = 0; $bottom = 0
    for ($y = 0; $y -lt $source.Height; $y++) {
        for ($x = 0; $x -lt $source.Width; $x++) {
            if ($source.GetPixel($x, $y).A -gt 0) {
                $left = [Math]::Min($left, $x); $top = [Math]::Min($top, $y)
                $right = [Math]::Max($right, $x); $bottom = [Math]::Max($bottom, $y)
            }
        }
    }
    $crop = [Drawing.Rectangle]::new($left, $top, $right - $left + 1, $bottom - $top + 1)
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $frames = @()
    foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size, $size)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $stream = [IO.MemoryStream]::new()
        try {
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $scale = ($size - 2) / [double][Math]::Max($crop.Width, $crop.Height)
            $w = [int]($crop.Width * $scale); $h = [int]($crop.Height * $scale)
            $target = [Drawing.Rectangle]::new([int](($size - $w) / 2), [int](($size - $h) / 2), $w, $h)
            $graphics.DrawImage($source, $target, $crop, [Drawing.GraphicsUnit]::Pixel)
            if ($size -eq 256) {
                $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
                $frames += ,$stream.ToArray()
            } else {
                # Classic DIB frames keep small tray icons compatible with GDI.
                $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Bmp)
                $bmp = $stream.ToArray()
                $maskLength = [int]([Math]::Ceiling($size / 32.0) * 4 * $size)
                $dib = [byte[]]::new($bmp.Length - 14 + $maskLength)
                [Array]::Copy($bmp, 14, $dib, 0, $bmp.Length - 14)
                [Array]::Copy([BitConverter]::GetBytes([int]($size * 2)), 0, $dib, 8, 4)
                $frames += ,$dib
            }
        } finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    $writer = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $assets 'app.ico')))
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = [byte]($sizes[$i] % 256)
            $writer.Write($dimension); $writer.Write($dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose() }
} finally { $source.Dispose() }
