#!/bin/sh
# Übersetzungen (Story-Szenen, Manga-Hörspiele) liegen in texts/ gepackt als <sprache>.tl, nicht im Klartext lesbar.
#   Tools/texts.sh unpack   texts/*.tl  -> texts/*.json (Klartext zum Bearbeiten, gitignoriert)
#   Tools/texts.sh pack     texts/*.json -> texts/*.tl  (danach die .tl committen)
set -e
cd "$(dirname "$0")/.."
case "$1" in
    pack) for f in texts/*.json; do dotnet run --project Touge -- --texts pack "$f" "${f%.json}.tl"; echo "$f -> ${f%.json}.tl"; done ;;
    unpack) for f in texts/*.tl; do dotnet run --project Touge -- --texts unpack "$f" "${f%.tl}.json"; echo "$f -> ${f%.tl}.json"; done ;;
    *) echo "usage: Tools/texts.sh pack|unpack" >&2; exit 1 ;;
esac
