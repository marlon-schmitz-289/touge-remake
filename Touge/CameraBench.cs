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
///     <b>clip</b>: a surface within the 0.3-m near plane; <b>hidden</b>: the road 20 m ahead behind the car or off the picture;
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
        public int Frames, Blocked, Void, Clip, Hidden, Cuts, QuickCuts;
        public double Dist;
        private readonly List<float> _acc = [], _ang = [];
        private Vector3 _r1, _r2, _d1, _d2, _car;
        private int _n;
        private float _lastCut = -9;

        public void Frame(CameraHull hull, Vector3 car, Vector3 eye, Vector3 look, float t, bool cut = false)
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
            if (Vector3.Distance(car, _car) > 3) _n = 0; // a reset: the game snaps
            _car = car;
            var r = eye - car;
            var dir = Vector3.Normalize(look - eye);
            if (_n >= 2)
            {
                _acc.Add((r - 2 * _r1 + _r2).Length() * Fps * Fps);
                _ang.Add((dir - 2 * _d1 + _d2).Length() * Fps * Fps);
            }
            (_r2, _r1, _d2, _d1, _n) = (_r1, r, _d1, dir, _n + 1);
        }

        private static readonly Vector3[] Axes = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];

        public static string Spread(List<float> v)
        {
            if (v.Count == 0) return "-";
            var s = v.Order().ToArray();
            return $"{MathF.Sqrt(v.Average(x => x * x)):F1}/{s[(int)(s.Length * 0.99f)]:F1}/{s[^1]:F0}";
        }

        public override string ToString() =>
            $"{name,-14} frames {Frames,6}  blocked {Blocked,5} ({100.0 * Blocked / Math.Max(Frames, 1),5:F2} %)  void {Void,5}  clip {Clip,5}  " +
            $"hidden {100.0 * Hidden / Math.Max(Frames, 1),5:F1} %  dist {Dist / Math.Max(Frames, 1),5:F1} m  jitter pos {Spread(_acc)}  dir {Spread(_ang)}" +
            (Cuts > 0 ? $"  cuts {Cuts} (<1.5 s: {QuickCuts})" : "");
    }

    public static bool Run(Iso9660 iso, IReadOnlyList<string> courses, string car)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var total = new Dictionary<string, int[]>();
        var models = iso.OpenAfs("CDVD/DATA/MODEL/COURSE.AFS");
        foreach (var c in courses.Count > 0 ? courses : Default)
            foreach (var reverse in new[] { false, true })
                Course(iso, c.Contains('_') ? c : c + (models.Find(c + "_DAY.PAC") != null ? "_DAY" : "_NIT"), reverse, car, total);
        Console.WriteLine("[CamBench] Summe (blocked/void/clip/frames):");
        foreach (var (k, v) in total) Console.WriteLine($"[CamBench]   {k,-14} {v[0],6} {v[1],6} {v[2],6} / {v[3]}");
        return true;
    }

    private static void Course(Iso9660 iso, string course, bool reverse, string carName, Dictionary<string, int[]> total)
    {
        var drive = new Drive(iso, course, reverse, CarSpecs.All[carName]) { ForceDrift = true };
        drive.ResetTo(0);
        var hull = CourseLoader.Hull(iso, course, reverse);
        var car = drive.Car;
        var replay = new Replay { Info = { Course = course, Reverse = reverse, Cars = [new ReplayCar("YOU", carName, 0)] } };
        var rec = new ReplayRecorder(replay, [car]);
        var pos = new List<Vector3> { car.Position };
        var rot = new List<Quaternion> { car.Orientation };
        var vel = new List<Vector3> { car.Velocity };
        var goal = drive.Pilot.Length - Ui.LapTimer.Gate;
        var stuck = 0f;
        for (var t = 0; t < 400 / Drive.Dt && drive.Pilot.Track(car.Position).Along < goal; t++)
        {
            stuck = car.SpeedKmh < 5 ? stuck + Drive.Dt : 0;
            if (stuck > 2) // stuck after a spin: back onto the line like R (a jump the cameras snap over in the game)
            {
                drive.ResetNearest();
                rec.Mark();
                stuck = 0;
            }
            rec.Before([car]);
            var input = drive.PilotInput(t * Drive.Dt);
            car.Step(input, drive.Ground, Drive.Dt);
            rec.After([input]);
            pos.Add(car.Position);
            rot.Add(car.Orientation);
            vel.Add(car.Velocity);
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
        var fov65 = 65 * MathF.PI / 180;
        foreach (var view in new[] { CameraView.Chase, CameraView.Far })
        {
            var s = new Stats(CameraRig.Name(view));
            var follow = new CameraRig.Follow();
            for (var f = 0; ; f++)
            {
                var tt = f / Fps / Drive.Dt;
                var i = (int)tt;
                if (i + 1 >= pos.Count) break;
                var a = Vector3.Distance(pos[i], pos[i + 1]) > 3 ? 1 : tt - i; // a reset: no glide across it (SyncPose)
                var pose = Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(rot[i], rot[i + 1], a)) * Matrix4x4.CreateTranslation(Vector3.Lerp(pos[i], pos[i + 1], a));
                var (eye, look, fov) = CameraRig.Place(view, ref follow, false, 1 / Fps, pose, pose, mounts, vel[i + 1], fov65, hull);
                s.Frame(hull, pose.Translation, eye, look, f / Fps);
                if (Hidden(track, pose, eye, look, fov)) s.Hidden++;
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
        var chaseFollow = new CameraRig.Follow();
        var first = true;
        Viewer(player, 1, (p, time) =>
        {
            var c = p.Translation;
            var (eye, look, _, cut) = tvCams.Update(c, 1 / Fps, first);
            tv.Frame(hull, c, eye, look, time, cut && !first);
            var (ce, cl, _) = CameraRig.Place(CameraView.Chase, ref chaseFollow, false, 1 / Fps, p, p, mounts, player.Cars[0].Velocity, fov65, hull);
            chase.Frame(hull, c, ce, cl, time);
            first = false;
        });
        Report(name, tv, total);
        Report(name, chase, total);
    }

    private static void Report(string course, Stats s, Dictionary<string, int[]> total)
    {
        Console.WriteLine($"[CamBench]   {s}");
        var key = s.ToString()[..14].Trim();
        if (!total.TryGetValue(key, out var v)) total[key] = v = new int[4];
        (v[0], v[1], v[2], v[3]) = (v[0] + s.Blocked, v[1] + s.Void, v[2] + s.Clip, v[3] + s.Frames);
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

    /// <summary>The road 20 m ahead of the car (driving line) off the picture or inside the car's projected box.</summary>
    private static bool Hidden(LinePilot line, in Matrix4x4 pose, Vector3 eye, Vector3 look, float fov)
    {
        var view = Matrix4x4.CreateLookAt(eye, look, Vector3.UnitY) * Matrix4x4.CreatePerspectiveFieldOfView(fov, 16 / 9f, 0.3f, 2000);
        var ahead = line.PointAt(line.Track(pose.Translation).Along + 20) + Vector3.UnitY * 0.1f;
        if (Project(ahead, view) is not { } p || MathF.Abs(p.X) > 1 || MathF.Abs(p.Y) > 1) return true;
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        for (var k = 0; k < 8; k++)
        {
            var corner = Vector3.Transform(new Vector3((k & 1) == 0 ? -0.85f : 0.85f, (k & 2) == 0 ? -0.5f : 0.8f, (k & 4) == 0 ? -2.1f : 2.1f), pose);
            if (Project(corner, view) is not { } c) return false; // the eye inside the box: the car is not in front of the road
            (min, max) = (Vector2.Min(min, c), Vector2.Max(max, c));
        }
        return p.X > min.X && p.X < max.X && p.Y > min.Y && p.Y < max.Y && Vector3.Distance(eye, ahead) > Vector3.Distance(eye, pose.Translation);
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
