# Initial D Remake – Plan

Stand: 2026-10-03 · Nur Plan, noch nichts gebaut. Privat (Freunde), Original-Assets aus der eigenen ISO.
Eigene Engine in C#, aufgebaut nach dem Vorbild von MEFactory (MogliEngine/Penelope). Kein Unity, kein DriftusMaximus.

## 1. Ziel

3D-Touge-Racer mit Strecken und Autos aus *Initial D Special Stage* (PS2, SLPM-65268), dazu:
- eigene, bessere Fahrphysik (drift-tauglich, glaubwürdig, nicht Sim-hart)
- Lenkrad mit Force Feedback
- Online-Multiplayer mit Freunden (Battle 1v1, Time Attack, Free Roam bis 8 Spieler)

**Nicht in v1:** Story/Manga-Szenen, Original-Sound (erst Platzhalter), Tuning-Garage, Replays, KI-Gegner, Splitscreen.

## 2. Was wir von MEFactory lernen

| MEFactory | Übernehmen für Remake | Anders machen |
|---|---|---|
| Schichtung **Game → Engine → RHI (Penelope) → Platform** mit harten Grenzen | Gleiche Schichtung: `Touge` (Spiel) → `Kansei` (Engine) → `Penelope` (RHI) → SDL2/OpenAL | – |
| **Penelope**: WebGPU-artige RHI, Vulkan/OpenGL/**Metal**, Shader-Baker GLSL→SPIR-V/MSL, Depth/MSAA/Compute/Indirect | Konzept 1:1. Empfehlung: **Penelope-Code kopieren** statt neu schreiben (ist deine eigene Engine, spart Monate, Metal für Mac schon drin) | – |
| `Graphics3D`-Spike (`Camera3D`, `Mesh`, `MeshRenderer`) ist nur ein Würfel | Als Startpunkt | Echter 3D-Renderer nötig: Szenen, Materialien, Nacht/Scheinwerfer, Schatten, Frustum-Culling, Instancing |
| `MogliApp.Run<T>()`, `MogliGame`, `FrameContext`, `IPlatform` | Gleiches Bootstrap-Muster | Loop mit **fester Physik-Rate 120 Hz** + Interpolation beim Rendern |
| Input: `InputMap`, `InputAction`, `Gamepad` über SDL2 | Gleiches Action-Mapping | + Lenkrad-Achsen (volle Auflösung, 900°) + **SDL2-Haptic für FFB** (Silk.NET.SDL ist schon Dependency) |
| Arch-ECS nur im Spiel-Layer, nicht in der Engine | Ja – aber nur wo es hilft | Autos/Physik als normale Klassen/Structs; wenige Entities, kein ECS-Zwang |
| Netz: **LiteNetLib** + **Steamworks**-Lobbys, **Lockstep** (`SimulationHost`) | LiteNetLib + Steam-Lobby/Invite (Freunde einladen ohne IP) | **Kein Lockstep** – bei Rennen bremst Lockstep alle auf den langsamsten Ping. Stattdessen Owner-Authority + Snapshot-Interpolation |
| `ActionProcessor` als einziger Mutationspunkt | Gleiches Prinzip für Rennzustand (Start, Checkpoints, Ergebnis) beim Host | – |
| Golden-Tests in `Tests/` | Golden-Tests für Format-Konverter (Hash der erzeugten Meshes/Texturen) | – |

## 3. Grundentscheidungen

| # | Entscheidung | Warum |
|---|---|---|
| E1 | **.NET 10, C#, Silk.NET** (SDL2, OpenAL, Vulkan/Metal) – gleicher Stack wie MEFactory | Bekannt, cross-platform, alles schon einmal zum Laufen gebracht |
| E2 | **Assets nie im Repo oder Build.** Erster Start: ISO wählen → Konverter → lokaler Cache in eigenem Binärformat | Nichts Urheberrechtliches wird verschickt; jeder Freund braucht seine ISO |
| E3 | **Konverter als eigene Library** `Touge.Formats` (+ CLI `idss`) | Formate einmal knacken, in CLI und Spiel nutzen; ohne Grafik testbar |
| E4 | **Eigene Physik**, kein Physik-Paket: Raycast-Wheels, Reifenmodell (vereinfachte Magic Formula), Federung, Gewichtsverlagerung, Antriebsstrang | Volle Kontrolle übers Fahrgefühl; Rennspiel braucht nur Fahrzeug-vs-Strecke und Auto-vs-Auto |
| E5 | **Kollision**: Streckenmesh in BVH, Räder per Raycast, Karosserie als OBB gegen Leitplanken/Wände, Auto-Auto per OBB-SAT | Reicht für Touge, kein allgemeiner Rigid-Body-Solver nötig |
| E6 | **Netz**: Client-Host über LiteNetLib (Steam-Relay optional), Owner simuliert eigenes Auto, sendet 30 Hz Zustand, andere interpolieren (~100 ms Puffer) + Extrapolation bei Lücken | Kein Input-Lag fürs eigene Auto; Rennablauf autoritativ beim Host |
| E7 | **Recomp-Projekt (`initiald-special-stage-pc`) als Reverse-Engineering-Hilfe** | Originalcode läuft → Loader tracen, RAM nach Streckenladen dumpen. Spart Wochen beim Format-Knacken |

## 4. Was in der ISO steckt (gesichtet)

Alle relevanten Daten in **CRI AFS**-Archiven (Format bekannt, trivial). Darin fast überall **PAC**-Container (Magic `PAC\0`, unbekannt).

| Datei | Einträge | Vermutung |
|---|---|---|
| `MODEL/CAR.AFS` | 70 PAC | Autos In-Race (LOD) |
| `MODEL/HCAR.AFS` | 39 PAC | High-Detail-Autos (Auswahl/Replay) – erste Wahl fürs Remake |
| `MODEL/COURSE.AFS` (= `COURSE/COURSE.AFS`) | 43 PAC | Streckengeometrie (Strecke × Richtung × Tageszeit) |
| `COURSE/CRS_DATA.AFS` | 177 gemischt (`1LCR`, `CIF`, Float-Tabellen) | Kollision, Ideallinie, Startplätze, Checkpoints, Kamera |
| `MODEL/TEXTURE.AFS` | 116 PAC | Gemeinsame Texturen |
| `BINARY/CARPARTS.AFS` | – | Auto-Parameter → Startwerte fürs Physik-Tuning |
| `MODEL/LOAD30.PAC` | 1 | Kleiner Testfall fürs PAC-Format |

Erwartet (PS2-typisch): Meshes als VIF-Pakete/Triangle-Strips, oft vorbeleuchtet (Vertex-Farben); Texturen GS-swizzled 4/8-bit mit CLUT.

## 5. Phasen

Jede Phase endet mit etwas Sichtbarem/Fahrbarem.

### Phase 0 – Formate knacken · größtes Risiko
1. AFS-Reader (+ `.TBL`-Namen) → `idss list/extract`
2. PAC-Struktur aus `LOAD30.PAC`/`HCAR.AFS` ableiten; Gegenprobe im Recomp
3. Texturen: Unswizzle + CLUT → PNG
4. Meshes: VIF/Strips → Dreiecke, UVs, Normalen, Vertex-Farben → Debug-Export **glTF** (für Blender)
5. Strecke: Render-Mesh + Kollisions-/Pfaddaten aus `CRS_DATA`
- **Fertig:** 1 Auto + 1 Strecke korrekt texturiert in Blender.
- **Abbruch nach ~3 Wochen ohne Mesh:** Fallback Geometrie per GS-Dump aus Recomp/PCSX2.

### Phase 1 – Engine-Grundgerüst (`Kansei`)
- Penelope übernehmen, Bootstrap/Loop/Platform/Input/Audio nach MogliEngine-Muster
- 3D-Renderer: Kamera, Mesh/Material (Textur × Vertex-Farbe, Original-Look), Depth, Backface-Culling, Frustum-Culling, Skybox
- Cache-Format laden (eigenes Binärformat, direkt in GPU-Buffer)
- Debug: Freiflug-Kamera, FPS/Frametime-Overlay
- **Fertig:** Akina in Originalgrafik mit 144 fps abfliegen, auf Mac (Metal) und Windows (Vulkan).

### Phase 2 – Fahrphysik
- Raycast-Wheels, Feder/Dämpfer, Reifenmodell (Längs-/Querschlupf kombiniert), Differenzial (offen/LSD), Gewichtsverlagerung, Motor-Drehmomentkurve, Getriebe (Auto/manuell)
- `CarSpec` als JSON: Masse, Leistung, Radstand, Schwerpunkt, Antrieb (FR/FF/4WD) – Werte aus `CARPARTS` + realen Daten (AE86, FD3S, R32 …)
- Kollision gegen Strecken-BVH, Leitplanken – Boden/Wände da: `TriangleGround` (XZ-Grid statt BVH, Wände = Kanten Straße/`W…`), `Touge/CourseGround`
- Verfolgerkamera, Cockpit-Kamera
- Unit-Tests für Reifenmodell, Getriebe, Raycast-BVH (reine Mathe)
- Stand: `Kansei.Physics/Vehicle.cs` steht (Raycast-Wheels, Magic Formula mit Reibkreis, LSD, Auto/Manuell, Gegenlenkhilfe, Wand-Impulse), getestet nur auf Flachebene (AE86 0–100 ≈ 8,4 s). Fehlt: Einbau ins Spiel, Kameras, Tuning auf Akina, Auto-vs-Auto.
- **Fertig:** AE86 driftet kontrolliert durch Akinas Haarnadeln mit Pad.

### Phase 3 – Lenkrad
- SDL2-Joystick-Achsen (Lenkung, Gas, Bremse, Kupplung), H-Schaltung optional
- FFB per **SDL2 Haptic** (`SDL_HapticOpenFromJoystick`, Constant-Force + Spring/Damper): Rückstellmoment aus Physik (Self-Aligning-Torque der Vorderräder), Rumble bei Curbs/Kollision
- Kalibrier-Menü (Lenkwinkel, Deadzones, FFB-Stärke)
- **Fertig:** G29/G923 mit spürbarem Gegenlenkmoment im Drift (Windows; Mac als Bonus).

### Phase 4 – Multiplayer
- Steam-Lobby + Freunde einladen (Muster aus `SteamLobbyManager`), Fallback Direkt-IP
- LiteNetLib: Owner-Authority, Snapshot-Interpolation, Auto-Auto-Kontakt über kinematische Proxys
- Host-autoritativ: Countdown, Checkpoints, Zieleinlauf, Ergebnis
- Modi: **Battle** (Lead/Chase, Abstand gewinnt), **Time Attack** mit Bestenliste, **Free Roam**
- **Fertig:** 2 Leute übers Internet fahren ein Battle auf Akina, Ergebnis stimmt auf beiden Seiten.

### Phase 5 – Feinschliff
- Alle Strecken/Varianten, alle Autos tunen
- Sound: Motorsound pro Drehzahl (Platzhalter → Original-ADX aus der ISO über denselben Cache-Weg)
- Nacht mit Scheinwerfern, Schatten, Ghosts, KI (Ideallinie aus `CRS_DATA`), Replays

## 6. Repo-Struktur (geplant)

```
initiald-remake/
  Penelope/          RHI (aus MEFactory übernommen)
  Kansei/            Engine: Loop, Platform, Input/FFB, Audio, 3D-Renderer, UI
  Kansei.Physics/    BVH, Raycasts, OBB-SAT, Fahrzeugphysik
  Touge.Formats/     AFS, PAC, Texturen, Meshes, Kurse
  Touge.Formats.Cli/ idss list|extract|export-gltf|build-cache
  Touge/             Das Spiel
  Tests/             Unit + Golden (übersprungen ohne ISO)
```

## 7. Risiken

| Risiko | Folge | Gegenmittel |
|---|---|---|
| PAC/Mesh-Format zu verschlossen | Phase 0 dauert ewig | Recomp-Tracing; Fallback GS-Ripping |
| 3D-Renderer unterschätzt | Phase 1 zieht sich | Original-Look ist simpel (vorbeleuchtet) → erst das, PBR/Schatten später |
| Kollisionsdaten nicht auffindbar | Autos fallen durch / Bande fehlt | Kollision aus Render-Mesh generieren (Straßenmaterialien filtern) |
| Fahrgefühl „falsch“ | Macht keinen Spaß | Früh mit Lenkrad testen, Tuning live per Hot-Reload der JSON |
| FFB auf macOS | Mac-Freunde ohne FFB | Achsen gehen trotzdem; FFB = Windows-first |
| Netz-Kollisionen asymmetrisch | Rempler fühlen sich komisch an | Akzeptiert (Touge = selten Kontakt); später Host-Schiedsrichter |

## 8. Grobe Aufwandsschätzung

| Phase | Aufwand |
|---|---|
| 0 Formate | 2–6 Wochen (größte Unsicherheit) |
| 1 Engine + Renderer | 3–6 Wochen (mit Penelope-Übernahme; ohne: +2–3 Monate) |
| 2 Physik | 3–5 Wochen |
| 3 Lenkrad | 1 Woche |
| 4 Multiplayer | 2–3 Wochen |
| 5 Feinschliff | offen |

## 9. Offene Fragen

- Penelope aus MEFactory übernehmen (empfohlen) oder wirklich alles neu?
- Plattformen der Freunde? (Windows-only vereinfacht FFB + Tests)
- Welche Lenkräder? (G29/G923 = Logitech, sonst Thrustmaster/Fanatec testen)
- Steam für Lobby ok (alle haben Steam)? Sonst nur Direkt-IP/Relay.
