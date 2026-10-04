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
- Material-Flags (Zeichnen `0x1860E0`): **0x100 = Lack** (RGB kommt zur Laufzeit aus `CAR_ENV.BIN`, siehe unten; das sind die 0x1100/Alpha-0x40-Materialien, *kein* Glas), 0x200 = RGB aus Material, Alpha von der Instanz, 0x400 = zweiter Durchgang (Decals). Im zweiten Durchgang schalten 0x800/0x8000 das Z-Schreiben ab (`0x14E2B0` → GS `ZBUF` mit ZMSK = 1, `0x14E2A0` wieder an; Pakete aus `0x148CC0`). Kein Z-Offset/Bias: Decals gewinnen nur über die Zeichenreihenfolge bei gleichem Z (24-bit-Z). 0x1000/0x2000/0x4000 nicht genau geklärt.
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
Überlappungen (`touge --zfight`): Abschnitte überdecken sich teils großflächig fast koplanar (Akina `crs03` über `crs01`, 1373 m², 0,0 mm; `crs05` legt Gras-/Buschtextur `KINA_DAY097_*` 0,5 mm über `crs03`), dazu Decals in derselben Ebene. Je Kurs 47k–203k Dreiecke, 3k–42k Paare innerhalb 3 cm, davon 57–5753 unter 1 mm. Das Spiel zeichnet sie nie gleichzeitig: Abschnitte haben ein Sichtfenster über den ROAD-Index des Autos (`CRS_INFO`-Abschnittstabelle, unten), Akina `crs01` 0–274, `crs03` 275–540.
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
- `CRS_NAVI_<KURS>.BIN`: u32 n + 12 B 0, n × (f32 x, y, 0, 1) – die Minimap: genau die ROAD-Mittellinie (n = ROAD-n; MYOUGI 1786 statt 1779, MYOUGI0/USUI0 ±1 Punkt), `x = k·X + a`, `y = −k·Z + b` mit einem k je Kurs (AKINA 0,0004167 = 1/2400, IROHA 0,0004426, USUI0 0,000738), Fehler 0 (MYOUGI ≤ 0,024). Liegt in x ≈ 0…1, y ≈ −1…0 (Kartenbox des Spiels). Der Remake nimmt darum ROAD in Metern direkt (`Touge/Hud`).
- `CRS_INFO_<KURS>.BIN` (Magic `CIF\0`, u32 Version 0x40000, alle Kurse): Loader `0x162420` (Name aus `"CRS_INFO_"` + Kurs + `".BIN"`). Slot = Byte `+0x1D7` (Nacht) × 2 + Byte `+0x1D8` (Regen) des Kursobjekts: Tag 0, Regen 1, Nacht 2, (Nacht + Regen 3, ungenutzt). Reader `CourseInfo`.
  - **Autolicht** (`0x162580`): ab 0x10 u32 Lichtanzahl je Nacht-Flag (3 / 1), ab 0x20 + 48·slot bis zu 3 × (f32 x, y, z, 1) Richtung, ab 0xE0 + 48·slot 3 × RGBA (0..1), danach Ambient; je Licht Kursobjekt +0xD0/+0x100 und `0x1769B0(1, "light%d", Richtung, Farbe)` (Parallellichter der Auto-VU). Richtung = wohin das Licht fällt: Akina Licht 0 (−1, −0,5, 0) → Sonne bei +X wie das Sonnensprite. Licht 0 (Sonne) je Kurs, Tag: AKINA (−1, −0,5, 0) 0,7 = Vorlagenwert (Höhe 26,6°, Azimut +X); AKAGI (0,744, −0,668, −0,011) 0,7 (41,9°, Sonne bei −X); IROHA (0,589, −0,750, 0,312) 0,7 (48,6°); MYOUGI0 (−0,435, −0,826, 0,357) 0,89 (55,7°); USUI0 (−0,937, −0,521, −0,166) 0,7 (31,4°). Kurse ohne `_DAY` (HAPPOU, MOMIJI, MYOUGI, SHIONA, SHOMARU, USUI) haben die Vorlage (−1, −0,5, 0) mit 0 oder 0,3. Regen: wie Tag, schwächer (Akina 0,4; Iroha (−1, −0,5, 0) 0,4). Nacht: ein Licht (0, −1, 0) 0,22. Das Welt-Mesh ist vorbeleuchtet; nur das Auto bekommt diese Lichter.
  - **Nebel**: 4 × s32 Start ab 0x1E0, 4 × s32 Ende ab 0x1F0, 4 × f32 RGBA (0..255) ab 0x200 (je Slot). `0x1628A0` kopiert `[slot]` → `+0x158`, `[slot + 4]` → `+0x15C`, Farbe halbiert als GS-Wort → `+0x160`; `0x163130` → `0x1815D0` → `0x182170` → `0x148910`/`0x148460`: GS-Fog = (Ende − z) · 255 / (Ende − Start), linear in der Kameratiefe (Meter). Ältere Versionen (`0x162290`, 3 Slots, `[slot]`/`[slot + 3]`) gibt es auf der Disc nicht. Akina: Tag −1200…9000, Regen −400…1300, Nacht 0…4000 (Farbe 205/210/200, 130/130/135, schwarz); Iroha −500…5000 / −500…1300 / 0…4000; Akagi −150…3500 / −500…1200 / 0…6000; MYOUGI0 0…12000 / −500…1500 / 0…5000; USUI0 0…10000 / −500…1700 / 0…4000; Nacht sonst MOMIJI/SHIONA 0…4000, MYOUGI/USUI 0…9000, SHOMARU 0…2000, HAPPOU 8000…9000. Negativer Start = Dunst schon an der Kamera (Akina Tag 12 %).
  - **Abschnitte** (`0x180210` CourseMgr, kein Reader): ab 0x2C0 u16 n (Akina 40) + n × u16 erster ROAD-Index je Abschnitt; dann zwei Tabellen (Richtung `_0`/`_1`) à n × 28 B: s32 Abschnitt, ROAD von, bis, **Sichtfenster** von, bis (ROAD-Index des Autos), Gate (−1 oder Nummer → `gate%02d`), Flag (0/1, → `0x181F00`). Pro Abschnitt lädt das Spiel `crs`, `shd`, `gal`, `crslod`, `shdlod` (+ Gate). Pro Bild (`0x180F10`): gezeichnet wird nur, wessen Fenster den ROAD-Index des Autos enthält (+0x20), und daraus nach Abstand in ROAD-Punkten (`gp−0x7C14`) `crs`/`shd` oder `crslod`/`shdlod` (+0x24, `0x181D30`/`0x181E00`). Davor 0x2A0…0x2BF u16-Werte (Akina a0, 99, f60, f59, 3e8, 7bd, b6b, ffff …) – Sinn offen.
