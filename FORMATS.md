# Formate (Initial D Special Stage, SLPM-65268)

## AFS (CRI) – geknackt
`"AFS\0"`, u32 count, count × (u32 offset, u32 size). Namen in der Nachbardatei `.TBL`: count × u32 Offset in eine NUL-terminierte Namensliste (erster Offset = count·4).

## PAC – geknackt
Header 0x20: `"PAC\0"`, u32 count, u32 ?, u32 Tabellen-Offset (0x20), char name[16].
Eintrag 0x20: char name[16], u32 offset (ab PAC-Start), u32 size, u32 type, u32 ?.

| type | Magic | Inhalt |
|---|---|---|
| 1 | `GIM` | Textur |
| 3 | `CMD` | Mesh (Autos: benannte Teile `_bonnet00`, `_Fspoile02`, `_mirror00` …) |
| 3 | (anderes) | Strecken-Meshes – Magic unklar |
| 6 | `ANIf` | Animation (TSDATA) |

## Inhalt
- `MODEL/HCAR.AFS` 39 Autos (`AE85.PAC`, `AE86L.PAC`, `AE86T.PAC` …), je ~1700 GIM + CMD gesamt → Teile + Tuning-Varianten
- `MODEL/CAR.AFS` 70 Autos (In-Race)
- `MODEL/COURSE.AFS` Strecken je `_DAY` / `_NIT` / `_RIN` (Regen) + `ENV_TEX_*` (Env-Maps)
- `COURSE/CRS_DATA.AFS` keine PACs: `1LCR` (Kollision), `CIF`, Float-Tabellen (Pfade)

## GIM (Textur) – geknackt
`"GIM\0"`[16], name[16], 0x30 Bild: u16 w, h, psm, upload-w, upload-h, upload-psm, u32 Daten-Offset; 0x40 CLUT gleich aufgebaut.
- psm 0x14 = 4 bit, 0x13 = 8 bit; CLUT immer CT32 (16 bzw. 256 Farben).
- Upload-psm = psm → linear. Upload-psm 0 (CT32, halbe/viertel Größe) → GS-Swizzle, siehe `Gs.cs`.
- 256er-CLUT: Index-Bits 3/4 vertauscht (CSM1). Alpha 0x80 = deckend.

## CMD (Mesh) – geknackt
`"CMD\0" "1.02V"`; 0x10 #Texturen, 0x14 #Knoten, 0x18 #Materialien, 0x20 Textur-Namen (16 B), 0x24 Knoten (name[16] + 4×4-Matrix), danach Materialien (0x20 B: Offset, QWC, Textur-Index, ?, Flags, RGBA, #Dreiecke, #Vertices), 0x30 BBox.
- Daten = VIF-Stream, pro Batch: V4-32 → addr 1 (xyz + ADC-Bit 0x8000 in w), V3-32 → addr 2 (Normale), V2-32 → addr 3 (UV), V3-32 → addr 4 (?), MSCAL. Triangle-Strips.
- Vertices im Auto-Koordinatensystem (Meter, +Z vorne, +Y oben). Räder lokal, Knoten `fr_l/fr_r/re_l/re_r` am `body00` geben die Position. AE86: `fr_rk_close/open` = Klappscheinwerfer.
- Material-Flags (Zeichnen `0x1860E0`): **0x100 = Lack** (RGB kommt zur Laufzeit aus `CAR_ENV.BIN`, siehe unten; das sind die 0x1100/Alpha-0x40-Materialien, *kein* Glas), 0x200 = RGB aus Material, Alpha von der Instanz, 0x400 = zweiter Durchgang (Decals). 0x1000/0x2000/0x800/0x4000/0x8000 nicht genau geklärt.
- Teile mit Varianten-Suffix `00`–`05` (Tuning), `tire00FL`…, `Bcali00FR`… (Bremssättel).

