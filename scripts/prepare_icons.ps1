#Requires -Version 7.0
[CmdletBinding()]
param([string]$PreviewPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$projectRoot = Split-Path -Parent $PSScriptRoot
$assetDirectory = Join-Path $projectRoot 'src/Cad2Nc.App/Assets'
$source = [System.Drawing.Bitmap]::FromFile((Join-Path $assetDirectory 'CAD2NC-source.png'))
$sizes = @(16,24,32,48,64,128,256)
try {
    if ($source.Width -ne $source.Height -or $source.GetPixel(0,0).A -ne 0) {
        throw 'The icon source must be square with a genuinely transparent exterior.'
    }
    $frames = @()
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $stream = [System.IO.MemoryStream]::new()
        try {
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source,[System.Drawing.Rectangle]::new(0,0,$size,$size))
            $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
            $frames += ,$stream.ToArray()
            if ($size -eq 256) { $bitmap.Save((Join-Path $assetDirectory 'CAD2NC.png'),[System.Drawing.Imaging.ImageFormat]::Png) }
        }
        finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    $file = [System.IO.File]::Create((Join-Path $assetDirectory 'CAD2NC.ico'))
    $writer = [System.IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i=0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    }
    finally { $writer.Dispose() }
    if ($PreviewPath) {
        $previewFullPath = [System.IO.Path]::GetFullPath($PreviewPath,$projectRoot)
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $previewFullPath) | Out-Null
        $sheet = [System.Drawing.Bitmap]::new(750,650)
        $graphics = [System.Drawing.Graphics]::FromImage($sheet)
        $font = [System.Drawing.Font]::new('Segoe UI',11)
        $dark = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(13,20,31))
        try {
            $graphics.Clear([System.Drawing.Color]::White)
            $graphics.FillRectangle($dark,0,325,750,325)
            foreach ($y in @(0,325)) {
                $brush = if ($y -eq 0) { [System.Drawing.Brushes]::Black } else { [System.Drawing.Brushes]::White }
                $x = 20
                for ($i=0; $i -lt $sizes.Count; $i++) {
                    $size = $sizes[$i]
                    $stream = [System.IO.MemoryStream]::new([byte[]]$frames[$i])
                    $frameBitmap = [System.Drawing.Bitmap]::FromStream($stream)
                    try { $graphics.DrawImageUnscaled($frameBitmap,$x,$y+45); $graphics.DrawString([string]$size,$font,$brush,$x,$y+15) }
                    finally { $frameBitmap.Dispose(); $stream.Dispose() }
                    $x += [Math]::Max(44,$size+16)
                }
            }
            $sheet.Save($previewFullPath,[System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $dark.Dispose(); $font.Dispose(); $graphics.Dispose(); $sheet.Dispose() }
    }
    Write-Host ('Prepared icon frames: ' + ($sizes -join ', '))
}
finally { $source.Dispose() }
