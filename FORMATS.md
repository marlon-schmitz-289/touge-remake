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

## Offen
- GIM: Header, Pixelformat (4/8/32 bit, CLUT, Swizzle?)
- CMD: Vertex-/Index-Layout
- Strecken-Mesh-Format, CRS_DATA
