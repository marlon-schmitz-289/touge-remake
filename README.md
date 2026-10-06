# Initial D Remake

Privater Nachbau (eigene C#-Engine). Plan: [PLAN.md](PLAN.md), Formate: [FORMATS.md](FORMATS.md).

Assets kommen zur Laufzeit aus der eigenen ISO (SLPM-65268), nie ins Repo.

## Installieren (ohne Terminal)

Fertige Pakete baut `Tools/publish.sh` (self-contained, kein .NET nötig) nach `out/dist/`:

```sh
Tools/publish.sh                    # alle: auf macOS osx-arm64 + win-x64 + linux-x64, auf Linux win-x64 + linux-x64
Tools/publish.sh osx-arm64          # nur eins (osx-x64 = Intel-Mac auf Wunsch; stürzt unter Rosetta beim Fensterstart ab, ungetestet auf echtem Intel)
python3 Tools/Icon/make_icon.py     # Icon neu erzeugen (eigene Grafik, Pillow; .icns nur auf macOS)
```

Die Pakete enthalten **keine Spieldaten** – jeder braucht seine eigene ISO.

- **macOS** (`Touge-osx-arm64.zip`): entpacken, `Touge.app` nach Programme ziehen, doppelklicken. Die App ist nur ad-hoc signiert
  (keine Apple-ID); eine heruntergeladene Kopie blockiert Gatekeeper beim ersten Start: Rechtsklick → Öffnen → Öffnen, bzw. ab
  macOS 15 Systemeinstellungen → Datenschutz & Sicherheit → „Dennoch öffnen“. Oder einmal im Terminal:
  `xattr -dr com.apple.quarantine /Applications/Touge.app`. Beim ersten Start fragt macOS, ob Touge auf Downloads/Schreibtisch/
  Dokumente zugreifen darf (Suche nach der ISO) – erlauben, die Liste füllt sich danach; ad hoc signiert fragt es nach jedem Neubau erneut.
- **Windows** (`Touge-win-x64.zip`): entpacken, `Touge\Touge.exe` starten (kein Konsolenfenster). SmartScreen „Unbekannter
  Herausgeber“: Weitere Informationen → Trotzdem ausführen. Braucht einen Vulkan-fähigen Grafiktreiber.
- **Linux** (`Touge-linux-x64.tar.gz`): `tar xzf Touge-linux-x64.tar.gz`, dann `Touge/Touge` starten; `Touge/install-desktop.sh`
  trägt es ins Anwendungsmenü ein (`~/.local/share/applications/touge.desktop`). Braucht Vulkan (`libvulkan1` + Mesa/Treiber).

Ohne Argument (Doppelklick) öffnet das Spiel den Launcher zur ISO-Auswahl (siehe [Starten](#starten)), danach startet es die
gemerkte ISO direkt. Unter Windows/Linux kann die ISO auch auf `Touge.exe`/`Touge` gezogen werden (ist sie verschoben oder die
falsche, zeigt der Launcher den Grund); als erstes Argument geht sie überall (`Touge.app/Contents/MacOS/Touge "<iso>"`), alle
Kommandozeilen-Flags gelten wie unten.
Wird das Spiel so gestartet (ohne Argument oder nur mit der ISO), schreibt es seine Ausgabe zusätzlich nach `touge.log` im
Profilordner (macOS `~/Library/Application Support/InitialDRemake/`, Windows `%APPDATA%\InitialDRemake\`, Linux
`~/.config/InitialDRemake/`; der vorige Lauf bleibt als `touge.prev.log`) – dort nachsehen, wenn etwas nicht startet. Schriften/Assets liegen neben dem Programm
(`AppContext.BaseDirectory`), das Arbeitsverzeichnis ist egal.

## Projekte

| Projekt | Inhalt |
|---|---|
| `Penelope` | GPU-Abstraktion (Vulkan/Metal/OpenGL), aus MEFactory übernommen |
| `Kansei` | Engine: Fenster, Input, Loop mit fester Tick-Rate, World- und Car-Renderer, 2D-Overlay (HUD), Audio (OpenAL Soft: Musik-Stream, SFX-Pool, Loop-Stimmen mit Pitch/Gain, Master/Musik/SFX-Lautstärke, Offline-Mix über Loopback) |
| `Kansei.Physics` | `IGround`, `TriangleGround` (Raycast + Wände über XZ-Grid), Fahrzeugphysik `Vehicle` (FR/MR, FF, 4WD) + `CarSpec` (Default AE86), `CarSpecs` (alle 32 Autos), `LinePilot` (fährt die Fahrlinie ab) |
| `Touge.Formats` | Spielformate: ISO, AFS, PAC, LZ, GIM, CMD/SMD, Kollision, Fahrlinie, Lack, Audio (ADX, VAG/SPU-ADPCM, SYSSE-Bank, Sony HD/BD in MRG, SECT-Kurven), WAV-Export |
| `Touge.Formats.Cli` | `idss` – Formate untersuchen/exportieren |
| `Touge` | Das Spiel (derzeit: alle 32 Autos auf jeder Strecke fahren mit Original-Motor-/Reifen-/Crash-Sound und Eurobeat, Freiflug per F1) |

## Starten

Zum Doppelklicken: Pakete aus [Installieren](#installieren-ohne-terminal). Ohne Paket: `dotnet run --project Touge`. (Das gebaute `Touge` aus `Touge/bin/…` ist nicht
eigenständig und findet ein .NET unter `~/.dotnet` ohne `DOTNET_ROOT` nicht.) Ohne Argumente öffnet sich der **Launcher** (`Touge/Launcher.cs`, `Ui/LauncherScreen.cs`) im Stil des Front-Ends – ohne Daten der Disc, also noch ohne Original-Sounds:
- Er sucht im Hintergrund (höchstens 4 s, je Ort nur eine Ordnerebene tief) in Downloads, Schreibtisch, Dokumente, Home, aktuellem und App-Ordner
  sowie auf eingehängten Laufwerken (`/Volumes`, `/media`, `/run/media`, `/mnt`, Laufwerksbuchstaben) nach `*.iso` und zeigt nur passende Discs
  (ISO 9660, `SYSTEM.CNF` bootet `SLPM_652.68`, `CDVD/DATA` da, Image nicht abgeschnitten) mit Titel, Version und Größe.
- **BROWSE FOR THE ISO**: Dateibrowser im Spiel (Laufwerke/Ordner/`*.iso`, ←/„..“ eine Ebene hoch, Tab oder Klick ins PATH-Feld zum Tippen,
  Strg+V/Cmd+V fügt einen Pfad ein – auch aus dem Terminal mit `\ `-Escapes, als `file://`-URL oder nur als Dateiname wie nach Cmd+C im
  Finder, der dann im aktuellen Ordner bzw. den Suchorten gesucht wird –, Enter öffnet den Ordner bzw. prüft die Datei). Symlinks auf die ISO gehen. **SYSTEM FILE DIALOG**: Dateidialog des Systems (osascript,
  zenity/kdialog, PowerShell/WinForms), fehlt er, sagt der Launcher das. Eine `.iso` aufs Fenster ziehen oder einen Pfad einfügen wählt sie auch.
- Falsche Dateien (fehlt, unlesbar, keine Disc, anderes Spiel, abgeschnitten, Ordner) stehen rot im Panel, nichts stürzt ab.
- Die gewählte Disc startet das Spiel im selben Fenster und wird in `last-disc.txt` neben `settings.json` (App-Daten-Ordner des Benutzers,
  z. B. `~/Library/Application Support/InitialDRemake/` bzw. `%APPDATA%\InitialDRemake\`) gemerkt; der nächste Start geht direkt ins Spiel.
  Andere Disc: Optionen → **GAME DISC** → CHANGE GAME DISC (zurück in den Launcher) oder einmal mit `--launcher` starten.
- Mit ISO als erstem Argument (oder `INITIALD_ISO`) läuft alles wie bisher, ohne Launcher.

```sh
dotnet run --project Touge [-- --launcher]   # Launcher (ohne --launcher startet die gemerkte Disc direkt)
dotnet run --project Touge -- --shot out/proof/launcher/discs.png | --menu browse [--browse <ordner>] --shot … | --drop <datei.iso> --shot …   # Launcher-Bilder: Liste nach der Suche, Dateibrowser, gezogene Datei (Fehler bzw. Start)
dotnet run --project Touge -- --data-dir <ordner> [--menu options:gamedisc] --shot out/proof/l.png   # gemerkte Disc aus <ordner>/last-disc.txt: Bild des Front-Ends bzw. der Optionsseite GAME DISC
TOUGE_AUTOPICK=1|change out/dist/osx-arm64-app/Touge.app/Contents/MacOS/Touge   # Ende-zu-Ende ohne Argumente (wie Finder): wählt nach der Suche die erste gefundene Disc; change verlässt das Spiel nach 8 s wie GAME DISC (Ablauf in touge.log)
dotnet run --project Touge -- "<pfad>/Initial D - Special Stage (Japan) (v2.00).iso"   # Front-End (Hinweis, Karten, Titel, Hauptmenü) → Kurs, Auto, Rennen, Ergebnis, Rekorde, Optionen
dotnet run --project Touge -- "<pfad>/Initial D - Special Stage (Japan) (v2.00).iso" [AKINA_DAY|AKINA_NIT|USUI_NIT|…]   # direkt fahren, ohne Menü
dotnet run --project Touge -- "<iso>" AKINA_NIT --menu boot|logo|disclaimer|title|mode|quit|course|route|time|weather|maker[:n]|car[:n]|gearbox|intro|pause|records|guide|guide-list|guide-talk|options[:gamesetting|hud|screen|graphics|sound|playlist|controller|dualsense] --shot out/proof/ui_car.png [--shot-size 3200x1800]   # Menü-Bild (options:<seite> öffnet eine Optionsseite, maker:<n> mit Hersteller n hervorgehoben, car:<n> die Autos des Herstellers von --car mit Zeile n, z. B. --car IMP --menu car:2 = das gesperrte IMP3)
dotnet run --project Touge -- "<iso>" --flow out/proof   # ganzer Ablauf per Skript im Fenster (Titel → Auswahl → Laden → Countdown → Rennen mit Pilot in 16× → Pause → Ziel → Ergebnis → Rekorde → Optionen → Car Guide), PNG je Schritt
dotnet run --project Touge -- "<iso>" --flow out/proof/legend/flow --legend [--car FD3S]   # Legend of the Streets per Skript: Akina, zwei Battles (Autopilot, 16×), Leiter danach, PNG je Schritt
dotnet run --project Touge -- "<iso>" --legend-sim [--legend-progress out/progress.json] [--car FD3S] [--player-skill 0.5]   # Legend ohne Fenster: Autopilot (als Spieler mit Können k, sonst 0,8; mit Gummiband) fährt jede Rivalenleiter hoch (Niederlage beendet den Kurs), Freischaltungen + Bilanz, Fortschritt als JSON
dotnet run --project Touge -- "<iso>" --cam-bench [AKINA,IROHA_DAY,…] [--car R34]   # Kameras ohne Fenster (~25 s für 6 Kurse × 2 Richtungen): Pilot mit Handbremsdrifts bis ins Ziel, 144-Hz-Bilder wie im Spiel interpoliert; je CHASE/FAR CHASE live und im Replay, Showcase-Orbit (alle ~100 m, 72 Winkel) und TV: Bilder mit Kurs zwischen Auto und Auge (blocked), ohne Boden unter dem Auge (void, aus der Karte), Fläche näher als die Near-Plane (clip), Straße 20 m voraus verdeckt/außerhalb (hidden; bergab ab 5 % Gefälle eigens, und davon hinter der Karosserie), Abstand, Ruckeln (Beschleunigung des Auges relativ zum Auto und der Blickrichtung, RMS/p99/max), TV-Schnitte (< 1,5 s), bei TV heißt hidden: Automitte außerhalb 90 % des Bildes; Replay-Tempo ¼–4×: Beschleunigung des gezeigten Autos; dazu Stellen für Bilder (steilstes Gefälle, engste Kurven, schlimmste Orbit-Winkel)
dotnet run --project Touge -- "<iso>" --ai-bench solo|drift|battle|corners[:AKINA,IROHA] [--car R34]   # KI ohne Fenster vermessen (~40 s je Matrix): solo = Zeit gegen H (der Pilot mit Können 1 und dem Driftstil des Autos, „guter Spieler“) je Kurs/Richtung/Auto/Können, Wandtreffer (wo), Schräglauf je Kurventyp, Fehler je 5 km; drift = jeder Driftstil gegen Grip (gehalten/abgebrochen/verblasst, β, Kurvenzeit, Ausgangstempo, Treffer je Drift, Dreher); battle = Autopilot 0,8 gegen fünf Figuren und den AE86 mit 0,5/0,65/1, Race und Lead/Chase, Tempo-Delta je Paarung aus Solo-Läufen, Überholquote nach Delta, Führungswechsel, Kontakte; corners = Kurvenarten, Straßenbreite, Überholzonen; human:<datei.rpl> misst einen aufgezeichneten Lauf daneben. Umgebung: AIBENCH_TRACE=von:bis (Strecke loggen), AIBENCH_SKILLS/AIBENCH_DRIFTS/AIBENCH_MISTAKES/AIBENCH_NOH (Matrix), AIBENCH_HITS (jeden Wandtreffer mit Zustand), AIBENCH_RIVALS/AIBENCH_RULES (z. B. LeadChase1), AIBENCH_BTRACE=von:bis, AIBENCH_ATTEMPTS, AIBENCH_CONTACTS (Battles)
dotnet run --project Touge -- "<iso>" --menu legend|legend-rivals|legend-card[:AKINA/takumi] [--legend-progress <json>|--data-dir <ordner>] --shot out/proof/l.png   # Legend-Schritt als Bild (Fortschritt aus der Datei bzw. dem Ordner, sonst leer)
dotnet run --project Touge -- "<iso>" --jukebox 720   # Rennmusik-Jukebox offline ohne Fenster: Zufallsfolge, M, Menüpause, Titelende → nächster, Log
dotnet run --project Touge -- "<iso>" --frontend-capture out/proof/fe.wav   # ganzer Menüablauf per Skript offline: Original-SE/BGM als WAV + Log aller Auslöser
dotnet run --project Touge -- "<iso>" --shot out/akina.png   # ein Frame als PNG, dann Ende
dotnet run --project Touge -- "<iso>" --ground out/g.png [--at 300] [--reverse]   # Kollision: Raycast-Timing + Draufsicht mit Wänden, Sperren, Fahrlinie/Auslauf; Ausschnitte _at300/_start/_goal
dotnet run --project Touge -- "<iso>" --shot out/ae86.png --orbit 35[:20]   # Kamera ums Auto (0 vorne, 90 links, 180 hinten), optional Abstand in m (Standard 5,5)
dotnet run --project Touge -- "<iso>" AKINA_NIT --lights off|low|high --shot out/l.png   # Autolicht beim Start (Standard: nachts/Regen/Nebel Abblendlicht, klarer Tag aus)
dotnet run --project Touge -- "<iso>" --autodrive 60 [--shot out/ad.png]   # Pilot fährt 60 s die Fahrlinie ab, Log pro Sekunde
dotnet run --project Touge -- "<iso>" AKINA_DAY --at 710 --autodrive 40 [--ram] [--reverse]   # hinter dem Ziel: Auslauf bis vor die Endsperre bzw. mit --ram Vollgas in die Sperre; Zusammenfassung (Weg hinter dem Ziel, Abstand zur Sperre, Wandkontakt, Höhe)
dotnet run --project Touge -- "<iso>" AKINA_DAY --reverse [--autodrive 60|--shot …|--ground …]   # Gegenrichtung (bergauf): CRS_COLI_<KURS>_1 + CRS_DRV_<KURS>_O, Start am anderen Ende
dotnet run --project Touge -- "<iso>" --hud overview --shot out/map.png   # Minimap-Modus beim Start (north|overview|off)
dotnet run --project Touge -- "<iso>" --hud-scale 130 --shot-size 1920x1080 --shot out/h.png   # HUD-Größe 80–130 % (Menü: Options → HUD SIZE)
dotnet run --project Touge -- "<iso>" AKINA_NIT --bench 900 [--quality off] [--offscreen]   # Pilot fährt in Echtzeit (Fenster) bis ins Ziel (höchstens 900 s): pro Sekunde Position, fps, Frametime, CPU-ms, Draws, Effekte, GC, Speicher, macOS-Wärmezustand; am Ende avg/p99/max, Frames > 18/25 ms, je 500 m. --offscreen (Metal): ohne Display-Takt, Frametime = GPU-Zeit
dotnet run --project Touge -- "<iso>" … --hidden   # oder TOUGE_HIDDEN=1: Fenster nie sichtbar, App ohne Dock-Symbol/Fokus, Bild offscreen (Metal), stumm (auch --mute allein) – für Screenshot-/Test-Läufe im Hintergrund
dotnet run --project Touge -- "<iso>" AKINA_RIN --flow out/proof --bench 900   # wie ein Spieler: Front-End → Kurs/Zeit/Wetter wie angegeben → 24 Autovorschauen + Lackwechsel → Rennen mit --bench-Log
dotnet run --project Touge -- "<iso>" --at 300 --autodrive 5.2 --drift --shot out/drift.png   # --drift: Pilot reißt alle 7 s (ab 4,5 s) einen Handbremsdrift (auch mit --bench)
dotnet run --project Touge -- "<iso>" --autodrive 30 --audio-capture out/a.wav 30 [--no-music]   # Spielton offline als WAV + Auswertung (Pitch↔Drehzahl, Quietschen↔Schlupf, Pegel, Allokationen)
dotnet run --project Touge -- "<iso>" --car FD3S --paint 2   # Auto (HCAR-Name oder Index 0–31) und CAR_ENV-Lackfarbe (0 = Standard), gilt für alle Modi
dotnet run --project Touge -- "<iso>" --car AE86T --livery stock   # Aufkleber/Kennzeichen: rival (Standard: Auto der Anime-Figur, z. B. Tofu-Schriftzug, RedSuns, Emperor), stock (Serienauto des Spiels), none (ohne Aufkleber/Kennzeichen)
dotnet run --project Touge -- "<iso>" --hud off --cars out/proof/c_cars.png [--orbit 145]   # Kontaktbogen aller 32 Autos (4 × 8, Reihenfolge wie unten, Blickwinkel 35° bzw. --orbit), dann Ende
dotnet run --project Touge -- "<iso>" --zfight [AKINA]   # Z-Fighting-Kandidaten aller Kurse/Autos (fast koplanar, überlappend), gruppiert je Batch-Paar
dotnet run --project Touge -- "<iso>" USUI0_RIN --flicker out/proof/fl [--at n]   # Flackern messen: 8 Fahrlinienpunkte, 8 Winkel ums Auto, 8 Fundstellen, je 3× mit anderer Rundung (Schwelle nach Bildhelligkeit); dazu „motion“: dieselben Punkte/Winkel mit Auto, Kamera 3× je 1 cm vor, je mit/ohne SSR – zählt Pixel, deren Spiegelungsanteil nicht gleichmäßig mitläuft
dotnet run --project Touge -- "<iso>" --sun --shot out/sun.png   # freie Kamera hinter dem Auto, Blick zur Sonne (Blendung prüfen)
dotnet run --project Touge -- "<iso>" AKINA_NIT --fog [--autodrive 25 --shot out/fog.png]   # Wetter FOG: dichter Nebel (Sicht ~60 m) über dem Tag- oder Nachtkurs
dotnet run --project Touge -- "<iso>" AKINA_RIN --render-scale 50 [--bench 15 --offscreen|--shot …]   # 3D-Auflösung in % des Fensters (Option RENDER SCALE), HUD bleibt scharf
dotnet run --project Touge -- "<iso>" [--input-debug] [--sim-wheel]   # Eingabe-Overlay (Geräte, Rohachsen/-tasten, gelesene Lenkung/Pedale, FFB-Anteile); virtuelles Lenkrad ohne Hardware (beide auch beim normalen Start mit Menüs)
dotnet run --project Touge -- "<iso>" AKINA_DAY --menu controls:keyboard|pad|gamepad|wheel [--sim-wheel] --shot out/proof/input_controls_wheel.png   # Steuerungsseite als Bild
dotnet run --project Touge -- "<iso>" AKINA_NIT --menu options:playlist --hint-device pad|wheel|keys --shot out/proof/hints/playlist_pad.png   # Tastenhinweise eines Geräts erzwingen (sonst: zuletzt benutztes Gerät)
dotnet run --project Touge -- "<iso>" AKINA_NIT --battle keisuke[@0.9] [--rule race|chase] [--lead player|rival] [--car AE86T]   # Schnellbattle gegen die KI (@: Können des Rivalen): Telop „VS …“, 3-2-1-GO, Battle, YOU WIN/LOSE, Ergebnis
dotnet run --project Touge -- "<iso>" IROHA_DAY --battle takumi --autodrive 420 [--player-skill 0.5[,aggr,drift]] [--ai-level easy|normal|hard|legend] [--rubber-band]   # ohne Fenster: Autopilot (Spielerauto, Können k, mit Gummiband wie gegen einen Menschen) gegen die KI, Log je Sekunde (Abstand s/m, Führung, KI-Modus, Kontakte) + Ergebnis; BATTLE_DRIFTS=1 loggt die Drifts des Rivalen, BATTLE_ATTEMPTS=1 die Überholversuche
dotnet run --project Touge -- "<iso>" IROHA_DAY --battle itsuki --autodrive 110 --shot out/proof/b.png [--battle-result]   # Bild nach dem Battle: Zielbanner bzw. Ergebnisblatt
dotnet run --project Touge -- "<iso>" --menu story[:n[:scene[:teil[:zeile]]|:show[:i[:sek]]|:race|:end]] [--progress 12] --shot out/proof/s.png   # STORY: Kapitelwahl, Szene, Rennstart von Kapitel n oder THE END (--progress: Kapitel 0…n−1 geschafft, nur Testlauf; mit gespeichertem Fortschritt ignoriert)
dotnet run --project Touge -- "<iso>" --flow out/proof/story --story --progress 7   # STORY-Ablauf im Fenster: Wahl → Szene → Battle (Pilot) → Ergebnis → Szene danach; verlorenes Kapitel mit RETRY
dotnet run --project Touge -- "<iso>" --story-check media[:n]   # ohne Fenster: jede Manga-Sequenz/Szene wie im Spiel laden und prüfen, Untertitel mit Zeiten
dotnet run --project Touge -- "<iso>" --story-check [n|calibrate] [--player-skill 0.55]   # ohne Fenster: Kapiteltabelle + Szenen der Disc gegen die Übersetzung, dann jedes Kapitel mit dem Autopiloten (Durchlauf-Log); calibrate = Rivalenstärke (Können 0,1…1, sonst Motormoment)/Zeitgrenzen gegen einen Spieler mit Können k messen
dotnet run --project Touge -- "<iso>" AKINA_NIT --versus split [--bot] [--split vertical] [--car FD3S --car2 AE86T] [--net-rule battle|race] [--autodrive 25 --shot out/proof/s.png]   # geteilter Bildschirm direkt (Lobby; --bot: beide Autopiloten, Rennen startet sofort)
dotnet run --project Touge -- "<iso>" AKINA_DAY --versus host|join:<ip[:port]>|online|menu [--bot] [--port 47860] [--name TAKUMI] [--players 2] [--net-sim 80:5%:20] [--menu pause] [--shot-after 40 --shot out/proof/o.png]   # online im Fenster (--bot: Lobby läuft von selbst, Autopilot fährt; --menu pause: Pause über dem laufenden Rennen fürs Bild)
dotnet run --project Touge -- "<iso>" --menu freebattle --shot out/proof/freebattle/lobby.png   # VERSUS → VS CPU: Lobby des freien Battles gegen die KI als Bild
dotnet run --project Touge -- "<iso>" --flow out/proof/freebattle/flow --freebattle   # freies Battle per Skript: Hauptmenü → VERSUS → VS CPU → Lobby (LEAD/CHASE, du führst, Ryosuke, CAR → Autowahl MAZDA → FD3S, Lack 2) → Battle (Autopilot, 16×) → Ergebnis → RETRY → Pause-Exit → Lobby → RACE → Battle → EXIT
dotnet run --project Touge -- "<iso>" AKINA_NIT --flow out/proof/vs --versus flow   # Versus-Ablauf per Skript: Hauptmenü → VERSUS → SPLIT → Lobby (Strecke rückwärts, Spieler 1: CAR → Autowahl NISSAN → R32; START gesperrt bis READY) → Rennen → Pause → RETRY → EXIT → Lobby → Hauptmenü, PNG je Schritt
dotnet run --project Touge -- "<iso>" IROHA_DAY --headless --host [--bot] [--port 47860] [--players 2] [--races 2] [--net-rule race] [--seconds 600]   # Host ohne Fenster
dotnet run --project Touge -- "<iso>" --headless --join 127.0.0.1[:47860] --bot [--car FD3S] [--net-sim 80:5%:20]   # Bot-Client ohne Fenster (wartet, bis der Host da ist), Log je Sekunde + Zusammenfassung je Rennen
dotnet run --project Touge -- "<iso>" AKINA_DAY --replay-test 380 [--battle keisuke] [--drift] [--save out/proof/r.rpl]   # ohne Fenster: Lauf aufnehmen, Datei schreiben/lesen, auf frischen Autos abspielen, Positionsfehler je Tick (mit Keyframes) und nur aus Eingaben, 20 Sprünge
dotnet run --project Touge -- "<iso>" --replay out/proof/r.rpl [--replay-at 30] [--replay-cam tv|chase|far|hood|cockpit|bumper|free] [--replay-focus 1] [--shot out/proof/rv.png [--shot-after 5]]   # Replay im Viewer (--shot-after: Bild nach so vielen Sekunden Wiedergabe) (--replay-focus 1: die Kameras folgen dem Rivalen)
dotnet run --project Touge -- "<iso>" AKINA_NIT --cam far|hood|cockpit|chase|bumper [--autodrive 5] --shot out/proof/cams/c.png   # Startkamera wählen (sonst Einstellung)
dotnet run --project Touge -- "<iso>" AKINA_DAY --ghost out/proof/r.rpl --autodrive 9 --drift --shot out/proof/ghost.png   # Geist aus einer Replay-Datei (mit Menüs: der Bestzeit-Lauf)
dotnet run --project Touge -- "<iso>" --data-dir out/proof/data --menu replay|replay-best|replay-records|replay-delete|saveload|saveload-actions|saveload-name --shot out/proof/m.png   # REPLAY & RECORD / SAVE & LOAD als Bild (anderer App-Daten-Ordner: die echten Daten bleiben unberührt)
dotnet run --project Touge -- "<iso>" AKINA_NIT --flow out/proof/fourpasses/flow --fourpasses|--fourpasses-wet [--data-dir out/proof/fourpasses/flow_data]   # FOUR PASSES per Skript: 12. Feld → DRY bzw. WET → Auto → alle vier Etappen mit Pilot in 16× (Telop, Rennen, STAGE n CLEAR, Etappenblatt, Pause in Etappe 2) → Endergebnis → REPLAY & RECORD (Replays, RECORDS)
dotnet run --project Touge -- "<iso>" AKINA_NIT --menu fourpasses|fourpasses-weather|fourpasses-stage|fourpasses-finish|fourpasses-result --shot out/proof/fourpasses/m.png   # FOUR PASSES als Bild (Etappenblatt/Ziel/Endergebnis mit erfundenen Zeiten)
dotnet run --project Touge -- "<iso>" --flow out/proof/flow --data-dir out/proof/flow_data   # --flow mit echtem Speichern: der Ordner ist ein ganzes Profil (settings.json samt Rekorden und Optionen, progress.json, Replays, Spielstände, Fotos), Ergebnis → EXIT legt Replay + Bestzeit-Lauf ab, der nächste Start mit demselben Ordner zeigt BEST RUNS und RECORDS übereinstimmend. Ohne --data-dir schreibt nur ein schlichter Start (ISO [--menu x]) ins echte Profil, jeder Test-/Skriptlauf in einen Wegwerf-Ordner im Temp-Verzeichnis
dotnet run --project Touge -- "<iso>" --flow out/proof/saveload --saveload --data-dir out/proof/saveload_data   # SAVE & LOAD in einer Sitzung: Platz 1 (Name) → Legend-Sieg → Time-Attack-Rekord → Platz 2 → Platz 1 laden → Platz 2 laden → RENAME/DELETE → AUTOSAVE; Log [SaveLoadCheck] … PASS|FAIL, am Ende SUMMARY
dotnet run --project Touge -- "<iso>" AKINA_DAY --menu photo --shot out/proof/photo.png   # Fotomodus über dem pausierten Rennen
```

REPLAY & RECORD (`Touge/Replay`, `Ui/ReplayMenu`, `Ui/ReplayViewer`, BGM „WORRY“ wie im Original): jeder Lauf und jedes Battle wird aufgenommen
(`ReplayRecorder`: Eingaben aller Autos je Physik-Tick + alle 0,5 s und nach jedem Versetzen (R, Neustart der KI) der volle Zustand jedes Autos,
`Vehicle.Save/Load`). Die Physik ist deterministisch: Abspielen nur aus den Eingaben trifft die Aufnahme auf den Millimeter (`--replay-test`), die
Keyframes machen Springen billig und tragen, was die Eingaben nicht haben. Fertige Läufe landen mit den Menüs (oder `--data-dir`) in `Replays/` neben settings.json,
sobald der Lauf endet (Ergebnis/Pause → EXIT, neuer Lauf, Beenden) (die neuesten 40 plus behaltene, gzip, ~1–2,5 KiB/s), ein neuer Rekord zusätzlich als `Replays/Best/<Rekordschlüssel>.rpl`. Zuschauen: Pause → Replay (der Lauf
bis hier, danach geht das Rennen genau dort weiter), Ergebnis → REPLAY, Hauptmenü → REPLAY & RECORD (Reiter REPLAYS, BEST RUNS, RECORDS = die
Bestzeiten wie bisher; Zeile 2 sagt, was es war: TIME ATTACK, `LEGEND vs <Rivale> WIN`, `STORY ch.<n> <Titel>`, `FREE BATTLE vs <Rivale> LOSE`, `FOUR PASSES <n>/4`, `BATTLE vs <Rivale>` (Kommandozeilen-Battle; ältere Dateien von Legend/Story/freiem Battle: BATTLE); ↑/↓, Entscheiden ansehen, K bzw. Pad Y behalten (KEPT: nie weggekürzt), X/Entf bzw. Pad X löschen mit JA/NEIN).
Zurück aus Viewer/Fotomodus steht der Cursor wieder auf REPLAY bzw. PHOTO. Viewer: blinkendes REPLAY, Auto/Tempo/Gang, Zeitleiste;
Kameras C bzw. Pad Y: TV (die Originalkameras aus `REPLAY.AFS`/REPCAM je Kurs und Richtung, an der Strecke, Zoom und Fahrt wie im Original,
schauen aufs Auto; jede Einstellung mindestens 2,5 s, ist das Auto 0,25 s hinter Gelände verdeckt, wird auf eine Kamera geschnitten, die es sieht –
die der Tabelle oder eine Streckenkamera 12–35 m voraus), Verfolger, weiter Verfolger, Motorhaube, Cockpit, Stoßstange, frei (WASD/QE, IJKL oder rechte Maustaste, Shift schnell; Pad Sticks + Trigger). Leertaste/Enter bzw.
Pad A Pause, ←/→ (Pad D-Pad/LB/RB) gehalten zurück/vor (4×), ↑/↓ Tempo ¼–4×, Tab (Pad BACK) anderes Auto, H (Pad X) Overlay aus, R (Pad L3)
von vorn, P (Pad R3) Fotomodus, Esc/Backspace (Pad B) zurück. Motor/Reifen/Effekte laufen mit, Eurobeat spielt weiter.
GEIST (Optionen → GAME SETTING → GHOST, Standard an): in Time Attack fährt der Bestzeit-Lauf derselben Strecke, Richtung und Hilfen als
durchsichtiges, bläuliches Auto mit (eigenes Auto des Laufs; nur die vorderste Fläche wird gemischt; blendet innerhalb 10 m zur Kamera aus).
FOTOMODUS (Pause → Photo oder P im Replay): Spiel steht, freie Kamera wie oben, ↑/↓ Blickwinkel 10–100°, ←/→ Belichtung ±2 EV, H Overlay,
Enter/Leertaste (Pad A) speichert ein PNG ohne Overlay in `Screenshots/` neben settings.json, Esc (Pad B) zurück. Tiefenunschärfe: nicht gebaut.
SAVE & LOAD (`SaveSlots`, `Ui/SaveLoadScreen`): 3 Spielstände (Name, Spielzeit, Rekorde, Fortschritt „STORY n/31 LEGEND n/34“, Datum). Ein Spielstand ist eine Kopie aller
`*.json` direkt im App-Data-Ordner (settings.json, progress.json – der eine Fortschritt von Story und Legend – und was sonst dort liegt) in
`Saves/Slot<n>/`, dazu die Bestzeit-Läufe `Replays/Best/` (Geister und BEST RUNS gehören zum Profil); Laden kopiert sie zurück (fehlende Fortschrittsdateien werden entfernt), übernimmt alles außer Steuerung, Bildschirm und Netz-Port/Join-Adresse dieses
Rechners, liest den Fortschritt neu ein (Legend-Leiter, Story-Kapitel und das Legend-Auto zeigen sofort den geladenen Stand) und meldet `SaveSlots.Loaded`.
Speichern schreibt vorher Einstellungen und Fortschritt aus dem Speicher; Laden ist alles oder nichts (Bereitstellen, bei einer gesperrten Datei zurückrollen) und nimmt der Legend-Leiter die NEW!-Marken des vorigen Profils.
Ein gewonnenes Battle oder geschafftes Kapitel speichert den Fortschritt und sichert ihn bei AUTOSAVE gleich in den aktiven Platz. Entscheiden auf einem Platz: SAVE / LOAD / RENAME / DELETE / CANCEL
(Überschreiben, Laden, Löschen fragen JA/NEIN), ein leerer Platz fragt nach dem Namen (Arcade-Eingabe ↑/↓ Buchstabe, ←/→ Stelle, oder tippen).
AUTOSAVE (an/aus) speichert nach jedem fertigen Lauf und beim Beenden in den Platz in Benutzung (zuletzt gespeichert/geladen).

Battle (`Touge/Race`, Grundlage für Legend of the Streets, Story und Multiplayer): `RaceSession` mit N Autos, jedes mit einem Fahrer
(`ICarDriver`: Spieler-Eingabe, KI, später Replay/Netz), Startaufstellung, Zusammenstößen, Auslauf nach dem Ziel und den Regeln (`Battle`).
Rivalen (`--battle <id>` oder ein Auto): itsuki, iketani, kenji, takeshi, shingo, mako, kai, seiji, kyoichi, keisuke, ryosuke, wataru, takumi, bunta
(Auto mit der Lackierung der Figur, je eigener Fahrstil: Können, Aggressivität, Drift). Regeln: `race` (Standard, wie das Original: nebeneinander,
wer zuerst im Ziel ist, gewinnt; dazu Sieg vorzeitig ab 8 s Vorsprung) und `chase` (Lead/Chase wie im Anime: der Verfolger gewinnt, wenn er
überholt und 1,5 s vorne bleibt – erst nach 10 s, ein Überholen gleich nach dem Start zählt nicht, die KI
versucht es dann auch nicht; der Führende mit 4 s Vorsprung oder ≥ 1 s Vorsprung im Ziel; klebt der Verfolger am Ziel dran: DRAW;
`--lead player|rival` wer vorne startet, Standard der Rivale). KI (`Kansei.Physics/RivalPilot` als Dirigent):
`CourseMap` (je Fahrlinie einmal: Kurven nach Art, freie voll griffige Straße links/rechts alle 2 m, Überholzonen = Bremszonen vor engen
Kurven mit ≥ 5 m Straße und Geraden ≥ 80 m), eigene Ideallinie `RacingLine` (geringste Krümmung in der Straßenbreite minus 1,3 m, in
Driftkurven mehr für das Heck, außen-innen-außen mit spätem Scheitel) mit Tempoplan (Querbeschleunigung des Könnens, Motor-/Luftwiderstand
vorwärts, Bremsen rückwärts). Eine Könnensskala 0…1 für alle Modi: Kurven 0,67 g (Anfänger) bis 1,45 g (AE86 am Haftlimit, ein guter Spieler),
Bremsen 0,6–0,95 g, je Auto nach seiner Haftgrenze auf der Kreisbahn (`GripLimit`). Driften (`DriftController`) je Stil und Kurve, einmal je
Kurve aus dem Renn-Seed entschieden: FR in Haarnadeln und engen Kurven (Takumi/Keisuke fast immer, Ryosuke gut die Hälfte, Takeshi nie), 4WD
kurzer Powerslide in Haarnadeln (≤ 15°), FF nur ein Handbremsen-Einlenken (~9°); Einleitung per Handbremse, Bremsdrift (Takumi) oder Finte
(Stil ≥ 0,8, nur mit Platz außen), Halten über einen Schräglauf-Sollwert aus der Bahnkrümmung, weiches Ausleiten, Abfangen bei zu viel
Schräglauf/Innenkante/Wand; nicht unter 40 km/h, nicht über 65 km/h im Scheitel, nicht mit einem Auto daneben. Renntaktik: folgt mit
0,4–0,8 s, macht als schnellerer Verfolger Druck (am Heck, auf der eigenen Linie – kein Versatz nach der Querlage des Vordermanns, die
gehört zu einer anderen Stelle der Kurve), zieht in Bremszonen vor dem Bremspunkt des Vordermanns auf die Innenseite, wenn die Straße bis
¾ zum Scheitel Platz für zwei hat, bremst 3–8 m später (mehr mit mehr Tempovorteil) und überholt nur, wenn er bis zum Einlenken wirklich
daneben ist (sonst reiht er sich wieder ein – kein Hineinstechen), auf Geraden mit Tempoüberschuss; verteidigt je Zone höchstens einmal
die Innenseite (nie gegen ein Auto schon daneben), Abstandsbremse nach Zeit bis zum Kontakt mit der Querbewegung beider Autos über 0,5 s,
hält neben einem Auto seine Seite (Abstand mit Kurvenradius und Schräglauf größer, folgt einem Wackler des anderen nur langsam), im
Lead/Chase in der Startphase hinten, nach Wandtreffer/Dreher kein Einscheren vor einen Nachfolger. Nach dem Ziel bremst der Auslauf
hinter einem stehenden Auto (kurze Ausläufe: SHIONA/SHOMARU). Kleine Fehler je Kurve aus dem Seed (Bremspunkt daneben, weiter Ausgang, Blockieren, früh am Gas, Drift-Wackler/-Überdreher),
nach Können gestaffelt (EASY 6–10, LEGEND 0–1 je 5 km), nie Richtung Wand. Gummiband nur auf dem Plan-Grip (± 3–4 %, ab 1 s Zeitabstand, voll
bei 4 s, nicht im Lead/Chase und auf den letzten 10 %). Messen ohne Fenster: `--ai-bench` (siehe oben, Ergebnisse PLAN.md). Auto gegen Auto: Kastenkollision (SAT, über den Tick abgetastet – kein Durchtunneln), Impulse mit Drall, Funken,
Kamerawackeln und Crash-Ton. HUD oben rechts: VS + Rivale, Position 1ST/2ND, LEAD/CHASE, ADVANTAGE (Zeitabstand), Abstandsbalken bis zur
Vorsprungsgrenze, OVERTAKE!/OVERTAKEN; roter Punkt auf der Streckenuhr. Ton des Rivalen (Motor, Reifen, Wand) nach Entfernung, mit Doppler
und Stereo. Ende: YOU WIN!!/YOU LOSE/DRAW mit WIN.adx/LOSE.adx (DRAW: WIN.adx wie das Zieljingle), Ergebnisblatt (Rivale, Auto, entschieden durch, Abstand, Zeiten, Führungswechsel,
Kontakte – eine Berührung zählt neu erst nach 0,25 s Abstand) → Retry / Course Select / Car Select / Exit. Steuerung wie beim Fahren (Tastatur und Pad); B (Richtung wechseln) ist im Battle aus.

VERSUS (`Ui/Versus`, `TougeGame.Versus`, `Touge/Net`; im Original gibt es keinen Mehrspielermodus, Menüs im Stil der anderen:
Logo-Kachelwand, roter Laufschrift-Kopf, Chromplatten, Karbonpaneele, Original-SE, BGM „LIVE IN TOKYO“ in den Lobbys, „JOY“ beim
Ergebnis, WIN/LOSE-Jingle beim Entscheid): SPLIT SCREEN, ONLINE oder VS CPU.
- **VS CPU** (freies Battle gegen die KI, `Ui/FreeBattle`, `TougeGame.FreeBattle`): Lobby links das Battle (Kurs, Route, Bedingungen
  wie im Versus/Time Attack, Regel RACE = Battle des Originals mit 8-s-Vorsprungssieg oder LEAD / CHASE = Anime-Runde mit Führendem und
  Jäger, WHO LEADS YOU/RIVAL nur bei LEAD / CHASE, AI LEVEL EASY/NORMAL/HARD/LEGEND = Können-Band 0–0,2 (dazu 85 % Motormoment) / 0,35–0,65 / 0,65–0,85 / 0,88–1, die Figur liegt nach ihrem Können darin
  (AKINA bergab, 14 Rivalen, Siege Trueno/FD3S bei Spieler 0,2 · 0,5 · 0,8: EASY 14/14 überall, NORMAL 0/0 · 6/3 · 13/14, HARD 0/0 · 0/1 · 10/8, LEGEND 0/0 · 0/0 · 1/3;
  vorher hatte der FD3S bei 0,8 LEGEND 12/14, der Trueno 2/14); HARD
  holt nur auf (halbes Gummiband), LEGEND ohne Gummiband und mit halb so vielen Fehlern; Sterne auf der Karte nach dem Können der Stufe),
  rechts die Rivalenkarte (einer der 14 aus `--battle`: Team, Auto, Stufe in Sternen, kurze Notiz zum Fahrstil; ◀ ▶ blättert) und das
  eigene Auto mit Lack und AT/MT (◀ ▶ blättert, ENTSCHEIDEN oder ◀ ▶ auf CAR öffnet dieselbe Autowahl wie Time Attack: Hersteller → Autos mit 3D-Vorschau). In der Lobby läuft das Thema des Rivalen aus `MG_BGM.AFS` (wie auf Legends VS-Karte). START → Laden
  (gleicher Kurs: nur die Autos) → Telop „VS …“, 3-2-1-GO → Battle → YOU WIN/LOSE (WIN/LOSE.adx) → Battle-Blatt mit `R_WIN01`/`R_LOSE`
  → RETRY / REPLAY / CHANGE SETTINGS (zur Lobby) / EXIT (Hauptmenü). Pause → Exit führt zur Lobby. Replays heißen „FREE BATTLE vs
  <Rivale>“. Die letzte Wahl merkt sich `settings.json` (`FreeBattle`, Auto/Lack/Getriebe wie überall). Tastatur, Pad und Lenkrad über
  die Menütasten.
- **Geteilter Bildschirm** (2 Spieler): Lobby links das Rennen (Kurs, Route, Bedingungen DAY/NIGHT/WET/DAY FOG/NIGHT FOG, Regel
  BATTLE = Battle des Originals mit 8-s-Vorsprungssieg bzw. RACE = beide bis ins Ziel, Bildschirm oben/unten oder links/rechts, Gerät von
  Spieler 2), rechts je Spieler Auto und Lackfarbe (ENTSCHEIDEN oder ◀ ▶ auf CAR öffnet die Autowahl Hersteller → Autos für diesen Spieler, gesteuert nur von ihm, solange sind die Tasten des anderen Spielers gesperrt; online ebenso); Spieler 2 hat einen eigenen (blauen) Cursor auf seiner Karte und muss READY drücken,
  dann START. Geräte: Spieler 2 nimmt ein Pad (nur die Pad-Belegung aus CONTROLLER) oder – ohne Pad – die Pfeil-Hälfte der Tastatur
  (Pfeile fahren, R-CTRL Handbremse, R-SHIFT/R-ALT Gang hoch/runter, BACKSPACE zurück auf die Straße, ENTER Kamera; in den Menüs Pfeile +
  ENTER); Spieler 1 behält alles andere (Lenkrad, die übrigen Pads, Tastatur – mit Spieler 2 auf der Tastatur ohne Pfeil-Hälfte, Menüs
  WASD + LEERTASTE). Zwei Ansichten mit je eigener Kamera (C bzw. Kamerataste je Spieler), eigenem HUD (Zeit, Karte mit rotem Punkt für den
  Gegner, Kombiinstrument des eigenen Autos, Platz 1ST/2ND mit Abstand) und eigenem Licht (die Scheinwerfer der jeweiligen Ansicht
  beleuchten die Straße, das andere Auto glüht), Trennfuge mit roter Linie; Pause durch beide (START am Pad von Spieler 2), RETRY, EXIT →
  Lobby. Die Zeit im HUD läuft für alle ab GO (wie die Zeiten im Ergebnis); der Hinweis „zurück auf die Straße“ nennt die Taste des
  jeweiligen Spielers. 60 fps bei 3200×1800 ohne Abstriche (GPU 8,4 statt 7,3 ms je Bild, `--offscreen`).
- **Online** (2–4 Spieler): HOST A GAME, JOIN BY ADDRESS (IP oder Name,
  optional `:port`, getippt), YOUR NAME, UDP PORT (getippt, 1024–65535, Standard 47860, gespeichert als `NetPort`, `--port` für einen Lauf), darunter die Spiele im LAN (UDP-Broadcast, Liste aktualisiert sich). Lobby: der Host wählt das
  Rennen, jeder sein Auto, Gäste melden READY, Ping je Spieler, der Host startet, wenn alle bereit sind. Laden (wartet auf alle, höchstens
  30 s), gemeinsamer Countdown (GO auf allen Rechnern zur selben Zeit: Host-Sekunden bis GO minus halbe Paketlaufzeit), Rennen, Ergebnis
  (Plätze, Zeiten; vorzeitiger Battle-Sieg: Sieger „WIN +n m“, der andere „n m BEHIND“; DNF nur, wer im Rennen nicht ins Ziel kam – Fenster und `--headless`-Log gleich) → Host REMATCH/LOBBY/LEAVE, Gäste folgen. Pause hält online nicht an (das
  Auto bremst, RETRY ist ausgegraut; Replay und Photo sind im Versus immer grau, Versus-Rennen werden nicht aufgezeichnet), EXIT des Hosts bringt alle in die Lobby, ein Gast verlässt die Sitzung. Verbindungsverlust (5 s still) → Meldung, zurück
  zu ONLINE; ein Gast, der geht, ist DNF (sein Auto verschwindet), bleibt nur einer übrig, gewinnt er.
- Netz (`Touge/Net`, Protokoll in FORMATS.md): UDP ohne Threads, einmal je Bild abgefragt; Stern um den Host (er leitet Zustände weiter).
  Jedes Auto rechnet nur sein eigener Rechner (keine Eingabeverzögerung), 30 Zustände/s (Lage, Bewegung, Eingabe, Federweg und Rutschen
  je Rad für Rauch/Spuren, Licht, Fortschritt, eigene Zielzeit). Fremde Autos: Hermite-Interpolation zwischen Zuständen bzw. Extrapolation
  bis 0,5 s auf die eigene Rennuhr (nebeneinander sieht nebeneinander aus), Korrekturen gleiten in 0,1 s ein, darüber hinaus hält das Auto an.
  Kontakt Auto gegen Auto: jeder Rechner löst ihn für sein eigenes Auto (das fremde gibt erst nach, wenn dessen Rechner es sagt). Host
  entscheidet: Countdown, Zielreihenfolge nach den Zielzeiten der Fahrer selbst (erst wenn sicher ist, dass keine frühere mehr kommen kann),
  Battle-Vorsprung, DNF 30 s nach dem Ersten. Zuverlässigkeit durch Wiederholen ganzer Zustände (Lobby 5/s, Ergebnis bis zum nächsten
  Rennen) statt Quittungen; alte Pakete eines früheren Rennens werden verworfen.
- **NAT / Portweiterleitung**: Im selben LAN reicht HOST bzw. die LAN-Liste. Übers Internet muss der Host im Router den UDP-Port (47860)
  an seinen Rechner weiterleiten (Port Forwarding, oft unter „Freigaben“), die Gäste geben die öffentliche IP des Hosts ein
  (`<ip>:47860`); die Firewall des Hosts muss eingehendes UDP für das Spiel erlauben (macOS fragt beim ersten Hosten). Ohne Weiterleitung
  (CGNAT, Mobilfunk) geht es nur über ein VPN wie Tailscale/ZeroTier/Hamachi (dann deren Adresse eingeben). Gäste brauchen nichts.
- Testen ohne zweites Fenster: `--headless` (Host oder Bot-Client ohne Fenster, Autopilot, `--net-sim` Latenz/Verlust/Jitter je
  Richtung), z. B. Host im Fenster `--versus host --bot` und Bot ohne Fenster `--headless --join 127.0.0.1 --bot`.

Fahren (Standard, alles außer F-Tasten/M/N/B/T/1–3 unter Optionen → CONTROLLER umbelegbar): W/S oder ↑/↓ Gas/Bremse, A/D oder ←/→ lenken, Leertaste Handbremse, S im Stand halten = Rückwärts (Automatik), T Automatik/Manuell,
Shift/Strg hoch-/runterschalten (manuell, auch in R), R (Pad: Y) zurück auf die Fahrlinie (nächster freier Punkt, Blick in Fahrtrichtung), B Richtung wechseln (bergab ↔ bergauf, setzt auf die Fahrlinie der Gegenrichtung; Minimap/Fortschritt folgen), C (Pad BACK) Kamera weiter: CHASE (Verfolger wie im Original, Werte aus dem ELF, FORMATS.md „Fahrkameras“: Auge 4,6 m hinter und 1,7 m über dem Auto im Blickrahmen, Blick entlang der Fahrzeugachse samt Neigung – bergab schaut die Kamera die Straße hinunter –, die Ausrichtung läuft dem Auto 0,25 s nach, im Drift sieht man die Flanke, Blickwinkel weiter als an Bord: bei FIELD OF VIEW 60° die 69,5° des Originals) → FAR CHASE (dasselbe 1,5-mal so weit, 6,9 m/2,55 m; beide ohne Tempo-Abhängigkeit und ohne Wackeln gegenüber dem Auto; Gelände, Felsen, Bäume und der Kursrand ziehen das Auge heran, nie hindurch, ein leicht angeschnittenes Hindernis pumpt es nicht hin und her – auch Showcase-Orbit, Freie/Foto-Kamera) → HOOD (auf der Motorhaube, Lage aus dem Automodell) → COCKPIT (Fahrerauge rechts hinter der Scheibe, Innenraum aus den HCAR-Daten, Kombiinstrument groß als Armaturenbrett statt unten rechts) → BUMPER (Stoßstange),
L Licht an/aus, H Fernlicht an/aus (Umschalter, schaltet das Licht auch ein; Pad: D-Pad hoch/runter),
F2 Grafikqualität ULTRA ↔ LOW (MSAA, Schatten, AO, Bloom, Regen-SSR zusammen; `--quality off` startet LOW).
Menüs: Ohne Kurs/Test-Flags startet das Spiel im Front-End im Stil des Originals (`Ui/FrontEnd`, nur Vektorformen + Schrift, keine
Original-Menütexturen; Ablauf/Zeiten/Bewegung aus dem Originalcode, siehe FORMATS.md): Hinweis zu den Speicherdaten → Karte „Based on …“ →
Hinweis „Fiktion“ → Titel (Akina bei Nacht im Hintergrund abgeflogen, Logo, blinkendes PRESS START BUTTON; nach 10 s ohne Eingabe zurück zu
den Karten) → Hauptmenü (Trommel mit den 7 Modi des Originals, Chromplatten, pulsierender gelber Rahmen; nach 30 s ohne Eingabe zurück zum
Titel). Gebaut sind davon LEGEND OF THE STREETS, TIME ATTACK, VERSUS (eigene Ergänzung nach TIME ATTACK, siehe unten), STORY, REPLAY & RECORD, IKETANI'S CAR GUIDE, SAVE & LOAD und OPTIONS – alle Modi des Originals
im Original. Als letzter Eintrag QUIT GAME (nur Windows/Linux/macOS): Abfrage „QUIT THE GAME?“ YES/NO (NO vorgewählt, ←/→ wählen,
Enter/A entscheiden, Esc/B = NO), YES speichert die Einstellungen und schließt das Fenster. Esc im Hauptmenü zurück zum Titel, im Titel beenden. Danach alle Bildschirme ebenfalls im Originalstil (`Ui/Menu` + `Ui/Canvas`:
graue Logo-Kachelwand, roter/blauer Laufschrift-Kopf, Chromplatten, Karbonpaneele, gelber Pulsrahmen, Verlaufswörter rot/blau, alles Englisch):
TIME ATTACK = Kurswahl (3 × 4 Raster wie im Original, Streckenlinie im Karbon-„Monitor“ statt Foto, Länge/Höhe/Bestzeit; das 12. Feld ist
FOUR PASSES, siehe unten) → Route (DOWNHILL/UPHILL bzw. CLOCKWISE/COUNTER-CLOCKWISE aus dem Drehsinn der Linie) → Tageszeit (DAY/NIGHT) → Wetter
(DRY/WET/FOG, nachts DRY/FOG) – Schritte mit nur einer Möglichkeit entfallen – → Autowahl in zwei Schritten (`Ui/CarPicker`):
Hersteller (7 Chromplatten mit Autozahl, rechts die Aufstellung: Modellnamen, gesperrte als ?????, „1 LOCKED“) → Autos dieses Herstellers
(Liste links, das 3D-Auto dreht sich rechts daneben, ↑/↓ Auto, ←/→ Lackfarbe, Leistung/Gewicht, Antrieb FF/MR/FR/4WD; gesperrte Autos ?????
mit Hinweis, piepen; ZURÜCK zu den Herstellern; geöffnet wird auf dem zuletzt gefahrenen Auto) → Getriebe (AT/MT) → Laden (weiß, „Now Loading...“) → Streckentelop + 3-2-1-GO
(CAR010/CAR011, Auto steht bis GO) → Rennen. Esc (Pad: Start) pausiert (alarm_02): Continue/Retry/Replay/Photo/Exit/Quit Game (Quit Game wie im Hauptmenü mit Abfrage, nur Desktop). Start und Ziel sind die beiden Bögen an den
Streckenenden: bergab endet, wo bergauf startet, und umgekehrt (Auto steht 5 m hinter seinem Startbogen, Zeit läuft ab dem Bogen). Im Ziel
„FINISH!!“ bzw. „NEW RECORD!!“ (das Spiel übernimmt das Auto wie ein Arcade-Racer: rollt aus und bremst gleichmäßig bis
4,5 m vor die Endsperre, vor Kurven des Auslaufs auch stärker, `Drive.Coast`; WIN-Jingle) → Ergebnis (Gesamt-/Sektorzeiten mit Delta, Bestzeit, Differenz, Driftpunkte, Zeilen zählen mit
NAME001, BGM „JOY“) → Retry / Replay / Course Select / Car Select / Exit. Rekorde: Bestzeit je Kurs und Route. Optionen (`Ui/Options`) wie im
Original zweistufig: Abschnittsliste aus Chromplatten (OPSL) → Seite mit Zeilen (OPGM: Reiter links, Chromplatte mit gravierten Werten, mehr als
3 Werte als ◀ Wert ▶, Lautstärken als 10 Blöcke, Hilfetext unten; ↑/↓ Zeile, ←/→ bzw. Enter/A ändern, Esc/B zurück), alles wirkt sofort und wird gespeichert:
GAME SETTING (Einheit km/h/mph im Kombiinstrument, Getriebe-Vorwahl AT/MT, Lenkhilfe OFF/LOW/FULL = `CounterSteerAssist` × 0/0,5/1, Drift-Hilfe
LOW/NORMAL/HIGH = `DriftDamping` × 0,5/1/1,6 – beide ab dem nächsten Lauf; andere Hilfen als FULL/NORMAL fahren eigene Bestzeiten, RECORDS zeigt nur die Serienwerte –, Startkamera (alle fünf Ansichten; alte Einstellung BumperCam = BUMPER), Blickwinkel 50–90°, Kamerawackeln, Aufkleber
ANIME/STOCK/NONE), HUD (an/aus, HUD SIZE 80–130 %, Navi-Karte, NOW PLAYING ON/OFF/PAUSE ONLY), SCREEN (WINDOW/BORDERLESS, außer macOS auch FULLSCREEN exklusiv; Auflösung aus den Modi des
Bildschirms; VSync; Bildratenbegrenzung 30–240; Render-Skalierung 50–150 %), GRAPHICS (Voreinstellung LOW/MEDIUM/HIGH/ULTRA bzw. CUSTOM aus den
Schaltern MSAA, Sonnenschatten, AO, Bloom, Regen-Spiegelungen), SOUND (Gesamt, Musik an/aus + Lautstärke, SE, Motor, Menü-SE), PLAYLIST (Renntitel einzeln an/aus), CONTROLLER
(Steuerungsbildschirm, siehe unten; ohne Live-Eingabe nur die Belegung als Text). Andere Features hängen eigene Seiten an `Menu.Options.Pages` an (Zeilen per `Options.Row.Choice/Toggle/Slider`
oder eigener Bildschirm über `Page.Input`/`Page.Draw`). Zurück geht die besuchten
Schritte rückwärts. Zwischen Modulen 30-Frame-Schwarzblenden. Ton: Original-SE aus SYSSE (SYS005 Cursor, SYS006 Bestätigen, BEEP001
Zurück/gesperrt, sys002 START, alarm_02 Pause, CAR010/011 Countdown, NAME001 Ergebniszeilen) und Menü-BGM aus BGM.AFS mit Loop-Punkten:
Titel/Hauptmenü „GAMBLE RUMBLE“ (eigene Wahl, das Original ist dort still bzw. spielt den Vorspannfilm), Kurswahl „LIVE IN TOKYO“,
Hersteller/Auto/Rekorde „WORRY“, Laden still, Countdown/Rennen/Pause Eurobeat, Ziel „WIN“ (einmal), Ergebnis „JOY“ (wie im Original).
FOUR PASSES (`FourPasses`, `Ui/Menu.FourPasses`, `TougeGame.FourPasses`, `Ui/FourPassHud`; das Original 四峠走破, 12. Feld der Time-Attack-Kurswahl):
vier Pässe hintereinander mit einer Gesamtzeit, Etappen wie im Original aus der ELF-Tabelle (FORMATS.md): AKAGI bergab → AKINA bergab →
HAPPOGAHARA bergauf → IROHAZAKA bergab, alle nachts. Kurswahl zeigt die vier Karten im „Monitor“, Gesamtlänge und Rekord; danach nur Wetter
(DRY/WET wie im Original; WET = Regen über den Nachtkursen wie in der Story) → Hersteller/Auto/Getriebe wie Time Attack (das Auto bleibt für alle
Etappen) → Laden → Telop „FOUR PASSES STAGE n / 4“ mit der Gesamtzeit bisher → Rennen (HUD oben rechts: STAGE n/4, Kurs, laufende Gesamtzeit;
Sektor-Deltas gegen die Etappe des Rekordlaufs) → „STAGE n CLEAR“ (Etappenzeit, Gesamtzeit) → Etappenblatt (Pässe mit Zeit und Delta der
Gesamtzeit gegen den Rekord, PACE, nächste Etappe) mit NEXT / RETRY / EXIT → … nach der vierten FINISH!!/NEW RECORD!! und das Endblatt (Gesamtzeit,
vier Passzeiten mit Delta, Bestzeit, Differenz, Driftpunkte) mit RETRY / REPLAY / COURSE SELECT / CAR SELECT / EXIT. RETRY (auch in der Pause) und
ein neues Auto beginnen wieder bei Etappe 1. Rekord je Wetter (und Hilfen, wie Time Attack) in `settings.json` (`FOURPASS_A`/`FOURPASS_WET_A`:
16 kumulierte Sektorzeiten, letzte = Gesamtzeit), angezeigt in RECORDS und REPLAY & RECORD → RECORDS als Zeile FOUR PASSES (DRY/WET).
Jede Etappe ist ein eigenes Replay mit dem Etikett „FOUR PASSES n/4“.
IKETANI'S CAR GUIDE (`Ui/CarGuide`, wie das Original 池谷先輩の車紹介, BGM „WORRY“): Itsuki/Takumi/Iketani-Dialog (Entscheiden
schreibt die Zeile fertig, dann weiter) → Liste aller 32 Autos unter Herstellerköpfen, das Auto dreht sich in 3D rechts daneben (Drehbühne auf freier, gerader Straße mitten im geladenen Kurs, weg von Start-/Zielbögen), Iketanis Text
(eigenes Englisch) und Datenblatt (Motor, Hubraum, Bauart, Leistung/Drehmoment mit Drehzahl aus der Momentkurve, Gewicht, Antrieb, Getriebe,
kg/PS, Fahrer in Initial D). ↑/↓ Auto, ←/→ Lackfarbe, Entscheiden = Iketani spricht (Original-Ansage `IKETANI.AFS` `INTRO_<AUTO>.ADX`,
japanisch, 34–66 s; Liste fährt weg, Auto in die Mitte, Text tippt im Tempo der Ansage mit, Fortschrittsbalken, Musik leiser), Entscheiden/Zurück bricht ab,
Zurück → Hauptmenü (gespeichertes Auto kommt zurück). `--menu guide` (Dialog), `guide-list`, `guide-talk` mit `--car`/`--paint` für Bilder (ohne Hauptmenü schließt Zurück das Fenster).
LEGEND OF THE STREETS (`Ui/LegendScreen`, `Race/Legend`, wie das Original 公道最速伝説): Kurswahl im Raster (11 Kurse) mit Punkten je Rivale
(gold = besiegt, goldener Rand = Kurs geschafft) → Rivalenleiter des Kurses (Name, Team, Auto, Status WIN / CHALLENGE / NEW! / LOCKED, rotes Kreuz
über besiegten wie das Original; rechts Datenblatt: Auto, Route, Tageszeit, Wetter, Stufe 1–5, Bilanz, bester Abstand) → VS-Karte über dem
3D-Auto des Rivalen auf seinem Kurs bei seiner Tageszeit/seinem Wetter (seine Lackierung, sein Thema aus `MG_BGM.AFS`; der Kurs lädt hinter der Blende) → Herstellerwahl/Auto/Getriebe wie Time Attack → Laden → Telop „VS …“ →
Battle (Regel des Originals: wer zuerst im Ziel ist, dazu Sieg ab 8 s Vorsprung) → YOU WIN/LOSE und Battle-Blatt (Musik `R_WIN01`/`R_LOSE` aus `MG_BGM.AFS`) → RETRY / RIVAL SELECT /
CAR SELECT / EXIT. Rivalen und Bedingungen aus dem Original (34 Rivalen auf 11 Kursen, Richtung/Tageszeit/Wetter je Rivale, siehe FORMATS.md);
je Kurs eine Leiter (der nächste Rivale nach einem Sieg über den vorigen), die fünf Zusatzkurse (MYOGI+ … SHIONA) ab 3 geschafften Hauptkursen,
Bunta (Akina) nach allen 24 Rivalen der Hauptkurse, Takumi von Project D (Irohazaka) nach allen anderen außer Bunta; ein Sieg über Bunta schaltet
sein Impreza (IMP3) frei – wie im Original auch das Schlusskapitel von STORY; bis dahin ist es in jeder Autowahl (Time Attack, Legend, Versus-Lobby) „?????“ mit dem Hinweis, wie man es bekommt. Revanche gegen einen besiegten Rivalen im Regen (wie das Original). Die vier
Rivalen eines Hauptkurses fahren mit 60/78/95/100 % Motormoment (auf ihr ausgeglichenes Auto) und Können je Sprosse (0,1–0,2 / ~0,3 / 0,3–0,45 / 0,3–0,5; MYOGI 0,1 / 0,25 / 0,3 / 0,4; in den starken
Autos niedriger; Zusatzkurse 0,65–0,8, PD-Takumi 0,9, Bunta 1) – eigene Abstimmung. MYOGI: Takumi im AE85 (im Original Zeile 1) ist die
letzte Sprosse statt der zweiten (vorher 72 % auf den 83 PS des AE85 bergauf = ~60 PS: der Autopilot im FD3S war nach 29 s 8 s weg).
`--legend-sim --player-skill k` (Leiter wie ein Spieler; Trueno / FD3S / R34 / S15 / AE85): Anfänger 0,2 11 / 11 / 13 / 11 / 13 Rivalen
(vor BoP 12 / 17 / 16 / 17 / 6), NORMAL 0,5 18 / 18 / 25 / 17 / 23 (vorher 21 / 29 / 25 / 26 / 12), HARD 0,8 27 / 25 / 30 / 27 / 30
(vorher 27 / 31 / 31 / 31 / 13) – das Auto entscheidet nicht mehr. MYOGI-Takumi: Anfänger verlieren (außer R34 +0,7 s), NORMAL meist knapp
im Ziel, HARD gewinnt (Trueno −0,7 s knapp verloren). `LEGEND_ALL=1` fährt jeden Rivalen einmal (Kalibrierung).
Nach einem Battle steht der Cursor auf dem nächsten Rivalen, Neues (Rivalen, Kurse, Auto) läuft unten als rotes Band ein. Fortschritt in
`progress.json` neben `settings.json` (`Rivals`: Siege, Niederlagen, bester Abstand je Rivale; dieselbe Datei wie Story, `Progress`; ein altes
`legend.json` wird beim Laden übernommen und beim nächsten Speichern gelöscht). Pause-Exit und Zurück aus der
Autowahl führen zur Leiter, EXIT ins Hauptmenü.
Navigation Pfeile/WASD, Enter, Esc bzw. D-Pad/Stick, A, B.
STORY (`Touge/Story`, wie der Story-Modus des Originals, Daten zur Laufzeit aus der ISO, FORMATS.md „Story“): 31 Kapitel in drei Teilen
(THE LEGEND OF AKINA 1–19, THE TAKAHASHI BROTHERS 20–24, PROJECT D 25–31) vom „Geist von Akina“ bis zu Bunta im Impreza. Kapitelwahl (BGM WORRY
wie im Original): Teile als Chromplatten (←/→), Kapitelliste (↑/↓, geschafft = CLEAR, gesperrte grau), Infotafel mit Titel, kurzer Inhalt, Kurs/Richtung/
Nacht, Fahrer und Auto (Takumi im AE86, in einigen Kapiteln Keisuke im FD, Ryosuke im FC, Takumi im 180SX), Rivale mit Team und Auto, Ziel. Kurs, Richtung,
Autos und Ziel liest das Spiel aus der Kapiteltabelle im ELF. Ablauf: Laden → Szene vor dem Rennen → Telop „VS …“, Countdown → Battle (bzw. Lauf
allein) → eigenes Banner (YOU WIN/LOSE bzw. CLEAR!!/TIME UP/FAILED mit WIN/LOSE/TIMEUP.adx) → Ergebnis (Battle-Blatt + Story-Tafel) →
gewonnen: CONTINUE → Szene danach → nächstes Kapitel frei und gewählt; verloren: RETRY / CHAPTER SELECT. Nach dem letzten Kapitel „THE END“ mit
THERACEISOVER. Szenen wie im Original (Daten zur Laufzeit aus der ISO, FORMATS.md „Story – Manga“): erst die Manga-Sequenz des Kapitels
(Schwarzweiß-Panels aus dem Heft, die ein-/ausblenden und einfliegen, über ziehendem Himmel, Titelkarte der Folge) mit dem japanischen
Hörspiel, dann (Kapitel 2–30) die Szene mit Farb-Porträts, deren Mund sich zur japanischen Stimmspur bewegt (Lippen-Ziffern der Disc)
und die blinzeln; nach dem Sieg die Szene danach (Kapitel 30 dazu der Epilog, Kapitel 9 eine zweite Manga-Sequenz). Englische Untertitel
je Zeile zur Stimme: Szenen = die 585 Äußerungen der Disc in eigener Übersetzung an ihren Zeiten auf der Spur, im nachgebauten
Sprechfenster mit Namensplatte; Manga-Hörspiele (kein Text auf der Disc) = eigene Übersetzung des Gehörten (`MangaText`), als Band unten;
englische Titel unter den Titelkarten. Steuerung: Entscheiden = nächste Zeile (springt in der Spur weiter), ↑/↓ = AUTO an/aus (aus: hält
am Ende jeder Zeile, ▼), → oder START am Pad (wie `SKIPMSG` im Original) = Szene überspringen, Zurück: vor dem Rennen zur Kapitelwahl, danach überspringen. Lautstärke: Optionen →
SOUND → VOICE (auch Iketani im Car Guide). Bilder werden je Szene im Hintergrund dekodiert, verteilt hochgeladen und danach freigegeben.
Ohne die Medien (Manga-Tabellen nicht lesbar) Textpanels über dem Flug wie bisher (ST_BGM_N-Musik). Ziele (Code aus dem ELF): Rennen; vorne bleiben bis ins Ziel; hinterher
und überholen (auch: in 120 s); 100 s dranbleiben; allein mit Zeitgrenze, Tofu bergauf mit höchstens 3 Wandtreffern, Mitfahrer mit 10.000 Driftpunkten –
im Rennen oben rechts eine Tafel mit Restzeit/Wandtreffern/Drift. Rivalenstärke je Kapitel aus zwei Kalibrierungen
(Autopilot als Spieler mit seinen Fehlern, `--story-check calibrate --player-skill k`): am Anfang gegen einen Anfänger (0,2), zum Ende hin
gegen einen NORMAL-Spieler (0,55), Gewicht (Kapitel/30)², minus 0,03 (mit den ausgeglichenen Autos und dem Kickdown neu gemessen; Kapitel 12
etwas tiefer, weil die Bisektion nicht monoton ist); wo selbst 0,1 für den Anfänger zu stark ist (Kapitel 2 95 %, 10 96 %, 21 Seijis Evo gegen
Keisukes FD 78 %) fährt der Rivale gedrosselt; das Finale (Bunta) ist ein starker Fahrer (0,6) im gedrosselten Impreza (83 %, `STORY_CAL_AT=0.65`), den
NORMAL schlägt (+6,1 s). Zeitgrenzen vom Anfänger- zur NORMAL-Zeit (Akina bergab 5'24 → 4'53, bergauf 5'37 → 5'07). Anfänger (0,2)
schafft 14 von 31 Kapiteln (0–14 außer 4 Driftpunkte und 13), 0,25 14, NORMAL 30 (nicht: 4), 0,8 30. Pause → Exit führt zur Kapitelwahl zurück. Regen bei Nacht (Kapitel 11, MYOGI) gibt es auf der Disc nicht: der Nachtkurs bekommt
Regen (Tropfen, nasse Spiegelungen, Gischt, Regen-Sound, Telop WET); die Haftung ist wie auf den _RIN-Kursen unverändert. Fortschritt in `progress.json` neben den Einstellungen (Schlüssel `story/nn`, Zähler
`…/tries`, `…/wins`; dieselbe Datei hält Legend of the Streets); Story-Läufe zählen nicht als Time-Attack-Rekord.
Einstellungen, letzte Wahl und Bestzeiten liegen als JSON im App-Data-Ordner (macOS `~/Library/Application Support/InitialDRemake/settings.json`,
Windows `%APPDATA%\InitialDRemake`), nicht im Repo; Starts mit Kurs oder Test-Flags lesen/schreiben sie nicht (Fenster dort immer 1600×900).
Die Datei hat eine `Version` (derzeit 2); ältere werden beim Laden umgestellt (v1: `HighQuality` → die fünf Grafikschalter, SE-Lautstärke auch
für Menü-SE), Werte außerhalb des Bereichs auf Standard/Grenze gesetzt; gespeichert wird über eine temporäre Datei (kein halber Stand bei Absturz).
HUD: F4 an/aus, N Minimap mitdrehend → nordausgerichtet → ganze Strecke (`--hud north|overview|off` beim Start), Größe in den Optionen
(HUD SIZE 80–130 %, `--hud-scale 80..130` für Testläufe; Streckenuhr und Kombiinstrument bei 100 % 1,6× so groß wie früher – 640×304 px bei
1080p –, auf schmalen Bildern (4:3, 5:4) gedeckelt, damit das Auto in der Mitte frei bleibt; oben rechts bleibt frei). Oben links Zeit, Bestzeit
und 4 Sektoren (je 25 % der Strecke, Delta zur Bestzeit grün/rot; Zeit läuft ab der Startlinie, stoppt im Ziel), oben Mitte Drift-Kombo
(Punkte aus Winkel × Tempo, Multiplikator, Wandkontakt löscht), unten links die Streckenuhr (`Ui/MapWidget`: rundes Instrument im Stil des
Kombiinstruments mit Minimap – vorausliegende Straße hell, gefahrene gedimmt, Zoom nach Tempo, nachts in der Instrumentenfarbe des Autos –,
Fortschritt als Bogen auf dem Rand in Sektorfarben und Restdistanz im Fenster unten),
unten rechts das Kombiinstrument des jeweiligen Autos (`Ui/Cluster`: je Auto eigene Instrumentenliste mit Position – Tacho links/rechts,
Zusatzinstrumente wie Ladedruck, Öldruck, Tank/Temperatur-Kombi, R32-Konsolentrio, R34-Multifunktionsdisplay, FD-Zusatzinstrumente, alle innerhalb
des Gehäuses; Gehäuse Hutze/Nissan-Keil/Einzelrohre/LCD, Strich-/Block-/Ring-/Uhrenskala, Altezza-Chronograph, S2000-LCD-Balken;
Skala/roter Bereich/Farben/Nachtbeleuchtung je Auto; Zahlen am Rand außerhalb der Zeigerspitze, Beschriftungen im zeigerfreien unteren Bogen,
kleine Instrumente nur mit Beschriftung + rotem Bereich; Gehäuse höchstens 400×190 px bei 1080p (mit der Höhe skaliert); km/h im
Kilometerzähler-Fenster, Gang + AT/MT unter dem Drehzahlmesser; Tests prüfen Überdeckung, Größe/Lage bei 720p/1080p/4:3/21:9 und dass keine Schrift
im Zeigerbereich liegt). Alles in einem gemeinsamen Sicherheitsrahmen (`Style.Safe`: 44/900 Rand, ab 2:1 mittig begrenzt). Falschfahrt-Warnung, Hinweis mit der Zurücksetzen-Belegung des zuletzt benutzten Geräts (Standard R / Pad Y), wenn das Auto feststeckt.
Schrift: Rajdhani Bold (SIL Open Font License, `Touge/Assets/Fonts/OFL.txt`), zur Laufzeit als Distanzfeld-Atlas.
Ton: Rennmusik als Jukebox über alle 31 Eurobeat-Titel, unabhängig von der Strecke: jeder Titel einmal bis zum Ende, dann zufällig der nächste (keine Wiederholung, bis alle eingeschalteten liefen), läuft über Retry/Kurswechsel weiter (Menümusik hält ihn an). M (Pad: D-Pad rechts) nächster Titel, F3 Musik an/aus. Beim Titelstart fährt oben rechts „NOW PLAYING“ mit Titel und Interpret ein (5 s, in der Pause dauerhaft; unter Battle-Tafel, Story-Ziel bzw. Versus-Platzierung, schmaler oder darunter, wo Zeit-/Drift-Tafel im Weg wären; Options → HUD → NOW PLAYING: ON, OFF, PAUSE ONLY). Optionen → PLAYLIST: jeden Titel ON/OFF schalten (ALL SONGS für alle; alle aus = Stille).
Auto (nur im Stand, < 3 km/h): 1/2 voriges/nächstes Auto, 3 nächste Lackfarbe. Autos (`--car`, Index in Klammern):
AE86T (0), AE86L, AE85, MR2, MRS, ALTEZ, GT-4, R32, R34, ER34, S13 (10), S14Q, S14, S15, ONE80, SIL80, EK9, EG6, INTGR, S2000,
EVO3 (20), EVO4, EVO7, FD3S, FD3SA, FC3S, NA6C, NB8C, IMP, IMP2, IMP3 (30), CAPPU. Physik je Auto: Spur/Radstand/Radradius und
Gangzahl aus dem Spiel, Masse, Leistung, Übersetzungen, Antrieb aus realen Daten (`Kansei.Physics/CarSpecs.cs`); Motorsound je
Auto aus der Original-Zuordnung (FORMATS.md, AE86T/AE86L mit der voll getunten `AE86`-Bank).
Balance (BoP, wie die Arcade-Reihe): `CarSpecs.Real` sind die echten Autos (Datenblatt, Car Guide, Autowahl zeigen sie), gefahren wird
`CarSpecs.All` = echtes Auto mit einem Faktor je Auto auf das Motormoment (`CarSpecs.Bop`, sonst nichts: Gewicht, Reifen, Übersetzung,
Antrieb, Driftschicht bleiben echt – der R34 zieht weiter in 5,7 s auf 100, der Trueno in 7,9 s, verliert aber in den Kurven nicht mehr
gegen 280 PS). Faktoren 0,75 (FD3SA, 280 → 211 PS) … 1,55 (AE85, 83 → 129 PS), AE86 1,06 (130 → 137 PS), R34 1,05; kg/PS 4,5–11,1 → 4,8–7,6.
Kalibriert mit `--ai-bench solo` (alle 32 Autos × alle 11 Legend-Kurse, also auch die Rundkurse MYOGI/USUI, × beide Richtungen,
Referenzfahrer H und Autopilot 0,8; Newton-Schritte auf die mittlere Feldzeit je Kurs): mittlere Zeit je Auto best→schlechtestes
12,6 % → 0,7 % (H; Autopilot 11,6 → 0,6 %, Anfänger 0,2 2,0 %), größte Abweichung eines Autos auf einem Kurs vom Feld 15,2 % → 4,8 %
(EK9 IROHA bergauf: FF in engen Bergauf-Haarnadeln, EG6/INTGR +4, MR2 −3,3; sonst nur der Cappuccino auf dem MYOGI-Rundkurs +3,4, alle
anderen ≤ 3 %). Automatik mit Kickdown: bei Vollgas schaltet sie zurück, sobald der kleinere Gang klar (> 10 %) mehr zieht – vorher erst
unter 40 % der Abregeldrehzahl, der Trueno kroch auf dem MYOGI-Rundkurs bergauf im 4. mit 3100/min und 78 km/h (R34: 115) und verlor dort
allein ~7 s; das war der eigentliche Grund für 8 % Streuung auf MYOGI (jetzt ~3 %, ohne Cappuccino 2 %).
Ein Grip-Faktor dazu drückte die Streuung kaum weiter (0,8 / 2,9 %), hätte aber dem Trueno Grip genommen und den 4WDs gegeben – verworfen.
Der Cappuccino (Spur 1,18 m) fiel vorher in 18 von 36 Läufen von der Straße; sein Schwerpunkt sinkt jetzt mit der schmaleren Spur (2 von 36).
Pad: linker Stick lenken, Trigger Gas/Bremse, A Handbremse, Schultertasten schalten, Y zurück auf die Straße, BACK Kamera, D-Pad hoch Licht, runter Fernlicht, rechts nächster Musiktitel (nicht, wenn D-Pad rechts unter CONTROLLER belegt ist), START Pause.
Q Kupplung (Tastatur).

Steuerung, Lenkrad, Force Feedback (Optionen → CONTROLLER, `Ui/ControlsScreen`, gespeichert als `Settings.Controls`): drei Seiten
KEYBOARD / GAMEPAD / WHEEL (←/→ in der Reiterzeile). Jede Aktion (Lenken links/rechts, Gas, Bremse, Kupplung, Handbremse, Hoch-/Runterschalten,
Gang 1–6/R für die H-Schaltung, Zurück auf die Straße, Kamera, Licht, Fernlicht, Pause; am Lenkrad dazu Menü OK/Zurück) hat zwei Plätze je
Gerät: DECIDE und dann die Taste/den Knopf/den Hat drücken oder eine Achse bewegen – Pedale über die halbe Strecke, mittige Achsen (Lenkung) über 15 % (die Startstellung wird ihre Ruhelage:
Logitech-Pedale ruhen bei +1, Lenkachse in der Mitte, kombinierte Pedale = zwei Hälften einer Achse); ENTF bzw. Pad X leert einen Platz, ESC oder
6 s ohne Eingabe bricht ab, feste Tasten (F1–F4, M, N, T, B, 1–3, ENTF) werden mit BEEP001 abgelehnt, dieselbe Eingabe wird anderen Aktionen des Geräts weggenommen. Pad: Stick-Totzone, Kurve, Rumble. Lenkrad: Gerät
(jeder Joystick; Standard der erste, den SDL als Lenkrad meldet, sonst der erste, der kein Pad ist), CALIBRATE (1. Mitte/Pedale los → Ruhelagen,
2. Anschlag zu Anschlag und alle Pedale durchtreten → Endwerte), ROTATION (Lenkwinkel des Rads laut Treiber, 180–1080°), SENSITIVITY (voller
Einschlag bei 540°/Empfindlichkeit, höchstens beim Anschlag; im Live-Panel angezeigt), Totzone/Kurve Lenkung, Pedal-Totzone, Achsen umkehren,
FORCE FEEDBACK (Stärke, DECIDE = Test rechts→links) und FFB DIRECTION. Rechts ein Live-Panel (Lenkrad im echten Winkel, Pedalbalken,
Rohachsen, Knöpfe, Kraft). Standardbelegung Lenkrad nach Logitech G29/G920 unter Windows (Achse 1 Lenkung, 2 Gas, 3 Bremse, 4 Kupplung, Wippen
Knopf 5/6, Schaltkulisse 13–19, OPTIONS = Pause), andere Räder (Thrustmaster T300/T150/TMX, Fanatec …) per Drücken belegen. Menüs am Lenkrad: Hat =
Pfeile, Wippen = links/rechts, MENU DECIDE/BACK.
Menüs mit jedem Gerät: Tastatur, jedes angeschlossene Pad (nicht nur das erste) und Lenkrad. Gehaltenes D-Pad, gehaltener linker Stick (> 60 %), gehaltener
Hat und gehaltene Wippen wiederholen wie gehaltene Pfeiltasten (0,35 s, dann alle 0,035 s; `HoldRepeat`). Tastenhinweise (rote Hinweiszeile, Replay-Viewer,
Fotomodus, Versus-Lobby, „zurück auf die Straße“) folgen dem zuletzt benutzten Gerät (`Ui/Hints`): Tastatur ENTER/ESC/Tasten, Pad A/B/X/Y, LB/RB,
BACK/START, D-PAD, L-STICK/R-STICK, Lenkrad die belegten MENU-DECIDE/BACK-Knöpfe und HAT; umbelegbare Aktionen zeigen die aktuelle Belegung.
Lenkung: vom zuletzt gelenkten Gerät (`DriverInput`; das Lenkrad übernimmt erst, wenn es 10 % des Einschlags von seiner Stellung beim
Gerätewechsel weggedreht wird, der Pad-Stick schon jenseits der Totzone): Lenkrad 1:1 und ungeglättet (`VehicleInput.DirectSteer`: Radeinschlag = Eingabe ×
`MaxSteer`, ohne die Pad/Tastatur-Hilfen Tempo-Lenkreduktion, Gegenlenkhilfe, Schräglaufgrenze, Lenkrate), Pad-Stick wie bisher, Tasten mit
Rampe. Pedale: das am weitesten getretene aller Geräte; Kupplungspedal begrenzt die Kupplung (`VehicleInput.Clutch`). H-Schaltung (nur MT): ein
Gang gehalten = dieser Gang, keiner = Leerlauf, Wippe/Schalttaste = zurück zu sequenziell.
Force Feedback (`ForceFeedback`, pro Physik-Tick, als eine Constant-Force über SDL2-Haptic auf der Lenkachse, `SDL_HAPTIC_STEERING_AXIS`,
Autocenter aus): Rückstellmoment der Vorderreifen aus Last × x·e^((1−x²)/2), x = Schräglauf/Spitzenschräglauf (wächst mit der Seitenkraft, wird
hinter der Haftgrenze leicht, zieht im Drift in Richtung Gegenlenken), Rumpeln auf Curbs/Rinnen/Gras (Sinus mit Streifenfrequenz Tempo/1,2 m,
höchstens 25 Hz), Stoß bei Wandkontakt (aus der Aufprallgeschwindigkeit, ~0,15 s), Soft-Lock jenseits des Spiel-Einschlags. Kräfte gehen nur ans Lenkrad, solange es das
aktive Gerät ist (sonst 0, damit ein unbenutztes Rad ohne Autocenter nicht mitdreht). Pads bekommen Stöße
und Curbs als Rumble. Auf Akina mit dem Piloten (90 s, erzwungene Drifts): mittlere Kraft 0,19 bei 70 %, 0,1 % der Ticks am Anschlag, in 99 %
der Drift-Ticks zieht das Rad Richtung Gegenlenken. Nicht mit echter Hardware geprüft (kein Lenkrad am Testrechner); Vorzeichen der Kraft je
Treiber verschieden → FFB DIRECTION.
DualSense (PS5-Controller, `DualSenseFeedback`, Optionen → DUALSENSE, immer sichtbar (auch ohne Pad einstellbar), gespeichert als
`Settings.DualSense`; ältere Dateien bekommen die Standardwerte): alles über SDL2s Gamecontroller-API, gleicher Weg unter macOS/Windows/Linux –
Lightbar `SDL_GameControllerSetLED`, Spieler-LEDs `SetPlayerIndex`, Rumble `GameControllerRumble`, adaptive Trigger, Mikro-LED und
Lautsprecher-Lautstärke/-Pfad als 47-Byte-Effektblock über `SDL_GameControllerSendEffect` (`Kansei/Input/DualSense`; SDL verpackt ihn selbst:
USB Report 0x02, Bluetooth Report 0x31 mit CRC-32 über 0xA2 + Report), Neigung aus dem Beschleunigungssensor, Touchpad-Ereignisse.
LIGHTBAR OFF / CAR COLOUR (Lack) / RPM (grün → gelb ab 60 % → rot ab 85 %, ab 95 % Blinken mit 8 Hz) / PLAYER (blau, rot, grün, pink;
geteilter Bildschirm: jedes Pad seine Farbe, sein Auto, seine Spieler-LED), roter Puls bei Kontakten, Menüs/Pause ruhiges Grün, BRIGHTNESS.
TRIGGERS: R2 leichter Widerstand, vibriert bei durchdrehenden Antriebsrädern, Kick beim Gangwechsel; L2 wird mit dem Bremsdruck härter,
pulsiert (20 Hz) bei blockierendem Rad; TRIGGER FORCE. RUMBLE: Motor (Drehzahl, Begrenzer), Curbs/Gras, Kontakte, Landungen, Driftwinkel,
Gangwechsel; RUMBLE STRENGTH (andere Pads behalten das bisherige Rumble). SPEAKER: Menü-SE, Countdown CAR010/CAR011, Schaltklick, Wandtreffer
aus dem Controller-Lautsprecher – über USB ist der DualSense ein 4-Kanal-Audiogerät (`Kansei/Audio/PadSpeaker`, SDL-Audio, Pfad X_X_R: rechter
Kanal → Lautsprecher); über Bluetooth gibt es am PC kein Controller-Audio, die Zeile zeigt dann USB ONLY; SPEAKER VOLUME (Lautstärke-Byte des Pads 0x3D…0x64 – darunter ist er stumm –, 0 = aus; SPEAKER TEST spielt
auch bei SPEAKER OFF). PLAYER LEDS,
MIC LED (aus / an bei Musik aus / an bei Licht an), TOUCHPAD (Wischen = nächster Song, Klick = Kartenmodus wie N), TILT STEER (aus,
Neigung wie ein Lenkrad zum Stick addiert, TILT SENSITIVITY: volle Lenkung bei 60°…20°; übernimmt die Lenkung erst ab 30 % Ausschlag
oder wenn das Pad ohnehin aktiv ist). Jede Funktion mit TEST-Zeile. Beim Beenden: Lightbar
und Spieler-LEDs aus, Trigger frei, Lautsprecher stumm (auch bei einem Absturz; nicht bei kill -9). `--dualsense-log` loggt jeden Befehl mit SDL-Rückgabe;
`--dualsense-test` spielt 20 s lang alles nacheinander durch (Lightbar-Modi, Trigger, Rumble, Countdown aus dem Lautsprecher, Spieler-/Mikro-LED,
Neigung live) – zum Fühlen: `dotnet run --project Touge -- "<iso>" --dualsense-test`.
F1 Freiflug: WASD fliegen, Q/E runter/hoch, rechte Maustaste oder Pfeiltasten umschauen, Shift schnell,
Leertaste ~400 m weiter auf der Fahrlinie. F11 Vollbild, Esc Pause-Menü.
Backend: Metal (macOS) bzw. Vulkan, umschaltbar mit `--backend metal|vulkan|opengl` (oder `PENELOPE_BACKEND`).

| OS | Backend | Einrichtung |
|---|---|---|
| macOS | Metal (Standard) | nichts |
| macOS | Vulkan über MoltenVK | [LunarG Vulkan SDK](https://vulkan.lunarg.com/sdk/home#mac) installieren (Loader nach `/usr/local/lib`, MoltenVK, Validation-Layer, `vulkaninfo`) oder `brew install molten-vk vulkan-loader vulkan-validationlayers vulkan-tools` (`/opt/homebrew/lib`). Penelope sucht den Loader dort selbst (`VulkanDevice.LoaderPath`, oder `SDL_VULKAN_LIBRARY=<pfad>/libvulkan.1.dylib`) und schaltet Portability-Enumeration/-Subset ein, wenn vorhanden. Test: `vulkaninfo --summary` |
| macOS | OpenGL | nicht unterstützt: der GL-Backend braucht 4.5 core, macOS kann nur 4.1 (klare Fehlermeldung beim Start) |
| Windows | Vulkan (Standard) | aktueller GPU-Treiber (Vulkan 1.3); Validation optional mit dem LunarG SDK |
| Windows/Linux | OpenGL | ungetestet; der GL-Backend kann noch keine Bind-Group-Sets > 0 und keinen Tiefen-Resolve, die Spielszene läuft dort derzeit nicht |

Vulkan-Diagnose: `PENELOPE_VALIDATION=1` schaltet `VK_LAYER_KHRONOS_validation` ein (Meldungen auf stdout),
`PENELOPE_VK_DEVICE=<n>` erzwingt Gerät n aus `vulkaninfo --summary` (auf dem Mac z. B. 1 = Mesa KosmicKrisp statt MoltenVK).
Push-Konstanten bleiben überall ≤ 128 B (Vulkan-Minimum); die großen Blöcke (Szene 720 B, Himmel, SSR) liegen als
Uniform-Slices pro Draw in einem Ring (`PostProcess.Upload`, dynamischer Offset).

Streckenenden: Start und Ziel sind die Bögen an beiden Enden (Ziel = Start der Gegenrichtung, beide Richtungen zwischen denselben Bögen, im Menü
Bogen bis Bogen); Endsperre (feste Wand) an den „Straße gesperrt“-Böcken oder, wo die Straße ohne Haarnadel weitergeht, bis 115 m hinter
dem Ziel; eine zweite Wand 5 m hinter dem Startplatz (`Touge/CourseEnd`, Herleitung in FORMATS.md „Kursenden“), auch beim freien Fahren.
Rundkurse haben keine. `--ground` zeichnet Fahrlinie (gelb), Auslauf (cyan) und die Sperren mit, dazu Ausschnitte an Start und Ziel.

Strecke: alle Abschnitte `crsNN` + `mnt00`/`gate*` als ein Mesh, dazu die Bäume (`TREE_*`-Platzierung wie im Original, Vorlagen
`treeMid/Lrg_*` zur Straße gedreht, eingebacken); `crslod*`/`shd*` werden nicht gezeichnet.

Grafik: Szene in HDR (RGBA16F, 4× MSAA mit Resolve, Alpha-to-Coverage für Laub) plus G-Puffer (RGBA8: Ambient-Anteil,
Spiegelgewicht, Streifen) und Tiefe, beide mit aufgelöst (Depth-Resolve Sample 0: Metal, Vulkan `SAMPLE_ZERO`). Danach in halber Auflösung
Ambient Occlusion (ao.frag: 12 Taps im 1,2-m-Radius aus der Tiefe, 4×4-Bayer-Drehung + 4×4-Bilateral-Blur, kein
zeitliches Rauschen) und im Regen Bildschirmraum-Spiegelungen auf nassem Boden (ssr.frag gegen die volle fp32-Tiefe, Spiegelebene = Straßennormale aus der Tiefe,
Treffer erst nach Bisektion + Dickentest; ssr_blur.frag verschmiert sie senkrecht zu Streifen, Rückfall Himmel), Bloom,
im Tonemapping AO nur auf den Ambient-Anteil (Sonne/Lampen bleiben), SSR, ACES, Belichtung, Farbstimmung, Vignette je
Tageszeit (`_DAY`/`_NIT`/`_RIN`, `TougeGame.AtmosphereFor`) und ±1-LSB-Dither gegen Banding. Texturen sRGB mit
Mipmaps (CPU, Alpha-Abdeckung bleibt erhalten) und 8× anisotrop. Himmel: analytischer Verlauf + Sonne hinter dem Sky-Mesh.
Nebel (fog.glsl, pro Pixel auf Strecke, Auto, Effekte, Regen und Horizont von Himmel/Sky-Mesh): linear nach Entfernung +
Höhennebel (am dichtesten am tiefsten Punkt der Fahrlinie, Täler laufen voll), Start/Ende (negativer Start → 0) und Farbton aus dem Kurs (`CRS_INFO`), tags
warm zur Sonne hin, nachts dunkelblau mit Lichthof um Laternen, im Regen dichter grauer
Dunst. Z-Fighting: Dreiecke, die < 1 mm über einem früheren liegen (Decals, überlappende
Streckenabschnitte, Auto-Aufkleber), werden beim Laden zu Overlay-Ebenen (`ZFight`) und pro Ebene 2 mm Richtung Kamera
gezogen – wie auf der PS2 gewinnt die spätere Schicht, statt je nach Rundung zu flackern.

Licht: Streckennormalen beim Laden (winkelgewichtet, Kante ab 60°), Vertexfarbe bleibt das gebackene Licht und wird nur
neu verteilt: `gebacken × (Rest + Sonne × Schatten × N·L)` (`Atmosphere.BakedKeep/BakedSun`). Sonnenschatten: 3 Kaskaden
(bis 12/40/150 m) in einem 6144×2048-Tiefenatlas, texelgenau eingerastet, 3×3-PCF; Auto, Bäume, Schilder und Gebäude
werfen Schatten, gebackene Schatten werden nicht doppelt abgedunkelt. Auto: Klarlack mit Fresnel, Spiegelung der
Env-Maps des Kurses (`ENV_TEX_*`, je Straßenpunkt per `CRS_ENV` gewählt), Scheiben dunkel + spiegelnd, Rücklichter
leuchten beim Bremsen (Bloom). Autolicht (`Headlights`, L/H): aus / Abblend- / Fernlicht, zwei Scheinwerfer an den
Linsenmitten des Modells (`CarModel.Lamps`), Abblendlicht mit japanischer Hell-Dunkel-Grenze (0,7° unter dem Horizont,
links zum Straßenrand ansteigend), Licht staut sich unter der Grenze und wird zum Auto hin dünner (Straße von ~5 bis
~60 m gleichmäßig statt heller Fleck vor der Stoßstange), Fernlicht ohne Grenze, breiter, 2,5× heller, bis 160 m.
Mit Licht an die Nachtteile des Spiels (`Flight01`, `Blamp02`: leuchtende Gläser) plus Glühen (Bloom),
Klappscheinwerfer (AE86, MR2, FD3S, FC3S, ONE80, NA6C) fahren in 0,6 s zwischen `fr_rk_close`/`fr_rk_open` und
leuchten erst oben (zugeklappt mit dem Tagteil); Rückleuchten rot (Bremse heller), Rückfahrlicht weiß im Rückwärtsgang. Grün/blaue Kontrollleuchten
im Kombiinstrument. Alle lokalen Lichter (Scheinwerfer, Laternen, Rückleuchten) sind auf Flächen gedeckelt
(`LightCap`, weich, je Licht nach N·L und in Summe) und wirken nur bei Dunkelheit (`Atmosphere.LocalLightShare`:
tags/Regen 0 – kein Lichtkegel auf der Straße, die Gläser glühen gedämpft; nur die Spiegelung der Rückleuchten
auf nasser Straße bleibt). Keine Lichtkegel im Dunst mehr.
Nacht: die `CRS_LIGHT`-Punkte als Straßenlaternen (4 nächste).

Tag (`_DAY`) und Regen: Sonnenrichtung = Hauptlicht des Originals fürs Auto (`CRS_INFO`, je Kurs, Akina 26,6° von +X wie
das Sonnensprite), warm gegen kühlen Himmels-Schatten und warmes Bodenlicht (`Atmosphere.SunColor/ShadeSky/ShadeGround`), weniger
gebackenes Licht im Schatten (Keep 0,38, Sonne 1,15), Sonnenglanz auf grauen harten Flächen (`Specular`), Kontaktschatten
unter dem Auto (`ContactShadow`, auch bei Regen/Nacht ohne Sonne), Sonne über der Himmelskuppel entlang des echten
Sichtstrahls (kleiner HDR-Kern → Bloom-Blendung, Henyey-Greenstein-Hof), Filmkontrast im Tonemapping (`Contrast`), Nebel-Start/-Ende aus CRS_INFO.

Regen (`_RIN`, `Atmosphere.Wetness`): bedeckt (keine Sonnenschatten, weiches Umgebungslicht), alles nass-dunkler und
satter; nur nach oben zeigende graue, deckende Flächen (Asphalt, Beton) glänzen, fleckig per Rauschen – Gras, Laub,
Fels werden nur dunkler. Glänzende Flächen spiegeln den Himmel unscharf (Wasser-Fresnel, unter Bäumen/an Wänden
gedämpft), Auto/Leitplanken/Wände per SSR (auf rauem Asphalt senkrecht verschmiert) und Lampen als lange Streifen; Pfützen (Rauschmaske auf ebenem Boden) sind dunkle, scharfe Spiegel mit
Regenringen. Fallender Regen: 9000 Tropfen als kamerabezogene Streifen (rain.vert, ohne Vertexpuffer, in einer 36×24×36-m-Box
um die Kamera umgebrochen), durch die Kamerabewegung gestreckt, von Scheinwerfern beleuchtet. Auto: Lack etwas dunkler,
helle Tropfen auf Lack und Scheiben (Glanz + Himmel durch die Wölbung, kaum Nassfleck), nach Pixelgröße ausgeblendet
statt zu flimmern. Scheinwerfer an, Gischt hinter den Hinterrädern nach Tempo (fällt im Bogen, `Effects.EmitSpray`), kaum
Rauch/Bremsspuren. Ton: Regen-Loop `rain` (SYSSE) und nasses Reifenquietschen `RAIN_SRIP`.

Nebel-Wetter (FOG, eigene Ergänzung; Menü Wetter oder `--fog`, gespeichert als `Settings.Fog`, über `_DAY`/`_NIT`,
`TougeGame.FogAtmosphere`): exponentieller Höhennebel mit σ = 0,05/m auf Höhe des Autos (Sicht ≈ 3/σ ≈ 60 m, Basis folgt dem
Auto, nach oben ×1/e alle 50 m dünner, Täler dichter), langsam ziehende Nebelbänke (Rauschen ±30 % der Dichte, `FogDrift`),
Himmel in jeder Richtung verhüllt (`skyFog` mindestens der Nebel der ersten 150 m). Tag: hellgrau-weiß, bedeckt und flach (keine
Sonne/Schatten, weiches Ambient), Sonne nur als hellerer Fleck im Dunst, Scheinwerfer an (Abblendlicht), aber nur die Gläser glühen (tags kein Licht auf
der Straße, `LocalLightShare`).
Nacht: fast schwarzer Nebel, Lichthof der Lampen und Scheinwerferkegel im Dunst (nur bei dichtem Nebel, σ > 0: 8 Proben über 60 m in
glow.frag, Form wie das Licht auf der Straße – Abblendlicht mit scharfer Oberkante, Fernlicht als heller Schleier). Allgemein für alle Lichter: die Extinktion des
Nebels an der Kamera schluckt Lampenlicht mit der Entfernung (`uTailPos[0].w`, auf Flächen und im Lichthof), was durchkommt, ist
weich gedeckelt (`FogLightCap`: Kegel als Schleier statt weißem Fleck). Nebelbänke im Volumen abgetastet (15/45 m entlang des
Strahls, nicht an der Fläche), die ersten 3 m vor der Kamera klar (Auto behält Kontrast).

Effekte (`Effects`/`EffectsRenderer`, effect.frag): Reifenrauch je Rad aus der Rutschgeschwindigkeit (Schlupf × Tempo,
gewichtet mit Radlast) – weiche Billboards mit Rauschen, von Sonne (mit Schatten), Ambient und Scheinwerfern/Laternen
beleuchtet, wachsen, steigen und verblassen, hinten nach vorn sortiert. Bremsspuren als Streifen 2 cm über der Straße an
den Radaufstandspunkten (Ring aus 4096 Segmenten, 25 s sichtbar, dann 10 s Ausblenden). Funken (additiv, HDR → Bloom) an
der Wandkontaktstelle beim Schrammen, dazu kurzes Kamerawackeln je nach Aufprallstärke. Gezeichnet nach Auto/Strecke
in der HDR-Szene (Tiefe getestet, nicht geschrieben), ein Vertex-Ring pro Effektart, keine Allokationen pro Partikel.

## Werkzeuge

```sh
dotnet run --project Touge.Formats.Cli -- list <pfad>/MODEL/HCAR.AFS
dotnet run --project Touge.Formats.Cli -- car <CAR.PAC> out/car [CAR_ENV.BIN [n]]
dotnet run --project Touge.Formats.Cli -- course <KURS.PAC> out/kurs
dotnet run --project Touge.Formats.Cli -- sound "<iso>"                       # alle Audio-Assets: Format, Kanäle, Rate, Dauer, Loop, Rolle
dotnet run --project Touge.Formats.Cli -- wav "<iso>" NIGHT_OF_FIRE out/wav    # passende Assets als WAV (out/ ist gitignored)
dotnet run --project Touge.Formats.Cli -- manga "<iso>" out/manga 2           # Story-Kapitel wie im Original: Manga-Panels + Zeitleiste, Porträts (+ Gesichter), Hintergründe, Stimmspuren, Skript mit Zeiten
dotnet test
```
