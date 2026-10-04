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
dotnet run --project Touge -- "<iso>" AKINA_NIT --menu boot|logo|disclaimer|title|mode|quit|course|route|time|weather|maker|car|gearbox|intro|pause|records|options --shot out/proof/ui_car.png [--shot-size 3200x1800]   # Menü-Bild
dotnet run --project Touge -- "<iso>" --flow out/proof   # ganzer Ablauf per Skript im Fenster (Titel → Auswahl → Laden → Countdown → Rennen mit Pilot in 16× → Pause → Ziel → Ergebnis → Rekorde → Optionen), PNG je Schritt
dotnet run --project Touge -- "<iso>" --frontend-capture out/proof/fe.wav   # ganzer Menüablauf per Skript offline: Original-SE/BGM als WAV + Log aller Auslöser
dotnet run --project Touge -- "<iso>" --shot out/akina.png   # ein Frame als PNG, dann Ende
dotnet run --project Touge -- "<iso>" --ground out/g.png [--at 300]   # Kollision: Raycast-Timing + Draufsicht mit Wänden
dotnet run --project Touge -- "<iso>" --shot out/ae86.png --orbit 35   # Kamera ums Auto (0 vorne, 90 links, 180 hinten)
dotnet run --project Touge -- "<iso>" --autodrive 60 [--shot out/ad.png]   # Pilot fährt 60 s die Fahrlinie ab, Log pro Sekunde
dotnet run --project Touge -- "<iso>" AKINA_DAY --at 710 --autodrive 40 [--ram] [--reverse]   # hinter dem Ziel: Auslauf bis vor die Endsperre bzw. mit --ram Vollgas in die Sperre; Zusammenfassung (Weg hinter dem Ziel, Abstand zur Sperre, Wandkontakt, Höhe)
dotnet run --project Touge -- "<iso>" AKINA_DAY --reverse [--autodrive 60|--shot …|--ground …]   # Gegenrichtung (bergauf): CRS_COLI_<KURS>_1 + CRS_DRV_<KURS>_O, Start am anderen Ende
dotnet run --project Touge -- "<iso>" --hud overview --shot out/map.png   # Minimap-Modus beim Start (north|overview|off)
dotnet run --project Touge -- "<iso>" --bench 30 [--quality off]   # Pilot fährt 30 s in Echtzeit (Fenster), dann Frametime avg/p99/max (mit Ton)
dotnet run --project Touge -- "<iso>" --at 300 --autodrive 5.2 --drift --shot out/drift.png   # --drift: Pilot reißt alle 7 s (ab 4,5 s) einen Handbremsdrift (auch mit --bench)
dotnet run --project Touge -- "<iso>" --autodrive 30 --audio-capture out/a.wav 30 [--no-music]   # Spielton offline als WAV + Auswertung (Pitch↔Drehzahl, Quietschen↔Schlupf, Pegel, Allokationen)
dotnet run --project Touge -- "<iso>" --car FD3S --paint 2   # Auto (HCAR-Name oder Index 0–31) und CAR_ENV-Lackfarbe (0 = Standard), gilt für alle Modi
dotnet run --project Touge -- "<iso>" --car AE86T --livery stock   # Aufkleber/Kennzeichen: rival (Standard: Auto der Anime-Figur, z. B. Tofu-Schriftzug, RedSuns, Emperor), stock (Serienauto des Spiels), none (ohne Aufkleber/Kennzeichen)
dotnet run --project Touge -- "<iso>" --hud off --cars out/proof/c_cars.png [--orbit 145]   # Kontaktbogen aller 32 Autos (4 × 8, Reihenfolge wie unten, Blickwinkel 35° bzw. --orbit), dann Ende
dotnet run --project Touge -- "<iso>" --zfight [AKINA]   # Z-Fighting-Kandidaten aller Kurse/Autos (fast koplanar, überlappend), gruppiert je Batch-Paar
dotnet run --project Touge -- "<iso>" USUI0_RIN --flicker out/proof/fl [--at n]   # Flackern messen: 8 Fahrlinienpunkte, 8 Winkel ums Auto, 8 Fundstellen, je 3× mit anderer Rundung (Schwelle nach Bildhelligkeit); dazu „motion“: dieselben Punkte/Winkel mit Auto, Kamera 3× je 1 cm vor, je mit/ohne SSR – zählt Pixel, deren Spiegelungsanteil nicht gleichmäßig mitläuft
dotnet run --project Touge -- "<iso>" --sun --shot out/sun.png   # freie Kamera hinter dem Auto, Blick zur Sonne (Blendung prüfen)
```

Fahren (Standard): W/S oder ↑/↓ Gas/Bremse, A/D oder ←/→ lenken, Leertaste Handbremse, S im Stand halten = Rückwärts (Automatik), T Automatik/Manuell,
Shift/Strg hoch-/runterschalten (manuell, auch in R), R (Pad: Y) zurück auf die Fahrlinie (nächster freier Punkt, Blick in Fahrtrichtung), B Richtung wechseln (bergab ↔ bergauf, setzt auf die Fahrlinie der Gegenrichtung; Minimap/Fortschritt folgen), C Verfolger-/Stoßstangenkamera,
F2 Grafikqualität hoch/niedrig (4× MSAA, Bloom und Sonnenschatten an/aus; `--quality off` startet niedrig).
Menüs: Ohne Kurs/Test-Flags startet das Spiel im Front-End im Stil des Originals (`Ui/FrontEnd`, nur Vektorformen + Schrift, keine
Original-Menütexturen; Ablauf/Zeiten/Bewegung aus dem Originalcode, siehe FORMATS.md): Hinweis zu den Speicherdaten → Karte „Based on …“ →
Hinweis „Fiktion“ → Titel (Akina bei Nacht im Hintergrund abgeflogen, Logo, blinkendes PRESS START BUTTON; nach 10 s ohne Eingabe zurück zu
den Karten) → Hauptmenü (Trommel mit den 7 Modi des Originals, Chromplatten, pulsierender gelber Rahmen; nach 30 s ohne Eingabe zurück zum
Titel). Gebaut sind davon TIME ATTACK, REPLAY & RECORD (→ Rekorde) und OPTIONS; die anderen Modi piepen (BEEP001) wie gesperrte Einträge
im Original. Als letzter Eintrag QUIT GAME (nur Windows/Linux/macOS): Abfrage „QUIT THE GAME?“ YES/NO (NO vorgewählt, ←/→ wählen,
Enter/A entscheiden, Esc/B = NO), YES speichert die Einstellungen und schließt das Fenster. Esc im Hauptmenü zurück zum Titel, im Titel beenden. Danach alle Bildschirme ebenfalls im Originalstil (`Ui/Menu` + `Ui/Canvas`:
graue Logo-Kachelwand, roter/blauer Laufschrift-Kopf, Chromplatten, Karbonpaneele, gelber Pulsrahmen, Verlaufswörter rot/blau, alles Englisch):
TIME ATTACK = Kurswahl (3 × 4 Raster wie im Original, Streckenlinie im Karbon-„Monitor“ statt Foto, Länge/Höhe/Bestzeit; das 12. Feld „FOUR
PASSES“ ist gesperrt) → Route (DOWNHILL/UPHILL bzw. CLOCKWISE/COUNTER-CLOCKWISE aus dem Drehsinn der Linie) → Tageszeit (DAY/NIGHT) → Wetter
(DRY/WET) – Schritte mit nur einer Möglichkeit entfallen – → Hersteller (7 Chromplatten, Modellliste im Karbonpaneel) → Auto (3D-Auto dreht
sich, ←/→ Modell, ↑/↓ Lackfarbe, Antrieb FF/MR/FR/4WD) → Getriebe (AT/MT) → Laden (weiß, „Now Loading...“) → Streckentelop + 3-2-1-GO
(CAR010/CAR011, Auto steht bis GO) → Rennen. Esc (Pad: Start) pausiert (alarm_02): Continue/Retry/Exit/Quit Game (Quit Game wie im Hauptmenü mit Abfrage, nur Desktop). Im Ziel (Zielbogen, nicht das Ende der
Fahrlinie) „FINISH!!“ bzw. „NEW RECORD!!“ (das Spiel übernimmt das Auto wie ein Arcade-Racer: rollt aus und bremst gleichmäßig bis
4,5 m vor die Absperrung, `Drive.Coast`; WIN-Jingle) → Ergebnis (Gesamt-/Sektorzeiten mit Delta, Bestzeit, Differenz, Driftpunkte, Zeilen zählen mit
NAME001, BGM „JOY“) → Retry / Course Select / Car Select / Exit. Rekorde: Bestzeit je Kurs und Route. Optionen: Grafik, Musik an/aus,
Musik-/SE-Lautstärke, HUD, Navi-Karte, Kamera, Aufkleber (ANIME/STOCK/NONE), Steuerung (Tastatur/Pad-Belegung). Zurück geht die besuchten
Schritte rückwärts. Zwischen Modulen 30-Frame-Schwarzblenden. Ton: Original-SE aus SYSSE (SYS005 Cursor, SYS006 Bestätigen, BEEP001
Zurück/gesperrt, sys002 START, alarm_02 Pause, CAR010/011 Countdown, NAME001 Ergebniszeilen) und Menü-BGM aus BGM.AFS mit Loop-Punkten:
Titel/Hauptmenü „GAMBLE RUMBLE“ (eigene Wahl, das Original ist dort still bzw. spielt den Vorspannfilm), Kurswahl „LIVE IN TOKYO“,
Hersteller/Auto/Rekorde „WORRY“, Laden still, Countdown/Rennen/Pause Eurobeat, Ziel „WIN“ (einmal), Ergebnis „JOY“ (wie im Original).
Navigation Pfeile/WASD, Enter, Esc bzw. D-Pad/Stick, A, B.
Einstellungen, letzte Wahl und Bestzeiten liegen als JSON im App-Data-Ordner (macOS `~/Library/Application Support/InitialDRemake/settings.json`,
Windows `%APPDATA%\InitialDRemake`), nicht im Repo; Starts mit Kurs oder Test-Flags lesen/schreiben sie nicht.
HUD: F4 an/aus, N Minimap mitdrehend → nordausgerichtet → ganze Strecke (`--hud north|overview|off` beim Start). Oben links Zeit, Bestzeit
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
Ton: M nächster Eurobeat-Titel, F3 Musik an/aus (Startstück fest je Kurs).
Auto (nur im Stand, < 3 km/h): 1/2 voriges/nächstes Auto, 3 nächste Lackfarbe. Autos (`--car`, Index in Klammern):
AE86T (0), AE86L, AE85, MR2, MRS, ALTEZ, GT-4, R32, R34, ER34, S13 (10), S14Q, S14, S15, ONE80, SIL80, EK9, EG6, INTGR, S2000,
EVO3 (20), EVO4, EVO7, FD3S, FD3SA, FC3S, NA6C, NB8C, IMP, IMP2, IMP3 (30), CAPPU. Physik je Auto: Spur/Radstand/Radradius und
Gangzahl aus dem Spiel, Masse, Leistung, Übersetzungen, Antrieb aus realen Daten (`Kansei.Physics/CarSpecs.cs`); Motorsound je
Auto aus der Original-Zuordnung (FORMATS.md, AE86T/AE86L mit der voll getunten `AE86`-Bank).
Pad: linker Stick lenken, Trigger Gas/Bremse, A Handbremse, Schultertasten schalten.
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

Streckenenden: Ziel ist der Zielbogen; die „Straße gesperrt“-Böcke 13–63 m dahinter und die Straße 5 m hinter dem Start sind feste
Wände (`Touge/CourseEnd`, Herleitung in FORMATS.md „Kursenden“), auch beim freien Fahren. Rundkurse haben keine.

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
warm zur Sonne hin, nachts dunkelblau mit Lichthof um Laternen und sichtbaren Scheinwerferkegeln, im Regen dichter grauer
Dunst. Z-Fighting: Dreiecke, die < 1 mm über einem früheren liegen (Decals, überlappende
Streckenabschnitte, Auto-Aufkleber), werden beim Laden zu Overlay-Ebenen (`ZFight`) und pro Ebene 2 mm Richtung Kamera
gezogen – wie auf der PS2 gewinnt die spätere Schicht, statt je nach Rundung zu flackern.

Licht: Streckennormalen beim Laden (winkelgewichtet, Kante ab 60°), Vertexfarbe bleibt das gebackene Licht und wird nur
neu verteilt: `gebacken × (Rest + Sonne × Schatten × N·L)` (`Atmosphere.BakedKeep/BakedSun`). Sonnenschatten: 3 Kaskaden
(bis 12/40/150 m) in einem 6144×2048-Tiefenatlas, texelgenau eingerastet, 3×3-PCF; Auto, Bäume, Schilder und Gebäude
werfen Schatten, gebackene Schatten werden nicht doppelt abgedunkelt. Auto: Klarlack mit Fresnel, Spiegelung der
Env-Maps des Kurses (`ENV_TEX_*`, je Straßenpunkt per `CRS_ENV` gewählt), Scheiben dunkel + spiegelnd, Rücklichter
leuchten beim Bremsen (Bloom). Nacht: zwei Scheinwerfer-Kegel (flach/breit) und die `CRS_LIGHT`-Punkte als
Straßenlaternen (4 nächste), Rückleuchten als kleine rote Punktlichter.

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
