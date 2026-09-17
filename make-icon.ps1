# Renders the crescent-moon icon at several sizes and writes moon.ico (PNG-in-ICO, Vista+).
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($sz in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $sz, $sz
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $m = $sz * 0.06
    # soft dark disc behind the moon so it reads on light and dark backgrounds
    $disc = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 22, 19, 16))
    $g.FillEllipse($disc, 0, 0, $sz - 1, $sz - 1)
    $moon = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 176, 72))
    $r = New-Object System.Drawing.RectangleF ($sz * 0.18), ($sz * 0.18), ($sz * 0.64), ($sz * 0.64)
    $g.FillEllipse($moon, $r)
    $bite = New-Object System.Drawing.RectangleF ($r.X + $r.Width * 0.32), ($r.Y - $r.Height * 0.18), $r.Width, $r.Height
    $g.FillEllipse($disc, $bite)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@{ Size = $sz; Bytes = $ms.ToArray() }
    $bmp.Dispose(); $ms.Dispose()
}

$out = Join-Path $PSScriptRoot 'moon.ico'
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)   # ICONDIR
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {                                                        # ICONDIRENTRY
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$p.Bytes.Length); $bw.Write([uint32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $bw.Write($p.Bytes) }
$bw.Close(); $fs.Close()
Write-Host "Wrote $out"
