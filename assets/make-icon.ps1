# Regenerates src/StickFix.App/Assets/StickFix.ico. The .ico is committed, so this only runs when the design changes.
Add-Type -AssemblyName System.Drawing
$output = Join-Path $PSScriptRoot '..\src\StickFix.App\Assets\StickFix.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function RoundedRect([int]$x, [int]$y, [int]$w, [int]$h, [int]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90)
    $p.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $p.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $p.CloseFigure()
    $p
}

function Render($size) {
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    $tile = RoundedRect 8 8 240 240 52
    $gradient = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.Point 0, 0), (New-Object Drawing.Point 256, 256), [Drawing.Color]::FromArgb(34, 211, 238), [Drawing.Color]::FromArgb(79, 70, 229))
    $g.FillPath($gradient, $tile)

    # Gamepad silhouette: body plus two angled grips.
    $white = [Drawing.Brushes]::White
    $g.FillPath($white, (RoundedRect 34 78 188 92 44))
    foreach ($angle in -20, 20) {
        $state = $g.Save()
        $cx = if ($angle -lt 0) { 66 } else { 190 }
        $g.TranslateTransform($cx, 150)
        $g.RotateTransform($angle)
        $g.FillPath($white, (RoundedRect -24 -28 48 92 24))
        $g.Restore($state)
    }

    # Controls, cut out in the tile's dark colour.
    $dark = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(30, 41, 89))
    $g.FillRectangle($dark, 64, 106, 36, 12)
    $g.FillRectangle($dark, 76, 94, 12, 36)
    $g.FillEllipse($dark, 166, 88, 22, 22)
    $g.FillEllipse($dark, 166, 116, 22, 22)
    $g.FillEllipse($dark, 146, 102, 22, 22)
    $g.FillEllipse($dark, 186, 102, 22, 22)

    $g.Dispose()
    $bmp
}

# Only the 256px frame may be PNG. Smaller ones must be plain bitmaps, or the tray and System.Drawing can't read them.
function ToDib($bmp) {
    $size = $bmp.Width
    $stream = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $stream
    $w.Write([uint32]40); $w.Write([int32]$size); $w.Write([int32]($size * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]0); $w.Write([uint32]0); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    # All-zero AND mask, padded to 32-bit rows. Alpha above does the real work.
    $w.Write((New-Object byte[] ([int]([math]::Ceiling($size / 32.0) * 4 * $size))))
    $stream.ToArray()
}

$frames = foreach ($size in $sizes) {
    $bmp = Render $size
    if ($size -ge 256) {
        $stream = New-Object IO.MemoryStream
        $bmp.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        $data = $stream.ToArray()
    } else {
        $data = [byte[]](ToDib $bmp)
    }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Data = $data }
}

New-Item -ItemType Directory -Force (Split-Path $output) | Out-Null
$file = [IO.File]::Create($output)
$w = New-Object IO.BinaryWriter $file
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $edge = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$edge); $w.Write([byte]$edge); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$f.Data.Length); $w.Write([uint32]$offset)
    $offset += $f.Data.Length
}
foreach ($f in $frames) { $w.Write($f.Data) }
$w.Close()
"Wrote $output"