## CAR_ENV.BIN (Lackfarben) – geknackt
`BINARY/CAR_ENV.BIN`, geladen in `0x15CBB0`, angewendet in `0x15D060` (Auto-ID, Farbindex). 32 Blöcke hintereinander: `"CEB\0"`, s16 Auto-ID, s16 #Farben, 8 B 0; dann #Farben × 0x70: u32 R, G, B, A (0xFF), 0x60 B Tabelle (12 Lichtzustände × 2 × 2 × Bytepaar, Standard Zeile 11 = `52 52`).
- Auto-ID = Index in der Namensliste im ELF (`0x2C4978`: AE86T, AE86L, AE85, MR2 … CAPPU), siehe `CarPaint.Cars`.
- Farbe 0 = Standard (Anime-Farbe: FD3S gelb, FC3S weiß, R32 schwarz, AE86T weiß – die schwarze Panda-Unterseite ist im Mesh). 1–7 Farben je Auto.
- Das Spiel setzt RGB auf alle Teile (`0x193E90`), nur Materialien mit Flag 0x100 übernehmen es. Das Bytepaar geht an `0x193F00` → Alpha der Lackfarbe bzw. zweiter Alpha-Wert (vermutlich Reflexionsstärke, nicht verifiziert).
- Sonderfälle in `0x15D060` nicht umgesetzt: AE86T Farbe 1 tauscht `_emblem00/01`; SIL80, S2000, FD3S (und S13) bekommen bei Teile-Byte 0x13 = 5 fest kodierte Farben.

## LZ (gepackte PAC-Einträge) – geknackt
u32 `0x01DA3D12` (Byte 3: 1 = gepackt, 0 = roh), u32 entpackte Größe, u32 gepackte Größe. LZSS mit 64-KB-Fenster (Start `0xFEFD`, genullt), Flag-Byte LSB zuerst (1 = Literal), Match = u16 absolute Fensterposition + u8 Länge−4. Im Spiel `0x1C54F0`. Siehe `Lz.cs`.

## SMD (Strecken-Mesh) – geknackt
`"SMD\0" "0.00"`; 0x08 #Dreiecke, 0x0C #Vertices, 0x10 #Texturen, 0x14 #Materialien, 0x18 Texturtabelle, 0x1C Materialtabelle, 0x20 BBox. VIF wie CMD, aber addr 2 = UV, addr 3 = V4-8 Vertexfarbe (vorbeleuchtet), keine Normalen. Weltkoordinaten in Metern.
Strecken-PACs: `crsNN` (Abschnitte), `crslodNN` (LOD), `shdNN` (Schatten), `tree*`, `gate*`, `mnt00`, `sky`. Varianten `_DAY`, `_NIT`, `_RIN`.
GIM kann auch 32-bit Truecolor sein (psm 0, ohne CLUT).

## CRS_DATA (nicht gepackt)
Kursreihenfolge im ELF (Tabelle `0x24CD00`, Index = Byte `0x328156`): MYOUGI0, USUI0, AKAGI, AKINA, HAPPOU, IROHA, MYOUGI, USUI, MOMIJI, SHIONA, SHOMARU.
- `CRS_ROAD_<KURS>[_L|_R].BIN` – geknackt: u32 n, u32 ? (0/3, vom Loader `0x164A50` ignoriert), n × xyz. Straßenmitte/linker/rechter Rand, ~2 m Abstand. ENV, FLR, SHD sind Tabellen pro ROAD-Punkt. Siehe `CourseRoad.cs`.
- `CRS_DRV_<KURS>_I/O.BIN` – geknackt: n × xyz (12 B, sonst nichts pro Punkt), ~10 m. `_I` = Fahrtrichtung entlang ROAD, `_O` = Gegenrichtung (Flag `+0x1D6` im Kurs-Struct). Datei immer 1000 Punkte (USUI0 916); **gültige Anzahl hartkodiert im ELF** (`0x2C4920`, je Kurs u32 + u32 0, gleich für I/O), genutzt vom Nächster-Punkt-Suchlauf `0x157170` (sucht ±8 um den letzten Index, nur x/z, wrap bei n). Danach Auslauf hinter dem Ziel, dann Speichermüll. Rundkurse (MYOUGI0, USUI0) enthalten mehrere Runden (3 bzw. 2). Siehe `DrivingLine.cs`.
- `CRS_ENV_<KURS>.BIN` – geknackt: 4 × s8 pro ROAD-Punkt = Index der Env-Map-Textur `ENV_TOP%02d`, `ENV_BOTTOM%02d`, `ENV_LEFT%02d`, `ENV_RIGHT%02d` aus `ENV_TEX_<KURS>_<ZEIT>.PAC` (Auto-Reflexion, `0x163E50`). Länge passt nicht immer zu ROAD (MOMIJI 2781 vs 2627); Spiel liest mit Index mod n.
- `CRS_FLR_<KURS>.BIN` – geknackt: 1 Byte pro ROAD-Punkt, ≠ 0 = Sonnen-**Flare** sichtbar (`0x161F70` → Effekt 0x15 = 11 Flare-Elemente zwischen Sonnenposition je Kurs und Kamera). Nur Kurse mit `_DAY` haben FLR; ohne Datei ist Flare immer an. Akina/Akagi/Iroha-Dateien sind kürzer als ROAD (z. B. 3955 von 4089) – Spiel liest dahinter fremden Speicher, hier als „aus“ gewertet.
- `CRS_SHD_<KURS>.BIN`: f32 pro ROAD-Punkt, Helligkeitsfaktor (interpoliert, `0x163C00`). Kein Reader.
- `CRS_LIGHT_<KURS>.BIN`: u32 n, 12 B ?, n × (f32 x, y, z, w = 1) Lichtpunkte (Aufhellung des Autos im 16-m-Radius), Reader `CourseRoad.ReadLights`. Akina: 8 Punkte in zwei Vierergruppen (Start, Ziel), 6–7 m über und 5–10 m neben der Fahrlinie = Laternenköpfe. USUI: 2 Punkte bei x = −79 km (Platzhalter).
- Env-Maps `ENV_TEX_<KURS>_<ZEIT>.PAC`: 64×32-GIMs `ENV_TOP00…`, `ENV_BOTTOM00…`, `ENV_LEFT00…`, `ENV_RIGHT00…` (Akina Tag: 9/4/3/3), kleine Panoramen (Himmel mit Baumkante, Straße/Leitplanke, Waldrand). Wie das Spiel sie auf das Auto projiziert, ist nicht nachgesehen; das Remake nimmt sie als groben Würfel im Auto-Raum (`car.frag`).
- `CRS_NAVI_<KURS>.BIN`: u32 n + 12 B, n × 16 B (normierte 2D-Koordinaten, vermutlich Minimap). Nicht weiter analysiert.
- `CRS_COLI_<KURS>_0/1.BIN`: Kollision, siehe unten.

