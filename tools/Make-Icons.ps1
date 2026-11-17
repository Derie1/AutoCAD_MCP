<#
  Draws the ribbon icons into src\Resources\ (embedded in the plugin DLL):
    McpStart_16/32.png   green circle, white "play" triangle
    McpStop_16/32.png    red circle, white square
    McpStatus_16/32.png  blue circle, white "i"
  Re-run after changing colours or shapes; replace the PNGs with your own if you like
  (same names and sizes: 16x16 and 32x32, transparent background).
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\Resources'
New-Item -ItemType Directory -Force $out | Out-Null

function New-Icon([string]$name, [int]$size, [System.Drawing.Color]$color, [scriptblock]$symbol) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $pad = [Math]::Max(1, $size / 16)
    $d = $size - 2 * $pad
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $color), $pad, $pad, $d, $d)
    $edge = [System.Drawing.Color]::FromArgb(90, 0, 0, 0)
    $g.DrawEllipse((New-Object System.Drawing.Pen $edge, ([Math]::Max(1, $size / 32))), $pad, $pad, $d, $d)
    & $symbol $g $size
    $g.Dispose()
    $bmp.Save((Join-Path $out "${name}_$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

$white = [System.Drawing.Brushes]::White
$play = {
    param($g, $s)
    $pts = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($s * 0.38), ($s * 0.27)),
        (New-Object System.Drawing.PointF ($s * 0.38), ($s * 0.73)),
        (New-Object System.Drawing.PointF ($s * 0.76), ($s * 0.50)))
    $g.FillPolygon($white, $pts)
}
$square = {
    param($g, $s)
    $a = $s * 0.32
    $g.FillRectangle($white, $a, $a, $s - 2 * $a, $s - 2 * $a)
}
$info = {
    param($g, $s)
    $w = [Math]::Max(2, $s * 0.14)
    $x = ($s - $w) / 2
    $g.FillEllipse($white, $x - $w * 0.1, $s * 0.20, $w * 1.2, $w * 1.2)   # dot
    $g.FillRectangle($white, $x, $s * 0.42, $w, $s * 0.36)                 # stem
}

foreach ($size in 16, 32) {
    New-Icon 'McpStart'  $size ([System.Drawing.Color]::FromArgb(46, 158, 72))  $play
    New-Icon 'McpStop'   $size ([System.Drawing.Color]::FromArgb(201, 53, 43))  $square
    New-Icon 'McpStatus' $size ([System.Drawing.Color]::FromArgb(31, 111, 197)) $info
}
Get-ChildItem $out -Filter *.png | Select-Object Name, Length
