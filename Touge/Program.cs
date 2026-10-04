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
// --drift: Pilot reißt alle 7 s (ab 4,5 s) einen 2,5-s-Handbremsdrift (Reifenrauch/Bremsspuren testen), z. B. --autodrive 6.3 --drift --shot.
var iso = args.FirstOrDefault(a => a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
          ?? Environment.GetEnvironmentVariable("INITIALD_ISO");
if (iso == null || !File.Exists(iso))
{
    Console.Error.WriteLine("usage: touge <Initial D Special Stage (SLPM-65268).iso> [KURS_ZEIT, z. B. AKINA_DAY]  (oder INITIALD_ISO setzen)");
    return 1;
}
string[] valueFlags = ["--shot", "--at", "--orbit", "--ground", "--autodrive", "--backend", "--bench", "--quality", "--audio-capture"];
string? Arg(string flag) { var i = Array.IndexOf(args, flag); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
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
        !args.Contains("--no-music")) ? 0 : 2;
}
if (autodrive is { } seconds && shot == null)
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    var drive = new Drive(isoFile, course.ToUpperInvariant());
    drive.ResetTo(at);
    drive.ForceDrift = args.Contains("--drift");
    return drive.AutoDrive(seconds) ? 0 : 2;
}
if (Arg("--ground") is { } groundPng)
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    var c = course.ToUpperInvariant();
    CourseGround.Proof(isoFile, c[..c.LastIndexOf('_')], groundPng, at);
    return 0;
}

KanseiApp.Run(new TougeGame(iso, course.ToUpperInvariant(), shot, at, orbit, autodrive, bench, Arg("--quality") != "off", args.Contains("--drift")), new WindowSettings
{
    Title = $"Touge – {course}",
    WindowPixelWidth = 1600,
    WindowPixelHeight = 900,
    Backend = KanseiApp.ResolveBackend(args),
});
return 0;
