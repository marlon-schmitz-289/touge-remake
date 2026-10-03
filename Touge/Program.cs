using Kansei.Core;
using Kansei.Windowing;
using Touge;

// touge <ISO> [KURS_ZEIT] [--shot out.png]   z. B. touge "Initial D - Special Stage (Japan) (v2.00).iso" AKINA_NIT
// --shot rendert einen Frame als PNG und beendet sich; --at <n> startet bei Punkt n der Fahrlinie;
// --orbit <grad> Kamera ums geparkte Auto (0 vorne, 90 links, 180 hinten).
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
var orbitIdx = Array.IndexOf(args, "--orbit");
float? orbit = orbitIdx >= 0 && orbitIdx + 1 < args.Length ? float.Parse(args[orbitIdx + 1], System.Globalization.CultureInfo.InvariantCulture) : null;
var course = args.Where((_, i) => (shotIdx < 0 || i != shotIdx + 1) && (atIdx < 0 || i != atIdx + 1) && (orbitIdx < 0 || i != orbitIdx + 1)).FirstOrDefault(a => a.Contains('_') && !a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) ?? "AKINA_DAY";

KanseiApp.Run(new TougeGame(iso, course.ToUpperInvariant(), shot, at, orbit), new WindowSettings
{
    Title = $"Touge – {course}",
    WindowPixelWidth = 1600,
    WindowPixelHeight = 900,
    Backend = KanseiApp.ResolveBackend(args),
});
return 0;
