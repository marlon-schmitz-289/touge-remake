using System.Globalization;
using System.Numerics;
using Kansei.Physics;
using Touge.Formats;
using Touge.Ui;
using Corner = Kansei.Physics.CourseMap.Corner;

namespace Touge.Race;

/// <summary>
///     --ai-bench solo|drift|battle|corners|human[:KURS,…|:datei.rpl] [--car X]: measures the AI headless (no window).
///     <c>solo</c>: every course × direction × car (AE86T FR, R34 4WD, EK9 FF) × skill alone from the start line to the goal —
///     time, % over the reference (fastest run of the pilot at higher planned grip, skill 1…5, with at most one wall hit
///     more than the cleanest run), wall hits (where), widest lateral, body slip per corner class. <c>drift</c>: the
///     prototype <see cref="DriftOverlay"/> through the tight bends against plain grip. <c>battle</c>: the autopilot (0.8,
///     AE86T) against five rivals, race and lead/chase both ways. <c>corners</c>: bend classes and road room per course.
///     <c>human</c>: a recorded run measured the same way next to the pilot. AIBENCH_TRACE=from:to (m) logs each solo run
///     there every 0.1 s, AIBENCH_SKILLS=a,b,… replaces the solo skills, AIBENCH_DRIFT=r,β°,gain,g,exit one drift set.
/// </summary>
public static class AiBench
{
    private const float Dt = Drive.Dt, Deg = 180 / MathF.PI, G = 9.81f;

    /// <summary>The 11 Legend courses: the two circuits and the 9 mountain courses (day versions).</summary>
    public static readonly string[] Courses = ["MYOUGI0", "USUI0", "AKINA", "AKAGI", "IROHA", "MYOUGI", "USUI", "HAPPOU", "SHOMARU", "MOMIJI", "SHIONA"];

    public static readonly string[] Cars = ["AE86T", "R34", "EK9"];

    /// <summary>Bends of <paramref name="line"/> (<see cref="CourseMap.FindCorners"/>).</summary>
    public static List<Corner> Corners(Vector3[] line) => CourseMap.FindCorners(line);

    private static Vector2 Xz(Vector3 v) => new(v.X, v.Z);

    /// <summary>Per-corner measurement of one run.</summary>
    public struct CornerRun
    {
        public float Time, Entry, Min, Exit, MaxBeta, SlideTime;
        /// <summary>Least room (m) from the car's centre to the end of the road on the bend's outside, between its start and end.</summary>
        public float MinGap;
        public bool Reached;
        /// <summary>The run got past the bend's end (a replay that stops mid-corner leaves it partial).</summary>
        public bool Done;
    }

    /// <param name="Splits">Time from the start line at every <see cref="SplitStep"/> m past it (a replay that stops early has fewer).</param>
    public sealed record Result(float Time, int WallHits, int WallTicks, float MaxLateral, CornerRun[] Corners, bool Finished, List<float> HitsAt, List<float> Splits)
    {
        public string TimeText => Finished ? Time.ToString("F2", CultureInfo.InvariantCulture) : "DNF";
    }

    /// <summary>What the pilot did in a run: drifts held/aborted, mistakes, its corner plan.</summary>
    public sealed record PilotStats(int Drifts, int Aborts, int Mistakes, DriftController.Entry?[] Plan, int[]? AbortWhy = null, int Faded = 0);

    /// <summary>The countdown's length (s): 3-2-1-GO.</summary>
    private const float Countdown = 3;

    /// <summary>The countdown on the line like the game's rivals: revs by skill (<see cref="RivalPilot.LaunchThrottle"/>).</summary>
    private static void RevUp(Vehicle car, RivalPilot pilot)
    {
        for (var t = 0f; t < Countdown; t += Dt) car.Rev(pilot.LaunchThrottle, Dt);
    }

