using System.Globalization;
using Kansei.Core;
using Kansei.Windowing;
using Touge;

// touge <ISO> [KURS_ZEIT] [--shot out.png]   z. B. touge "Initial D - Special Stage (Japan) (v2.00).iso" AKINA_NIT
// --shot rendert einen Frame als PNG und beendet sich; --at <n> startet bei Punkt n der Fahrlinie;
// --orbit <grad>[:<m>] Kamera ums geparkte Auto (0 vorne, 90 links, 180 hinten), Abstand Standard 5,5 m.
// --lights off|low|high: Autolicht beim Start (Standard: nachts/Regen Abblendlicht, tags aus; im Spiel L/H).
// --autodrive <s>: Pilot fährt die Fahrlinie ab, Log pro Sekunde; ohne --shot ohne Fenster, mit --shot Verfolgerbild am Ende.
//   Hinter dem Ziel: Auslauf bis vor die Endsperre (Drive.Coast), mit --ram Vollgas weiter in die Sperre; Zusammenfassung danach.
// --audio-capture <wav> <s> (mit --autodrive): Spielton offline (OpenAL-Loopback) als WAV + Auswertung, ohne Fenster; --no-music ohne BGM.
// --audio-capture <wav> 0 --sweep: nur Motor, Drehzahlrampe Leerlauf → Begrenzer (Vollgas, 3. Gang) und zurück (Schub), WAV + CSV je Tick.
// --ground <png>: Kollision des Kurses laden, Raycasts timen, Draufsicht mit Wandsegmenten, Sperren und Fahrlinie/Auslauf schreiben, Ausschnitte bei --at, Start, Ziel (ohne Fenster).
// --bench <s>: Pilot fährt höchstens <s> Sekunden (bzw. bis ins Ziel) in Echtzeit mit Verfolgerkamera und Ton; pro Sekunde Position, fps, CPU-ms,
//   Draws, Effekte, GC, Speicher, Wärmezustand; am Ende Frametimes (avg/p99/max, > 18/25 ms) und je 500 m. Mit --flow: erst durch die Menüs (Kurs/Zeit
//   wie angegeben, 24 Autovorschauen), dann das Rennen.
// --quality off: ohne MSAA/Bloom starten (F2 schaltet um).
// --offscreen (Metal): Bild nur in ein eigenes Ziel statt ins Fenster, ohne Display-Takt – mit --bench zeigt die Frametime dann die echten GPU-Kosten.
// --flicker <prefix>: Z-Fighting im Bild messen (8 Punkte der Fahrlinie + 8 Winkel ums Auto, je 3× mit verschobener Rundung), Ausschnitte als <prefix>_course/_car.png;
//   dazu _motion: Kamera in 1-cm-Schritten, je mit/ohne SSR (Springen nasser Spiegelungen).
// --zfight [filter]: Z-Fighting-Kandidaten (fast koplanar, überlappend) aller Kurse und Autos auflisten (ohne Fenster).
// --hud north|overview|off: Minimap nordausgerichtet / ganze Strecke / HUD aus (Standard: mitdrehend; N und F4 schalten um).
// --hud-scale <prozent>: HUD-Größe 80–130 (Standard 100; im Menü Options HUD SIZE).
// --reverse: Gegenrichtung (bergauf): CRS_COLI_<KURS>_1 (Rundkurse _0) + CRS_DRV_<KURS>_O, Start am anderen Ende; im Spiel B.
// --car <NAME|index> (HCAR-Name wie AE86T, FD3S, R32, EVO3 … oder 0–31), --paint <n> (CAR_ENV-Farbe, 0 = Standard).
// --cars <png>: Kontaktbogen aller 32 Autos (Orbit 35°, 4 × 8 Kacheln in CarPaint.Cars-Reihenfolge), dann Ende; mit --hud off.
// --fog: dichter Nebel über dem Tag- oder Nachtkurs (Sicht ~60 m; im Menü Wetter FOG).
// --sun: freie Kamera am Startpunkt schaut zur Sonne (Blendung prüfen).
// Ohne Kurs und ohne Test-Flags (außer --backend) startet das Spiel im Front-End (Ui/FrontEnd: Hinweis, Karten, Titel, Hauptmenü) mit den gespeicherten Einstellungen (Ui/Settings).
// --menu boot|logo|disclaimer|title|mode|quit (Front-End; quit = QUIT GAME mit offener Abfrage) bzw. course|route|time|weather|maker|car|gearbox|intro|pause|records|options bzw. guide|guide-list|guide-talk (Car Guide: Dialog, Liste, Iketani spricht): diesen Schritt/dieses Menü beim Start öffnen (auch mit Test-Flags, z. B. --menu mode --shot out/m.png).
// --flow <dir>: ganzer Ablauf per Skript im Fenster (Titel → Auswahl → Laden → Countdown → Rennen (Pilot, 16×) → Pause → Ziel → Ergebnis → Rekorde → Optionen), PNG je Schritt nach <dir>, Einstellungen bleiben unberührt.
// --jukebox <s>: Renn-Musik (Jukebox) offline ohne Fenster: Zufallsfolge, Weiterschalten, Songende → nächster Titel, Log.
// --frontend-capture <wav>: ganzer Menüablauf per Skript offline (Titel → Auswahl → Countdown → Ergebnis mit erfundener Fahrt) mit Original-SE/BGM als WAV, Log aller Auslöser.
// --shot-size WxH: Größe des --shot-Bildes (Standard 1280x720), z. B. 3200x1800 für die HUD-Skalierung.
// --render-scale <prozent>: 3D-Auflösung in % des Fensters (50–150, Optionen SCREEN), z. B. mit --bench für GPU-Kosten.
// --input-debug: Eingabe-Overlay (Geräte, Rohachsen/-tasten, Lenkung/Pedale wie das Spiel sie liest, Force-Feedback-Anteile); auch beim normalen Start.
// --sim-wheel: virtuelles Lenkrad (Lenkung pendelt, Pedale pumpen) für Bilder/Tests ohne Hardware; --menu controls:keyboard|pad|wheel öffnet die Steuerungsseite.
// --battle <rivale|auto> [--rule race|chase] [--lead player|rival]: Schnellbattle gegen die KI (Telop, Countdown, Battle-HUD, Ergebnis);
//   mit --autodrive <s> ohne Fenster: Autopilot gegen die KI, Log je Sekunde (Abstand, Führung, Kontakte) + Zusammenfassung.
// --menu story[:n[:scene[:teil[:zeile]]|:race]]: STORY-Kapitelwahl, eine Szene oder der Rennstart von Kapitel n; --progress <n>: Kapitel 0…n−1 gelten
//   als geschafft (nur Testläufe); --flow <dir> --story: Ablauf durch STORY (Wahl, Szene, Battle, Ergebnis, Szene danach, ein verlorenes Kapitel).
// --story-check [n]: Kapiteltabelle und Szenen der Disc gegen die Übersetzung prüfen, dann jedes Kapitel (oder nur n) mit dem Autopiloten fahren (ohne Fenster).
// --drift: Pilot reißt alle 7 s (ab 4,5 s) einen 2,5-s-Handbremsdrift (Reifenrauch/Bremsspuren testen), z. B. --autodrive 6.3 --drift --shot.
var iso = args.FirstOrDefault(a => a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
          ?? Environment.GetEnvironmentVariable("INITIALD_ISO");
if (iso == null || !File.Exists(iso))
{
    Console.Error.WriteLine("usage: touge <Initial D Special Stage (SLPM-65268).iso> [KURS_ZEIT, z. B. AKINA_DAY]  (oder INITIALD_ISO setzen)");
    return 1;
}
string[] valueFlags = ["--story-check", "--progress", "--battle", "--rule", "--lead", "--flow", "--shot", "--at", "--orbit", "--ground", "--autodrive", "--backend", "--bench", "--quality", "--audio-capture", "--zfight", "--flicker", "--hud", "--hud-scale", "--car", "--paint", "--cars", "--menu", "--shot-size", "--livery", "--frontend-capture", "--lights", "--render-scale", "--jukebox"];
string? Arg(string flag) { var i = Array.IndexOf(args, flag); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
// --car: HCAR name (AE86T, FD3S, R32, EVO3, …) or index 0–31 in that list (Touge.Formats.CarPaint.Cars)
var carArg = Arg("--car") ?? "AE86T";
var carIndex = int.TryParse(carArg, out var ci) ? ci : Array.FindIndex(Touge.Formats.CarPaint.Cars, c => c.Equals(carArg, StringComparison.OrdinalIgnoreCase));
if ((uint)carIndex >= Touge.Formats.CarPaint.Cars.Length)
{
    Console.Error.WriteLine($"--car {carArg}: unbekannt, möglich: {string.Join(' ', Touge.Formats.CarPaint.Cars)} oder 0–{Touge.Formats.CarPaint.Cars.Length - 1}");
    return 1;
}
var car = Touge.Formats.CarPaint.Cars[carIndex];
var paint = int.Parse(Arg("--paint") ?? "0");
// --livery: none (no decals/plates), stock (the game's stock car) or rival (default: the anime character's car), also 0–2
if (!Enum.TryParse<Touge.Formats.Livery>(Arg("--livery") ?? "rival", true, out var livery) || !Enum.IsDefined(livery))
{
    Console.Error.WriteLine($"--livery {Arg("--livery")}: unbekannt, möglich: none stock rival");
    return 1;
}
var shot = Arg("--shot");
var at = int.Parse(Arg("--at") ?? "0");
float? orbit = Arg("--orbit") is { } o ? float.Parse(o.Split(':')[0], CultureInfo.InvariantCulture) : null;
var orbitDistance = Arg("--orbit") is { } od && od.Split(':') is [_, var m] ? float.Parse(m, CultureInfo.InvariantCulture) : 5.5f;
if (Arg("--lights") is { } lightArg && !Enum.TryParse<Headlights.Mode>(lightArg, true, out _))
{
    Console.Error.WriteLine($"--lights {lightArg}: unbekannt, möglich: off low high");
    return 1;
}
var course = args.Where((a, i) => i == 0 || !valueFlags.Contains(args[i - 1]))
                 .FirstOrDefault(a => a.Contains('_') && !a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) ?? "AKINA_DAY";
float? autodrive = Arg("--autodrive") is { } ad ? float.Parse(ad, CultureInfo.InvariantCulture) : null;
float? bench = Arg("--bench") is { } b ? float.Parse(b, CultureInfo.InvariantCulture) : null;
if (Arg("--frontend-capture") is { } frontWav)
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    return AudioCapture.FrontEnd(isoFile, frontWav) ? 0 : 2;
}
if (args.Contains("--story-check"))
{
    // the story's chapter table and scenes against the English, then every chapter with the autopilot (Touge/Story)
    using var isoFile = new Touge.Formats.Iso9660(iso);
    if (Arg("--story-check") == "calibrate")
    {
        Touge.Story.StoryHeadless.Calibrate(isoFile);
        return 0;
    }
    return Touge.Story.StoryHeadless.Run(isoFile, int.TryParse(Arg("--story-check"), out var only) ? only : null) ? 0 : 2;
}
if (Arg("--jukebox") is { } jukeboxSeconds)
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    return AudioCapture.Jukebox(isoFile, float.Parse(jukeboxSeconds, CultureInfo.InvariantCulture)) ? 0 : 2;
}
if (Arg("--audio-capture") is { } wav)
{
    var capIndex = Array.IndexOf(args, "--audio-capture");
    if (args.Contains("--sweep"))
    {
        using var sweepIso = new Touge.Formats.Iso9660(iso);
        return AudioCapture.Sweep(sweepIso, wav, car) ? 0 : 2;
    }
    if (autodrive == null || capIndex + 2 >= args.Length)
    {
        Console.Error.WriteLine("usage: touge <iso> [KURS] --autodrive <s> --audio-capture <out.wav> <s> [--no-music]");
        return 1;
    }
    using var isoFile = new Touge.Formats.Iso9660(iso);
    return AudioCapture.Run(isoFile, course.ToUpperInvariant(), at, autodrive.Value, wav, float.Parse(args[capIndex + 2], CultureInfo.InvariantCulture),
        !args.Contains("--no-music"), car) ? 0 : 2;
}
// --battle <rival|car> [--rule race|chase] [--lead player|rival]: quick battle against the AI (Touge/Race)
Touge.Race.BattleSetup? battle = null;
if (Arg("--battle") is { } rivalArg)
{
    try
    {
        battle = new Touge.Race.BattleSetup(Touge.Race.Rivals.Find(rivalArg), Arg("--rule") is "chase" or "leadchase" ? Touge.Race.BattleRule.LeadChase : Touge.Race.BattleRule.Race,
            Arg("--lead") == "player" ? 0 : 1);
    }
    catch (ArgumentException e)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
}
if (autodrive is { } battleSeconds && shot == null && battle != null)
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    var drive = new Drive(isoFile, course.ToUpperInvariant(), args.Contains("--reverse"), Kansei.Physics.CarSpecs.All[car]);
    return Touge.Race.BattleRun.Headless(drive, battle, battleSeconds, car) ? 0 : 2;
}
if (autodrive is { } seconds && shot == null)
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    var drive = new Drive(isoFile, course.ToUpperInvariant(), args.Contains("--reverse"), Kansei.Physics.CarSpecs.All[car]);
    drive.ResetTo(at);
    (drive.ForceDrift, drive.Ram) = (args.Contains("--drift"), args.Contains("--ram"));
    return drive.AutoDrive(seconds) ? 0 : 2;
}
if (args.Contains("--zfight"))
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    var filter = Arg("--zfight");
    ZFightReport.Run(isoFile, filter == null || filter.StartsWith("--") ? null : filter);
    return 0;
}
if (Arg("--ground") is { } groundPng)
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    var c = course.ToUpperInvariant();
    CourseGround.Proof(isoFile, c, groundPng, at, args.Contains("--reverse"));
    return 0;
}

