# Regenerates Assets/app.ico - the app icon (dark grey square with the blue "SUL" wordmark)
# at the sizes Windows asks for. Run from the repo root:  powershell -File tools/make-icon.ps1
# Windows PowerShell 5.1 (System.Drawing). The .ico holds PNG frames (Vista+).
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
# The header's own look: the blue wordmark on the dark title-bar grey.
$back = [System.Drawing.ColorTranslator]::FromHtml('#2B2B2E')
$accent = [System.Drawing.ColorTranslator]::FromHtml('#2B8AE6')
$frames = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Flat square, with a small rounding (2/14 of its side).
    $r = [Math]::Max(1, [int][Math]::Round($s * 2 / 14))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($s - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d - 1, $s - $d - 1, $d, $d, 0, 90)
    $path.AddArc(0, $s - $d - 1, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $back), $path)

    # The "SUL" wordmark, drawn as outlines and scaled to fill 86% of the width, so
    # the three letters are as large as each size allows (a font size cannot be
    # fitted that exactly, and at 16 px every pixel of width counts).
    $family = New-Object System.Drawing.FontFamily 'Segoe UI'
    $text = New-Object System.Drawing.Drawing2D.GraphicsPath
    $text.AddString('SUL', $family, [int][System.Drawing.FontStyle]::Bold, 100, (New-Object System.Drawing.PointF 0, 0), [System.Drawing.StringFormat]::GenericTypographic)
    $b = $text.GetBounds()
    $scale = ($s * 0.86) / $b.Width
    $m = New-Object System.Drawing.Drawing2D.Matrix
    $m.Translate([single](($s - $b.Width * $scale) / 2 - $b.X * $scale), [single](($s - $b.Height * $scale) / 2 - $b.Y * $scale))
    $m.Scale([single]$scale, [single]$scale)
    $text.Transform($m)
    $g.FillPath((New-Object System.Drawing.SolidBrush $accent), $text)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @{ Size = $s; Bytes = $ms.ToArray() }
    $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$f.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes) }
$w.Flush()
$target = Join-Path (Split-Path $PSScriptRoot -Parent) 'Assets\app.ico'
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
"wrote $target ($($out.Length) bytes)"
