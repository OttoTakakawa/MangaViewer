param(
    [string[]]$RuntimeIdentifiers = @("osx-arm64", "osx-x64"),
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "MangaReader.Avalonia\MangaReader.Avalonia.csproj"
$outputRoot = Join-Path $root "_release_macos"

if (-not (Test-Path $project)) {
    throw "找不到 macOS/Avalonia 项目：$project"
}

if (Test-Path $outputRoot) {
    Remove-Item $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Force $outputRoot | Out-Null

foreach ($rid in $RuntimeIdentifiers) {
    $publishDir = Join-Path $outputRoot "publish-$rid"
    $appDir = Join-Path $outputRoot "MangaReader-$rid.app"
    $contentsDir = Join-Path $appDir "Contents"
    $macosDir = Join-Path $contentsDir "MacOS"
    $resourcesDir = Join-Path $contentsDir "Resources"

    dotnet publish $project `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishReadyToRun=false `
        -o $publishDir

    New-Item -ItemType Directory -Force $macosDir, $resourcesDir | Out-Null
    Copy-Item (Join-Path $publishDir "*") $macosDir -Recurse -Force

    $infoPlist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "https://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>MangaReader</string>
  <key>CFBundleDisplayName</key>
  <string>MangaReader</string>
  <key>CFBundleIdentifier</key>
  <string>com.ottotakakawa.mangareader</string>
  <key>CFBundleVersion</key>
  <string>0.1.0</string>
  <key>CFBundleShortVersionString</key>
  <string>0.1.0-macos-mvp</string>
  <key>CFBundleExecutable</key>
  <string>MangaReader.Avalonia</string>
  <key>LSMinimumSystemVersion</key>
  <string>10.15</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
"@
    Set-Content -Path (Join-Path $contentsDir "Info.plist") -Value $infoPlist -Encoding UTF8

    $zipPath = Join-Path $outputRoot "MangaReader-$rid.zip"
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }
    Compress-Archive -Path $appDir -DestinationPath $zipPath -Force
}

Write-Host "macOS release complete: $outputRoot"
