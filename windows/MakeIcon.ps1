Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot 'icon.ico'

function Draw([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s,$s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $u = $s / 64.0

    # rounded background
    $r = 14*$u
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bg.AddArc(0,0,$r*2,$r*2,180,90); $bg.AddArc($s-$r*2-1,0,$r*2,$r*2,270,90)
    $bg.AddArc($s-$r*2-1,$s-$r*2-1,$r*2,$r*2,0,90); $bg.AddArc(0,$s-$r*2-1,$r*2,$r*2,90,90); $bg.CloseFigure()
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0,0)),([System.Drawing.Point]::new(0,$s)),([System.Drawing.Color]::FromArgb(255,38,132,255)),([System.Drawing.Color]::FromArgb(255,20,60,150))
    $g.FillPath($br,$bg)

    # SD card with clipped top-right corner
    $card = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pts = @(
        [System.Drawing.PointF]::new(16*$u,8*$u), [System.Drawing.PointF]::new(38*$u,8*$u),
        [System.Drawing.PointF]::new(48*$u,18*$u), [System.Drawing.PointF]::new(48*$u,46*$u),
        [System.Drawing.PointF]::new(16*$u,46*$u))
    $card.AddPolygon($pts)
    $g.FillPath([System.Drawing.Brushes]::White,$card)

    # gold contacts
    $gold = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,235,180,50))
    foreach ($x in 20,26,32,38) { $g.FillRectangle($gold,$x*$u,11*$u,3.5*$u,9*$u) }

    # green down arrow (import)
    $gr = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,40,200,110))
    $arrow = @(
        [System.Drawing.PointF]::new(27*$u,26*$u), [System.Drawing.PointF]::new(37*$u,26*$u),
        [System.Drawing.PointF]::new(37*$u,35*$u), [System.Drawing.PointF]::new(43*$u,35*$u),
        [System.Drawing.PointF]::new(32*$u,45*$u), [System.Drawing.PointF]::new(21*$u,35*$u),
        [System.Drawing.PointF]::new(27*$u,35*$u))
    $g.FillPolygon($gr,$arrow)

    # tray base
    $g.FillRectangle([System.Drawing.Brushes]::White, 14*$u, 53*$u, 36*$u, 4*$u)
    $g.Dispose(); return $bmp
}

$sizes = 16,24,32,48,64,256
$pngs = foreach ($s in $sizes) {
    $b = Draw $s; $ms = New-Object System.IO.MemoryStream
    $b.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png); ,$ms.ToArray()
}
$fs = [System.IO.File]::Create($out); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$off = 6 + 16*$sizes.Count
for ($i=0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = if ($s -ge 256) {0} else {$s}
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$off)
    $off += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close(); $fs.Close()
(Draw 256).Save((Join-Path $PSScriptRoot 'icon-preview.png'))