    /// <summary>One run alone from the spawn to the goal (or 600 s) with <paramref name="style"/>.</summary>
    /// <param name="cornerGrip">Per corner grip factors instead of the baked ones (<see cref="RivalPilot.CornerGrip"/>).</param>
    public static (Result R, PilotStats P) Solo(Drive drive, RivalStyle style, List<Corner> corners, int seed = 0, float[]? cornerGrip = null)
    {
        drive.ResetTo(0);
        var car = drive.Car;
        var pilot = new RivalPilot(drive.Line, style) { Seed = seed, CornerGrip = cornerGrip };
        RevUp(car, pilot);
        if (Trace is var (from, to))
        {
            pilot.Prepare(car, drive.Ground);
            var rl = pilot.Racing!;
            for (var i = rl.Map.Index(from); i <= rl.Map.Index(to); i++)
                Console.WriteLine($"[Line] {i * CourseMap.Step:F0} m off {rl.Offset[i]:+0.00;-0.00} [{rl.Lo[i]:+0.0;-0.0}, {rl.Hi[i]:+0.0;-0.0}] room L {rl.Map.RoomLeft[i]:F2} R {rl.Map.RoomRight[i]:F2} " +
                                  $"κ {rl.Curvature[i]:+0.000;-0.000} (course {rl.Map.Bend[i]:+0.000;-0.000}) v {rl.Speed[i] * 3.6f:F0} km/h" +
                                  (Environment.GetEnvironmentVariable("AIBENCH_ROOMDBG") != null ? " ground " + string.Join(" ", Enumerable.Range(-8, 17).Select(k =>
                                  {
                                      var q = rl.Map.Point[i] + rl.Map.Left[i] * (k * 0.5f);
                                      return drive.Ground.Raycast(q + Vector3.UnitY * 3, -Vector3.UnitY, 8, out var gh) ? $"{gh.Point.Y - rl.Map.Point[i].Y:+0.0;-0.0}/{gh.Surface}" : "x";
                                  })) : ""));
        }
        // braking and drift numbers per tick: share of the run on the brake, its deceleration, the brake in drift exits, revs in the hold
        var decels = new List<float>();
        int ticks = 0, exits = 0, exitBrake = 0, holds = 0, holdLow = 0, holdFree = 0;
        var peak = DriftController.PeakRpm(car.Spec);
        var r = Measure(drive, corners, () =>
        {
            var input = pilot.Drive(car, drive.Ground, [], Dt);
            var v0 = car.Velocity.Length();
            car.Step(input, drive.Ground, Dt);
            ticks++;
            // the tyres' share: downhill gravity pulls against the brakes (the plan's DecelAt counts the grade the same way)
            if (input.Brake > 0.05f && !input.Handbrake) decels.Add((v0 - car.Velocity.Length()) / Dt - G * car.Velocity.Y / MathF.Max(car.Velocity.Length(), 1));
            if (pilot.Drift.State == DriftController.Phase.Exit) (exits, exitBrake) = (exits + 1, exitBrake + (input.Brake > 0.05f ? 1 : 0));
            if (pilot.Drift.State == DriftController.Phase.Hold)
            {
                (holds, holdLow) = (holds + 1, holdLow + (car.Rpm < peak ? 1 : 0));
                // under the peak although the gear below would still stay under the upshift point (DriftController's downshift)
                if (car.Rpm < peak && car.Gear > 1 && car.Rpm * car.Spec.Gears[car.Gear - 2] / car.Spec.Gears[car.Gear - 1] <= car.Spec.AutoUpRpm - 300) holdFree++;
            }
            return input;
        }, () => pilot.Pilot.TargetSpeed, () =>
        {
            var (s, _) = pilot.Pilot.Track(car.Position);
            var rl = pilot.Racing!;
            var (lo, hi) = rl.BoundsAt(s);
            var (l, r) = rl.Map.Room(s);
            return (pilot.Drifting ? $"drift {pilot.Drift.State} cmd {pilot.Drift.Command * Deg:F0} {pilot.Drift.Debug}" :pilot.State.ToString()) + (pilot.Pilot.Sliding ? " slide" : "") + $" plan {rl.OffsetAt(s):+0.00;-0.00} [{lo:+0.0;-0.0},{hi:+0.0;-0.0}] room L {l:F2} R {r:F2} κ {rl.CurvatureAt(s):+0.000;-0.000} {pilot.MistakeNote}";
        });
        decels.Sort();
        var limit = GripLimit.BrakeLimit(car.Spec).Decel;
        if (decels.Count > 0)
            Console.WriteLine($"[Stats] {style.Skill:F2}/{style.Drift:F1} brake {100f * decels.Count / ticks:F1}% of the run, decel p50 {decels[decels.Count / 2] / G:F2} g p95 {decels[(int)(0.95f * (decels.Count - 1))] / G:F2} g = {decels[(int)(0.95f * (decels.Count - 1))] / limit:F2} of the limit {limit / G:F2} g," +
                              $" drift exits braked {(exits > 0 ? 100f * exitBrake / exits : 0):F0}% of {exits}, hold under peak rpm {(holds > 0 ? 100f * holdLow / holds : 0):F0}% of {holds} ({(holds > 0 ? 100f * holdFree / holds : 0):F0}% with a lower gear free)");
        return (r, new PilotStats(pilot.Drift.Held, pilot.Drift.Aborted, pilot.Mistakes, [.. Enumerable.Range(0, corners.Count).Select(pilot.DriftAt)], [.. pilot.Drift.AbortWhy], pilot.Drift.Faded));
    }

    /// <summary>
    ///     Measures a run of <paramref name="drive"/>'s car: <paramref name="tick"/> advances it one tick and returns the input
    ///     it got (null = no more ticks); time from the start line to the goal, wall hits, widest lateral, per-corner numbers.
    /// </summary>
    public const float SplitStep = 10;

    public static Result Measure(Drive drive, List<Corner> corners, Func<VehicleInput?> tick, Func<float>? target = null, Func<string>? note = null)
    {
        var splits = new List<float>();
        var car = drive.Car;
        var map = CourseMap.Of(drive.Line, drive.Ground, car.SurfaceGrip);
        var goal = drive.Pilot.Length - LapTimer.Gate;
        var runs = new CornerRun[corners.Count];
        int hits = 0, wallTicks = 0, ci = 0;
        var hitsAt = new List<float>();
        float t = 0, started = -1, lastHit = -9, maxLat = 0;
        for (; t < 600; t += Dt)
        {
            if (tick() is not { } input) break;
            var (s, lat) = drive.Pilot.Track(car.Position);
            if (!float.IsFinite(car.Position.X + car.Velocity.X)) break;
            if (started < 0 && s >= drive.Start) started = t;
            if (Trace is var (tf, tt) && s >= tf && s <= tt && (int)(t / Dt) % 12 == 0)
                Console.WriteLine($"[Trace] t {t:F2} s {s:F1} lat {lat:+0.0;-0.0} {car.SpeedKmh:F0} km/h tgt {(target?.Invoke() ?? 0) * 3.6f:F0} β {car.SlipAngle * Deg:+0;-0}° " +
                                  $"in thr {input.Throttle:F2} brk {input.Brake:F2} str {input.Steer:+0.00;-0.00} hb {(input.Handbrake ? 1 : 0)} wall {car.WallContacts} g{car.Gear} {note?.Invoke()}");
            if (started < 0) continue;
            while (s >= drive.Start + splits.Count * SplitStep) splits.Add(t - started);
            maxLat = MathF.Max(maxLat, MathF.Abs(lat));
            if (car.WallContacts > 0)
            {
                wallTicks++;
                if (car.WallImpactSpeed > 1 && t - lastHit > 0.5f)
                {
                    hits++;
                    hitsAt.Add(s);
                    if (Environment.GetEnvironmentVariable("AIBENCH_HITS") != null)
                    {
                        var wn = car.WallNormal;
                        var left = drive.Pilot.LeftAt(s);
                        Console.WriteLine($"[Hit] s {s:F0} lat {lat:+0.00;-0.00} {car.SpeedKmh:F0} km/h β {car.SlipAngle * Deg:+0;-0}° impact {car.WallImpactSpeed * 3.6f:F0} km/h wall on the {(Vector3.Dot(wn, left) < 0 ? "left" : "right")} {note?.Invoke()}");
                    }
                }
                lastHit = t;
            }
            while (ci < corners.Count && s > corners[ci].To + 20) runs[ci++].Done = true;
            for (var i = ci; i < corners.Count && corners[i].From - 20 <= s; i++)
            {
                ref var r = ref runs[i];
                var v = car.Velocity.Length() * 3.6f;
                if (!r.Reached) (r.Reached, r.Entry, r.Min, r.MinGap) = (true, v, v, float.MaxValue);
                if (s >= corners[i].From && s <= corners[i].To)
                {
                    var (rl, rr) = map.Room(s);
                    r.MinGap = MathF.Min(r.MinGap, corners[i].Dir > 0 ? rr + lat : rl - lat);
                }
                r.Time += Dt;
                r.Min = MathF.Min(r.Min, v);
                r.Exit = v;
                var b = MathF.Abs(car.SlipAngle) * Deg;
                r.MaxBeta = MathF.Max(r.MaxBeta, b);
                if (b > 10) r.SlideTime += Dt;
            }
            if (s >= goal) return new Result(t - started, hits, wallTicks, maxLat, runs, true, hitsAt, splits);
        }
        if (Environment.GetEnvironmentVariable("AIBENCH_HITS") != null)
        {
            var (s, lat) = drive.Pilot.Track(car.Position);
            Console.WriteLine($"[DNF] stuck at s {s:F0} lat {lat:+0.0;-0.0} {car.SpeedKmh:F0} km/h, pos {car.Position}, {note?.Invoke()}");
        }
        return new Result(t - MathF.Max(started, 0), hits, wallTicks, maxLat, runs, false, hitsAt, splits);
    }

