using System.Globalization;
using System.Numerics;
using Kansei.Physics;
using Touge.Formats;
using Touge.Replays;

namespace Touge;

/// <summary>
///     --cam-bench [KURS_ZEIT,…] [--car X]: the cameras measured headless on the real courses, both directions. The line pilot drives
///     from the start to the goal with a handbrake drift every 7 s (spins and swings around the car); the run is recorded and
///     played back like the viewer does. Per frame (144 Hz render, 120 Hz physics, interpolated like the game): for CHASE and
///     FAR CHASE live and in the replay, and for the TV cameras — <b>blocked</b>: a surface of the course as drawn
///     (<see cref="CameraHull"/>) between the car and the eye; <b>void</b>: no ground below the eye (out of the map);
///     <b>clip</b>: a surface within the 0.3-m near plane; <b>hidden</b>: the road 20 m ahead behind the car's body or off the picture;
///     <b>jitter</b>: RMS / p99 / max of the eye's acceleration relative to the car (m/s²) and of the view direction (rad/s²).
///     TV also counts cuts and cuts within 1.5 s of the last. Replay stepping: the shown car's acceleration at ¼×–4× speed.
/// </summary>
public static class CameraBench
{
    private const float Fps = 144;

    public static readonly string[] Default = ["AKINA", "IROHA", "USUI", "AKAGI", "MYOUGI", "SHOMARU"];

    /// <summary>Counters of one camera over a run.</summary>
    private sealed class Stats(string name)
    {
        public int Frames, Blocked, Void, Clip, Hidden, Down, HiddenDown, BodyDown, Cuts, QuickCuts;
        public double Dist;
        public readonly List<float> Acc = [], Ang = [];
        private Vector3 _r1, _r2, _d1, _d2, _car;
        private int _n;
        private float _lastCut = -9;

        /// <summary>
        ///     Per speed band (<see cref="Bands"/> ± 15 km/h): frames, eye–car distance, eye's offset along the car's heading, car's
        ///     screen X, Y (centre) and height (−1..1), then the squares of X, Y, height (their spread).
        /// </summary>
        public readonly double[,] Band = new double[Bands.Length, 9];

        /// <summary>
        ///     Rigidity: the eye in the car's own frame (pose⁻¹), its second difference per frame (m/s²: 0 = fixed to the car) and the car's screen position over the whole run (sums for the spread).
        /// </summary>
        public readonly List<float> Rel = [];
        public double ScreenN, SX, SY, SXX, SYY;
        private Vector3 _l1, _l2;
        private int _ln;

        public void Rigid(in Matrix4x4 pose, Vector3 eye, Vector3 look, float fov, bool snap)
        {
            Matrix4x4.Invert(pose, out var inv);
            var l = Vector3.Transform(eye, inv);
            if (snap) _ln = 0;
            if (_ln >= 2) Rel.Add((l - 2 * _l1 + _l2).Length() * Fps * Fps);
            (_l2, _l1, _ln) = (_l1, l, _ln + 1);
            var vp = Matrix4x4.CreateLookAt(eye, look, Vector3.UnitY) * Matrix4x4.CreatePerspectiveFieldOfView(fov, 16 / 9f, 0.3f, 2000);
            if (Project(pose.Translation, vp) is not { } m) return;
            (ScreenN, SX, SY, SXX, SYY) = (ScreenN + 1, SX + m.X, SY + m.Y, SXX + m.X * m.X, SYY + m.Y * m.Y);
            Screen.Add((l.Length(), m.Y));
        }

        /// <summary>Per frame: eye–car distance and the car's screen Y (pulled-in frames apart).</summary>
        public readonly List<(float Dist, float Y)> Screen = [];

        /// <summary>Share of frames the course pulled the eye in (2 % short of the full distance), the car's screen Y spread over the others.</summary>
        public static (double Pulled, double Sd) Free(IEnumerable<Stats> v)
        {
            var all = v.SelectMany(s => s.Screen).ToArray();
            if (all.Length == 0) return (0, 0);
            var full = all.Max(p => p.Dist);
            var free = all.Where(p => p.Dist > 0.98f * full).Select(p => (double)p.Y).ToArray();
            return (1 - (double)free.Length / all.Length, Sd(free.Length, free.Sum(), free.Sum(y => y * y)));
        }