- `CRS_COLI_<KURS>_0/1.BIN`: Kollision, siehe unten.
- `TREE_M_<KURS>_L/_R.BIN`, `TREE_L_<KURS>_L/_R.BIN` – geknackt (Loader `0x164F40`, Reader `CourseTrees`): u32 n, 12 B 0, n × 0x40: u8 Vorlage, 3 × u8 (0xF0–0xFE, vom Spiel nicht benutzt), 12 B 0, f32 xyz + 1 (Welt), f32 Drehung xyz in Grad (nur y), 0, f32 Skalierung xyz + 1. Modell = `"tree"` + `Mid`/`Lrg` (M/L) + `_L`/`_R` + `%02d` Vorlage (`0x2C5790`), aus der Kurs-PAC (lokale Vorlagen ±6 m, 6–15 m hoch). Das Spiel ersetzt die Drehung beim Laden: nächster ROAD-Punkt i (`0x163840`), Lot auf i → i+1 (`0x173CE0`), Drehung y = atan2(−v.z, v.x) − 90° mit v = Lotpunkt − Baum (`0x11A7F0` = atan2f), also −Z der Vorlage zur Straße; y −= Skalierung.y / 4; Matrix Skalierung · RotZ/X/Y (`0x174130`) · Verschiebung, dann in eine Baumgruppe (`0x17D5B0`, mit ROAD-Index für die Sichtbarkeit). Die gespeicherte Drehung weicht nur wenige Grad davon ab. Akina 1062 Bäume, 7–11 m neben der Straßenmitte. `treelod*` nutzt dieser Loader nicht. IROHA: PAC hat `treeLrg_R01` doppelt und kein `treeLrg_R02`/`treeMid_L02` – diese Bäume fehlen (Spiel findet das Modell nicht).

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

