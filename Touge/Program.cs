using System.Globalization;
using Kansei.Core;
using Kansei.Windowing;
using Touge;

// touge <ISO> [KURS_ZEIT] [--shot out.png]   z. B. touge "Initial D - Special Stage (Japan) (v2.00).iso" AKINA_NIT
// --shot rendert einen Frame als PNG und beendet sich; --at <n> startet bei Punkt n der Fahrlinie;
// --orbit <grad> Kamera ums geparkte Auto (0 vorne, 90 links, 180 hinten).
// --ground <png>: Kollision des Kurses laden, Raycasts timen, Draufsicht mit Wandsegmenten schreiben (ohne Fenster).
var iso = args.FirstOrDefault(a => a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
          ?? Environment.GetEnvironmentVariable("INITIALD_ISO");
if (iso == null || !File.Exists(iso))
{
    Console.Error.WriteLine("usage: touge <Initial D Special Stage (SLPM-65268).iso> [KURS_ZEIT, z. B. AKINA_DAY]  (oder INITIALD_ISO setzen)");
    return 1;
}
string[] valueFlags = ["--shot", "--at", "--orbit", "--ground"];
string? Arg(string flag) { var i = Array.IndexOf(args, flag); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var shot = Arg("--shot");
var at = int.Parse(Arg("--at") ?? "0");
float? orbit = Arg("--orbit") is { } o ? float.Parse(o, CultureInfo.InvariantCulture) : null;
var course = args.Where((a, i) => i == 0 || !valueFlags.Contains(args[i - 1]))
                 .FirstOrDefault(a => a.Contains('_') && !a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) ?? "AKINA_DAY";
if (Arg("--ground") is { } groundPng)
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    var c = course.ToUpperInvariant();
    CourseGround.Proof(isoFile, c[..c.LastIndexOf('_')], groundPng, at);
    return 0;
}

KanseiApp.Run(new TougeGame(iso, course.ToUpperInvariant(), shot, at, orbit), new WindowSettings
{
    Title = $"Touge – {course}",
    WindowPixelWidth = 1600,
    WindowPixelHeight = 900,
    Backend = KanseiApp.ResolveBackend(args),
});
return 0;
