#Requires -Version 5.1
<#
.SYNOPSIS
    Build & publish MangaReader.Native to _release/ directory (no zip, direct overwrite)
.PARAMETER Mode
    standalone (default, ~60MB, no .NET needed) | runtime-dep (lightweight, needs .NET 8)
.PARAMETER OutDir
    Output directory (default: project-root/_release)
#>

param(
    [ValidateSet('standalone', 'runtime-dep')]
    [string]$Mode = 'standalone',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectFile = Join-Path $ProjectRoot 'MangaReader.Native\MangaReader.Native.csproj'

if (-not $OutDir) {
    $OutDir = Join-Path $ProjectRoot '_release'
}

# version from git
$Version = '1.0.0'
try {
    $gitTag = git -C $ProjectRoot describe --tags --abbrev=0 2>$null
    if ($gitTag) { $Version = $gitTag.TrimStart('v') }
} catch {}

# Generate AppIcon.ico from icon.png (32-bit multi-frame)
Write-Host "`n  [GEN] Generating AppIcon.ico from icon.png..." -ForegroundColor Yellow
& python (Join-Path $ProjectRoot '_scripts/gen_icon.py')
if ($LASTEXITCODE -ne 0) {
    Write-Host "  [WARN] Icon generation failed, using existing AppIcon.ico" -ForegroundColor DarkYellow
}

# kill running instance if any
$running = Get-Process -Name 'MangaReader.Native' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "  [KILL] Closing running MangaReader.Native (PID $($running.Id))..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Seconds 1
}

# preserve user config & data
$preserveList = @('MangaReader_DataLocation.txt', 'MangaReader_Data')
$backupDir = Join-Path $env:TEMP "mangareader_pack_backup_$(Get-Random)"
foreach ($name in $preserveList) {
    $src = Join-Path $OutDir $name
    if (Test-Path $src) {
        $dst = Join-Path $backupDir $name
        Copy-Item $src $dst -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# clean & publish directly to output dir
if (Test-Path $OutDir) { Remove-Item "$OutDir\*" -Recurse -Force -ErrorAction SilentlyContinue }
else { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

# restore preserved files
if (Test-Path $backupDir) {
    Copy-Item "$backupDir\*" $OutDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $backupDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`n=== Build MangaReader.Native ($Mode) v$Version ===" -ForegroundColor Cyan

if ($Mode -eq 'standalone') {
    & dotnet publish $ProjectFile -c Release -o $OutDir -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none -p:DebugSymbols=false 2>&1 | ForEach-Object { "$_" }
} else {
    & dotnet publish $ProjectFile -c Release -o $OutDir `
        --self-contained false 2>&1 | ForEach-Object { "$_" }
}

if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED" -ForegroundColor Red
    exit 1
}

# attach README
$ReadmeSrc = Join-Path $ProjectRoot 'README.md'
if (Test-Path $ReadmeSrc) {
    Copy-Item $ReadmeSrc (Join-Path $OutDir 'README.txt')
    Write-Host '  [OK] README attached' -ForegroundColor Green
}

Write-Host "`n=== Done ===" -ForegroundColor Green
Write-Host "  Version : v$Version   Mode: $Mode" -ForegroundColor White
Write-Host "  Output  : $OutDir" -ForegroundColor Cyan
Write-Host "  Run     : $(Join-Path $OutDir 'MangaReader.Native.exe')" -ForegroundColor White