## Audio – geknackt
Kein Code dafür im EE-ELF außer der CRI-Middleware (`ADXF/ADXT/ADXB`, Ver. 2001); dekodiert wird im IOP (`CRI_ADXI.IRX`, ADX) bzw. von der SPU2 (VAG). `0ADX.DIR`/`1ADX.DIR` sind nur Listen der AFS-Dateien (ADXF-Partitionen), `PS2SE.AFS` aus `1ADX.DIR` gibt es auf der Disc nicht. `MANGA/ST_BGM.AFS` ist byte-gleich mit `SOUND/ST_BGM_N.AFS`. Film `MOVIE/D_HIGH_1.SFD` (Sofdec, ADX im MPEG-PS) nicht angefasst.

**ADX** (alle `*.AFS` außer CARSE): Big Endian, `80 00`, u16 Offset der `(c)CRI`-Kennung (Daten ab Offset+4), u8 3 (Typ), u8 18 (Frame), u8 4 (Bit), u8 Kanäle, u32 Rate, u32 Samples, u16 Hochpass 500 Hz, u8 Version (alle 3), u8 Flags (alle 0 = unverschlüsselt). Loop-Block v3 ab 0x14: u16 Ausrichtung, u16 ?, u32 an, u32 Start-Sample, u32 Start-Byte, u32 End-Sample, u32 End-Byte. Frames je Kanal abwechselnd: u16 Skala, 32 Nibbles (hohes zuerst), `s = n·Skala + (c1·s1 + c2·s2) >> 12`, `c1/c2` aus Hochpass und Rate (12-bit-Festkomma, 48 kHz → 7400/−3343). Skala mit Bit 15 = Ende. Beim Loop wird der Prädiktorzustand vom ersten Durchlauf an der Loop-Startstelle wiederhergestellt. Geprüft: `Adx.cs` ist sample-genau gleich mit ffmpegs ADX-Decoder (NO_ONE_SLEEP_IN_TOKYO 20,3 M Samples, eine RACEVOIC-Stimme).

