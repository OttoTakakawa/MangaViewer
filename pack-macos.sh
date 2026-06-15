#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$ROOT/MangaReader.Avalonia/MangaReader.Avalonia.csproj"
OUTPUT_ROOT="$ROOT/_release_macos"
CONFIGURATION="${CONFIGURATION:-Release}"
if [ "$#" -gt 0 ]; then
  RIDS=("$@")
else
  RIDS=("osx-arm64" "osx-x64")
fi

if [ ! -f "$PROJECT" ]; then
  echo "macOS/Avalonia project not found: $PROJECT" >&2
  exit 1
fi

rm -rf "$OUTPUT_ROOT"
mkdir -p "$OUTPUT_ROOT"

for RID in "${RIDS[@]}"; do
  PUBLISH_DIR="$OUTPUT_ROOT/publish-$RID"
  APP_DIR="$OUTPUT_ROOT/MangaReader-$RID.app"
  CONTENTS_DIR="$APP_DIR/Contents"
  MACOS_DIR="$CONTENTS_DIR/MacOS"
  RESOURCES_DIR="$CONTENTS_DIR/Resources"

  dotnet publish "$PROJECT" \
    -c "$CONFIGURATION" \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:PublishReadyToRun=false \
    -o "$PUBLISH_DIR"

  mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"
  cp -R "$PUBLISH_DIR"/. "$MACOS_DIR"/

  cat > "$CONTENTS_DIR/Info.plist" <<'PLIST'
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
PLIST

  chmod +x "$MACOS_DIR/MangaReader.Avalonia" || true
  (cd "$OUTPUT_ROOT" && zip -qry "MangaReader-$RID.zip" "MangaReader-$RID.app")
done

echo "macOS release complete: $OUTPUT_ROOT"
