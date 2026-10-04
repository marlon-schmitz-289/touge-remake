# Initial D Remake

Privater Nachbau (eigene C#-Engine). Plan: [PLAN.md](PLAN.md), Formate: [FORMATS.md](FORMATS.md).

Assets kommen zur Laufzeit aus der eigenen ISO (SLPM-65268), nie ins Repo.

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

```sh
dotnet run --project Touge -- "<pfad>/Initial D - Special Stage (Japan) (v2.00).iso"   # Front-End (Hinweis, Karten, Titel, Hauptmenü) → Kurs, Auto, Rennen, Ergebnis, Rekorde, Optionen
dotnet run --project Touge -- "<pfad>/Initial D - Special Stage (Japan) (v2.00).iso" [AKINA_DAY|AKINA_NIT|USUI_NIT|…]   # direkt fahren, ohne Menü
dotnet run --project Touge -- "<iso>" AKINA_NIT --menu boot|logo|disclaimer|title|mode|quit|course|route|time|weather|maker|car|gearbox|intro|pause|records|guide|guide-list|guide-talk|options[:gamesetting|hud|screen|graphics|sound|playlist|controller] --shot out/proof/ui_car.png [--shot-size 3200x1800]   # Menü-Bild (options:<seite> öffnet eine Optionsseite)
dotnet run --project Touge -- "<iso>" --flow out/proof   # ganzer Ablauf per Skript im Fenster (Titel → Auswahl → Laden → Countdown → Rennen mit Pilot in 16× → Pause → Ziel → Ergebnis → Rekorde → Optionen → Car Guide), PNG je Schritt
dotnet run --project Touge -- "<iso>" --flow out/proof/legend/flow --legend [--car FD3S]   # Legend of the Streets per Skript: Akina, zwei Battles (Autopilot, 16×), Leiter danach, PNG je Schritt
dotnet run --project Touge -- "<iso>" --legend-sim [--legend-progress out/legend.json] [--car FD3S]   # Legend ohne Fenster: Autopilot fährt jede Rivalenleiter hoch (Niederlage beendet den Kurs), Freischaltungen + Bilanz, Fortschritt als JSON
dotnet run --project Touge -- "<iso>" --menu legend|legend-rivals|legend-card[:AKINA/takumi] [--legend-progress <json>] --shot out/proof/l.png   # Legend-Schritt als Bild (Fortschritt aus der Datei, sonst leer)
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
dotnet run --project Touge -- "<iso>" AKINA_NIT --battle keisuke [--rule race|chase] [--lead player|rival] [--car AE86T]   # Schnellbattle gegen die KI: Telop „VS …“, 3-2-1-GO, Battle, YOU WIN/LOSE, Ergebnis
dotnet run --project Touge -- "<iso>" IROHA_DAY --battle takumi --autodrive 420   # ohne Fenster: Autopilot (Spielerauto) gegen die KI, Log je Sekunde (Abstand s/m, Führung, KI-Modus, Kontakte) + Ergebnis
dotnet run --project Touge -- "<iso>" IROHA_DAY --battle itsuki --autodrive 110 --shot out/proof/b.png [--battle-result]   # Bild nach dem Battle: Zielbanner bzw. Ergebnisblatt
dotnet run --project Touge -- "<iso>" --menu story[:n[:scene[:teil[:zeile]]|:race|:end]] [--progress 12] --shot out/proof/s.png   # STORY: Kapitelwahl, Szene, Rennstart von Kapitel n oder THE END (--progress: Kapitel 0…n−1 geschafft, nur Testlauf; mit gespeichertem Fortschritt ignoriert)
dotnet run --project Touge -- "<iso>" --flow out/proof/story --story --progress 5   # STORY-Ablauf im Fenster: Wahl → Szene → Battle (Pilot) → Ergebnis → Szene danach; verlorenes Kapitel mit RETRY
dotnet run --project Touge -- "<iso>" --story-check [n|calibrate]   # ohne Fenster: Kapiteltabelle + Szenen der Disc gegen die Übersetzung, dann jedes Kapitel mit dem Autopiloten (Durchlauf-Log); calibrate = Rivalenstärke/Zeitgrenzen messen
```

Battle (`Touge/Race`, Grundlage für Legend of the Streets, Story und Multiplayer): `RaceSession` mit N Autos, jedes mit einem Fahrer
(`ICarDriver`: Spieler-Eingabe, KI, später Replay/Netz), Startaufstellung, Zusammenstößen, Auslauf nach dem Ziel und den Regeln (`Battle`).
Rivalen (`--battle <id>` oder ein Auto): itsuki, iketani, kenji, takeshi, shingo, mako, kai, seiji, kyoichi, keisuke, ryosuke, wataru, takumi, bunta
(Auto mit der Lackierung der Figur, je eigener Fahrstil: Können, Aggressivität, Drift). Regeln: `race` (Standard, wie das Original: nebeneinander,
wer zuerst im Ziel ist, gewinnt; dazu Sieg vorzeitig ab 8 s Vorsprung) und `chase` (Lead/Chase wie im Anime: der Verfolger gewinnt, wenn er
überholt und 1,5 s vorne bleibt – erst nach 10 s, ein Überholen gleich nach dem Start zählt nicht, die KI
versucht es dann auch nicht; der Führende mit 4 s Vorsprung oder ≥ 1 s Vorsprung im Ziel; klebt der Verfolger am Ziel dran: DRAW;
`--lead player|rival` wer vorne startet, Standard der Rivale). KI (`Kansei.Physics/RivalPilot`): fährt die Fahrlinie des Kurses (CRS_DRV _I/_O)
mit Bremspunkten nach Kurvenradius und Können, folgt mit Abstand, überholt innen vor Kurven oder auf der freien Seite (nur wo die Straße breit
genug ist), verteidigt die Innenseite vor Kurven, fährt nie in ein Auto daneben (wird die Straße zu schmal: hält
ihre Seite und lupft, um sich dahinter einzureihen), Drift-Stil mit kurzem Handbremsimpuls in Haarnadeln; dezentes Gummiband (±5 % Tempo ab
30 m Abstand zum Spieler). Auto gegen Auto: Kastenkollision (SAT, über den Tick abgetastet – kein Durchtunneln), Impulse mit Drall, Funken,
Kamerawackeln und Crash-Ton. HUD oben rechts: VS + Rivale, Position 1ST/2ND, LEAD/CHASE, ADVANTAGE (Zeitabstand), Abstandsbalken bis zur
Vorsprungsgrenze, OVERTAKE!/OVERTAKEN; roter Punkt auf der Streckenuhr. Ton des Rivalen (Motor, Reifen, Wand) nach Entfernung, mit Doppler
und Stereo. Ende: YOU WIN!!/YOU LOSE/DRAW mit WIN.adx/LOSE.adx (DRAW: WIN.adx wie das Zieljingle), Ergebnisblatt (Rivale, Auto, entschieden durch, Abstand, Zeiten, Führungswechsel,
Kontakte – eine Berührung zählt neu erst nach 0,25 s Abstand) → Retry / Course Select / Car Select / Exit. Steuerung wie beim Fahren (Tastatur und Pad); B (Richtung wechseln) ist im Battle aus.

Fahren (Standard, alles außer F-Tasten/M/N/B/T/1–3 unter Optionen → CONTROLLER umbelegbar): W/S oder ↑/↓ Gas/Bremse, A/D oder ←/→ lenken, Leertaste Handbremse, S im Stand halten = Rückwärts (Automatik), T Automatik/Manuell,
Shift/Strg hoch-/runterschalten (manuell, auch in R), R (Pad: Y) zurück auf die Fahrlinie (nächster freier Punkt, Blick in Fahrtrichtung), B Richtung wechseln (bergab ↔ bergauf, setzt auf die Fahrlinie der Gegenrichtung; Minimap/Fortschritt folgen), C Verfolger-/Stoßstangenkamera,
L Licht an/aus, H Fernlicht an/aus (Umschalter, schaltet das Licht auch ein; Pad: D-Pad hoch/runter),
F2 Grafikqualität ULTRA ↔ LOW (MSAA, Schatten, AO, Bloom, Regen-SSR zusammen; `--quality off` startet LOW).
Menüs: Ohne Kurs/Test-Flags startet das Spiel im Front-End im Stil des Originals (`Ui/FrontEnd`, nur Vektorformen + Schrift, keine
Original-Menütexturen; Ablauf/Zeiten/Bewegung aus dem Originalcode, siehe FORMATS.md): Hinweis zu den Speicherdaten → Karte „Based on …“ →
Hinweis „Fiktion“ → Titel (Akina bei Nacht im Hintergrund abgeflogen, Logo, blinkendes PRESS START BUTTON; nach 10 s ohne Eingabe zurück zu
den Karten) → Hauptmenü (Trommel mit den 7 Modi des Originals, Chromplatten, pulsierender gelber Rahmen; nach 30 s ohne Eingabe zurück zum
Titel). Gebaut sind davon LEGEND OF THE STREETS, TIME ATTACK, STORY, REPLAY & RECORD (→ Rekorde), IKETANI'S CAR GUIDE und OPTIONS; die anderen Modi piepen (BEEP001) wie gesperrte Einträge
im Original. Als letzter Eintrag QUIT GAME (nur Windows/Linux/macOS): Abfrage „QUIT THE GAME?“ YES/NO (NO vorgewählt, ←/→ wählen,
Enter/A entscheiden, Esc/B = NO), YES speichert die Einstellungen und schließt das Fenster. Esc im Hauptmenü zurück zum Titel, im Titel beenden. Danach alle Bildschirme ebenfalls im Originalstil (`Ui/Menu` + `Ui/Canvas`:
graue Logo-Kachelwand, roter/blauer Laufschrift-Kopf, Chromplatten, Karbonpaneele, gelber Pulsrahmen, Verlaufswörter rot/blau, alles Englisch):
TIME ATTACK = Kurswahl (3 × 4 Raster wie im Original, Streckenlinie im Karbon-„Monitor“ statt Foto, Länge/Höhe/Bestzeit; das 12. Feld „FOUR
PASSES“ ist gesperrt) → Route (DOWNHILL/UPHILL bzw. CLOCKWISE/COUNTER-CLOCKWISE aus dem Drehsinn der Linie) → Tageszeit (DAY/NIGHT) → Wetter
(DRY/WET/FOG, nachts DRY/FOG) – Schritte mit nur einer Möglichkeit entfallen – → Hersteller (7 Chromplatten, Modellliste im Karbonpaneel) → Auto (3D-Auto dreht
sich, ←/→ Modell, ↑/↓ Lackfarbe, Antrieb FF/MR/FR/4WD) → Getriebe (AT/MT) → Laden (weiß, „Now Loading...“) → Streckentelop + 3-2-1-GO
(CAR010/CAR011, Auto steht bis GO) → Rennen. Esc (Pad: Start) pausiert (alarm_02): Continue/Retry/Exit/Quit Game (Quit Game wie im Hauptmenü mit Abfrage, nur Desktop). Start und Ziel sind die beiden Bögen an den
Streckenenden: bergab endet, wo bergauf startet, und umgekehrt (Auto steht 5 m hinter seinem Startbogen, Zeit läuft ab dem Bogen). Im Ziel
„FINISH!!“ bzw. „NEW RECORD!!“ (das Spiel übernimmt das Auto wie ein Arcade-Racer: rollt aus und bremst gleichmäßig bis
4,5 m vor die Endsperre, vor Kurven des Auslaufs auch stärker, `Drive.Coast`; WIN-Jingle) → Ergebnis (Gesamt-/Sektorzeiten mit Delta, Bestzeit, Differenz, Driftpunkte, Zeilen zählen mit
NAME001, BGM „JOY“) → Retry / Course Select / Car Select / Exit. Rekorde: Bestzeit je Kurs und Route. Optionen (`Ui/Options`) wie im
Original zweistufig: Abschnittsliste aus Chromplatten (OPSL) → Seite mit Zeilen (OPGM: Reiter links, Chromplatte mit gravierten Werten, mehr als
3 Werte als ◀ Wert ▶, Lautstärken als 10 Blöcke, Hilfetext unten; ↑/↓ Zeile, ←/→ bzw. Enter/A ändern, Esc/B zurück), alles wirkt sofort und wird gespeichert:
GAME SETTING (Einheit km/h/mph im Kombiinstrument, Getriebe-Vorwahl AT/MT, Lenkhilfe OFF/LOW/FULL = `CounterSteerAssist` × 0/0,5/1, Drift-Hilfe
LOW/NORMAL/HIGH = `DriftDamping` × 0,5/1/1,6 – beide ab dem nächsten Lauf; andere Hilfen als FULL/NORMAL fahren eigene Bestzeiten, RECORDS zeigt nur die Serienwerte –, Startkamera, Blickwinkel 50–90°, Kamerawackeln, Aufkleber
ANIME/STOCK/NONE), HUD (an/aus, HUD SIZE 80–130 %, Navi-Karte), SCREEN (WINDOW/BORDERLESS, außer macOS auch FULLSCREEN exklusiv; Auflösung aus den Modi des
Bildschirms; VSync; Bildratenbegrenzung 30–240; Render-Skalierung 50–150 %), GRAPHICS (Voreinstellung LOW/MEDIUM/HIGH/ULTRA bzw. CUSTOM aus den
Schaltern MSAA, Sonnenschatten, AO, Bloom, Regen-Spiegelungen), SOUND (Gesamt, Musik an/aus + Lautstärke, SE, Motor, Menü-SE), PLAYLIST (Renntitel einzeln an/aus), CONTROLLER
(Steuerungsbildschirm, siehe unten; ohne Live-Eingabe nur die Belegung als Text). Andere Features hängen eigene Seiten an `Menu.Options.Pages` an (Zeilen per `Options.Row.Choice/Toggle/Slider`
oder eigener Bildschirm über `Page.Input`/`Page.Draw`). Zurück geht die besuchten
Schritte rückwärts. Zwischen Modulen 30-Frame-Schwarzblenden. Ton: Original-SE aus SYSSE (SYS005 Cursor, SYS006 Bestätigen, BEEP001
Zurück/gesperrt, sys002 START, alarm_02 Pause, CAR010/011 Countdown, NAME001 Ergebniszeilen) und Menü-BGM aus BGM.AFS mit Loop-Punkten:
Titel/Hauptmenü „GAMBLE RUMBLE“ (eigene Wahl, das Original ist dort still bzw. spielt den Vorspannfilm), Kurswahl „LIVE IN TOKYO“,
Hersteller/Auto/Rekorde „WORRY“, Laden still, Countdown/Rennen/Pause Eurobeat, Ziel „WIN“ (einmal), Ergebnis „JOY“ (wie im Original).
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
sein Impreza (IMP3) für Legend-Battles frei (dort bis dahin „?????“; Time Attack hat es immer). Revanche gegen einen besiegten Rivalen im Regen (wie das Original). Die ersten drei
Rivalen eines Hauptkurses fahren mit 80/88/95 % Motormoment (eigene Abstimmung, damit der Trueno am Anfang mithalten kann).
Nach einem Battle steht der Cursor auf dem nächsten Rivalen, Neues (Rivalen, Kurse, Auto) läuft unten als rotes Band ein. Fortschritt in
`legend.json` neben `settings.json` (Siege, Niederlagen, bester Abstand je Rivale; `Race/Legend.Progress`). Pause-Exit und Zurück aus der
Autowahl führen zur Leiter, EXIT ins Hauptmenü.
Navigation Pfeile/WASD, Enter, Esc bzw. D-Pad/Stick, A, B.
STORY (`Touge/Story`, wie der Story-Modus des Originals, Daten zur Laufzeit aus der ISO, FORMATS.md „Story“): 31 Kapitel in drei Teilen
(THE LEGEND OF AKINA 1–19, THE TAKAHASHI BROTHERS 20–24, PROJECT D 25–31) vom „Geist von Akina“ bis zu Bunta im Impreza. Kapitelwahl (BGM WORRY
wie im Original): Teile als Chromplatten (←/→), Kapitelliste (↑/↓, geschafft = CLEAR, gesperrte grau), Infotafel mit Titel, kurzer Inhalt, Kurs/Richtung/
Nacht, Fahrer und Auto (Takumi im AE86, in einigen Kapiteln Keisuke im FD, Ryosuke im FC, Takumi im 180SX), Rivale mit Team und Auto, Ziel. Kurs, Richtung,
Autos und Ziel liest das Spiel aus der Kapiteltabelle im ELF. Ablauf: Laden → Szene vor dem Rennen → Telop „VS …“, Countdown → Battle (bzw. Lauf
allein) → eigenes Banner (YOU WIN/LOSE bzw. CLEAR!!/TIME UP/FAILED mit WIN/LOSE/TIMEUP.adx) → Ergebnis (Battle-Blatt + Story-Tafel) →
gewonnen: CONTINUE → Szene danach → nächstes Kapitel frei und gewählt; verloren: RETRY / CHAPTER SELECT. Nach dem letzten Kapitel „THE END“ mit
THERACEISOVER. Szenen: Textpanels im Stil der Menüs über dem Flug entlang der Straße, Sprecherplatte mit Teamfarbe, Schreibmaschinentext
(Gedanken hellblau kursiv), Titelkarte; Entscheiden = weiter (erst Zeile fertig), → = Szene überspringen; Zurück: vor dem Rennen zur Kapitelwahl, danach Szene überspringen. Text: die Zeilen der Original-Szenen
(`MG_OBJ` `STRnn.BIN`, 585 Äußerungen) in eigener englischer Übersetzung, Zeile für Zeile in Reihenfolge und Teilen des Originals (Kapitel 1/2 ohne Szene auf
der Disc: eigener kurzer Text). Musik der Szenen aus ST_BGM_N (STORY_STnn). Ziele (Code aus dem ELF): Rennen; vorne bleiben bis ins Ziel; hinterher
und überholen (auch: in 120 s); 100 s dranbleiben; allein mit Zeitgrenze, Tofu bergauf mit höchstens 3 Wandtreffern, Mitfahrer mit 10.000 Driftpunkten –
im Rennen oben rechts eine Tafel mit Restzeit/Wandtreffern/Drift. Rivalenstärke je Kapitel aus einer Kalibrierung gegen den Autopiloten, am Anfang
mit Spielraum. Pause → Exit führt zur Kapitelwahl zurück. Regen bei Nacht (Kapitel 11, MYOGI) gibt es auf der Disc nicht: der Nachtkurs bekommt
Regen (Tropfen, nasse Spiegelungen, Gischt, Regen-Sound, Telop WET); die Haftung ist wie auf den _RIN-Kursen unverändert. Fortschritt in `progress.json` neben den Einstellungen (Schlüssel `story/nn`, Zähler
`…/tries`, `…/wins`; die Datei ist für alle Modi mit Karriere gedacht, z. B. Legend of the Streets); Story-Läufe zählen nicht als Time-Attack-Rekord.
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
im Zeigerbereich liegt). Alles in einem gemeinsamen Sicherheitsrahmen (`Style.Safe`: 44/900 Rand, ab 2:1 mittig begrenzt). Falschfahrt-Warnung, Hinweis „R“ zum Zurücksetzen, wenn das Auto feststeckt.
Schrift: Rajdhani Bold (SIL Open Font License, `Touge/Assets/Fonts/OFL.txt`), zur Laufzeit als Distanzfeld-Atlas.
Ton: Rennmusik als Jukebox über alle 31 Eurobeat-Titel, unabhängig von der Strecke: jeder Titel einmal bis zum Ende, dann zufällig der nächste (keine Wiederholung, bis alle eingeschalteten liefen), läuft über Retry/Kurswechsel weiter (Menümusik hält ihn an). M (Pad: D-Pad rechts) nächster Titel, F3 Musik an/aus. Beim Titelstart fährt oben rechts „NOW PLAYING“ mit Titel und Interpret ein (5 s, in der Pause dauerhaft; im Battle unter der Battle-Tafel). Optionen → PLAYLIST: jeden Titel ON/OFF schalten (ALL SONGS für alle; alle aus = Stille).
Auto (nur im Stand, < 3 km/h): 1/2 voriges/nächstes Auto, 3 nächste Lackfarbe. Autos (`--car`, Index in Klammern):
AE86T (0), AE86L, AE85, MR2, MRS, ALTEZ, GT-4, R32, R34, ER34, S13 (10), S14Q, S14, S15, ONE80, SIL80, EK9, EG6, INTGR, S2000,
EVO3 (20), EVO4, EVO7, FD3S, FD3SA, FC3S, NA6C, NB8C, IMP, IMP2, IMP3 (30), CAPPU. Physik je Auto: Spur/Radstand/Radradius und
Gangzahl aus dem Spiel, Masse, Leistung, Übersetzungen, Antrieb aus realen Daten (`Kansei.Physics/CarSpecs.cs`); Motorsound je
Auto aus der Original-Zuordnung (FORMATS.md, AE86T/AE86L mit der voll getunten `AE86`-Bank).
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
dotnet test
```