**VAG / SPU-ADPCM** (SYSSE, CARSE): 16-B-Frames, u8 Prädiktor<<4|Shift, u8 Flags (4 Loop-Start, 1 Ende, 1+2 Ende mit Sprung zurück), 28 Nibbles (niedriges zuerst), Filter {0,0},{60,0},{115,−52},{98,−55},{122,−60}, `s = (n<<12>>shift) + (s1·f0 + s2·f1 + 32) >> 6` wie die Hardware. ffmpegs `adpcm_psx` rundet anders (bis ±62 Abweichung); Wellenform sonst gleich.
- `SOUND/SYSSE.BIN`: u32 n (38), u32 Pos Offsettabelle (0x10), u32 Pos Pitch-Tabelle (0xB0), u32 Datenstart (0x150); n × u32 Offset ab Datenstart, n × u32 SPU-Pitch (0x1000 = 48 kHz; hier 0x759 ≈ 22 kHz, 0x3AC ≈ 11 kHz). Namen in `SYSSE.TBL` (Format wie AFS-.TBL).
- `SOUND/CARSE.AFS`: Paare `<AUTO>_D/_U.DAT` + `.MRG`. **MRG** = u32 3, 3 × u32 Offset, u32 Größe direkt vor jedem Teil: `mrg.lst` (Text: Namen der Teile), Sony `*.bd` (VAG-Rohdaten), `*.hd` (Sony-Bankheader `IECSsreV`/`IECSdaeH`/`IECSigaV`/`IECSlpmS`/`IECSteSS`/`IECSgorP`). Im `IECSigaV`-Chunk: u32 Größe, u32 letzter Index, Offsets (ab Chunk) auf 8-B-Einträge u32 BD-Offset, u16 Rate in Hz (≈ 22050, je Sample leicht verstimmt), u8 Loop, u8 0xFF.
- **DAT** (`"SECTver0.502"`, Laden in sub_00198860/sub_00198780): 16 × 0x90-B-Blöcke (Offsettabelle ab 0x20), je Block 3 Kurven × 6 Punkte (i32 x 0…255, i32 y 0…127); Standard y = 64. Kurve 0 = **Pitch** (an den Soundtreiber als Pitch-Bend y·128, 0x2000 = Mitte), 1 = **Lautstärke** (× Kanal-Lautstärke/255 × 0,6), 2 = **Pan**. Ausgewertet stückweise linear zu 256 Zeilen (sub_001981A0); Index = drehzahlartiger Wert 0…255 (sub_00178D20, berechnet in sub_0018A170 – Formel nicht entschlüsselt). Pro Bank werden nur die ersten 8 Einträge gespielt (Programm i → Vagi i). `_U` = Last, `_D` = Schub: beide bekommen denselben Index, Lautstärke `_U` × t(2−t), `_D` × (1−t²) mit t = geglättetes Gas (60 Hz: +20 % Richtung 1 bei Pedal > 0,2, sonst −10 %). AE86_U-Lautstärkefenster: Schicht 0+4 Index 0–14 (Leerlauf), 1+5 ~13–42, 2+6 ~29–81, 3+7 ab ~63; Schichten 4–7 tonal (67/147/223/238 Hz), 0–3 breitbandig. Pitch-Bend-Bereich: im HD-Split steht 0x0100, Einheit unklar (Remake nimmt 24 Halbtöne an). `TU_<AUTO>` hat dieselbe MRG (byte-gleich) wie `<AUTO>`, nur andere DAT (getunte Kurven); `SRIP_A`/`SRIP_B` gleiche MRG, andere DAT.

### Katalog (`idss sound <ISO>`, `idss wav <ISO> <filter> <outDir>`)
| Archiv | Einträge | Format | Kanäle | Rate (Hz) | Gesamt (min–max je Datei) | mit Loop |
|---|---|---|---|---|---|---|
| SOUND/BGM | 9 | ADX | 2 | 48000 | 6,9 min (10–120 s) | 4 |
| SOUND/RACEBGM | 31 | ADX | 2 | 48000 | 109,5 min (143–297 s) | 31 |
| SOUND/ST_BGM_N | 39 | ADX | 2 | 48000 | 41,9 min (15–137 s) | 0 |
| SOUND/IKETANI | 33 (1 `ren_intro.bat`) | ADX | 1 | 24000 | 26,0 min (34–66 s) | 0 |
| SOUND/RACEVOIC | 1483 | ADX | 1 | 24000, 22050 | 76,3 min (0,2–15 s) | 0 |
| MANGA/MG_BGM | 30 | ADX | 2 | 48000 | 20,8 min (31–57 s) | 30 |
| MANGAV/MG_VC00–07 | 4770 (7 leer/Dummy) | ADX | 1 | 24000 (vereinzelt 48000) | 217 min (0,2–7,3 s) | 0 |
| SOUND/CARSE | 50 MRG → 380 Samples (+ 50 DAT) | VAG | 1 | ≈ 22050 (SRIP 18900) | 9,3 min (0,4–3,3 s) | 376 |
| SOUND/SYSSE.BIN | 38 | VAG | 1 | 22043, 11016, 22500 | 0,9 min (0,2–7 s) | 2 |

