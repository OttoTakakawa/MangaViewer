# Color Token Replacement Script - Part 5
# Note: This script will backup the original file, then perform replacement

$xamlPath = "G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\MainWindow.xaml"
$backupPath = "G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\MainWindow.xaml.bak5"

# Backup original file
Copy-Item -Path $xamlPath -Destination $backupPath -Force

# Read file content
$content = Get-Content -Path $xamlPath -Raw -Encoding UTF8

# Color replacement mapping (Token Key -> hardcoded color)
$replacements = @{
    # Additional color replacements
    'Foreground="#8A7F77"' = 'Foreground="{StaticResource Brush.TextMuted}"'
    'Foreground="#B45309"' = 'Foreground="{StaticResource Brush.Accent}"'
    'Foreground="#8D8177"' = 'Foreground="{StaticResource Brush.TextMuted}"'
    'Foreground="#447154"' = 'Foreground="{StaticResource Brush.StatusReading}"'
    'Foreground="#475569"' = 'Foreground="{StaticResource Brush.TextMuted}"'
    'Foreground="#64748B"' = 'Foreground="{StaticResource Brush.TextMuted}"'
    'Stroke="#3B82F6"' = 'Stroke="{StaticResource Brush.Accent}"'
    'BorderBrush="#DDE4EE"' = 'BorderBrush="{StaticResource Brush.BorderSubtle}"'
    'BorderBrush="#E2E8F0"' = 'BorderBrush="{StaticResource Brush.BorderSubtle}"'
    'BorderBrush="#111827"' = 'BorderBrush="{StaticResource Brush.TextPrimary}"'
    'Color="#F8FAFC"' = 'Color="{StaticResource Color.SurfaceMuted}"'
    'Color="#F3F4F6"' = 'Color="{StaticResource Color.ScrollTrack}"'
    'Color="#EEF2F7"' = 'Color="{StaticResource Color.ScrollTrack}"'
    'Color="#2D241E"' = 'Color="{StaticResource Color.TextPrimary}"'
    'Color="#171310"' = 'Color="{StaticResource Color.TextPrimary}"'
    'Color="#E61C1713"' = 'Color="{StaticResource Color.TextPrimary}"'
    'Color="#B8191512"' = 'Color="{StaticResource Color.TextPrimary}"'
    'Color="#33110D0A"' = 'Color="{StaticResource Color.TextPrimary}"'
}

# Execute replacement
foreach ($key in $replacements.Keys) {
    $content = $content.Replace($key, $replacements[$key])
}

# Write replaced content
$content | Set-Content -Path $xamlPath -Encoding UTF8 -NoNewline

Write-Host "Replacement completed!"
Write-Host "Original file backed up to: $backupPath"