// menus (and the saved settings) only when started plainly: any course or test flag means a scripted run
var plain = args.Where((a, i) => a != iso && a != "--backend" && (i == 0 || args[i - 1] != "--backend")).All(a => a is "--menu" or "--input-debug" or "--sim-wheel" || a == Arg("--menu"));
// a plain start opens the window as saved (Options: SCREEN), test runs always in a 1600×900 window
var saved = plain ? Touge.Ui.Settings.Load() : new Touge.Ui.Settings();
KanseiApp.Run(new TougeGame(iso, course.ToUpperInvariant(), shot, at, orbit, autodrive, bench, Arg("--quality") != "off", args.Contains("--drift"), Arg("--flicker"))
    { HudMode = Arg("--hud"), HudScale = Arg("--hud-scale") is { } hs ? float.Parse(hs, CultureInfo.InvariantCulture) / 100 : 1, Reverse = args.Contains("--reverse"), Fog = args.Contains("--fog"), Car = car, Paint = paint, Livery = livery, ContactSheet = Arg("--cars"), LookAtSun = args.Contains("--sun"), OrbitDistance = orbitDistance,
      Battle = battle, ShotBattleResult = args.Contains("--battle-result"), Lights = Arg("--lights") is { } lights ? Enum.Parse<Headlights.Mode>(lights, true) : null,
      RenderScale = Arg("--render-scale") is { } rs ? int.Parse(rs) : 100,
      InputDebug = args.Contains("--input-debug"), SimWheel = args.Contains("--sim-wheel"),
      UseMenus = plain, StartMenu = Arg("--menu"), Flow = Arg("--flow"), Offscreen = args.Contains("--offscreen"),
      StoryFlow = args.Contains("--story"), StoryProgress = int.TryParse(Arg("--progress"), out var progress) ? progress : 0,
      ShotSize = Arg("--shot-size") is { } size && size.Split('x') is [var sw, var sh] ? (int.Parse(sw), int.Parse(sh)) : (1280, 720) }, new WindowSettings
{
    Title = $"Touge – {course}",
    WindowPixelWidth = saved.Width,
    WindowPixelHeight = saved.Height,
    VSync = saved.VSync,
    FullscreenMode = saved.Display switch
    {
        Touge.Ui.Settings.DisplayMode.Borderless => FullscreenMode.Borderless,
        Touge.Ui.Settings.DisplayMode.Fullscreen => FullscreenMode.Exclusive,
        _ => FullscreenMode.Windowed,
    },
    Backend = KanseiApp.ResolveBackend(args),
});
return 0;