        public static double Sd(double n, double s, double ss) => n > 1 ? Math.Sqrt(Math.Max(ss / n - s * s / (n * n), 0)) : 0;

        public void Framing(in Matrix4x4 pose, float kmh, Vector3 eye, Vector3 look, float fov)
        {
            var b = Array.FindIndex(Bands, v => MathF.Abs(kmh - v) <= 15);
            if (b < 0) return;
            var vp = Matrix4x4.CreateLookAt(eye, look, Vector3.UnitY) * Matrix4x4.CreatePerspectiveFieldOfView(fov, 16 / 9f, 0.3f, 2000);
            Vector3 c = pose.Translation, up = Vector3.TransformNormal(Vector3.UnitY, pose), fwd = Vector3.TransformNormal(Vector3.UnitZ, pose);
            if (Project(c, vp) is not { } m || Project(c + up * 0.8f, vp) is not { } top || Project(c - up * 0.5f, vp) is not { } bottom) return;
            var h = top.Y - bottom.Y;
            double[] add = [1, Vector3.Distance(eye, c), Vector3.Dot(eye - c, fwd), m.X, m.Y, h, m.X * m.X, m.Y * m.Y, h * h];
            for (var k = 0; k < add.Length; k++) Band[b, k] += add[k];
        }

        public void Frame(CameraHull hull, Vector3 car, Vector3 eye, Vector3 look, float t, bool cut = false, bool snap = false)
        {
            Frames++;
            if (hull.Hit(car + Vector3.UnitY, eye) != null) Blocked++;
            if (!hull.Floor(eye)) Void++;
            foreach (var d in Axes)
                if (hull.Solid.Raycast(eye, d, 0.3f, out _))
                {
                    Clip++;
                    break;
                }
            Dist += Vector3.Distance(eye, car);
            if (cut)
            {
                Cuts++;
                if (t - _lastCut < 1.5f) QuickCuts++;
                (_lastCut, _n) = (t, 0);
            }
            if (snap || Vector3.Distance(car, _car) > 3) _n = 0; // a reset: the game snaps
            _car = car;
            var r = eye - car;
            var dir = Vector3.Normalize(look - eye);
            if (_n >= 2)
            {
                Acc.Add((r - 2 * _r1 + _r2).Length() * Fps * Fps);
                Ang.Add((dir - 2 * _d1 + _d2).Length() * Fps * Fps);
            }
            (_r2, _r1, _d2, _d1, _n) = (_r1, r, _d1, dir, _n + 1);
        }

        public static readonly float[] Bands = [30, 80, 130, 180];

        private static readonly Vector3[] Axes = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];

        public static string Spread(List<float> v)
        {
            if (v.Count == 0) return "-";
            var s = v.Order().ToArray();
            return $"{MathF.Sqrt(v.Average(x => x * x)):F1}/{s[(int)(s.Length * 0.99f)]:F1}/{s[^1]:F0}";
        }

