# Color Token Replacement Script - Part 2
# Note: This script will backup the original file, then perform replacement

$xamlPath = "G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\MainWindow.xaml"
$backupPath = "G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\MainWindow.xaml.bak2"

# Backup original file
Copy-Item -Path $xamlPath -Destination $backupPath -Force

# Read file content
$content = Get-Content -Path $xamlPath -Raw -Encoding UTF8

# Color replacement mapping (Token Key -> hardcoded color)
$replacements = @{
    # Additional color replacements
    'Value="#F3F4F6"' = 'Value="{StaticResource Brush.ScrollTrack}"'
    'Value="#DDE3EA"' = 'Value="{StaticResource Brush.BorderStrong}"'
    'Value="#CBD5E1"' = 'Value="{StaticResource Brush.ScrollThumb}"'
    'Value="#C4CCD7"' = 'Value="{StaticResource Brush.BorderStrong}"'
    'Value="#F1F5F9"' = 'Value="{StaticResource Brush.ScrollTrack}"'
    'Value="#EEF2FF"' = 'Value="{StaticResource Brush.Accent}"'
    'Value="#C7D2FE"' = 'Value="{StaticResource Brush.Accent}"'
    'Value="#E6F8FAFC"' = 'Value="{StaticResource Brush.SurfaceMuted}"'
    'Value="#1F2937"' = 'Value="{StaticResource Brush.TextPrimary}"'
    'Value="#263238"' = 'Value="{StaticResource Brush.TextPrimary}"'
    'Value="#1D1714"' = 'Value="{StaticResource Brush.TextPrimary}"'
    'Value="#26201B"' = 'Value="{StaticResource Brush.TextPrimary}"'
    'Value="#FDFBF8"' = 'Value="{StaticResource Brush.Surface}"'
    'Value="#F6E7D6"' = 'Value="{StaticResource Brush.SurfaceHover}"'
    'Value="#D8C7B8"' = 'Value="{StaticResource Brush.BorderStrong}"'
    'Value="#22FFFFFF"' = 'Value="{StaticResource Brush.BorderSubtle}"'
    'Value="#F1111827"' = 'Value="{StaticResource Brush.TextPrimary}"'
}

# Execute replacement
foreach ($key in $replacements.Keys) {
    $content = $content.Replace($key, $replacements[$key])
}

# Write replaced content
$content | Set-Content -Path $xamlPath -Encoding UTF8 -NoNewline

Write-Host "Replacement completed!"
Write-Host "Original file backed up to: $backupPath"