    /// <summary>AIBENCH_BTRACE=from:to (s): a line every 0.125 s of each battle within that time.</summary>
    private static readonly (float, float)? BattleTrace = Environment.GetEnvironmentVariable("AIBENCH_BTRACE") is { } bt && bt.Split(':') is [var a, var b]
        ? (float.Parse(a, CultureInfo.InvariantCulture), float.Parse(b, CultureInfo.InvariantCulture)) : null;

    /// <summary>AIBENCH_TRACE=from:to (m along): a line every 0.1 s of each solo run within that stretch.</summary>
    private static readonly (float, float)? Trace = Environment.GetEnvironmentVariable("AIBENCH_TRACE") is { } tr && tr.Split(':') is [var a, var b]
        ? (float.Parse(a, CultureInfo.InvariantCulture), float.Parse(b, CultureInfo.InvariantCulture)) : null;

    /// <summary>Entry point; false when nothing ran.</summary>
    public static bool Run(Iso9660 iso, string mode, string[] filter, string? carFilter)
    {
        var courses = filter.Length > 0 ? [.. filter.Select(f => f.ToUpperInvariant())] : Courses;
        var cars = carFilter != null ? [carFilter] : Cars;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        switch (mode)
        {
            case "solo": SoloMatrix(iso, courses, cars); return true;
            case "drift": DriftReport(iso, courses, cars); return true;
            case "battle": Battles(iso, courses); return true;
            case "corners": CornerTable(iso, courses); return true;
            case "calibrate": Calibrate(iso, courses); return true;
            case "human": return Human(iso, filter);
        }
        Console.Error.WriteLine("--ai-bench solo|drift|battle|corners [KURS…] [--car X]");
        return false;
    }

