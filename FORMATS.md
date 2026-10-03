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
- `COURSE/CRS_DATA.AFS` keine PACs: `1LCR`, `CIF`, Float-Tabellen → Kollision/Pfade (offen)

## GIM (Textur) – geknackt
`"GIM\0"`[16], name[16], 0x30 Bild: u16 w, h, psm, upload-w, upload-h, upload-psm, u32 Daten-Offset; 0x40 CLUT gleich aufgebaut.
- psm 0x14 = 4 bit, 0x13 = 8 bit; CLUT immer CT32 (16 bzw. 256 Farben).
- Upload-psm = psm → linear. Upload-psm 0 (CT32, halbe/viertel Größe) → GS-Swizzle, siehe `Gs.cs`.
- 256er-CLUT: Index-Bits 3/4 vertauscht (CSM1). Alpha 0x80 = deckend.

## CMD (Mesh) – geknackt
`"CMD\0" "1.02V"`; 0x10 #Texturen, 0x14 #Knoten, 0x18 #Materialien, 0x20 Textur-Namen (16 B), 0x24 Knoten (name[16] + 4×4-Matrix), danach Materialien (0x20 B: Offset, QWC, Textur-Index, ?, Flags, RGBA, #Dreiecke, #Vertices), 0x30 BBox.
- Daten = VIF-Stream, pro Batch: V4-32 → addr 1 (xyz + ADC-Bit 0x8000 in w), V3-32 → addr 2 (Normale), V2-32 → addr 3 (UV), V3-32 → addr 4 (?), MSCAL. Triangle-Strips.
- Vertices im Auto-Koordinatensystem (Meter, +Z vorne, +Y oben). Räder lokal, Knoten `fr_l/fr_r/re_l/re_r` am `body00` geben die Position. AE86: `fr_rk_close/open` = Klappscheinwerfer.
- Material-Flags: 0x1100 + Alpha 0x40 = Glas; Lack ohne Textur nur über RGBA.
- Teile mit Varianten-Suffix `00`–`05` (Tuning), `tire00FL`…, `Bcali00FR`… (Bremssättel).

## Offen
- Strecken-Mesh-Format (type 3 ohne CMD-Magic), CRS_DATA (Kollision/Pfade)
- Bedeutung von VU addr 4, Material-Flags im Detail
