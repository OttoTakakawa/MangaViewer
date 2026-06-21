# Color Token Replacement Script - Part 7
# Note: This script will backup the original file, then perform replacement

$xamlPath = "G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\MainWindow.xaml"
$backupPath = "G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\MainWindow.xaml.bak7"

# Backup original file
Copy-Item -Path $xamlPath -Destination $backupPath -Force

# Read file content
$content = Get-Content -Path $xamlPath -Raw -Encoding UTF8

# Color replacement mapping (Token Key -> hardcoded color)
$replacements = @{
    # Additional color replacements
    'Value="#64748B"' = 'Value="{StaticResource Brush.TextMuted}"'
    'Foreground="#D6A05F"' = 'Foreground="{StaticResource Brush.Accent}"'
    'Background="#01000000"' = 'Background="{StaticResource Brush.Surface}"'
    'Foreground="#B91C1C"' = 'Foreground="{StaticResource Brush.Danger}"'
    'Background="#E5E7EB"' = 'Background="{StaticResource Brush.BorderSubtle}"'
    'Background="#99000000"' = 'Background="{StaticResource Brush.TextPrimary}"'
    'Stroke="#00000033"' = 'Stroke="{StaticResource Brush.BorderSubtle}"'
    'Background="#CC25211E"' = 'Background="{StaticResource Brush.TextPrimary}"'
}

# Execute replacement
foreach ($key in $replacements.Keys) {
    $content = $content.Replace($key, $replacements[$key])
}

# Write replaced content
$content | Set-Content -Path $xamlPath -Encoding UTF8 -NoNewline

Write-Host "Replacement completed!"
Write-Host "Original file backed up to: $backupPath"