    private static Drive Load(Iso9660 iso, string course, bool reverse, string car)
    {
        var quiet = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            var models = iso.OpenAfs("CDVD/DATA/MODEL/COURSE.AFS");
            return new Drive(iso, course + (models.Find(course + "_DAY.PAC") != null ? "_DAY" : "_NIT"), reverse, CarSpecs.All[car]);
        }
        finally { Console.SetOut(quiet); }
    }

    /// <summary>
    ///     human:&lt;file.rpl&gt;[,…]: a recorded player run (car 0) measured like the AI's, next to the pilot at skill 0.8 and
    ///     1.0 in the same car on the same course (the human reference for the skill scale).
    /// </summary>
    private static bool Human(Iso9660 iso, string[] files)
    {
        foreach (var file in files)
        {
            var replay = Touge.Replays.Replay.Load(file);
            var info = replay.Info;
            var c = info.Cars[0];
            var spec = new Settings { SteerAssist = c.SteerAssist, DriftAssist = c.DriftAssist }.Assisted(CarSpecs.All[c.Car]);
            var d = new Drive(iso, info.Course, info.Reverse, spec);
            var cs = Corners(d.Line);
            var player = new Touge.Replays.ReplayPlayer(replay, [d.Car], d.Ground);
            player.Seek(0);
            var human = Measure(d, cs, () => player.Step() ? replay.Input(player.Tick - 1, 0) : null);
            Console.WriteLine($"[Human] {Path.GetFileName(file)}: {info.Mode} {info.Course} {(info.Reverse ? "up" : "down")} {c.Car} assists S{c.SteerAssist}D{c.DriftAssist}, file time {info.Time:F2} s");
            Line(info.Course, info.Reverse, c.Car, -1, 0, human, new PilotStats(0, 0, 0, []), cs, null, "human");
            foreach (var style in new[] { new RivalStyle(0.5f, 0.5f, 0.3f), new RivalStyle(0.8f, 0.5f, 0.3f), new RivalStyle(1f, 0.5f, 0.3f), Human(d.Car.Spec) })
            {
                var (r, ps) = Solo(d, style, cs);
                var k = style.Skill;
                Line(info.Course, info.Reverse, c.Car, k, style.Drift, r, ps, cs, human, style.Mistakes == 0 ? "H" : "ai");
                // over the road both covered (the human's run may stop early: a battle decided), and the first 150 m off the start
                var n = Math.Min(human.Splits.Count, r.Splits.Count) - 1;
                var st = Math.Min((int)(150 / SplitStep), n);
                if (n > 0)
                    Console.WriteLine($"[Human]   vs {k:F1} covered {n * SplitStep:F0} m: human {human.Splits[n]:F2} s AI {r.Splits[n]:F2} s (Δt {human.Splits[n] - r.Splits[n]:+0.00;-0.00} s), start 150 m Δt {human.Splits[st] - r.Splits[st]:+0.00;-0.00} s");
                // per corner kind: the human's time through the corners against the AI's (+ = human slower)
                foreach (var g in cs.Select((x, i) => (x.Kind, i)).GroupBy(x => x.Kind))
                {
                    var ok = g.Where(x => human.Corners[x.i].Done && r.Corners[x.i].Done).ToList();
                    if (ok.Count == 0) continue;
                    Console.WriteLine($"[Human]   vs {k:F1} {g.Key,-8} Δt {ok.Sum(x => human.Corners[x.i].Time - r.Corners[x.i].Time),6:+0.0;-0.0} s over {ok.Count} " +
                                      $"(human β̄max {ok.Average(x => human.Corners[x.i].MaxBeta):F1}° slide {ok.Sum(x => human.Corners[x.i].SlideTime):F1} s, " +
                                      $"min km/h {ok.Average(x => human.Corners[x.i].Min):F0} vs {ok.Average(x => r.Corners[x.i].Min):F0})");
                }
            }
        }
        return files.Length > 0;
    }

    /// <summary>The cars each drivetrain class is calibrated with (<see cref="CornerCalibration"/>): light and heavy, front- and mid-engined (a hit in any of them counts).</summary>
    private static readonly (CornerCalibration.Class Class, string[] Cars)[] CalibrationCars =
        [(CornerCalibration.Class.Rwd, ["AE86T", "FD3S", "MR2", "S2000"]), (CornerCalibration.Class.Awd, ["R34", "EVO7", "GT-4"]), (CornerCalibration.Class.Ff, ["EK9", "INTGR"])];

    /// <summary>Penalty per wall hit in or after a bend, gain per pass for a metre of unused outside room, passes, and the factors' range.</summary>
    private const float CalHit = 0.08f, CalGain = 0.02f, CalRange = 0.15f;
    private const int CalPasses = 5;

    /// <summary>
    ///     calibrate: bakes <see cref="CornerCalibration"/>'s CornerGrip.csv (AIBENCH_OUT, default Kansei.Physics/CornerGrip.csv):
    ///     per course, direction and class both calibration cars as H, <see cref="CalPasses"/> passes, keeping the factors of the
    ///     pass with the fewest wall hits (then the shortest time).
    /// </summary>
    private static void Calibrate(Iso9660 iso, string[] courses)
    {
        var rows = new List<string> { $"# --ai-bench calibrate: course;class;line key;grip factor per bend (CourseMap.Corners)" };
        foreach (var course in courses)
        foreach (var rev in Directions)
        foreach (var (cls, cars) in CalibrationCars)
        {
            var drives = cars.Select(c => Load(iso, course, rev, c)).ToArray();
            var cs = Corners(drives[0].Line);
            var f = Enumerable.Repeat(1f, cs.Count).ToArray();
            (int Hits, float Time, float[] F) best = (int.MaxValue, 0, f);
            for (var pass = 0; pass <= CalPasses; pass++)
            {
                var quiet = Console.Out;
                Console.SetOut(TextWriter.Null);
                var runs = drives.Select(d => Solo(d, Human(d.Car.Spec), cs, cornerGrip: f).R).ToArray();
                Console.SetOut(quiet);
                var hits = runs.Sum(r => r.WallHits) + runs.Count(r => !r.Finished) * 10;
                var time = runs.Sum(r => r.Time);
                Console.WriteLine($"[Calibrate] {course} {(rev ? "up" : "down")} {cls} pass {pass}: {string.Join(" / ", runs.Select(r => r.TimeText))} s, hits {hits}");
                if (hits < best.Hits || hits == best.Hits && time < best.Time) best = (hits, time, f);
                if (pass == CalPasses) break;
                f = [.. f.Select((x, i) =>
                {
                    var hit = runs.Any(r => r.HitsAt.Any(a => a >= cs[i].From - 10 && a <= cs[i].To + 20) || !r.Finished);
                    var room = runs.Min(r => r.Corners[i].MinGap) - RacingLine.EdgeMargin;
                    return Math.Clamp(hit ? x - CalHit : x + CalGain * Math.Clamp(room, 0, 1), 1 - CalRange, 1 + CalRange);
                })];
            }
            rows.Add(CornerCalibration.Row($"{course} {(rev ? "up" : "down")}", cls, CornerCalibration.Key(drives[0].Line), best.F));
        }
        var path = Environment.GetEnvironmentVariable("AIBENCH_OUT") ?? "Kansei.Physics/CornerGrip.csv";
        File.WriteAllLines(path, rows);
        Console.WriteLine($"[Calibrate] {rows.Count - 1} rows → {path}");
    }

    private static void CornerTable(Iso9660 iso, string[] courses)
    {
        foreach (var course in courses)
        foreach (var rev in new[] { false, true })
        {
            var d = Load(iso, course, rev, "AE86T");
            var cs = Corners(d.Line);
            // road room either side of the line at the bend's middle (CourseMap.Room, 6 m at most): inside / outside
            var map = CourseMap.Of(d.Line, d.Ground, d.Car.SurfaceGrip);
            Console.WriteLine($"[Corners] {course} {(rev ? "up" : "down")} {d.Pilot.Length:F0} m: " +
                              string.Join(" ", cs.GroupBy(c => c.Kind).Select(g => $"{g.Key} {g.Count()}")) +
                              $", overtaking zones: braking {map.Zones.Count(z => z.Corner >= 0)}, straights {map.Zones.Count(z => z.Corner < 0)} ({map.Zones.Where(z => z.Corner < 0).Sum(z => z.To - z.From):F0} m)");
            var rooms = cs.Select(c =>
            {
                var (l, r) = map.Room((c.From + c.To) / 2);
                return c.Dir > 0 ? (In: l, Out: r) : (In: r, Out: l);
            }).ToList();
            foreach (var g in cs.Select((c, i) => (c, rooms[i])).GroupBy(x => x.c.Kind))
                Console.WriteLine($"[Corners]   {g.Key,-8} room inside mean {g.Average(x => x.Item2.In):F1} m (min {g.Min(x => x.Item2.In):F1}), outside mean {g.Average(x => x.Item2.Out):F1} m (min {g.Min(x => x.Item2.Out):F1})");
            if (Trace != null)
                foreach (var (c, i) in cs.Select((c, i) => (c, i)))
                    Console.WriteLine($"[Corners]   {c.From,6:F0}–{c.To,6:F0} m  r {c.Radius,5:F1} m  {c.Angle * Deg,4:F0}°  {(c.Dir > 0 ? "L" : "R")} {c.Kind}  room in {rooms[i].In:F1} out {rooms[i].Out:F1}");
        }
    }

    /// <summary>Skills of the matrix (AIBENCH_SKILLS=a,b,… replaces them; empty = only H).</summary>
    private static readonly float[] Skills = Environment.GetEnvironmentVariable("AIBENCH_SKILLS") is { } sk ? [.. sk.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, CultureInfo.InvariantCulture))] : [0.05f, 0.2f, 0.5f, 0.8f, 1f];

    /// <summary>Drift styles of the matrix (AIBENCH_DRIFTS=a,b,…; default 0.3, and 0.9 from skill 0.8). AIBENCH_NOH skips H.</summary>
    private static readonly float[]? Drifts = Environment.GetEnvironmentVariable("AIBENCH_DRIFTS") is { } dr ? [.. dr.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture))] : null;

    /// <summary>Directions of the matrix (AIBENCH_DIR=up|down, default both; up = the reverse line).</summary>
    private static readonly bool[] Directions = Environment.GetEnvironmentVariable("AIBENCH_DIR") switch { "up" => [true], "down" => [false], _ => [false, true] };

    /// <summary>Mistake factor of the matrix runs (AIBENCH_MISTAKES, default 1).</summary>
    private static readonly float Mistakes = float.Parse(Environment.GetEnvironmentVariable("AIBENCH_MISTAKES") ?? "1", CultureInfo.InvariantCulture);

    /// <summary>Drift style of the "good human" reference H per car: FR drifts the hairpins and tight bends, 4WD the hairpins, FF tucks.</summary>
    public static float HumanDrift(CarSpec spec) => spec.DriveFront <= 0 ? 0.9f : spec.DriveFront < 1 ? 0.7f : 0.5f;

    /// <summary>
    ///     H = the "good human" reference: the pilot on its own line at skill 1 (planned 1.45 g FR) with the car's drift
    ///     style, no mistakes.
    /// </summary>
    public static RivalStyle Human(CarSpec spec) => new(1, 0.5f, HumanDrift(spec), 0);

    private static void SoloMatrix(Iso9660 iso, string[] courses, string[] cars)
    {
        Console.WriteLine("[Bench] tag,course,dir,car,skill,drift,time_s,vs_H_pct,wall_hits,wall_ticks,max_lat_m,hairpin_maxbeta,hairpin_slide_s,tight_maxbeta,medium_maxbeta,drifts,aborts,mistakes");
        var summary = new List<(string Car, float Skill, float Drift, float Pct, int Hits, bool Finished, float Mistakes5Km)>();
        foreach (var course in courses)
        foreach (var rev in Directions)
        foreach (var car in cars)
        {
            var d = Load(iso, course, rev, car);
            var cs = Corners(d.Line);
            Result? h = null;
            if (Environment.GetEnvironmentVariable("AIBENCH_NOH") == null)
            {
                var (hr, hp) = Solo(d, Human(d.Car.Spec), cs);
                Line(course, rev, car, 1, HumanDrift(d.Car.Spec), hr, hp, cs, null, "H");
                h = hr.Finished ? hr : null;
            }
            var km = (d.Pilot.Length - d.Start) / 1000;
            foreach (var k in Skills)
            foreach (var drift in Drifts ?? [0.3f, 0.9f])
            {
                if (Drifts == null && drift > 0.5f && k < 0.8f) continue;
                var (r, p) = Solo(d, new RivalStyle(k, 0.5f, drift, Mistakes), cs);
                var pct = Line(course, rev, car, k, drift, r, p, cs, h, "run");
                summary.Add((car, k, drift, pct, r.WallHits, r.Finished, p.Mistakes * 5 / km));
                if (r.HitsAt.Count > 0)
                    Console.WriteLine($"[Hits] {course} {(rev ? "up" : "down")} {car} {k:F2}/{drift:F1}: " + string.Join(" ", r.HitsAt.Select(a =>
                        $"{a:F0}m({cs.FirstOrDefault(c => a >= c.From - 15 && a <= c.To + 25)?.Kind ?? "straight"})")));
            }
        }
        Console.WriteLine("[Summary] car skill drift: % slower than H mean/min/max, wall hits per run, finished, mistakes per 5 km");
        foreach (var g in summary.GroupBy(x => (x.Car, x.Skill, x.Drift)))
        {
            var ok = g.Where(x => float.IsFinite(x.Pct)).ToList();
            Console.WriteLine($"[Summary] {g.Key.Car,-6} {g.Key.Skill:F2} {g.Key.Drift:F1}: {(ok.Count > 0 ? ok.Average(x => x.Pct) : float.NaN),6:F1} {(ok.Count > 0 ? ok.Min(x => x.Pct) : float.NaN),6:F1} {(ok.Count > 0 ? ok.Max(x => x.Pct) : float.NaN),6:F1}" +
                              $"  hits {g.Average(x => x.Hits):F2} (max {g.Max(x => x.Hits)})  finished {g.Count(x => x.Finished)}/{g.Count()}  mistakes/5km {g.Average(x => x.Mistakes5Km):F1}");
        }
    }

    private static float Line(string course, bool rev, string car, float skill, float drift, Result r, PilotStats p, List<Corner> cs, Result? best, string tag)
    {
        var pct = best != null && r.Finished ? 100 * (r.Time / best.Time - 1) : float.NaN;
        string Beta(string kind)
        {
            var sel = cs.Select((c, i) => (c, r.Corners[i])).Where(x => x.c.Kind == kind && x.Item2.Reached).ToList();
            return sel.Count == 0 ? "-" : $"{sel.Average(x => x.Item2.MaxBeta):F1}";
        }
        var hpSlide = cs.Select((c, i) => (c, r.Corners[i])).Where(x => x.c.Kind == "hairpin").Sum(x => x.Item2.SlideTime);
        Console.WriteLine($"[Bench] {tag},{course},{(rev ? "up" : "down")},{car},{skill:F2},{drift:F1},{r.TimeText},{pct:F1},{r.WallHits},{r.WallTicks},{r.MaxLateral:F1},{Beta("hairpin")},{hpSlide:F1},{Beta("tight")},{Beta("medium")},{p.Drifts},{p.Aborts},{p.Mistakes}");
        return pct;
    }

    /// <summary>
    ///     drift: each drift style (Takumi 0.9, Keisuke 0.8, Ryosuke 0.6, Takeshi 0.1) at skill 0.8 against the same pilot with
    ///     drift 0 (grip): per planned drift corner the time, max body slip, exit speed, wall hits, spins.
    /// </summary>
    private static void DriftReport(Iso9660 iso, string[] courses, string[] cars)
    {
        var styles = new[] { ("takumi", 0.9f), ("keisuke", 0.8f), ("ryosuke", 0.6f), ("takeshi", 0.1f) };
        var rows = new List<(string Car, string Who, int Eligible, int Planned, int Held, float Beta, float BetaStd, float DtPct, float DExit, int Hits, int Spins, float TotalPct, int Aborts)>();
        foreach (var course in courses)
        foreach (var rev in Directions)
        foreach (var car in cars)
        {
            var d = Load(iso, course, rev, car);
            var cs = Corners(d.Line);
            var (grip, _) = Solo(d, new RivalStyle(0.8f, 0.5f, 0, 0), cs);
            foreach (var (who, drift) in styles)
            {
                var (r, p) = Solo(d, new RivalStyle(0.8f, 0.5f, drift, 0), cs);
                var planned = Enumerable.Range(0, cs.Count).Where(i => p.Plan[i] != null && r.Corners[i].Reached && grip.Corners[i].Reached).ToList();
                var eligible = cs.Count(c => c.Hairpin);
                var betas = planned.Select(i => r.Corners[i].MaxBeta).ToList();
                var mean = betas.Count > 0 ? betas.Average() : 0;
                var std = betas.Count > 1 ? MathF.Sqrt(betas.Average(b => (b - mean) * (b - mean))) : 0;
                var tD = planned.Sum(i => r.Corners[i].Time);
                var tG = planned.Sum(i => grip.Corners[i].Time);
                var hits = r.HitsAt.Count(a => planned.Any(i => a >= cs[i].From - 15 && a <= cs[i].To + 25));
                var spins = planned.Count(i => r.Corners[i].MaxBeta > 70);
                var total = r.Finished && grip.Finished ? 100 * (r.Time / grip.Time - 1) : float.NaN;
                rows.Add((car, who, eligible, planned.Count, p.Drifts, mean, std, tG > 0 ? 100 * (tD / tG - 1) : 0,
                    planned.Count > 0 ? planned.Average(i => r.Corners[i].Exit - grip.Corners[i].Exit) : 0, hits, spins, total, p.Aborts));
                Console.WriteLine($"[Drift] {course} {(rev ? "up" : "down")} {car} {who}: {r.TimeText} s ({total:+0.0;-0.0}% vs grip {grip.TimeText}), planned {planned.Count} " +
                                  $"held {p.Drifts} aborted {p.Aborts} (slip/inside/wall {string.Join("/", p.AbortWhy ?? [])}) faded {p.Faded}, β̄max {mean:F1}±{std:F1}°, corner time {rows[^1].DtPct:+0.0;-0.0}%, Δexit {rows[^1].DExit:+0;-0} km/h, hits in drifts {hits} (run {r.WallHits}), spins {spins}");
            }
        }
        Console.WriteLine("[Drift] summary per car/style: planned corners (hairpins on the courses), held, aborted, β̄max, corner time vs grip, Δexit, hits per drift, spins, run vs grip");
        foreach (var g in rows.GroupBy(x => (x.Car, x.Who)))
        {
            var n = g.Sum(x => x.Planned);
            var any = g.Where(x => x.Planned > 0).ToList();
            Console.WriteLine($"[Drift]   {g.Key.Car,-6} {g.Key.Who,-8} planned {n,4} (hairpins {g.Sum(x => x.Eligible)}) held {g.Sum(x => x.Held),4} aborted {g.Sum(x => x.Aborts),3}  β̄max {(any.Count > 0 ? any.Average(x => x.Beta) : 0),5:F1}±{(any.Count > 0 ? any.Average(x => x.BetaStd) : 0):F1}°" +
                              $"  corner time {(any.Count > 0 ? any.Average(x => x.DtPct) : 0),5:+0.0;-0.0}%  Δexit {(any.Count > 0 ? any.Average(x => x.DExit) : 0),4:+0;-0} km/h  hits/drift {(n > 0 ? g.Sum(x => x.Hits) / (float)n : 0):F3}  spins {g.Sum(x => x.Spins)}" +
                              $"  run {g.Where(x => float.IsFinite(x.TotalPct)).Select(x => x.TotalPct).DefaultIfEmpty(float.NaN).Average():+0.0;-0.0}%");
        }
    }

    /// <summary>
    ///     Rivals of the battle bench: five characters (their cars and styles) and the autopilot's own car at three skills
    ///     (pace deltas of ~5 % slower, ~2 % slower, ~1 % faster than the autopilot at 0.8).
    /// </summary>
    private static Rivals.Rival[] BenchRivals =>
    [
        .. new[] { "itsuki", "shingo", "takeshi", "keisuke", "takumi" }.Select(Rivals.Find),
        .. new[] { 0.5f, 0.65f, 1f }.Select(k => new Rivals.Rival($"ae86@{k:0.00}", "AE86", "", "AE86T", new RivalStyle(k, 0.5f, 0.5f))),
    ];

    /// <summary>AIBENCH_PLAYER=rival:&lt;skill&gt;: the player is the rival's twin (same car, same driver at that skill) instead of the autopilot.</summary>
    private static readonly float? Mirror = Environment.GetEnvironmentVariable("AIBENCH_PLAYER") is { } pl && pl.StartsWith("rival:")
        ? float.Parse(pl[6..], CultureInfo.InvariantCulture) : null;

    /// <summary>The twins of <see cref="Mirror"/>: a good human's drift style in the AE86T, FD3S and R34 at <paramref name="skill"/>.</summary>
    private static Rivals.Rival[] MirrorRivals(float skill) =>
        [.. new[] { "AE86T", "FD3S", "R34" }.Select(c => new Rivals.Rival($"{c}@{skill:0.00}", c, "", c, new RivalStyle(skill, 0.5f, HumanDrift(CarSpecs.All[c]))))];

    /// <summary>
    ///     battle: the autopilot (<see cref="BattleRun.Autopilot"/>, AE86T) against each <see cref="BenchRivals"/> rival, race and
    ///     lead/chase both ways, on every course and direction. Each pairing's pace delta comes from both cars alone on the course
    ///     (+ = the rival is slower); the summary scores the racecraft against it: a chaser ≥ 3 % faster passes within 90 s, a
    ///     slower one hardly ever, lead changes in close races, contacts (AIBENCH_CONTACTS=1 logs each).
    /// </summary>
    private static void Battles(Iso9660 iso, string[] courses)
    {
        Console.WriteLine("[Battle] course,dir,rival,rule,lead,pace_delta_pct,outcome,reason,at_s,gap_s,overtakes,player_passes,first_pass_s,contacts,max_impact_kmh,wall_player,wall_rival,respawns");
        var rows = new List<(string Rival, BattleRule Rule, int Lead, float Delta, BattleOutcome O, string Reason, float At, int Contacts, float Impact, int Overtakes, float FirstPass, int Respawns)>();
        var logContacts = Environment.GetEnvironmentVariable("AIBENCH_CONTACTS") != null;
        foreach (var course in courses)
        foreach (var rev in new[] { false, true })
        {
            var d = Load(iso, course, rev, "AE86T");
            var cs = Corners(d.Line);
            var quiet = Console.Out;
            Console.SetOut(TextWriter.Null);
            var (auto, _) = Solo(d, BattleRun.Autopilot, cs);
            Console.SetOut(quiet);
            foreach (var rival in Mirror is { } mk ? MirrorRivals(mk) : BenchRivals)
            {
                if (Environment.GetEnvironmentVariable("AIBENCH_RIVALS") is { } only && !only.Split(',').Contains(rival.Id)) continue;
                Console.SetOut(TextWriter.Null);
                d.ChangeCar(rival.Spec);
                var (solo, _) = Solo(d, rival.Style, cs, 1);
                d.ChangeCar(CarSpecs.All["AE86T"]);
                Console.SetOut(quiet);
                var delta = Mirror != null ? 0 : auto.Finished && solo.Finished ? 100 * (solo.Time / auto.Time - 1) : float.NaN;
                foreach (var (rule, lead) in new[] { (BattleRule.Race, 1), (BattleRule.LeadChase, 1), (BattleRule.LeadChase, 0) })
                {
                    if (Environment.GetEnvironmentVariable("AIBENCH_RULES") is { } rules && !rules.Split(',').Contains($"{rule}{lead}")) continue;
                    Console.SetOut(TextWriter.Null);
                    if (Mirror != null) d.ChangeCar(rival.Spec); // the player the same car and driver as the rival
                    var race = BattleRun.Create(d, new BattleSetup(rival, rule, lead), new AiDriver(new RivalPilot(d.Line, Mirror != null ? rival.Style : BattleRun.Autopilot)), "AUTO");
                    Console.SetOut(quiet);
                    foreach (var rc in race.Cars) if (rc.Driver is AiDriver ai) RevUp(rc.Vehicle, ai.Pilot); // as in the game's countdown
                    var b = race.Battle!;
                    if (Environment.GetEnvironmentVariable("AIBENCH_ATTEMPTS") != null || BattleTrace != null)
                        foreach (var (car, who) in new[] { (race.Cars[0], "P"), (race.Cars[1], "R") })
                            ((AiDriver)car.Driver).Pilot.Log = Environment.GetEnvironmentVariable("AIBENCH_ATTEMPTS") == null ? _ => { }
                                : m => Console.WriteLine($"[Attempt] {course} {(rev ? "up" : "down")} {rival.Id} {rule}{lead} {who} t {race.Time:F1} {m}");
                    float firstPass = -1;
                    var overtakes = 0;
                    for (var n = 0; n < 600 / Dt && b.Outcome == BattleOutcome.None; n++)
                    {
                        var touches = race.Contacts;
                        race.Tick(Dt);
                        if (b.Overtakes != overtakes && firstPass < 0 && b.Time > b.StartGrace) firstPass = b.Time;
                        overtakes = b.Overtakes;
                        if (BattleTrace is var (t0, t1) && race.Time >= t0 && race.Time <= t1 && (int)(race.Time / Dt) % 15 == 0)
                        {
                            RaceCar p = race.Cars[0], r = race.Cars[1];
                            string Who(RaceCar x)
                            {
                                var pl = ((AiDriver)x.Driver).Pilot;
                                return $"{pl.State,-8}{(pl.Drifting ? "/d" : "  ")} {x.Along,7:F1}/{x.Lateral,5:+0.00;-0.00} {x.Vehicle.SpeedKmh,3:F0} tgt {pl.Pilot.TargetSpeed * 3.6f,3:F0} in {x.Input.Throttle:F1}/{x.Input.Brake:F1} {pl.Note}";
                            }
                            Console.WriteLine($"[BTrace] {race.Time,6:F2} Δs {r.Along - p.Along,6:+0.0;-0.0}  P {Who(p)}  |  R {Who(r)}");
                        }
                        if (logContacts && race.Contacts != touches && race.LastContact is { } c)
                        {
                            RaceCar p = race.Cars[0], r = race.Cars[1];
                            string Who(RaceCar x) => $"{((AiDriver)x.Driver).Pilot.State}{(((AiDriver)x.Driver).Pilot.Drifting ? "/drift" : "")} {x.Along:F1}/{x.Lateral:+0.00;-0.00} {x.Vehicle.SpeedKmh:F0} km/h in {x.Input.Throttle:F1}/{x.Input.Brake:F1}";
                            Console.WriteLine($"[Contact] {course} {(rev ? "up" : "down")} {rival.Id} {rule} lead {lead}: t {race.Time:F2} {c.ImpactSpeed * 3.6f:F1} km/h  P {Who(p)}  R {Who(r)}  Δs {r.Along - p.Along:+0.0;-0.0} Δlat {r.Lateral - p.Lateral:+0.00;-0.00}");
                        }
                    }
                    RivalPilot pa = ((AiDriver)race.Cars[0].Driver).Pilot, ra = ((AiDriver)race.Cars[1].Driver).Pilot;
                    Console.WriteLine($"[Battle] {course},{(rev ? "up" : "down")},{rival.Id},{rule},{(lead == 0 ? "player" : "rival")},{delta:F1},{b.Outcome},{b.Reason},{b.DecidedAt:F1},{b.DecidedGap:+0.00;-0.00},{b.Overtakes},{b.PlayerPasses},{firstPass:F1}," +
                                      $"{race.Contacts},{race.MaxImpact * 3.6f:F1},{race.Cars[0].WallTicks},{race.Cars[1].WallTicks},{race.Cars[0].Respawns + race.Cars[1].Respawns},attempts {pa.Attempts}/{pa.Commits} {ra.Attempts}/{ra.Commits}");
                    rows.Add((rival.Id, rule, lead, delta, b.Outcome, b.Reason, b.DecidedAt, race.Contacts, race.MaxImpact * 3.6f, b.Overtakes, firstPass, race.Cars[0].Respawns + race.Cars[1].Respawns));
                    if (Mirror != null) d.ChangeCar(CarSpecs.All["AE86T"]);
                }
            }
        }
        Console.WriteLine("[Battle] summary (player = autopilot 0.8 in the AE86T; pace Δ + = rival slower alone): wins/losses/draws, contacts, lead changes");
        foreach (var g in rows.GroupBy(o => (o.Rival, o.Rule, o.Lead)))
            Console.WriteLine($"[Battle]   {g.Key.Rival,-9} {g.Key.Rule,-9} {(g.Key.Rule == BattleRule.Race ? "" : g.Key.Lead == 0 ? "player leads" : "rival leads"),-12} pace Δ {g.Where(o => float.IsFinite(o.Delta)).Select(o => o.Delta).DefaultIfEmpty(float.NaN).Average(),5:+0.0;-0.0}%  " +
                              $"W {g.Count(o => o.O == BattleOutcome.Win),2} L {g.Count(o => o.O == BattleOutcome.Lose),2} D {g.Count(o => o.O == BattleOutcome.Draw),2}  contacts {g.Sum(o => o.Contacts),3} (max {g.Max(o => o.Impact):F0} km/h)  lead changes {g.Sum(o => o.Overtakes),3}  respawns {g.Sum(o => o.Respawns)}");
        // racecraft targets: lead/chase by the chaser's pace advantage (chaser = rival when the player leads)
        var chases = rows.Where(o => o.Rule == BattleRule.LeadChase && float.IsFinite(o.Delta))
            .Select(o => (Adv: o.Lead == 0 ? -o.Delta : o.Delta, Passed: o.Reason == "OVERTAKE", In90: o.Reason == "OVERTAKE" && o.FirstPass >= 0 && o.FirstPass <= 100 + 1.5f, o.Contacts)).ToList();
        void Rate(string what, Func<float, bool> sel)
        {
            var c = chases.Where(x => sel(x.Adv)).ToList();
            Console.WriteLine($"[Battle] chaser {what,-22}: {c.Count,3} runs, passed {c.Count(x => x.Passed),3} ({(c.Count > 0 ? 100f * c.Count(x => x.Passed) / c.Count : 0):F0} %), within 90 s {c.Count(x => x.In90),3} ({(c.Count > 0 ? 100f * c.Count(x => x.In90) / c.Count : 0):F0} %)");
        }
        Rate("≥ 3 % faster", a => a >= 3);
        Rate("1…3 % faster", a => a is >= 1 and < 3);
        Rate("within ±1 %", a => MathF.Abs(a) < 1);
        Rate("slower", a => a <= -1);
        var close = rows.Where(o => o.Rule == BattleRule.Race && MathF.Abs(o.Delta) <= 2).ToList();
        Console.WriteLine($"[Battle] races within 2 %: {close.Count}, lead changes per race {(close.Count > 0 ? close.Average(o => o.Overtakes) : 0):F2} (1–4 wanted), in 1–4: {close.Count(o => o.Overtakes is >= 1 and <= 4)}");
        Console.WriteLine($"[Battle] contacts {rows.Sum(o => o.Contacts)} in {rows.Count} battles ({(float)rows.Sum(o => o.Contacts) / rows.Count * 5:F2} per 5), over 15 km/h {rows.Count(o => o.Impact > 15)}, respawns {rows.Sum(o => o.Respawns)}");
    }
}