        public override string ToString() =>
            $"{name,-14} frames {Frames,6}  blocked {Blocked,5} ({100.0 * Blocked / Math.Max(Frames, 1),5:F2} %)  void {Void,5}  clip {Clip,5}  " +
            $"hidden {100.0 * Hidden / Math.Max(Frames, 1),5:F1} % (downhill {100.0 * HiddenDown / Math.Max(Down, 1),5:F1} %, behind the car {100.0 * BodyDown / Math.Max(Down, 1),4:F1} %)  dist {Dist / Math.Max(Frames, 1),5:F1} m  jitter pos {Spread(Acc)}  dir {Spread(Ang)}" +
            (Cuts > 0 ? $"  cuts {Cuts} (<1.5 s: {QuickCuts})" : "");
    }

    public static bool Run(Iso9660 iso, IReadOnlyList<string> courses, string car)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var total = new Dictionary<string, List<Stats>>();
        var models = iso.OpenAfs("CDVD/DATA/MODEL/COURSE.AFS");
        foreach (var c in courses.Count > 0 ? courses : Default)
            foreach (var reverse in new[] { false, true })
                Course(iso, c.Contains('_') ? c : c + (models.Find(c + "_DAY.PAC") != null ? "_DAY" : "_NIT"), reverse, car, total);
        Console.WriteLine("[CamBench] Summe (blocked/void/clip/frames, hidden, hidden downhill, downhill behind the car, jitter pos/dir RMS/p99/max):");
        foreach (var (k, v) in total)
            Console.WriteLine($"[CamBench]   {k,-14} {v.Sum(s => s.Blocked),6} {v.Sum(s => s.Void),6} {v.Sum(s => s.Clip),6} / {v.Sum(s => s.Frames)}  " +
                              $"{100.0 * v.Sum(s => s.Hidden) / v.Sum(s => s.Frames):F1} %  {100.0 * v.Sum(s => s.HiddenDown) / Math.Max(v.Sum(s => s.Down), 1):F1} %  {100.0 * v.Sum(s => s.BodyDown) / Math.Max(v.Sum(s => s.Down), 1):F1} %  " +
                              $"{Stats.Spread([.. v.SelectMany(s => s.Acc)])}  {Stats.Spread([.. v.SelectMany(s => s.Ang)])}");
        Console.WriteLine("[CamBench] Fest am Auto (Auge im Autorahmen: 2. Ableitung RMS/p99/max m/s², Auto im Bild X/Y Mittel ± sd über die Fahrt, ohne Einzug):");
        foreach (var (k, v) in total.Where(t => t.Value.Any(s => s.ScreenN > 0)))
        {
            double n = v.Sum(s => s.ScreenN), x = v.Sum(s => s.SX), y = v.Sum(s => s.SY);
            var (pulled, sd) = Stats.Free(v);
            Console.WriteLine($"[CamBench]   {k,-14} {Stats.Spread([.. v.SelectMany(s => s.Rel)])}  " +
                              $"X {x / n:F3} ± {Stats.Sd(n, x, v.Sum(s => s.SXX)):F3}  Y {y / n:F3} ± {Stats.Sd(n, y, v.Sum(s => s.SYY)):F3}  (eingezogen {100 * pulled:F1} %, sonst Y ± {sd:F4})");
        }
        Console.WriteLine("[CamBench] Bildaufbau je Tempo (km/h: Bilder, Abstand Auge–Auto m, Auge längs zum Auto m, Auto im Bild X/Y/Höhe −1..1):");
        foreach (var (k, v) in total)
            for (var b = 0; b < Stats.Bands.Length; b++)
            {
                var n = v.Sum(s => s.Band[b, 0]);
                if (n == 0) continue;
                double M(int i) => v.Sum(s => s.Band[b, i]) / n;
                double Sd(int i) => Stats.Sd(n, v.Sum(s => s.Band[b, i]), v.Sum(s => s.Band[b, i + 3]));
                Console.WriteLine($"[CamBench]   {k,-14} {Stats.Bands[b],3:F0}: {n,7}  {M(1):F2}  {M(2):F2}  {M(3):F3} {M(4):F3} {M(5):F3}  (sd {Sd(3):F3} {Sd(4):F3} {Sd(5):F3})");
            }
        return true;
    }

    private static void Course(Iso9660 iso, string course, bool reverse, string carName, Dictionary<string, List<Stats>> total)
    {
        var drive = new Drive(iso, course, reverse, CarSpecs.All[carName]) { ForceDrift = true };
        drive.ResetTo(0);
        var hull = CourseLoader.Hull(iso, course, reverse);
        var car = drive.Car;
        var replay = new Replay { Info = { Course = course, Reverse = reverse, Cars = [new ReplayCar("YOU", carName, 0)] } };
        var rec = new ReplayRecorder(replay, [car]);
        var pos = new List<Vector3> { car.Position };
        var rot = new List<Quaternion> { car.Orientation };
        var resets = new HashSet<int>(); // first pose after a reset
        var goal = drive.Pilot.Length - Ui.LapTimer.Gate;
        var stuck = 0f;
        for (var t = 0; t < 400 / Drive.Dt && drive.Pilot.Track(car.Position).Along < goal; t++)
        {
            // stuck after a spin, or off the course with no ground below (the pilot left IROHA rev that way): back onto the line like R
            stuck = car.SpeedKmh < 5 ? stuck + Drive.Dt : 0;
            if (stuck > 2 || (t % 60 == 0 && !hull.Floor(car.Position))) // a jump the cameras snap over in the game
            {
                drive.ResetNearest();
                rec.Mark();
                resets.Add(pos.Count);
                stuck = 0;
            }
            rec.Before([car]);
            var input = drive.PilotInput(t * Drive.Dt);
            car.Step(input, drive.Ground, Drive.Dt);
            rec.After([input]);
            pos.Add(car.Position);
            rot.Add(car.Orientation);
        }
        var name = $"{course}{(reverse ? " rev" : "")}";
        // places for pictures (--at n): the steepest downhill and the tightest bends of the line (points ~10 m apart)
        var line = drive.Line;
        var steep = Enumerable.Range(0, line.Length - 5).OrderBy(k => (line[k + 5].Y - line[k].Y) / MathF.Max(Vector2.Distance(new(line[k].X, line[k].Z), new(line[k + 5].X, line[k + 5].Z)), 1)).First();
        var bends = Enumerable.Range(3, Math.Max(line.Length - 6, 0)).OrderByDescending(k => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(line[k] - line[k - 3]), Vector3.Normalize(line[k + 3] - line[k])), -1, 1)))
            .Aggregate(new List<int>(), (l, k) => { if (l.Count < 3 && l.All(o => Math.Abs(o - k) > 20)) l.Add(k); return l; });
        Console.WriteLine($"[CamBench] {name}: steilstes Gefälle bei Punkt {steep}, engste Kurven bei {string.Join(", ", bends)}");
        Console.WriteLine($"[CamBench] {name}: {pos.Count * Drive.Dt:F0} s, {drive.Pilot.Track(car.Position).Along:F0} von {goal:F0} m");

        // live: the driving cameras at 144 Hz on the interpolated pose
        var track = new LinePilot(drive.Line);
        var mounts = new CameraRig.Mounts(Vector3.Zero, Vector3.Zero, Vector3.Zero, -Vector3.One, Vector3.One);
        var fovDefault = 60 * MathF.PI / 180; // the default FIELD OF VIEW
        foreach (var view in new[] { CameraView.Chase, CameraView.Far })
        {
            var s = new Stats(CameraRig.Name(view));
            var follow = new ChaseCamera();
            track = new LinePilot(drive.Line); // its tracking is local: from the start again
            for (int f = 0, last = 0; ; f++)
            {
                var tt = f / Fps / Drive.Dt;
                var i = (int)tt;
                if (i + 1 >= pos.Count) break;
                var reset = resets.Contains(i + 1);
                var a = reset ? 1 : tt - i; // a reset: no glide across it, the camera snaps (SyncPose)
                var snap = reset && last != i;
                last = i;
                var pose = Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(rot[i], rot[i + 1], a)) * Matrix4x4.CreateTranslation(Vector3.Lerp(pos[i], pos[i + 1], a));
                var (eye, look, fov) = CameraRig.Place(view, ref follow, snap, 1 / Fps, pose, pose, (pos[i + 1] - pos[i]) / Drive.Dt, mounts, fovDefault);
                s.Frame(hull, pose.Translation, eye, look, f / Fps, snap: snap);
                s.Rigid(pose, eye, look, fov, snap);
                s.Framing(pose, (pos[i + 1] - pos[i]).Length() / Drive.Dt * 3.6f, eye, look, fov);
                var (off, behind, down) = Hidden(track, pose, eye, look, fov);
                if (off || behind) s.Hidden++;
                if (down) (s.Down, s.HiddenDown, s.BodyDown) = (s.Down + 1, s.HiddenDown + (off || behind ? 1 : 0), s.BodyDown + (behind ? 1 : 0));
            }
            Report(name, s, total);
        }

        // the showcase orbit (car select, result sheet, story) around the car parked at every 10th line point (~100 m), 72 angles;
        // the places where the unclipped orbit was worst for pictures (--at n --orbit deg:7)
        var orbit = new Stats("ORBIT");
        var worst = new List<(int At, int Deg, int Bad)>();
        for (var at = 0; at + 1 < line.Length; at += 10)
        {
            var body = Matrix4x4.CreateWorld(line[at] + Vector3.UnitY * 0.5f, Vector3.Normalize((line[at + 1] - line[at]) with { Y = 0 }), Vector3.UnitY);
            var bad = (Deg: 0, N: 0);
            for (var k = 0; k < 72; k++)
            {
                var (eye, look) = CameraRig.Orbit(body, k * MathF.Tau / 72, 7, hull); // OrbitCar 5.5 m + 1.5 m
                orbit.Frame(hull, body.Translation, eye, look, 0);
                var (raw, _) = CameraRig.Orbit(body, k * MathF.Tau / 72, 7, null);
                if (hull.Hit(body.Translation + Vector3.UnitY, raw) != null || !hull.Floor(raw)) bad = (bad.N == 0 ? k * 5 : bad.Deg, bad.N + 1);
            }
            if (bad.N > 0) worst.Add((at, bad.Deg, bad.N));
        }
        Console.WriteLine($"[CamBench] {name}: Orbit ohne Kamerakollision schlecht bei (Punkt/Winkel/Anteil): {string.Join(", ", worst.OrderByDescending(w => w.Bad).Take(3).Select(w => $"{w.At}/{w.Deg}°/{w.Bad * 100 / 72}%"))}");
        Report(name, orbit, total);

        // the replay: speeds as the viewer steps them, then TV and chase at 1×
        var player = new ReplayPlayer(replay, [new Vehicle(car.Spec) { SurfaceGrip = car.SurfaceGrip }], drive.Ground);
        foreach (var speed in Ui.ReplayViewer.Speeds)
        {
            // the shown car's acceleration per frame against the recording's at that speed: spikes are steps in the picture
            var shown = new List<float>();
            Vector3 p1 = default, p2 = default;
            var n = 0;
            Viewer(player, speed, (p, _) =>
            {
                if (Vector3.Distance(p.Translation, p1) > 3) n = 0;
                if (n++ >= 2) shown.Add((p.Translation - 2 * p1 + p2).Length() * Fps * Fps);
                (p2, p1) = (p1, p.Translation);
            });
            var real = new List<float>();
            for (var i = 2; i < pos.Count; i++)
                if (Vector3.Distance(pos[i], pos[i - 2]) < 3) real.Add((pos[i] - 2 * pos[i - 1] + pos[i - 2]).Length() / (Drive.Dt * Drive.Dt) * speed * speed);
            Console.WriteLine($"[CamBench]   replay x{speed,-4} shown car acc RMS/p99/max {Stats.Spread(shown)} m/s²  (recording {Stats.Spread(real)})");
        }
        var tvCams = new TvCameras(ReplayCameras.Load(iso, course[..course.LastIndexOf('_')], reverse), Road(iso, course), reverse, hull);
        var tv = new Stats("TV");
        var chase = new Stats("replay CHASE");
        var chaseFollow = new ChaseCamera();
        var lastCar = Vector3.Zero;
        var first = true;
        Viewer(player, 1, (p, time) =>
        {
            var c = p.Translation;
            var (eye, look, tvFov, cut) = tvCams.Update(c, 1 / Fps, first);
            tv.Frame(hull, c, eye, look, time, cut && !first);
            // the car's centre off the picture (outside 90 %): the aim lags or the shot is too tight
            var vp = Matrix4x4.CreateLookAt(eye, look, Vector3.UnitY) * Matrix4x4.CreatePerspectiveFieldOfView(tvFov * MathF.PI / 180, 16 / 9f, 0.3f, 2000);
            if (Project(c + Vector3.UnitY * 0.6f, vp) is not { } q || MathF.Abs(q.X) > 0.9f || MathF.Abs(q.Y) > 0.9f) tv.Hidden++;
            var (ce, cl, _) = CameraRig.Place(CameraView.Chase, ref chaseFollow, false, 1 / Fps, p, p, (c - lastCar) * Fps, mounts, fovDefault);
            lastCar = c;
            chase.Frame(hull, c, ce, cl, time);
            first = false;
        });
        Report(name, tv, total);
        Report(name, chase, total);
    }

    private static void Report(string course, Stats s, Dictionary<string, List<Stats>> total)
    {
        Console.WriteLine($"[CamBench]   {s}");
        var key = s.ToString()[..14].Trim();
        if (!total.TryGetValue(key, out var v)) total[key] = v = [];
        v.Add(s);
    }

    /// <summary>
    ///     The viewer on <paramref name="player"/> at <paramref name="speed"/> from the start: game ticks at 120 Hz step the replay as
    ///     <see cref="TougeGame"/> does, frames at 144 Hz show the pose interpolated by the viewer's alpha.
    /// </summary>
    private static void Viewer(ReplayPlayer player, float speed, Action<Matrix4x4, float> frame)
    {
        player.Seek(0);
        float acc = 0;
        var tick = 0;
        for (var f = 0; !player.Done; f++)
        {
            var t = f / Fps;
            while ((tick + 1) * Drive.Dt <= t)
            {
                tick++;
                acc += speed;
                for (var n = 0; acc >= 1; acc--, n++)
                {
                    player.KeepPrev = n > 0; // as TougeGame.ReplayTick
                    if (!player.Step()) break;
                }
                player.KeepPrev = false;
            }
            var tickAlpha = (t - tick * Drive.Dt) / Drive.Dt;
            var alpha = Math.Clamp(acc + tickAlpha * MathF.Min(speed, 1), 0, 1);
            var pose = Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(player.PrevOrientation[0], player.Cars[0].Orientation, alpha))
                       * Matrix4x4.CreateTranslation(Vector3.Lerp(player.PrevPosition[0], player.Cars[0].Position, alpha));
            frame(pose, t);
        }
    }

    /// <summary>The road 20 m ahead of the car (driving line) off the picture, or behind the car's body; downhill: it lies over 1 m lower (5 %).</summary>
    private static (bool Off, bool Behind, bool Down) Hidden(LinePilot line, in Matrix4x4 pose, Vector3 eye, Vector3 look, float fov)
    {
        var view = Matrix4x4.CreateLookAt(eye, look, Vector3.UnitY) * Matrix4x4.CreatePerspectiveFieldOfView(fov, 16 / 9f, 0.3f, 2000);
        var ahead = line.PointAt(line.Track(pose.Translation).Along + 20) + Vector3.UnitY * 0.1f;
        var down = ahead.Y < line.PointAt(line.Track(pose.Translation).Along).Y - 1;
        if (Project(ahead, view) is not { } p || MathF.Abs(p.X) > 1 || MathF.Abs(p.Y) > 1) return (true, false, down);
        // the sight line through the car's body (a box 1.7 × 1.3 × 4.2 m around the centre of gravity, slab test in its frame)
        Matrix4x4.Invert(pose, out var local);
        Vector3 o = Vector3.Transform(eye, local), d = Vector3.Transform(ahead, local) - o, lo = new(-0.85f, -0.5f, -2.1f), hi = new(0.85f, 0.8f, 2.1f);
        float t0 = 0, t1 = 1;
        for (var k = 0; k < 3; k++)
        {
            float ok = k == 0 ? o.X : k == 1 ? o.Y : o.Z, dk = k == 0 ? d.X : k == 1 ? d.Y : d.Z, l = k == 0 ? lo.X : k == 1 ? lo.Y : lo.Z, h = k == 0 ? hi.X : k == 1 ? hi.Y : hi.Z;
            if (MathF.Abs(dk) < 1e-6f)
            {
                if (ok < l || ok > h) return (false, false, down);
                continue;
            }
            float a = (l - ok) / dk, b = (h - ok) / dk;
            (t0, t1) = (MathF.Max(t0, MathF.Min(a, b)), MathF.Min(t1, MathF.Max(a, b)));
        }
        return (false, t0 <= t1, down);
    }

    private static Vector2? Project(Vector3 p, in Matrix4x4 viewProj)
    {
        var c = Vector4.Transform(new Vector4(p, 1), viewProj);
        return c.W > 0.05f ? new Vector2(c.X / c.W, c.Y / c.W) : null;
    }

    private static Vector3[] Road(Iso9660 iso, string courseTime)
    {
        var data = iso.OpenAfs("CDVD/DATA/COURSE/CRS_DATA.AFS");
        var name = $"CRS_ROAD_{courseTime[..courseTime.LastIndexOf('_')]}.BIN";
        return CourseRoad.Read(data.Read(data.Find(name) ?? throw new FileNotFoundException(name)));
    }
}
