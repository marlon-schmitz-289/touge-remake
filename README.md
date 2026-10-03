# Initial D Remake

Privater Nachbau (eigene C#-Engine). Plan: [PLAN.md](PLAN.md), Formate: [FORMATS.md](FORMATS.md).

Assets kommen zur Laufzeit aus der eigenen ISO (SLPM-65268), nie ins Repo.

## Projekte

| Projekt | Inhalt |
|---|---|
| `Penelope` | GPU-Abstraktion (Vulkan/Metal/OpenGL), aus MEFactory übernommen |
| `Kansei` | Engine: Fenster, Input, Loop mit fester Tick-Rate, World- und Car-Renderer |
| `Kansei.Physics` | `IGround`, `TriangleGround` (Raycast + Wände über XZ-Grid), Fahrzeugphysik `Vehicle` + `CarSpec` (Default AE86), `LinePilot` (fährt die Fahrlinie ab) |
| `Touge.Formats` | Spielformate: ISO, AFS, PAC, LZ, GIM, CMD/SMD, Kollision, Fahrlinie, Lack |
| `Touge.Formats.Cli` | `idss` – Formate untersuchen/exportieren |
| `Touge` | Das Spiel (derzeit: AE86 auf jeder Strecke fahren, Freiflug per F1) |

## Starten

```sh
dotnet run --project Touge -- "<pfad>/Initial D - Special Stage (Japan) (v2.00).iso" [AKINA_DAY|AKINA_NIT|USUI_NIT|…]
dotnet run --project Touge -- "<iso>" --shot out/akina.png   # ein Frame als PNG, dann Ende
dotnet run --project Touge -- "<iso>" --ground out/g.png [--at 300]   # Kollision: Raycast-Timing + Draufsicht mit Wänden
dotnet run --project Touge -- "<iso>" --shot out/ae86.png --orbit 35   # Kamera ums Auto (0 vorne, 90 links, 180 hinten)
dotnet run --project Touge -- "<iso>" --autodrive 60 [--shot out/ad.png]   # Pilot fährt 60 s die Fahrlinie ab, Log pro Sekunde
dotnet run --project Touge -- "<iso>" --bench 30 [--quality off]   # Pilot fährt 30 s in Echtzeit (Fenster), dann Frametime avg/p99/max
dotnet run --project Touge -- "<iso>" --at 300 --autodrive 5.2 --drift --shot out/drift.png   # --drift: Pilot reißt alle 7 s (ab 4,5 s) einen Handbremsdrift (auch mit --bench)
```

Fahren (Standard): W/S oder ↑/↓ Gas/Bremse, A/D oder ←/→ lenken, Leertaste Handbremse, S im Stand halten = Rückwärts (Automatik), T Automatik/Manuell,
Shift/Strg hoch-/runterschalten (manuell, auch in R), R zurück auf die Fahrlinie, C Verfolger-/Stoßstangenkamera,
F2 Grafikqualität hoch/niedrig (4× MSAA, Bloom und Sonnenschatten an/aus; `--quality off` startet niedrig).
Pad: linker Stick lenken, Trigger Gas/Bremse, A Handbremse, Schultertasten schalten.
F1 Freiflug: WASD fliegen, Q/E runter/hoch, rechte Maustaste oder Pfeiltasten umschauen, Shift schnell,
Leertaste ~400 m weiter auf der Fahrlinie. F11 Vollbild, Esc Ende.
Backend: Metal (macOS) bzw. Vulkan, umschaltbar mit `--backend metal|vulkan|opengl`.

Grafik: Szene in HDR (RGBA16F, 4× MSAA mit Resolve, Alpha-to-Coverage für Laub), danach Bloom, ACES-Tonemapping,
Belichtung, Farbstimmung und Vignette je Tageszeit (`_DAY`/`_NIT`/`_RIN`, `TougeGame.AtmosphereFor`). Texturen sRGB mit
Mipmaps (CPU, Alpha-Abdeckung bleibt erhalten) und 8× anisotrop. Himmel: analytischer Verlauf + Sonne hinter dem Sky-Mesh,
Nebel in der Horizontfarbe.

Licht: Streckennormalen beim Laden (winkelgewichtet, Kante ab 60°), Vertexfarbe bleibt das gebackene Licht und wird nur
neu verteilt: `gebacken × (Rest + Sonne × Schatten × N·L)` (`Atmosphere.BakedKeep/BakedSun`). Sonnenschatten: 3 Kaskaden
(bis 12/40/150 m) in einem 6144×2048-Tiefenatlas, texelgenau eingerastet, 3×3-PCF; Auto, Bäume, Schilder und Gebäude
werfen Schatten, gebackene Schatten werden nicht doppelt abgedunkelt. Auto: Klarlack mit Fresnel, Spiegelung der
Env-Maps des Kurses (`ENV_TEX_*`, je Straßenpunkt per `CRS_ENV` gewählt), Scheiben dunkel + spiegelnd, Rücklichter
leuchten beim Bremsen (Bloom). Nacht: zwei Scheinwerfer-Kegel (flach/breit) und die `CRS_LIGHT`-Punkte als
Straßenlaternen (4 nächste). Regen: flache Flächen dunkler, Horizont-Spiegelung und Glanzlichter.

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
dotnet test
```
