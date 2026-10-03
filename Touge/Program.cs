using Kansei.Core;
using Kansei.Windowing;
using Touge;

// touge <ISO> [KURS_ZEIT] [--shot out.png]   z. B. touge "Initial D - Special Stage (Japan) (v2.00).iso" AKINA_NIT
// --shot rendert einen Frame als PNG und beendet sich; --at <n> startet bei Punkt n der Fahrlinie.
// --ground <png>: Kollision des Kurses laden, Raycasts timen, Draufsicht mit Wandsegmenten schreiben (ohne Fenster).
var iso = args.FirstOrDefault(a => a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
          ?? Environment.GetEnvironmentVariable("INITIALD_ISO");
if (iso == null || !File.Exists(iso))
{
    Console.Error.WriteLine("usage: touge <Initial D Special Stage (SLPM-65268).iso> [KURS_ZEIT, z. B. AKINA_DAY]  (oder INITIALD_ISO setzen)");
    return 1;
}
var shotIdx = Array.IndexOf(args, "--shot");
var shot = shotIdx >= 0 && shotIdx + 1 < args.Length ? args[shotIdx + 1] : null;
var atIdx = Array.IndexOf(args, "--at");
var at = atIdx >= 0 && atIdx + 1 < args.Length ? int.Parse(args[atIdx + 1]) : 0;
var groundIdx = Array.IndexOf(args, "--ground");
var course = args.Where((_, i) => (shotIdx < 0 || i != shotIdx + 1) && (atIdx < 0 || i != atIdx + 1) && (groundIdx < 0 || i != groundIdx + 1)).FirstOrDefault(a => a.Contains('_') && !a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) ?? "AKINA_DAY";
if (groundIdx >= 0 && groundIdx + 1 < args.Length)
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    var c = course.ToUpperInvariant();
    CourseGround.Proof(isoFile, c[..c.LastIndexOf('_')], args[groundIdx + 1], at);
    return 0;
}

KanseiApp.Run(new TougeGame(iso, course.ToUpperInvariant(), shot, at), new WindowSettings
{
    Title = $"Touge – {course}",
    WindowPixelWidth = 1600,
    WindowPixelHeight = 900,
    Backend = KanseiApp.ResolveBackend(args),
});
return 0;
