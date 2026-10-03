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
```

Fahren (Standard): W/S oder ↑/↓ Gas/Bremse, A/D oder ←/→ lenken, Leertaste Handbremse, S im Stand halten = Rückwärts (Automatik), T Automatik/Manuell,
Shift/Strg hoch-/runterschalten (manuell, auch in R), R zurück auf die Fahrlinie, C Verfolger-/Stoßstangenkamera.
Pad: linker Stick lenken, Trigger Gas/Bremse, A Handbremse, Schultertasten schalten.
F1 Freiflug: WASD fliegen, Q/E runter/hoch, rechte Maustaste oder Pfeiltasten umschauen, Shift schnell,
Leertaste ~400 m weiter auf der Fahrlinie. F11 Vollbild, Esc Ende.
Backend: Metal (macOS) bzw. Vulkan, umschaltbar mit `--backend metal|vulkan|opengl`.

## Werkzeuge

```sh
dotnet run --project Touge.Formats.Cli -- list <pfad>/MODEL/HCAR.AFS
dotnet run --project Touge.Formats.Cli -- car <CAR.PAC> out/car [CAR_ENV.BIN [n]]
dotnet run --project Touge.Formats.Cli -- course <KURS.PAC> out/kurs
dotnet test
```
