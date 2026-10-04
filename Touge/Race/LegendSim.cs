using Kansei.Physics;
using Touge.Formats;

namespace Touge.Race;

/// <summary>
///     --legend-sim: Legend of the Streets without a window. The autopilot (<see cref="BattleRun.Autopilot"/>) drives the
///     player's car up every course's ladder as a player would — only rivals the progress has unlocked, a lost battle ends
///     that course's run — with each battle's conditions (<see cref="Legend.Conditions"/>), the result counted into the
///     progress (saved to a JSON file for the menus: --legend-progress) and the unlocks printed as they happen.
/// </summary>
public static class LegendSim
{
    /// <summary>Returns false if a battle's simulation blew up.</summary>
    public static bool Run(Iso9660 iso, string car, string? progressPath, float seconds = 600)
    {
        var p = progressPath != null && File.Exists(progressPath) ? Legend.Progress.Load(progressPath) : new Legend.Progress();
        var models = iso.OpenAfs("CDVD/DATA/MODEL/COURSE.AFS");
        var times = Legend.CourseIds.ToDictionary(id => id, id => new[] { "DAY", "NIT", "RIN" }.Where(t => models.Find($"{id}_{t}.PAC") != null).ToArray());
        int battles = 0, wins = 0;
        var lost = new HashSet<string>(); // a rerun of a lost battle would end the same (deterministic)
        for (var pass = 0; pass < 3; pass++) // secret rivals and the additions open only after other courses
        {
            var progressed = false;
            for (var slot = 0; slot < Legend.CourseIds.Length; slot++)
                foreach (var e in Legend.Of(slot))
                {
                    if (p.Beaten(e.Key)) continue;
                    if (!Legend.Unlocked(e, p) || lost.Contains(e.Key)) break;
                    var (courseTime, wet) = Legend.Conditions(e, times[e.CourseId], p);
                    var drive = new Drive(iso, courseTime, e.Reverse, CarSpecs.All[car]);
                    var race = BattleRun.Create(drive, Legend.Setup(e), new AiDriver(new RivalPilot(drive.Line, BattleRun.Autopilot)), car + " (AUTO)");
                    Console.WriteLine($"\n[Legend] {e.Key}: {courseTime} {(e.Reverse ? "rückwärts" : "vorwärts")}{(wet ? " nass" : "")}, {e.Rival.Name} ({e.Rival.Car}, Stufe {Legend.Stars(e)})");
                    if (!BattleRun.Run(race, seconds)) return false;
                    var b = race.Battle!;
                    var before = Legend.All.Where(x => Legend.Unlocked(x, p)).Select(x => x.Key).ToHashSet();
                    var carBefore = Legend.CarLocked(Legend.SecretCar, p);
                    p.Add(e.Key, b.Outcome, b.DecidedGap);
                    battles++;
                    if (b.Outcome == BattleOutcome.Win) wins++;
                    var opened = Legend.All.Where(x => Legend.Unlocked(x, p) && !before.Contains(x.Key)).Select(x => x.Key).ToArray();
                    Console.WriteLine($"[Legend] => {b.Outcome} ({b.Reason}, {b.DecidedGap:+0.00;-0.00} s){(opened.Length > 0 ? ", neu offen: " + string.Join(", ", opened) : "")}" +
                                      $"{(carBefore && !Legend.CarLocked(Legend.SecretCar, p) ? $", Auto {Legend.SecretCar} frei" : "")}");
                    if (b.Outcome != BattleOutcome.Win)
                    {
                        lost.Add(e.Key);
                        break; // the ladder stops at a loss, as for a player
                    }
                    progressed = true;
                }
            if (!progressed) break;
        }
        Console.WriteLine($"\n[Legend] {battles} Battles, {wins} Siege; Rivalen besiegt {Legend.All.Count(e => p.Beaten(e.Key))}/{Legend.All.Length}, " +
                          $"Kurse geschafft {Enumerable.Range(0, Legend.CourseIds.Length).Count(s => Legend.Cleared(s, p))}/{Legend.CourseIds.Length}, " +
                          $"Zusatzkurse {(Legend.CourseOpen(Legend.MainCourses, p) ? "offen" : "zu")}, {Legend.SecretCar} {(Legend.CarLocked(Legend.SecretCar, p) ? "gesperrt" : "frei")}");
        foreach (var slot in Enumerable.Range(0, Legend.CourseIds.Length))
            Console.WriteLine($"[Legend]   {Legend.CourseIds[slot],-8} " + string.Join("  ", Legend.Of(slot).Select(e =>
                $"{e.Rival.Id}:{(p.Beaten(e.Key) ? "WIN" : !Legend.Unlocked(e, p) ? "zu" : p.Get(e.Key).Losses > 0 ? "LOSE" : "offen")}")));
        if (progressPath != null)
        {
            p.Save(progressPath);
            Console.WriteLine($"[Legend] Fortschritt -> {progressPath}");
        }
        return true;
    }
}
