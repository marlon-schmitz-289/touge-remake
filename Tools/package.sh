#!/bin/sh
# Builds double-clickable, self-contained copies of the game (no .NET install needed, no game data inside) into out/dist:
#   Touge.app (macOS, Apple silicon), Touge-win-x64.zip (Touge.exe), Touge-linux-x64.tar.gz (./Touge).
# Usage: Tools/package.sh [rid ...]   (default: osx-arm64 win-x64 linux-x64)
set -eu
cd "$(dirname "$0")/.."
out=out/dist
mkdir -p "$out"
[ $# -gt 0 ] || set -- osx-arm64 win-x64 linux-x64
for rid in "$@"; do
  pub="$out/publish-$rid"
  rm -rf "$pub"
  case "$rid" in win-*) gui=true ;; *) gui=false ;; esac # Windows: no console window behind the game
  dotnet publish Touge -c Release -r "$rid" --self-contained -p:DebugType=none -p:TougeGui=$gui -o "$pub"
  case "$rid" in
    osx-*)
      app="$out/Touge.app"
      rm -rf "$app"
      mkdir -p "$app/Contents"
      mv "$pub" "$app/Contents/MacOS"
      cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>Touge</string>
  <key>CFBundleDisplayName</key><string>Initial D Touge</string>
  <key>CFBundleIdentifier</key><string>local.initiald-remake.touge</string>
  <key>CFBundleExecutable</key><string>Touge</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
EOF
      if command -v codesign >/dev/null; then codesign --force --deep -s - "$app"; fi # ad-hoc: Apple silicon runs only signed code
      echo "→ $app" ;;
    win-*)
      rm -f "$out/Touge-$rid.zip"
      (cd "$pub" && zip -qr "../Touge-$rid.zip" .) && rm -rf "$pub"
      echo "→ $out/Touge-$rid.zip" ;;
    *)
      tar -czf "$out/Touge-$rid.tar.gz" -C "$pub" . && rm -rf "$pub"
      echo "→ $out/Touge-$rid.tar.gz" ;;
  esac
done