Rollen (aus Dateinamen; „?" = geraten):
- **Renn-BGM** (RACEBGM, Eurobeat, alle mit Loop): 100, BACK ON THE ROCKS, BEAT OF THE RISING SUN, BIG IN JAPAN, BURNING DESIRE, CRAZY FOR LOVE, CRAZY FOR YOUR LOVE, CRAZY NIGHT, DONT STAND SO CLOSE, DONT STOP THE MUSIC, DONT YOU, EXPRESS LOVE, GET ME POWER, GRAND PRIX, HEART BEAT, I NEED YOUR LOVE, KILLING MY LOVE, LOVE IS IN DANGER, MIKADO, NIGHT OF FIRE, NO ONE SLEEP IN TOKYO, REMEMBER ME, ROCK ME TO THE TOP, RUNNING IN THE 90S, SAVE ME, SPACEBOY, SPEED SPEED BOY, STATION TO STATION, STAY, WEST END GUY, WHITE LIGHT.
- **Menü/Ergebnis** (BGM): gam, JOY, LOSE, PANIC, THERACEISOVER, TIMEUP, TOKYO, WIN, WORRY. **Story**: ST_BGM_N (`STORY_MONO01–05`, `STORY_ST01–31`, `WIN02–04`), MG_BGM (Figurenthemen `TAKUMI01`, `RYOSUKE`, `BUNTA` …).
- **Auto-Ansagen** IKETANI `INTRO_<AUTO>.ADX` (32 Autos, 34–66 s, Sprache?).
- **Rennstimmen** RACEVOIC `b_<figur>_<situation>_NNN` (Situation: `start`, `front`, `rear`, `ppass`/`rpass` = überholt/wird überholt?, `fwin`/`rwin`/`pwin`, `flose`, `meter`, `special` …; ~40 Figuren). Story-Stimmen MG_VC `K<kapitel>_<szene>_NNN`.
- **Motor** CARSE `<AUTO>_U` / `_D` (= Last / Schub, siehe DAT), je 8 geloopte Schichten. Autos: AE86, AL (Altezza), CP (Cappuccino), EK9, EVO, FD, GC8, GTR, MR2, MRS (MR-S), NA6, S13 – mehrere Wagen teilen sich eine Bank (Zuordnung im ELF nicht gesucht). AE86: Schichten 4–7 haben tonale Grundfrequenz 65 → 237 Hz (4-Zylinder ≈ 1950 → 7100 U/min), 0–3 sind breitbandiger (Ansaug/Auspuff?); `_U` und `_D` teilen sich die Hälfte der Samples (D0=U0, D2=U1, D4=U4, D7=U6, dekodiert byte-gleich).
- **Reifen** CARSE `SRIP_A/B` (4 Samples, 2 davon 18,9 kHz ohne Loop), `RAIN_SRIP` (nass), **Turbo** `TURBO` (1 Loop).
- **SYSSE**: `backfire001`, `zbackfire002a–h`, `popoff`, `Blow` (Fehlzündung/Abblasventil), `cr001/002` (Crash?), `rain` (7 s, ohne Loop-Punkt; im Remake mit 0,4-s-Überblendung geloopt als Regen-Ambiente), `water`, `Steam`, `jump`, UI/System (`BEEP001`, `SKIP001`, `NAME001–003`, `CAR001–012`, `parts_ch`, `sys002`, `SYS005/006`, `alarm_01/02`).

Stichprobe (WAV-Export + Spektrum, Python/numpy): alle Exporte nicht still (RMS −18 … −2 dBFS) und tonal statt Rauschen (spektrale Flachheit 0,000–0,39; weißes Rauschen 1,0). Musik Schwerpunkt ~1,4–1,7 kHz, Stimme ~1,1 kHz, Reifen-Quietschen Spitze bei ~1 kHz.

## Offen
- INFO: 0x150…0x1DF (Ambient je Slot?), 0x2A0…0x2BF, Abschnitts-Flag; LOD-Abstand `gp−0x7C14`; zweites u32 im ROAD-Header
- Bedeutung von VU addr 4, Material-Flags außer 0x100/0x200/0x400
- CAR_ENV-Bytepaar-Tabelle: welcher Lichtzustand welche Zeile
- CARSE: Drehzahl → SECT-Index (sub_0018A170), Pitch-Bend-Bereich, Zuordnung Auto → Motor-Bank, SRIP-Index (Kurven springen, eher Zufall/LFO als Schlupf?), HD-Chunks außer `IECSigaV`/`IECSlpmS`/`IECSgorP`
