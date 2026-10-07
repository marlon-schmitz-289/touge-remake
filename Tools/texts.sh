#!/bin/sh
# Übersetzte Originaltexte (Story-Szenen, Manga-Hörspiele) aus dem privaten Repo nach texts/ holen bzw. aktualisieren.
# Touge.csproj kompiliert texts/*.Local.cs mit. Braucht Lesezugriff auf touge-remake-texts (Collaborator).
# Eigene Änderungen an den Texten: in texts/ committen und pushen wie in jedem Repo.
set -e
cd "$(dirname "$0")/.."
if [ -d texts/.git ]; then
    git -C texts pull --ff-only
else
    git clone https://github.com/marlon-schmitz-289/touge-remake-texts.git texts
fi
