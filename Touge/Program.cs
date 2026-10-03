using Kansei.Core;
using Kansei.Windowing;
using Touge;

// touge <ISO> [KURS_ZEIT] [--shot out.png]   z. B. touge "Initial D - Special Stage (Japan) (v2.00).iso" AKINA_NIT
// --shot rendert einen Frame vom Start der Fahrlinie als PNG und beendet sich.
var iso = args.FirstOrDefault(a => a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
          ?? Environment.GetEnvironmentVariable("INITIALD_ISO");
if (iso == null || !File.Exists(iso))
{
    Console.Error.WriteLine("usage: touge <Initial D Special Stage (SLPM-65268).iso> [KURS_ZEIT, z. B. AKINA_DAY]  (oder INITIALD_ISO setzen)");
    return 1;
}
var shotIdx = Array.IndexOf(args, "--shot");
var shot = shotIdx >= 0 && shotIdx + 1 < args.Length ? args[shotIdx + 1] : null;
var course = args.Where((_, i) => i != shotIdx + 1 || shotIdx < 0).FirstOrDefault(a => a.Contains('_') && !a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) ?? "AKINA_DAY";

KanseiApp.Run(new TougeGame(iso, course.ToUpperInvariant(), shot), new WindowSettings
{
    Title = $"Touge – {course}",
    WindowPixelWidth = 1600,
    WindowPixelHeight = 900,
    Backend = KanseiApp.ResolveBackend(args),
});
return 0;
