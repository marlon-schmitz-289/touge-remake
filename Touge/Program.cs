using System.Globalization;
using Kansei.Core;
using Kansei.Windowing;
using Touge;

// touge <ISO> [KURS_ZEIT] [--shot out.png]   z. B. touge "Initial D - Special Stage (Japan) (v2.00).iso" AKINA_NIT
// --shot rendert einen Frame als PNG und beendet sich; --at <n> startet bei Punkt n der Fahrlinie;
// --orbit <grad> Kamera ums geparkte Auto (0 vorne, 90 links, 180 hinten).
// --autodrive <s>: Pilot fährt die Fahrlinie ab, Log pro Sekunde; ohne --shot ohne Fenster, mit --shot Verfolgerbild am Ende.
// --audio-capture <wav> <s> (mit --autodrive): Spielton offline (OpenAL-Loopback) als WAV + Auswertung, ohne Fenster; --no-music ohne BGM.
// --ground <png>: Kollision des Kurses laden, Raycasts timen, Draufsicht mit Wandsegmenten schreiben (ohne Fenster).
// --bench <s>: Pilot fährt <s> Sekunden in Echtzeit mit Verfolgerkamera und Ton, danach Frametimes (avg/p99/max) und Ende.
// --quality off: ohne MSAA/Bloom starten (F2 schaltet um).
// --flicker <prefix>: Z-Fighting im Bild messen (8 Punkte der Fahrlinie + 8 Winkel ums Auto, je 3× mit verschobener Rundung), Ausschnitte als <prefix>_course/_car.png.
// --zfight [filter]: Z-Fighting-Kandidaten (fast koplanar, überlappend) aller Kurse und Autos auflisten (ohne Fenster).
// --hud north|overview|off: Minimap nordausgerichtet / ganze Strecke / HUD aus (Standard: mitdrehend; N und F4 schalten um).
// --reverse: Gegenrichtung (bergauf): CRS_COLI_<KURS>_1 (Rundkurse _0) + CRS_DRV_<KURS>_O, Start am anderen Ende; im Spiel B.
// --car <NAME|index> (HCAR-Name wie AE86T, FD3S, R32, EVO3 … oder 0–31), --paint <n> (CAR_ENV-Farbe, 0 = Standard).
// --cars <png>: Kontaktbogen aller 32 Autos (Orbit 35°, 4 × 8 Kacheln in CarPaint.Cars-Reihenfolge), dann Ende; mit --hud off.
// --sun: freie Kamera am Startpunkt schaut zur Sonne (Blendung prüfen).
// Ohne Kurs und ohne Test-Flags (außer --backend) startet das Spiel im Titelmenü mit den gespeicherten Einstellungen (Ui/Settings).
// --menu title|course|car|pause|settings: dieses Menü beim Start öffnen (auch mit Test-Flags, z. B. --menu car --shot out/m.png).
// --shot-size WxH: Größe des --shot-Bildes (Standard 1280x720), z. B. 3200x1800 für die HUD-Skalierung.
// --drift: Pilot reißt alle 7 s (ab 4,5 s) einen 2,5-s-Handbremsdrift (Reifenrauch/Bremsspuren testen), z. B. --autodrive 6.3 --drift --shot.
var iso = args.FirstOrDefault(a => a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
          ?? Environment.GetEnvironmentVariable("INITIALD_ISO");
if (iso == null || !File.Exists(iso))
{
    Console.Error.WriteLine("usage: touge <Initial D Special Stage (SLPM-65268).iso> [KURS_ZEIT, z. B. AKINA_DAY]  (oder INITIALD_ISO setzen)");
    return 1;
}
string[] valueFlags = ["--shot", "--at", "--orbit", "--ground", "--autodrive", "--backend", "--bench", "--quality", "--audio-capture", "--zfight", "--flicker", "--hud", "--car", "--paint", "--cars", "--menu", "--shot-size"];
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
var shot = Arg("--shot");
var at = int.Parse(Arg("--at") ?? "0");
float? orbit = Arg("--orbit") is { } o ? float.Parse(o, CultureInfo.InvariantCulture) : null;
var course = args.Where((a, i) => i == 0 || !valueFlags.Contains(args[i - 1]))
                 .FirstOrDefault(a => a.Contains('_') && !a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) ?? "AKINA_DAY";
float? autodrive = Arg("--autodrive") is { } ad ? float.Parse(ad, CultureInfo.InvariantCulture) : null;
float? bench = Arg("--bench") is { } b ? float.Parse(b, CultureInfo.InvariantCulture) : null;
if (Arg("--audio-capture") is { } wav)
{
    var capIndex = Array.IndexOf(args, "--audio-capture");
    if (autodrive == null || capIndex + 2 >= args.Length)
    {
        Console.Error.WriteLine("usage: touge <iso> [KURS] --autodrive <s> --audio-capture <out.wav> <s> [--no-music]");
        return 1;
    }
    using var isoFile = new Touge.Formats.Iso9660(iso);
    return AudioCapture.Run(isoFile, course.ToUpperInvariant(), at, autodrive.Value, wav, float.Parse(args[capIndex + 2], CultureInfo.InvariantCulture),
        !args.Contains("--no-music"), car) ? 0 : 2;
}
if (autodrive is { } seconds && shot == null)
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    var drive = new Drive(isoFile, course.ToUpperInvariant(), args.Contains("--reverse"), Kansei.Physics.CarSpecs.All[car]);
    drive.ResetTo(at);
    drive.ForceDrift = args.Contains("--drift");
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
    CourseGround.Proof(isoFile, c[..c.LastIndexOf('_')], groundPng, at, args.Contains("--reverse"));
    return 0;
}

// menus (and the saved settings) only when started plainly: any course or test flag means a scripted run
var plain = args.Where((a, i) => a != iso && a != "--backend" && (i == 0 || args[i - 1] != "--backend")).All(a => a == "--menu" || a == Arg("--menu"));
KanseiApp.Run(new TougeGame(iso, course.ToUpperInvariant(), shot, at, orbit, autodrive, bench, Arg("--quality") != "off", args.Contains("--drift"), Arg("--flicker"))
    { HudMode = Arg("--hud"), Reverse = args.Contains("--reverse"), Car = car, Paint = paint, ContactSheet = Arg("--cars"), LookAtSun = args.Contains("--sun"),
      UseMenus = plain, StartMenu = Arg("--menu"),
      ShotSize = Arg("--shot-size") is { } size && size.Split('x') is [var sw, var sh] ? (int.Parse(sw), int.Parse(sh)) : (1280, 720) }, new WindowSettings
{
    Title = $"Touge – {course}",
    WindowPixelWidth = 1600,
    WindowPixelHeight = 900,
    Backend = KanseiApp.ResolveBackend(args),
});
return 0;
