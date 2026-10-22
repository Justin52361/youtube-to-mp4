# Renders Assets/AppIcon.xaml into Assets/AppIcon.ico (PNG-compressed, multi-size).
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$root = Split-Path $PSScriptRoot -Parent
$dict = [Windows.Markup.XamlReader]::Load([IO.File]::OpenRead((Join-Path $root 'Assets\AppIcon.xaml')))
$source = $dict['AppIconImage']
$sizes = 16, 24, 32, 48, 64, 128, 256

$pngs = foreach ($s in $sizes) {
    $img = New-Object Windows.Controls.Image
    $img.Source = $source
    $img.Width = $s; $img.Height = $s
    $img.Measure([Windows.Size]::new($s, $s))
    $img.Arrange([Windows.Rect]::new(0, 0, $s, $s))
    $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap $s, $s, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($img)
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    ,$ms.ToArray()
}

$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $root 'Assets\AppIcon.ico'), $out.ToArray())
Write-Host "Wrote Assets\AppIcon.ico"
