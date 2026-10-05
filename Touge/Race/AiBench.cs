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
    private const float Dt = Drive.Dt, Deg = 180 / MathF.PI;

    /// <summary>The 9 mountain courses (day versions).</summary>
    public static readonly string[] Courses = ["AKINA", "AKAGI", "IROHA", "MYOUGI", "USUI", "HAPPOU", "SHOMARU", "MOMIJI", "SHIONA"];

    public static readonly string[] Cars = ["AE86T", "R34", "EK9"];

    /// <summary>Bends of <paramref name="line"/> (<see cref="CourseMap.FindCorners"/>).</summary>
    public static List<Corner> Corners(Vector3[] line) => CourseMap.FindCorners(line);

    private static Vector2 Xz(Vector3 v) => new(v.X, v.Z);

    /// <summary>Per-corner measurement of one run.</summary>
    public struct CornerRun
    {
        public float Time, Entry, Min, Exit, MaxBeta, SlideTime;
        public bool Reached;
    }

    public sealed record Result(float Time, int WallHits, int WallTicks, float MaxLateral, CornerRun[] Corners, bool Finished, List<float> HitsAt)
    {
        public string TimeText => Finished ? Time.ToString("F2", CultureInfo.InvariantCulture) : "DNF";
    }

    /// <summary>What the pilot did in a run: drifts held/aborted, mistakes, its corner plan.</summary>
    public sealed record PilotStats(int Drifts, int Aborts, int Mistakes, DriftController.Entry?[] Plan, int[]? AbortWhy = null);

    /// <summary>One run alone from the spawn to the goal (or 600 s) with <paramref name="style"/>.</summary>
    public static (Result R, PilotStats P) Solo(Drive drive, RivalStyle style, List<Corner> corners, int seed = 0)
    {
        drive.ResetTo(0);
        var car = drive.Car;
        var pilot = new RivalPilot(drive.Line, style) { Seed = seed };
        if (Environment.GetEnvironmentVariable("LATGAIN") is { } lg) pilot.Pilot.LateralGain = float.Parse(lg, CultureInfo.InvariantCulture);
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
        var r = Measure(drive, corners, () =>
        {
            var input = pilot.Drive(car, drive.Ground, [], Dt);
            car.Step(input, drive.Ground, Dt);
            return input;
        }, () => pilot.Pilot.TargetSpeed, () =>
        {
            var (s, _) = pilot.Pilot.Track(car.Position);
            var rl = pilot.Racing!;
            var (lo, hi) = rl.BoundsAt(s);
            var (l, r) = rl.Map.Room(s);
            return (pilot.Drifting ? $"drift {pilot.Drift.State} cmd {pilot.Drift.Command * Deg:F0} {pilot.Drift.Debug}" :pilot.State.ToString()) + $" plan {rl.OffsetAt(s):+0.00;-0.00} [{lo:+0.0;-0.0},{hi:+0.0;-0.0}] room L {l:F2} R {r:F2} κ {rl.CurvatureAt(s):+0.000;-0.000}";
        });
        return (r, new PilotStats(pilot.Drift.Held, pilot.Drift.Aborted, pilot.Mistakes, [.. Enumerable.Range(0, corners.Count).Select(pilot.DriftAt)], [.. pilot.Drift.AbortWhy]));
    }

    /// <summary>
    ///     Measures a run of <paramref name="drive"/>'s car: <paramref name="tick"/> advances it one tick and returns the input
    ///     it got (null = no more ticks); time from the start line to the goal, wall hits, widest lateral, per-corner numbers.
    /// </summary>
    public static Result Measure(Drive drive, List<Corner> corners, Func<VehicleInput?> tick, Func<float>? target = null, Func<string>? note = null)
    {
        var car = drive.Car;
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
            while (ci < corners.Count && s > corners[ci].To + 20) ci++;
            for (var i = ci; i < corners.Count && corners[i].From - 20 <= s; i++)
            {
                ref var r = ref runs[i];
                var v = car.Velocity.Length() * 3.6f;
                if (!r.Reached) (r.Reached, r.Entry, r.Min) = (true, v, v);
                r.Time += Dt;
                r.Min = MathF.Min(r.Min, v);
                r.Exit = v;
                var b = MathF.Abs(car.SlipAngle) * Deg;
                r.MaxBeta = MathF.Max(r.MaxBeta, b);
                if (b > 10) r.SlideTime += Dt;
            }
            if (s >= goal) return new Result(t - started, hits, wallTicks, maxLat, runs, true, hitsAt);
        }
        if (Environment.GetEnvironmentVariable("AIBENCH_HITS") != null)
        {
            var (s, lat) = drive.Pilot.Track(car.Position);
            Console.WriteLine($"[DNF] stuck at s {s:F0} lat {lat:+0.0;-0.0} {car.SpeedKmh:F0} km/h, pos {car.Position}, {note?.Invoke()}");
        }
        return new Result(t - MathF.Max(started, 0), hits, wallTicks, maxLat, runs, false, hitsAt);
    }

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
                // per corner kind: the human's time through the corners against the AI's (+ = human slower)
                foreach (var g in cs.Select((x, i) => (x.Kind, i)).GroupBy(x => x.Kind))
                {
                    var ok = g.Where(x => human.Corners[x.i].Reached && r.Corners[x.i].Reached).ToList();
                    if (ok.Count == 0) continue;
                    Console.WriteLine($"[Human]   vs {k:F1} {g.Key,-8} Δt {ok.Sum(x => human.Corners[x.i].Time - r.Corners[x.i].Time),6:+0.0;-0.0} s over {ok.Count} " +
                                      $"(human β̄max {ok.Average(x => human.Corners[x.i].MaxBeta):F1}° slide {ok.Sum(x => human.Corners[x.i].SlideTime):F1} s, " +
                                      $"min km/h {ok.Average(x => human.Corners[x.i].Min):F0} vs {ok.Average(x => r.Corners[x.i].Min):F0})");
                }
            }
        }
        return files.Length > 0;
    }

    private static void CornerTable(Iso9660 iso, string[] courses)
    {
        foreach (var course in courses)
        foreach (var rev in new[] { false, true })
        {
            var d = Load(iso, course, rev, "AE86T");
            var cs = Corners(d.Line);
            Console.WriteLine($"[Corners] {course} {(rev ? "up" : "down")} {d.Pilot.Length:F0} m: " +
                              string.Join(" ", cs.GroupBy(c => c.Kind).Select(g => $"{g.Key} {g.Count()}")));
            // road room either side of the line at the bend's middle (CourseMap.Room, 6 m at most): inside / outside
            var map = CourseMap.Of(d.Line, d.Ground, d.Car.SurfaceGrip);
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

    /// <summary>Skills of the matrix (AIBENCH_SKILLS=a,b,… replaces them).</summary>
    private static readonly float[] Skills = Environment.GetEnvironmentVariable("AIBENCH_SKILLS") is { } sk ? [.. sk.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture))] : [0.05f, 0.2f, 0.5f, 0.8f, 1f];

    /// <summary>Drift styles of the matrix (AIBENCH_DRIFTS=a,b,…; default 0.3, and 0.9 from skill 0.8). AIBENCH_NOH skips H.</summary>
    private static readonly float[]? Drifts = Environment.GetEnvironmentVariable("AIBENCH_DRIFTS") is { } dr ? [.. dr.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture))] : null;

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
        foreach (var rev in new[] { false, true })
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
        foreach (var rev in new[] { false, true })
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
                                  $"held {p.Drifts} aborted {p.Aborts} (slip/inside/wall/none {string.Join("/", p.AbortWhy ?? [])}), β̄max {mean:F1}±{std:F1}°, corner time {rows[^1].DtPct:+0.0;-0.0}%, Δexit {rows[^1].DExit:+0;-0} km/h, hits in drifts {hits} (run {r.WallHits}), spins {spins}");
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

    private static void Battles(Iso9660 iso, string[] courses)
    {
        string[] rivals = ["itsuki", "shingo", "takeshi", "keisuke", "takumi"];
        Console.WriteLine("[Battle] course,dir,rival,rule,lead,outcome,reason,at_s,gap_s,overtakes,player_passes,contacts,max_impact_kmh,wall_player,wall_rival,respawns");
        var outcomes = new List<(string Rival, BattleRule Rule, int Lead, BattleOutcome O, int Contacts, int Overtakes)>();
        foreach (var course in courses)
        foreach (var rev in new[] { false, true })
        foreach (var id in rivals)
        foreach (var (rule, lead) in new[] { (BattleRule.Race, 1), (BattleRule.LeadChase, 1), (BattleRule.LeadChase, 0) })
        {
            var d = Load(iso, course, rev, "AE86T");
            var quiet = Console.Out;
            Console.SetOut(TextWriter.Null);
            var race = BattleRun.Create(d, new BattleSetup(Rivals.Find(id), rule, lead), new AiDriver(new RivalPilot(d.Line, BattleRun.Autopilot)), "AUTO");
            var b = race.Battle!;
            for (var n = 0; n < 600 / Dt && b.Outcome == BattleOutcome.None; n++) race.Tick(Dt);
            Console.SetOut(quiet);
            Console.WriteLine($"[Battle] {course},{(rev ? "up" : "down")},{id},{rule},{(lead == 0 ? "player" : "rival")},{b.Outcome},{b.Reason},{b.DecidedAt:F1},{b.DecidedGap:+0.00;-0.00},{b.Overtakes},{b.PlayerPasses}," +
                              $"{race.Contacts},{race.MaxImpact * 3.6f:F1},{race.Cars[0].WallTicks},{race.Cars[1].WallTicks},{race.Cars[0].Respawns + race.Cars[1].Respawns}");
            outcomes.Add((id, rule, lead, b.Outcome, race.Contacts, b.Overtakes));
        }
        Console.WriteLine("[Battle] summary (player = autopilot 0.8 in the AE86T): wins/losses/draws, contacts, lead changes");
        foreach (var g in outcomes.GroupBy(o => (o.Rival, o.Rule, o.Lead)))
            Console.WriteLine($"[Battle]   {g.Key.Rival,-8} {g.Key.Rule,-9} {(g.Key.Rule == BattleRule.Race ? "" : g.Key.Lead == 0 ? "player leads" : "rival leads"),-12} " +
                              $"W {g.Count(o => o.O == BattleOutcome.Win),2} L {g.Count(o => o.O == BattleOutcome.Lose),2} D {g.Count(o => o.O == BattleOutcome.Draw),2}  contacts {g.Sum(o => o.Contacts),3}  lead changes {g.Sum(o => o.Overtakes),3}");
    }
}
