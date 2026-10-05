using System.Globalization;
using System.Numerics;
using Kansei.Physics;
using Touge.Formats;
using Touge.Ui;

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

    /// <summary>A bend of the line: from/to (m along), tightest radius, heading change (rad), +1 left / −1 right.</summary>
    public sealed record Corner(float From, float To, float Radius, float Angle, int Dir)
    {
        public string Kind => Radius < 25 && Angle > 1.6f ? "hairpin" : Radius < 45 ? "tight" : Radius < 90 ? "medium" : "fast";
    }

    /// <summary>
    ///     Bends of <paramref name="line"/>: runs of points with radius under 120 m (Menger curvature over ±2 points like
    ///     <see cref="LinePilot"/>), gaps under 12 m merged, at least 20° of heading change.
    /// </summary>
    public static List<Corner> Corners(Vector3[] line)
    {
        var n = line.Length;
        var along = new float[n];
        for (var i = 1; i < n; i++) along[i] = along[i - 1] + Xz(line[i] - line[i - 1]).Length();
        var k = new float[n];
        for (var i = 0; i < n; i++)
        {
            Vector2 a = Xz(line[Math.Max(i - 2, 0)]), b = Xz(line[i]), c = Xz(line[Math.Min(i + 2, n - 1)]);
            var den = Vector2.Distance(a, b) * Vector2.Distance(b, c) * Vector2.Distance(a, c);
            var cross = (b - a).X * (c - a).Y - (b - a).Y * (c - a).X;
            k[i] = den < 1e-6f ? 0 : -2 * cross / den; // + = left, as LinePilot's bend
        }
        var corners = new List<Corner>();
        var j = 0;
        while (j < n)
        {
            if (MathF.Abs(k[j]) < 1 / 120f) { j++; continue; }
            var dir = MathF.Sign(k[j]);
            var start = j;
            var end = j;
            while (j < n && (MathF.Sign(k[j]) == dir && MathF.Abs(k[j]) >= 1 / 120f || along[j] - along[end] < 12 && MathF.Sign(k[j]) != -dir))
            {
                if (MathF.Abs(k[j]) >= 1 / 120f && MathF.Sign(k[j]) == dir) end = j;
                j++;
            }
            float angle = 0, maxK = 0;
            for (var i = start; i <= end; i++)
            {
                var ds = i + 1 < n ? along[i + 1] - along[i] : 0;
                angle += MathF.Abs(k[i]) * ds;
                maxK = MathF.Max(maxK, MathF.Abs(k[i]));
            }
            if (angle > 20 / Deg) corners.Add(new Corner(along[start], along[end], 1 / maxK, angle, (int)dir));
            j = end + 1;
        }
        return corners;
    }

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

    /// <summary>Parameters of the drift overlay (<see cref="DriftOverlay"/>).</summary>
    public sealed record DriftParams(float MaxRadius, float Handbrake, float BetaTarget, float Gain, float ExitLead, float Overspeed = 1.1f, float EntryLead = 8)
    {
        public override string ToString() => FormattableString.Invariant($"in{EntryLead:+0;-0}m hb{Handbrake:F2}s β{BetaTarget * Deg:F0}° k{Gain:F1} aD {Overspeed:F2}g exit{ExitLead:F0}m");
    }

    /// <summary>
    ///     Prototype drift controller on top of the pilot (measurement only, not tuned: ~0.5–1 wall hit per drifted hairpin),
    ///     for bends tighter than <see cref="DriftParams.MaxRadius"/>: approach = brake to the drift speed √(a·r) with a =
    ///     <see cref="DriftParams.Overspeed"/> g; entry = handbrake (<see cref="DriftParams.Handbrake"/> s, at least 0.5 s
    ///     under 15 m/s) with full lock towards the bend from <see cref="DriftParams.EntryLead"/> m before it; hold = the
    ///     steering holds a body slip command (P on β, ×1.3 for the P controller's droop), the command integrates the error
    ///     of the travel's path curvature against the line's (plus 0.01/m per metre wide, 0.05/m per rad pointing out) with
    ///     gain <see cref="DriftParams.Gain"/>, throttle 0.45…1 on the drift speed (the drift layer needs &gt; 0.3); under 6°
    ///     command or <see cref="DriftParams.ExitLead"/> m before the bend's end the pilot takes over (its traction aid = exit).
    /// </summary>
    public sealed class DriftOverlay(DriftParams p, List<Corner> corners)
    {
        private int _done = -1;
        private float _hbUntil = -1, _t, _beta, _heading;

        public VehicleInput Apply(VehicleInput pilot, Vehicle car, LinePilot line, float s, float lat, float target, float dt)
        {
            _t += dt;
            var c = corners.FindIndex(x => s >= x.From - p.EntryLead - 80 && s < x.To + 5);
            if (c < 0) return pilot;
            var corner = corners[c];
            if (corner.Radius > p.MaxRadius || corner.Angle < 0.8f) return pilot;
            var v = car.Velocity.Length();
            // drift speed: Overspeed = lateral grip of the drift in g (the probe: AE86 ~1.5 g at R 10–25 m)
            var vd = MathF.Sqrt(p.Overspeed * 9.81f * corner.Radius);
            var turnIn = corner.From - p.EntryLead;
            if (s < turnIn)
            {
                // approach: brake to the drift speed at the turn-in (0.6 g), the pilot steers
                var want = MathF.Sqrt(vd * vd + 2 * 0.6f * 9.81f * (turnIn - s));
                if (v <= want) return pilot.Brake > 0 && v < want - 1 ? pilot with { Brake = 0, Throttle = 0.3f } : pilot;
                return pilot with { Throttle = 0, Brake = Math.Clamp((v - want) * 0.5f + 0.3f, 0, 1) };
            }
            if (_done != c)
            {
                _done = c;
                _hbUntil = _t + (v < 15 ? MathF.Max(p.Handbrake, 0.5f) : p.Handbrake);
                (_beta, _heading) = (p.BetaTarget, MathF.Atan2(car.Velocity.X, car.Velocity.Z));
            }
            // path curvature of the travel (1/m, + = towards the bend) since the last tick
            var h = MathF.Atan2(car.Velocity.X, car.Velocity.Z);
            var dh = MathF.IEEERemainder(h - _heading, MathF.Tau);
            _heading = h;
            var kappa = dh / MathF.Max(v * dt, 0.01f) * corner.Dir; // a left turn (towards +X) raises atan2(X, Z)
            if (s > corner.To - p.ExitLead) return pilot;
            int dir = corner.Dir;
            if (_t < _hbUntil) return new VehicleInput(0.5f, 0, -dir, Handbrake: true);
            // heading error: travel direction against the line's tangent, + = pointing out of the bend
            var tangent = line.PointAt(s + 3) - line.PointAt(s - 3);
            var travel = car.Velocity;
            var cross = tangent.X * travel.Z - tangent.Z * travel.X;
            var psi = MathF.Atan2(cross, tangent.X * travel.X + tangent.Z * travel.Z);
            var outPsi = psi * dir; // cross < 0: travel left of the tangent
            var wide = -lat * dir; // metres outside the line
            // curvature wanted: the line's + back towards it (wide of it / pointing out of the bend = tighter)
            var kWant = MathF.Abs(line.BendAhead(s - 5, 10).Curvature) + 0.01f * wide + 0.05f * outPsi;
            var err = kWant - kappa;
            _beta = Math.Clamp(_beta + p.Gain * err * dt, 0, 40 / Deg);
            var cmd = MathF.Min(_beta + p.Gain / 4 * err, 40 / Deg);
            if (cmd < 6 / Deg) return pilot; // turning too tight for a drift: grip (the pilot's traction aid catches it)
            var steer = Math.Clamp(dir * 3 * (car.SlipAngle * dir - 1.3f * cmd), -1, 1);
            var throttle = Math.Clamp(0.75f + 0.4f * (vd - v), 0.45f, 1);
            return new VehicleInput(throttle, 0, steer);
        }
    }

    /// <summary>One run alone from the spawn to the goal (or 600 s) with <paramref name="style"/>, optionally with a drift overlay.</summary>
    public static Result Solo(Drive drive, RivalStyle style, List<Corner> corners, DriftParams? drift = null)
    {
        drive.ResetTo(0);
        var car = drive.Car;
        var pilot = new RivalPilot(drive.Line, style);
        var overlay = drift != null ? new DriftOverlay(drift, corners) : null;
        return Measure(drive, corners, () =>
        {
            var input = pilot.Drive(car, drive.Ground, [], Dt);
            var (s0, l0) = drive.Pilot.Track(car.Position);
            if (overlay != null) input = overlay.Apply(input, car, drive.Pilot, s0, l0, pilot.Pilot.TargetSpeed, Dt);
            car.Step(input, drive.Ground, Dt);
            return input;
        }, () => pilot.Pilot.TargetSpeed);
    }

    /// <summary>
    ///     Measures a run of <paramref name="drive"/>'s car: <paramref name="tick"/> advances it one tick and returns the input
    ///     it got (null = no more ticks); time from the start line to the goal, wall hits, widest lateral, per-corner numbers.
    /// </summary>
    public static Result Measure(Drive drive, List<Corner> corners, Func<VehicleInput?> tick, Func<float>? target = null)
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
                                  $"in thr {input.Throttle:F2} brk {input.Brake:F2} str {input.Steer:+0.00;-0.00} hb {(input.Handbrake ? 1 : 0)} wall {car.WallContacts} g{car.Gear}");
            if (started < 0) continue;
            maxLat = MathF.Max(maxLat, MathF.Abs(lat));
            if (car.WallContacts > 0)
            {
                wallTicks++;
                if (car.WallImpactSpeed > 1 && t - lastHit > 0.5f)
                {
                    hits++;
                    hitsAt.Add(s);
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
            case "drift": DriftSweep(iso, courses, cars); return true;
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
            Line(info.Course, info.Reverse, c.Car, -1, 0, human, cs, null, "human");
            foreach (var k in new[] { 0.6f, 0.8f, 1f })
            {
                var r = Solo(d, new RivalStyle(k, 0.5f, 0.3f), cs);
                Line(info.Course, info.Reverse, c.Car, k, 0.3f, r, cs, null, "ai");
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
            // road room either side of the line at the bend's middle (RivalPilot.Room, 4 m at most): inside / outside
            var rp = new RivalPilot(d.Line, default);
            var rooms = cs.Select(c =>
            {
                var (l, r) = rp.Room(d.Car, d.Ground, (c.From + c.To) / 2);
                return c.Dir > 0 ? (In: l, Out: r) : (In: r, Out: l);
            }).ToList();
            foreach (var g in cs.Select((c, i) => (c, rooms[i])).GroupBy(x => x.c.Kind))
                Console.WriteLine($"[Corners]   {g.Key,-8} room inside mean {g.Average(x => x.Item2.In):F1} m (min {g.Min(x => x.Item2.In):F1}), outside mean {g.Average(x => x.Item2.Out):F1} m (min {g.Min(x => x.Item2.Out):F1})");
            if (Trace != null)
                foreach (var (c, i) in cs.Select((c, i) => (c, i)))
                    Console.WriteLine($"[Corners]   {c.From,6:F0}–{c.To,6:F0} m  r {c.Radius,5:F1} m  {c.Angle * Deg,4:F0}°  {(c.Dir > 0 ? "L" : "R")} {c.Kind}  room in {rooms[i].In:F1} out {rooms[i].Out:F1}");
        }
    }

    /// <summary>Skills of the matrix and the reference sweep (planned grip above the AI's range).</summary>
    private static readonly float[] Skills = Environment.GetEnvironmentVariable("AIBENCH_SKILLS") is { } sk ? [.. sk.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture))] : [0f, 0.3f, 0.6f, 0.8f, 1f], RefSkills = [1f, 1.5f, 2f, 2.5f, 3f, 3.5f, 4f, 4.5f, 5f];

    private static void SoloMatrix(Iso9660 iso, string[] courses, string[] cars)
    {
        Console.WriteLine("[Bench] course,dir,car,skill,drift,time_s,vs_ref_pct,wall_hits,wall_ticks,max_lat_m,hairpin_maxbeta,hairpin_slide_s,tight_maxbeta,medium_maxbeta");
        var summary = new List<(string Car, float Skill, float Pct, int Hits)>();
        foreach (var course in courses)
        foreach (var rev in new[] { false, true })
        foreach (var car in cars)
        {
            var d = Load(iso, course, rev, car);
            var cs = Corners(d.Line);
            var runs = new List<(float Skill, float Drift, Result R)>();
            foreach (var k in Skills)
            foreach (var drift in new[] { 0.3f, 0.9f })
                if (drift < 0.5f || k == 0.8f)
                    runs.Add((k, drift, Solo(d, new RivalStyle(k, 0.5f, drift), cs)));
            var refs = RefSkills.Select(k => (Skill: k, R: Solo(d, new RivalStyle(k, 0.5f, 0.3f), cs))).ToList();
            // the reference: fastest finished run over higher planned grip that hits the walls at most once more than the
            // cleanest run (some lines graze a wall at any pace)
            var minHits = runs.Select(x => x.R).Concat(refs.Select(x => x.R)).Where(r => r.Finished).Select(r => r.WallHits).DefaultIfEmpty(99).Min();
            var (bestSkill, best) = refs.Where(x => x.R.Finished && x.R.WallHits <= minHits + 1)
                .OrderBy(x => x.R.Time).FirstOrDefault();
            foreach (var (k, r) in refs) Line(course, rev, car, k, 0.3f, r, cs, best, "ref");
            Console.WriteLine($"[Ref] {course} {(rev ? "up" : "down")} {car}: {best?.TimeText ?? "-"} s (skill {bestSkill:F1}, hits {best?.WallHits})");
            foreach (var (k, drift, r) in runs)
            {
                var pct = Line(course, rev, car, k, drift, r, cs, best, "run");
                if (drift < 0.5f) summary.Add((car, k, pct, r.WallHits));
                if (k == 0.8f && drift < 0.5f && r.HitsAt.Count > 0)
                    Console.WriteLine($"[Hits] {course} {(rev ? "up" : "down")} {car} 0.8: " + string.Join(" ", r.HitsAt.Select(a =>
                        $"{a:F0}m({cs.FirstOrDefault(c => a >= c.From - 15 && a <= c.To + 25)?.Kind ?? "straight"})")));
            }
        }
        Console.WriteLine("[Summary] car skill: mean/min/max % slower than reference, wall hits per run");
        foreach (var g in summary.GroupBy(x => (x.Car, x.Skill)))
        {
            var ok = g.Where(x => float.IsFinite(x.Pct)).ToList();
            Console.WriteLine($"[Summary] {g.Key.Car,-6} {g.Key.Skill:F1}: {ok.Average(x => x.Pct),6:F1} {ok.Min(x => x.Pct),6:F1} {ok.Max(x => x.Pct),6:F1}  ({ok.Count}/{g.Count()} runs)  hits {g.Average(x => x.Hits):F1}");
        }
    }

    private static float Line(string course, bool rev, string car, float skill, float drift, Result r, List<Corner> cs, Result? best, string tag)
    {
        var pct = best != null && r.Finished ? 100 * (r.Time / best.Time - 1) : float.NaN;
        string Beta(string kind)
        {
            var sel = cs.Select((c, i) => (c, r.Corners[i])).Where(x => x.c.Kind == kind && x.Item2.Reached).ToList();
            return sel.Count == 0 ? "-" : $"{sel.Average(x => x.Item2.MaxBeta):F1}";
        }
        var hpSlide = cs.Select((c, i) => (c, r.Corners[i])).Where(x => x.c.Kind == "hairpin").Sum(x => x.Item2.SlideTime);
        Console.WriteLine($"[Bench] {tag},{course},{(rev ? "up" : "down")},{car},{skill:F1},{drift:F1},{r.TimeText},{pct:F1},{r.WallHits},{r.WallTicks},{r.MaxLateral:F1},{Beta("hairpin")},{hpSlide:F1},{Beta("tight")},{Beta("medium")}");
        return pct;
    }

    private static void DriftSweep(Iso9660 iso, string[] courses, string[] cars)
    {
        var sets = new List<DriftParams>();
        if (Environment.GetEnvironmentVariable("AIBENCH_DRIFT") is { } one && one.Split(',') is [var r1, var b1, var g1, var o1, var e1])
            sets.Add(new DriftParams(float.Parse(r1), 0.3f, float.Parse(b1) / Deg, float.Parse(g1), float.Parse(e1), float.Parse(o1)));
        else
        foreach (var r in new[] { 30f })
        foreach (var hb in new[] { 0.15f, 0.3f })
        foreach (var beta in new[] { 15f, 25f })
        foreach (var gain in new[] { 4f, 10f })
        foreach (var g in new[] { 1.3f, 1.6f })
        foreach (var lead in new[] { -8f, 0f, 8f })
            sets.Add(new DriftParams(r, hb, beta / Deg, gain, 10, g, lead));
        var totals = new Dictionary<string, List<float>>();
        foreach (var course in courses)
        foreach (var rev in new[] { false, true })
        foreach (var car in cars)
        {
            var d = Load(iso, course, rev, car);
            var cs = Corners(d.Line);
            var style = new RivalStyle(0.8f, 0.5f, 0.3f);
            var grip = Solo(d, style, cs);
            Console.WriteLine($"[Drift] {course} {(rev ? "up" : "down")} {car} grip {grip.TimeText} s hits {grip.WallHits}");
            foreach (var p in sets)
            {
                var r = Solo(d, style, cs, p);
                var tight = cs.Select((c, i) => i).Where(i => cs[i].Radius < p.MaxRadius && cs[i].Angle > 0.8f && r.Corners[i].Reached).ToList();
                var dt = tight.Sum(i => r.Corners[i].Time - grip.Corners[i].Time);
                var beta = tight.Count > 0 ? tight.Average(i => r.Corners[i].MaxBeta) : 0;
                var exit = tight.Count > 0 ? tight.Average(i => r.Corners[i].Exit - grip.Corners[i].Exit) : 0;
                var pct = r.Finished && grip.Finished ? 100 * (r.Time / grip.Time - 1) : float.NaN;
                Console.WriteLine($"[Drift]   {p,-34} {r.TimeText,8} {pct,6:+0.0;-0.0}% corners {tight.Count,2} Δt {dt,6:+0.0;-0.0} s  β̄max {beta,5:F1}°  Δexit {exit,5:+0;-0} km/h  hits {r.WallHits} lat {r.MaxLateral:F1}");
                if (!totals.TryGetValue(p + " " + car, out var l)) totals[p + " " + car] = l = [];
                l.Add(r.Finished ? pct + 100 * r.WallHits : 1000);
            }
        }
        Console.WriteLine("[Drift] best sets (mean % vs grip + 100 per wall hit; lower is better):");
        foreach (var kv in totals.OrderBy(kv => kv.Value.Average()).Take(15))
            Console.WriteLine($"[Drift]   {kv.Key,-42} {kv.Value.Average(),7:F1}");
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
