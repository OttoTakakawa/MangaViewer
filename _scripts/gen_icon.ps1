Add-Type -AssemblyName System.Drawing

$src = "G:\Lanweilig\Heimlich\Karikatur\MangaView\icon.png"
$img = [System.Drawing.Image]::From($src)
Write-Host "Source: $($img.Width)x$($img.Height)"

$sizes = @(256, 64, 48, 32, 16)
$frames = @()

foreach ($sz in $sizes) {
    $scale = [math]::Min($sz / [math]::Max($img.Width, $img.Height), 1)
    $nw = [math]::Max(1, [math]::Round($img.Width * $scale))
    $nh = [math]::Max(1, [math]::Round($img.Height * $scale))

    $bmp = New-Object System.Drawing.Bitmap($img, [System.Drawing.Size]::new($nw, $nh))
    $frames += ,$bmp
    Write-Host "Frame: ${nw}x${nh}"
}

$dst = "G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\AppIcon.ico"
$dir = Split-Path -Parent $dst
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

# Remove old file
if (Test-Path $dst) { Remove-Item $dst -Force }

# Save largest frame as ICO (single frame, but good quality)
$biggest = $sizes[0]
$scale = [math]::Min($biggest / [math]::Max($img.Width, $img.Height), 1)
$nw = [math]::Max(1, [math]::Round($img.Width * $scale))
$nh = [math]::Max(1, [math]::Round($img.Height * $scale))
$bigBmp = New-Object System.Drawing.Bitmap($img, [System.Drawing.Size]::new($nw, $nh))
$bigBmp.Save($dst, [System.Drawing.Imaging.ImageFormat]::Icon)
$bigBmp.Dispose()
$fileSize = (Get-Item $dst).Length
Write-Host "Saved single-frame ICO: $fileSize bytes at $dst"

# Now manually add remaining sizes by building ICO with multiple frames
# Read the created ICO, parse it, and prepend additional frames
