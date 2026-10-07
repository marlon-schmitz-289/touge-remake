#!/bin/sh
# Self-contained builds zum Weitergeben (ohne installiertes .NET), nach out/dist/:
#   Touge-osx-arm64.zip                       (Touge.app, ad-hoc signiert; nur auf macOS baubar)
#     osx-x64 (Intel-Mac) baut auch, stürzt unter Rosetta aber beim Fensterstart ab (ohne Fenster läuft es) – daher nur auf Wunsch
#   Touge-win-x64.zip                         (Ordner mit Touge.exe, ohne Konsolenfenster)
#   Touge-linux-x64.tar.gz                    (Ordner mit Touge, touge.png, install-desktop.sh)
# Aufruf: Tools/publish.sh [rid …]   (Standard: alle; auf Linux ohne osx-*)
# Spieldaten sind NICHT dabei: das Spiel liest sie zur Laufzeit aus der ISO des Spielers.
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
dist="$root/out/dist"
version=${VERSION:-0.1.$(git -C "$root" rev-list --count HEAD 2>/dev/null || echo 0)} # VERSION: Release-Workflow (Tag ohne v)
if [ $# -eq 0 ]; then
    set -- win-x64 linux-x64
    [ "$(uname)" = Darwin ] && set -- osx-arm64 "$@"
fi
mkdir -p "$dist"

for rid in "$@"; do
    echo "== $rid"
    pub="$dist/$rid"
    rm -rf "$pub"
    dotnet publish "$root/Touge/Touge.csproj" -c Release -r "$rid" --self-contained -o "$pub" \
        -p:DebugType=none -p:Version="$version" --nologo -v quiet -warnaserror
    case "$rid" in
    osx-*)
        app="$dist/$rid-app/Touge.app"
        rm -rf "$dist/$rid-app"
        mkdir -p "$app/Contents/Resources"
        mv "$pub" "$app/Contents/MacOS"
        cp "$root/Tools/Icon/icon.icns" "$app/Contents/Resources/Touge.icns"
        cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
    <key>CFBundleName</key><string>Touge</string>
    <key>CFBundleDisplayName</key><string>Touge</string>
    <key>CFBundleIdentifier</key><string>local.initiald-remake.touge</string>
    <key>CFBundleExecutable</key><string>Touge</string>
    <key>CFBundleIconFile</key><string>Touge</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$version</string>
    <key>CFBundleVersion</key><string>$version</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.racing-games</string>
    <key>NSHighResolutionCapable</key><true/>
</dict></plist>
EOF
        # ad-hoc: no Apple ID needed; downloaded copies still need the quarantine step (README)
        codesign --force --deep --sign - "$app"
        codesign --verify --deep --strict "$app"
        rm -f "$dist/Touge-$rid.zip"
        ditto -c -k --keepParent "$app" "$dist/Touge-$rid.zip"
        ;;
    win-*)
        rm -f "$dist/Touge-$rid.zip"
        (cd "$dist" && mv "$rid" Touge && zip -qr "Touge-$rid.zip" Touge && mv Touge "$rid")
        ;;
    linux-*)
        cp "$root/Tools/Icon/icon.png" "$pub/touge.png"
        cat > "$pub/install-desktop.sh" <<'EOF'
#!/bin/sh
# Trägt Touge ins Anwendungsmenü ein (~/.local/share/applications), Pfad = dieser Ordner.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
# .desktop-Escaping: Strings \ -> \\; in Exec zusätzlich "-Quoting (\ " ` $) und % -> %%
str=$(printf '%s' "$here" | sed 's/\\/\\\\/g')
exe=$(printf '%s' "$here" | sed -e 's/\\/\\\\\\\\/g' -e 's/["`$]/\\\\&/g' -e 's/%/%%/g')
mkdir -p ~/.local/share/applications
cat > ~/.local/share/applications/touge.desktop <<DESKTOP
[Desktop Entry]
Type=Application
Name=Touge
Comment=Initial D Special Stage remake
Exec="$exe/Touge" %f
Path=$str
Icon=$str/touge.png
Terminal=false
Categories=Game;
DESKTOP
echo "Touge im Anwendungsmenü eingetragen ($here)"
EOF
        chmod +x "$pub/install-desktop.sh" "$pub/Touge"
        (cd "$dist" && mv "$rid" Touge && tar -czf "Touge-$rid.tar.gz" Touge && mv Touge "$rid")
        ;;
    esac
done
ls -la "$dist"
