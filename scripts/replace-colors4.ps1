# Color Token Replacement Script - Part 4
# Note: This script will backup the original file, then perform replacement

$xamlPath = "G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\MainWindow.xaml"
$backupPath = "G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\MainWindow.xaml.bak4"

# Backup original file
Copy-Item -Path $xamlPath -Destination $backupPath -Force

# Read file content
$content = Get-Content -Path $xamlPath -Raw -Encoding UTF8

# Color replacement mapping (Token Key -> hardcoded color)
$replacements = @{
    # Additional color replacements
    'Foreground="#CFB9A6"' = 'Foreground="{StaticResource Brush.TextMuted}"'
    'Foreground="#FAF5EF"' = 'Foreground="{StaticResource Brush.Surface}"'
    'Foreground="#D6C7BA"' = 'Foreground="{StaticResource Brush.TextMuted}"'
    'Foreground="#F0E5DA"' = 'Foreground="{StaticResource Brush.TextMuted}"'
    'Foreground="#F59E0B"' = 'Foreground="{StaticResource Brush.Accent}"'
    'Foreground="#7B7068"' = 'Foreground="{StaticResource Brush.TextMuted}"'
    'Foreground="#9A5A36"' = 'Foreground="{StaticResource Brush.TextMuted}"'
    'Background="#F8E7B4"' = 'Background="{StaticResource Brush.Favorite}"'
    'BorderBrush="#E4B95F"' = 'BorderBrush="{StaticResource Brush.Favorite}"'
    'BorderBrush="#E5E7EB"' = 'BorderBrush="{StaticResource Brush.BorderSubtle}"'
    'Foreground="#9CA3AF"' = 'Foreground="{StaticResource Brush.Dim}"'
    'Foreground="#111827"' = 'Foreground="{StaticResource Brush.TextPrimary}"'
    'Foreground="#6B7280"' = 'Foreground="{StaticResource Brush.TextMuted}"'
    'Foreground="#25211E"' = 'Foreground="{StaticResource Brush.TextPrimary}"'
}

# Execute replacement
foreach ($key in $replacements.Keys) {
    $content = $content.Replace($key, $replacements[$key])
}

# Write replaced content
$content | Set-Content -Path $xamlPath -Encoding UTF8 -NoNewline

Write-Host "Replacement completed!"
Write-Host "Original file backed up to: $backupPath"