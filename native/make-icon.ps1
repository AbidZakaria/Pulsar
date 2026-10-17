# Draws the Pulsar icon (original artwork) and writes native\pulsar.ico
Add-Type -AssemblyName System.Drawing
$sizes = 16,32,48,64,128,256; $pngs = @()
foreach ($s in $sizes) {
  $bmp = New-Object Drawing.Bitmap $s,$s; $g = [Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode='AntiAlias'; $g.Clear([Drawing.Color]::Transparent)
  $r = $s*0.22; $path = New-Object Drawing.Drawing2D.GraphicsPath
  $path.AddArc(0,0,2*$r,2*$r,180,90); $path.AddArc($s-2*$r-1,0,2*$r,2*$r,270,90); $path.AddArc($s-2*$r-1,$s-2*$r-1,2*$r,2*$r,0,90); $path.AddArc(0,$s-2*$r-1,2*$r,2*$r,90,90); $path.CloseFigure()
  $bg = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.Point]::new(0,0)),([Drawing.Point]::new($s,$s)),([Drawing.Color]::FromArgb(40,18,86)),([Drawing.Color]::FromArgb(10,10,20))
  $g.FillPath($bg,$path)
  $c = $s/2
  foreach ($i in 3,2,1) {                       # pulse rings
    $rad = $s*0.12*$i+$s*0.04; $a = [int](70+60*(4-$i))
    $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb($a,255,90,140)),([float][Math]::Max(1,$s*0.035))
    $g.DrawEllipse($pen,$c-$rad,$c-$rad,2*$rad,2*$rad)
  }
  $core = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.Point]::new(0,0)),([Drawing.Point]::new($s,$s)),([Drawing.Color]::FromArgb(255,170,60)),([Drawing.Color]::FromArgb(255,60,130))
  $cr = $s*0.13; $g.FillEllipse($core,$c-$cr,$c-$cr,2*$cr,2*$cr)
  $ms = New-Object IO.MemoryStream; $bmp.Save($ms,[Drawing.Imaging.ImageFormat]::Png); $pngs += ,$ms.ToArray(); $g.Dispose(); $bmp.Dispose()
}
$out = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$off = 6 + 16*$sizes.Count
for ($i=0; $i -lt $sizes.Count; $i++) {
  $s=$sizes[$i]; $b = if ($s -ge 256) {0} else {$s}
  $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$off); $off += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
[IO.File]::WriteAllBytes("$PSScriptRoot\pulsar.ico",$out.ToArray()); "icon ok"
