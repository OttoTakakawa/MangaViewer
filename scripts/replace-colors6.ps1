# Color Token Replacement Script - Part 6
# Note: This script will backup the original file, then perform replacement

$xamlPath = "G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\MainWindow.xaml"
$backupPath = "G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\MainWindow.xaml.bak6"

# Backup original file
Copy-Item -Path $xamlPath -Destination $backupPath -Force

# Read file content
$content = Get-Content -Path $xamlPath -Raw -Encoding UTF8

# Color replacement mapping (Token Key -> hardcoded color)
$replacements = @{
    # Additional color replacements
    'Foreground="#6E645C"' = 'Foreground="{StaticResource Brush.TextMuted}"'
    'Foreground="#64748B"' = 'Foreground="{StaticResource Brush.TextMuted}"'
    'Fill="#223B82F6"' = 'Fill="{StaticResource Brush.Accent}"'
    'Background="#EFFFFFFF"' = 'Background="{StaticResource Brush.Surface}"'
    'BorderBrush="#55FFFFFF"' = 'BorderBrush="{StaticResource Brush.BorderSubtle}"'
    'Background="#BDF8FAFC"' = 'Background="{StaticResource Brush.SurfaceMuted}"'
    'Background="#D9111827"' = 'Background="{StaticResource Brush.TextPrimary}"'
    'BorderBrush="#A7F3D0"' = 'BorderBrush="{StaticResource Brush.StatusReading}"'
    'Background="#F8FFFC"' = 'Background="{StaticResource Brush.Surface}"'
    'BorderBrush="#BBF7D0"' = 'BorderBrush="{StaticResource Brush.StatusReading}"'
    'Background="#F8FAFC"' = 'Background="{StaticResource Brush.SurfaceMuted}"'
    'Background="#111827"' = 'Background="{StaticResource Brush.TextPrimary}"'
    'BorderBrush="#263244"' = 'BorderBrush="{StaticResource Brush.TextPrimary}"'
    'Foreground="#F8FAFC"' = 'Foreground="{StaticResource Brush.SurfaceMuted}"'
    'Background="#0B1220"' = 'Background="{StaticResource Brush.TextPrimary}"'
    'Foreground="#CBD5E1"' = 'Foreground="{StaticResource Brush.ScrollThumb}"'
}

# Execute replacement
foreach ($key in $replacements.Keys) {
    $content = $content.Replace($key, $replacements[$key])
}

# Write replaced content
$content | Set-Content -Path $xamlPath -Encoding UTF8 -NoNewline

Write-Host "Replacement completed!"
Write-Host "Original file backed up to: $backupPath"