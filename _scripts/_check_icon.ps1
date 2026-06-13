Add-Type -AssemblyName System.Drawing

$exe = 'G:\Lanweilig\Heimlich\Karikatur\MangaView\_release\MangaReader.Native.exe'
$icon = [System.Drawing.Icon]::ExtractAssociatedIcon($exe)
Write-Output "Exe icon: $($icon.Width)x$($icon.Height)"

$bmp = $icon.ToBitmap()
$bmp.Save('G:\Lanweilig\Heimlich\Karikatur\MangaView\_release\__extracted.png')

$srcIco = 'G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\AppIcon.ico'
$srcIcon = New-Object System.Drawing.Icon($srcIco, 48, 48)
$srcBmp = $srcIcon.ToBitmap()
$srcBmp.Save('G:\Lanweilig\Heimlich\Karikatur\MangaView\_release\__source.png')
Write-Output "Source icon: $($srcBmp.Width)x$($srcBmp.Height)"

$extBmp = [System.Drawing.Bitmap]::FromFile('G:\Lanweilig\Heimlich\Karikatur\MangaView\_release\__extracted.png')
$srcBmp2 = [System.Drawing.Bitmap]::FromFile('G:\Lanweilig\Heimlich\Karikatur\MangaView\_release\__source.png')

$w = [Math]::Min($extBmp.Width, $srcBmp2.Width)
$h = [Math]::Min($extBmp.Height, $srcBmp2.Height)
$match = 0
$total = $w * $h
for ($y = 0; $y -lt $h; $y++) {
    for ($x = 0; $x -lt $w; $x++) {
        if ($extBmp.GetPixel($x, $y) -eq $srcBmp2.GetPixel($x, $y)) {
            $match++
        }
    }
}
$pct = [Math]::Round($match / $total * 100, 1)
Write-Output "Pixel match: $pct% ($match/$total)"

# Check if they are the same icon
if ($pct -gt 95) {
    Write-Output "SAME ICON - this is likely a Windows icon cache issue"
} else {
    Write-Output "DIFFERENT ICONS - exe has wrong icon embedded"
}
