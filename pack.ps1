#Requires -Version 5.1
<#
.SYNOPSIS
    Build & publish MangaReader.Native to _release/{version}/ directory (no zip, direct overwrite)
.PARAMETER Mode
    standalone (default, ~60MB, no .NET needed) | runtime-dep (lightweight, needs .NET 8)
.PARAMETER OutDir
    Output directory (default: project-root/_release/{version})
.PARAMETER Version
    Version string (default: git tag or 1.0.0)
#>

param(
    [ValidateSet('standalone', 'runtime-dep')]
    [string]$Mode = 'standalone',
    [string]$OutDir = '',
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectFile = Join-Path $ProjectRoot 'MangaReader.Native\MangaReader.Native.csproj'

# version: explicit param > project file > git tag > default
if (-not $Version) {
    $Version = '1.0.0'
    try {
        $projectXml = Get-Content $ProjectFile -Raw
        $projectVersionMatch = [regex]::Match($projectXml, '<Version>\s*([^<]+)\s*</Version>', 'IgnoreCase')
        if ($projectVersionMatch.Success) { $Version = $projectVersionMatch.Groups[1].Value.Trim() }
    } catch {}
    try {
        $gitTag = git -C $ProjectRoot describe --tags --abbrev=0 2>$null
        if ($Version -eq '1.0.0' -and $gitTag) { $Version = $gitTag.TrimStart('v') }
    } catch {}
}

# output to versioned subdirectory so UpdateService.EnumerateLocalPackages() can find it
if (-not $OutDir) {
    $OutDir = Join-Path $ProjectRoot "_release\$Version"
}

# preserve root _release/ data if upgrading in-place
$PreserveOutDir = Split-Path $OutDir -Parent  # e.g. _release/

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

# preserve user config & data from the versioned output dir
$preserveList = @('MangaReader_DataLocation.txt', 'MangaReader_Data')
$backupDir = Join-Path $env:TEMP "mangareader_pack_backup_$(Get-Random)"
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
foreach ($name in $preserveList) {
    $src = Join-Path $OutDir $name
    if (Test-Path $src) {
        Copy-Item $src $backupDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# clean output dir
if (Test-Path $OutDir) { Remove-Item "$OutDir\*" -Recurse -Force -ErrorAction SilentlyContinue }
else { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

# restore preserved files
if (Test-Path $backupDir) {
    Copy-Item "$backupDir\*" $OutDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $backupDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`n=== Build MangaReader.Native ($Mode) v$Version ===" -ForegroundColor Cyan

$versionProps = @(
    "-p:Version=$Version",
    "-p:AssemblyVersion=$Version",
    "-p:FileVersion=$Version",
    "-p:InformationalVersion=$Version"
)

if ($Mode -eq 'standalone') {
    & dotnet publish $ProjectFile -c Release -o $OutDir -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none -p:DebugSymbols=false `
        @versionProps 2>&1 | ForEach-Object { "$_" }
} else {
    & dotnet publish $ProjectFile -c Release -o $OutDir `
        --self-contained false `
        @versionProps 2>&1 | ForEach-Object { "$_" }
}

if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED" -ForegroundColor Red
    exit 1
}

# also symlink/unzip-friendly root copy: _release/MangaReader.Native.exe
$rootExe = Join-Path $PreserveOutDir 'MangaReader.Native.exe'
$builtExe = Join-Path $OutDir 'MangaReader.Native.exe'
if (Test-Path $builtExe) {
    Copy-Item $builtExe $rootExe -Force
    Write-Host "  [OK] Root copy: $rootExe" -ForegroundColor Green
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
