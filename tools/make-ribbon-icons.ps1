# Generates 32x32 ribbon icons matching the in-page HeaderEmblem / workbench.ico style:
# rounded rect, linear gradient #3A7BD5 -> #2B6FBB, white bold glyph centered.
# Output is PNG-in-ICO (same container format as workbench.ico), plus the raw PNG.
# Glyphs passed as unicode codepoints to keep this file ASCII-only (encoding safe).
param(
    [string]$OutDir = 'D:\zmzc-code\trados-plugin\TradosToolkit\Resources'
)
Add-Type -AssemblyName System.Drawing

function New-BadgePng {
    param([string]$Glyph, [string]$OutPath)

    $size = 32
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded rect (radius 7 like the WPF HeaderEmblem CornerRadius)
    $r = 7
    $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point($size, $size)),
        [System.Drawing.Color]::FromArgb(255, 0x3A, 0x7B, 0xD5),
        [System.Drawing.Color]::FromArgb(255, 0x2B, 0x6F, 0xBB))
    $g.FillPath($brush, $path)

    $font = New-Object System.Drawing.Font('Microsoft YaHei UI', 15, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF(0, -1, $size, $size)
    $g.DrawString($Glyph, $font, [System.Drawing.Brushes]::White, $rect, $sf)

    $g.Dispose()
    $bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "png  -> $OutPath"
}

function New-PngIco {
    param([string]$PngPath, [string]$IcoPath)
    $png = [System.IO.File]::ReadAllBytes($PngPath)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)
    # ICONDIR
    $w.Write([uint16]0)      # reserved
    $w.Write([uint16]1)      # type = icon
    $w.Write([uint16]1)      # count
    # ICONDIRENTRY (32x32, PNG payload)
    $w.Write([byte]32)       # width
    $w.Write([byte]32)       # height
    $w.Write([byte]0)        # palette colors
    $w.Write([byte]0)        # reserved
    $w.Write([uint16]1)      # planes
    $w.Write([uint16]32)     # bpp
    $w.Write([uint32]$png.Length)          # bytes in resource
    $w.Write([uint32]22)                   # image offset
    $w.Write($png)
    $w.Flush()
    [System.IO.File]::WriteAllBytes($IcoPath, $ms.ToArray())
    $w.Close()
    Write-Host "ico  -> $IcoPath"
}

$jobs = @(
    @{ Glyph = [string][char]0x672F; Name = 'glossary-manager' },  # shu (terminology)
    @{ Glyph = [string][char]0x5E93; Name = 'tm-manager' }         # ku  (memory)
)
foreach ($j in $jobs) {
    $png = Join-Path $OutDir ($j.Name + '.png')
    $ico = Join-Path $OutDir ($j.Name + '.ico')
    New-BadgePng -Glyph $j.Glyph -OutPath $png
    New-PngIco -PngPath $png -IcoPath $ico
}
