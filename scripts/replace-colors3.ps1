# Color Token Replacement Script - Part 3
# Note: This script will backup the original file, then perform replacement

$xamlPath = "G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\MainWindow.xaml"
$backupPath = "G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\MainWindow.xaml.bak3"

# Backup original file
Copy-Item -Path $xamlPath -Destination $backupPath -Force

# Read file content
$content = Get-Content -Path $xamlPath -Raw -Encoding UTF8

# Color replacement mapping (Token Key -> hardcoded color)
$replacements = @{
    # Additional color replacements
    'Stroke="#6B7280"' = 'Stroke="{StaticResource Brush.TextMuted}"'
    'Background="#EEF2F7"' = 'Background="{StaticResource Brush.ScrollTrack}"'
    'Background="#FFFFFF"' = 'Background="{StaticResource Brush.Surface}"'
    'BorderBrush="#CBD5E1"' = 'BorderBrush="{StaticResource Brush.ScrollThumb}"'
    'Stroke="#FFFFFF"' = 'Stroke="{StaticResource Brush.Surface}"'
    'Background="#E6F8FAFC"' = 'Background="{StaticResource Brush.SurfaceMuted}"'
    'Foreground="#25211E"' = 'Foreground="{StaticResource Brush.TextPrimary}"'
    'Foreground="#81756B"' = 'Foreground="{StaticResource Brush.TextMuted}"'
    'Foreground="#7E6A58"' = 'Foreground="{StaticResource Brush.TextMuted}"'
    'Foreground="#B44A36"' = 'Foreground="{StaticResource Brush.StatusMissing}"'
    'Foreground="#9A6F25"' = 'Foreground="{StaticResource Brush.StatusHidden}"'
    'Foreground="#E5E7EB"' = 'Foreground="{StaticResource Brush.BorderSubtle}"'
    'Background="#FAFFFFFF"' = 'Background="{StaticResource Brush.Surface}"'
    'Color="#111827"' = 'Color="{StaticResource Color.TextPrimary}"'
}

# Execute replacement
foreach ($key in $replacements.Keys) {
    $content = $content.Replace($key, $replacements[$key])
}

# Write replaced content
$content | Set-Content -Path $xamlPath -Encoding UTF8 -NoNewline

Write-Host "Replacement completed!"
Write-Host "Original file backed up to: $backupPath"