## Kollision `CRS_COLI_<KURS>_0/1.BIN` – geknackt
Loader `0x15F340`. Header 0x30 (u32): `"1LCR"`, Version 1, #Materialien, Off, #Vertices, Off, #Faces, Off, #u32-Liste, Off (immer leer), #Sektoren, Off.
- Material 0x24: name[16] + 20 B Null. Index = Zahl im Namen (`R16road`, `W14hard`, `R25r_grass`, `R32gutter`, `R21r_bump` …), Lücken leer. `R` = befahrbar, `W` = Wand.
- Vertex 0x20: xyz, Normale xyz, 2 × 0. Weltkoordinaten wie die Strecke (Straßen-Vertices ±1 cm identisch mit `crsNN`).
- Face 0x10: s16 v[3], s16 Nachbar-Face über Kante v2v0/v0v1/v1v2 (−1 = offener Rand), u16 immer 0xFFFF (?), u16 Attribut = Material | 0x8000 bei Wand.
- Nur Dreiecke, eine zusammenhängende 2.5D-Fläche (fast alle Normalen nach oben). Wände sind keine senkrechten Flächen, sondern ein ~30 m breites Band `W…`-Faces neben der Straße; Kante Straße/Wand = Leitplanke. Kein Grid/BVH: das Spiel läuft über die Nachbarn in XZ zum Face unter dem Auto (`0x15F7D0`), Variante `0x15F960` stoppt an 0x8000-Faces.
- Sektor 0x38: Ebene (n, d), Dreieck xyz[3], s32 Start-Face – grobes Band entlang der Strecke (~40 m unter der Straße), `0x15F600` sucht den Sektor (ab dem letzten, vor/zurück) und startet dort den Walk.
- `_0`/`_1` = Fahrtrichtung (Byte 0x1D6 im Kursobjekt, auch Vorzeichen ±1). Gleiche Fläche (Vertex-Reihenfolge teils anders), nur Endzonen anders: Akina `_0` unten `R80btm` befahrbar, `_1` oben `R64top` und unten Wand. `MYOUGI0`/`USUI0` nur `_0`.
- Akina `_0`: 9851 Vertices, 18925 Dreiecke, 524 Sektoren; road 6396, W14hard 4789, r_grass 4462, gutter 2298, btm 488, r_bump 320, r_redline 172.
- Unbekannt: u16 bei Face+0xC, Sinn der u32-Liste, Physik-Werte je Material (nicht in der Datei).

## Offen
- NAVI, INFO (`CIF`); zweites u32 im ROAD-Header
- Bedeutung von VU addr 4, Material-Flags außer 0x100/0x200/0x400
- CAR_ENV-Bytepaar-Tabelle: welcher Lichtzustand welche Zeile
