# Color Token Replacement Script
# Note: This script will backup the original file, then perform replacement

$xamlPath = "G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\MainWindow.xaml"
$backupPath = "G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\MainWindow.xaml.bak"

# Backup original file
Copy-Item -Path $xamlPath -Destination $backupPath -Force

# Read file content
$content = Get-Content -Path $xamlPath -Raw -Encoding UTF8

# Color replacement mapping (Token Key -> hardcoded color)
$replacements = @{
    # Common color replacements
    'Value="#25211E"' = 'Value="{StaticResource Brush.TextPrimary}"'
    'Value="#6B7280"' = 'Value="{StaticResource Brush.TextMuted}"'
    'Value="#FFFFFF"' = 'Value="{StaticResource Brush.Surface}"'
    'Value="#E5E7EB"' = 'Value="{StaticResource Brush.BorderSubtle}"'
    'Value="#111827"' = 'Value="{StaticResource Brush.TextPrimary}"'
    'Value="#B45309"' = 'Value="{StaticResource Brush.Accent}"'
    'Value="#D1D5DB"' = 'Value="{StaticResource Brush.BorderStrong}"'
    'Value="#F8FAFC"' = 'Value="{StaticResource Brush.SurfaceMuted}"'
    'Value="#8D8177"' = 'Value="{StaticResource Brush.TextMuted}"'
    'Value="#9CA3AF"' = 'Value="{StaticResource Brush.Dim}"'
    'Value="#E4B95F"' = 'Value="{StaticResource Brush.Favorite}"'
    'Value="#9F1239"' = 'Value="{StaticResource Brush.Danger}"'
    'Value="#FFF1F2"' = 'Value="{StaticResource Brush.DangerSurface}"'
    'Value="#FECDD3"' = 'Value="{StaticResource Brush.DangerBorder}"'
    'Value="#EEF2F7"' = 'Value="{StaticResource Brush.ScrollTrack}"'
    'Value="#CBD5E1"' = 'Value="{StaticResource Brush.ScrollThumb}"'
    'Value="#94A3B8"' = 'Value="{StaticResource Brush.ScrollThumbHover}"'
    'Value="#B44A36"' = 'Value="{StaticResource Brush.StatusMissing}"'
    'Value="#9A6F25"' = 'Value="{StaticResource Brush.StatusHidden}"'
    'Value="#447154"' = 'Value="{StaticResource Brush.StatusReading}"'
    'Value="#3B5A7C"' = 'Value="{StaticResource Brush.StatusFinished}"'
    'Value="#F0EDE8"' = 'Value="{StaticResource Brush.SurfaceHover}"'
    'Value="#E8E2D8"' = 'Value="{StaticResource Brush.SurfaceSelected}"'
    'Value="#F8F2EC"' = 'Value="{StaticResource Brush.TagBg}"'
    'Value="#E5E0D8"' = 'Value="{StaticResource Brush.TagBorder}"'
    'Value="#D1CCC2"' = 'Value="{StaticResource Brush.BorderStrong}"'
}

# Execute replacement
foreach ($key in $replacements.Keys) {
    $content = $content.Replace($key, $replacements[$key])
}

# Write replaced content
$content | Set-Content -Path $xamlPath -Encoding UTF8 -NoNewline

Write-Host "Replacement completed!"
Write-Host "Original file backed up to: $backupPath"