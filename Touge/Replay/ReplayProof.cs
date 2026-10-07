using System.Numerics;
using Kansei.Physics;
using Touge.Formats;
using Touge.Race;

namespace Touge.Replays;

/// <summary>
///     --replay-test: records a run on the real course without a window (the line pilot, or with --battle the autopilot against
///     the AI), writes and re-reads the file, plays it back on fresh cars and compares every car's position tick by tick with the
///     recording: with keyframes (the viewer) and on inputs alone (determinism). One log line per 10 s, a summary, optionally the file.
/// </summary>
public static class ReplayProof
{
    public static bool Run(Iso9660 iso, string course, bool reverse, string car, float seconds, BattleSetup? battle, bool drift, string? save)
    {
        var drive = new Drive(iso, course, reverse, CarSpecs.All[car]) { ForceDrift = drift };
        drive.ResetTo(0);
        RaceSession? race = battle != null ? BattleRun.Create(drive, battle, new AiDriver(new RivalPilot(drive.Line, BattleRun.Autopilot)), "YOU") : null;
        Vehicle[] cars = race != null ? [.. race.Cars.Select(c => c.Vehicle)] : [drive.Car];
        var replay = new Replay
        {
            Info =
            {
                Course = course, Reverse = reverse, Date = DateTime.Now, Mode = battle != null ? "BATTLE" : "TIME ATTACK",
                Cars = [new ReplayCar("YOU", car, 0), .. battle != null ? new[] { new ReplayCar(battle.Rival.Name, battle.Rival.Car, 0, Power: battle.Rival.Power) } : []],
            },
        };
        var rec = new ReplayRecorder(replay, cars);
        var path = new List<Vector3[]>();
        var goal = drive.Pilot.Length - Ui.LapTimer.Gate;
        var timer = new Ui.LapTimer(drive.Pilot.Length - drive.Start, null); // the run time as the HUD's
        bool finished = false;
        var respawns = 0;
        var ticks = (int)(seconds / Drive.Dt);
        for (var t = 0; t < ticks; t++)
        {
            rec.Before(cars);
            if (race != null)
            {
                race.Tick(Drive.Dt);
                rec.After([.. race.Cars.Select(c => c.Input)]);
                var r = race.Cars.Sum(c => c.Respawns);
                if (r != respawns)
                {
                    respawns = r;
                    rec.Mark();
                }
                if (race.Battle is { Outcome: not BattleOutcome.None } b)
                {
                    replay.Info.Result ??= b.Outcome.ToString().ToUpperInvariant();
                    replay.Info.Time = race.Cars[0].FinishedAt ?? b.DecidedAt;
                }
            }
            else
            {
                finished |= drive.Pilot.Track(drive.Car.Position).Along >= goal;
                var input = finished ? drive.Coast() : drive.PilotInput(t * Drive.Dt);
                drive.Car.Step(input, drive.Ground, Drive.Dt);
                rec.After([input]);
                timer.Update(drive.Pilot.Track(drive.Car.Position).Along - drive.Start, Drive.Dt);
                if (timer.Phase == Ui.LapTimer.State.Finished) replay.Info.Time ??= timer.Time;
            }
            path.Add([.. cars.Select(c => c.Position)]);
        }
        var file = new MemoryStream();
        replay.Write(file);
        var bytes = file.Length;
        file.Position = 0;
        var back = Replay.Read(file);
        if (save != null) back.Save(save);
        Console.WriteLine($"[Replay] {course}{(reverse ? " rückwärts" : "")} {replay.Info.Mode} {string.Join(" vs ", replay.Info.Cars.Select(c => c.Car))} {replay.Info.Result} Zeit {Ui.Style.Time(replay.Info.Time)}: {back.Ticks} Ticks ({back.Seconds:F1} s), " +
                          $"{back.Keys.Count} Keyframes, Datei {bytes / 1024.0:F0} KiB ({bytes / back.Seconds / 1024:F1} KiB/s){(save != null ? $" -> {save}" : "")}");

        // playback on fresh cars of the same specs on the same ground
        Vehicle[] fresh = [.. cars.Select(c => new Vehicle(c.Spec) { SurfaceGrip = drive.Car.SurfaceGrip })];
        var player = new ReplayPlayer(back, fresh, drive.Ground);
        player.Seek(0);
        float max = 0, sum = 0, secondMax = 0;
        var n = 0;
        Console.WriteLine("[Replay]    t   fehler_max_m (Wiedergabe mit Keyframes, je 10 s)");
        for (var t = 0; player.Step(); t++)
        {
            for (var c = 0; c < cars.Length; c++)
            {
                var e = Vector3.Distance(fresh[c].Position, path[t][c]);
                (max, sum, secondMax, n) = (MathF.Max(max, e), sum + e, MathF.Max(secondMax, e), n + 1);
            }
            if ((t + 1) % 1200 == 0)
            {
                Console.WriteLine($"[Replay] {(t + 1) * Drive.Dt,4:F0}   {secondMax:F6}");
                secondMax = 0;
            }
        }
        var d = new ReplayPlayer(back, [.. cars.Select(c => new Vehicle(c.Spec) { SurfaceGrip = drive.Car.SurfaceGrip })], drive.Ground).MeasureDrift();
        Console.WriteLine($"[Replay] Positionsfehler Wiedergabe gegen Aufnahme über {n} Auto-Ticks: max {max * 1000:F3} mm, Mittel {sum / Math.Max(n, 1) * 1000:F4} mm");
        Console.WriteLine($"[Replay] Nur Eingaben (ohne Keyframes): an {d.Samples} Keyframes max {d.Max * 1000:F3} mm (bei {d.MaxAt:F1} s), Mittel {d.Mean * 1000:F4} mm, " +
                          $"erste Abweichung {(d.FirstAt is { } f ? $"bei {f:F1} s" : "keine")}{(finished ? " (mit Ziel und Auslauf)" : "")}");
        // seek: random jumps land exactly where straight playback was
        var rng = new Random(1);
        float seekMax = 0;
        for (var k = 0; k < 20; k++)
        {
            var at = rng.Next(1, back.Ticks);
            player.Seek(at);
            for (var c = 0; c < cars.Length; c++) seekMax = MathF.Max(seekMax, Vector3.Distance(fresh[c].Position, path[at - 1][c]));
        }
        Console.WriteLine($"[Replay] 20 Sprünge (Seek): max {seekMax * 1000:F3} mm Abweichung");
        return max < 0.01f;
    }
}
