using System.Numerics;
using Touge.Formats;

namespace Touge;

/// <summary>Driving cameras in the order C (pad BACK) cycles them.</summary>
public enum CameraView { Chase, Far, Hood, Cockpit, Bumper }

/// <summary>
///     Placement of the driving cameras: chase and far chase spring behind the car, hood, cockpit and bumper are fixed to
///     the body at mount points measured from the car's own HCAR model (<see cref="Measure"/>). HCAR has no dashboard or
///     steering wheel (FORMATS.md: <c>other00</c> is a low interior shell — seats, door cards, the dash top as flat textured
///     cards for the view through the glass), so the cockpit draws the per-car instrument cluster as the dash
///     (<see cref="Ui.Hud.Dashboard"/>) and keeps the car's body (roof, pillars, bonnet through the screen) as the frame.
/// </summary>
public static class CameraRig
{
    public static readonly CameraView[] Order = Enum.GetValues<CameraView>();

    public static string Name(CameraView v) => v switch
    {
        CameraView.Chase => "CHASE", CameraView.Far => "FAR CHASE", CameraView.Hood => "HOOD", CameraView.Cockpit => "COCKPIT", _ => "BUMPER",
    };

    public static CameraView Next(CameraView v) => Order[((int)v + 1) % Order.Length];

    /// <summary>Near plane (m): on board the bonnet and the cabin are a few cm from the eye.</summary>
    public static float Near(CameraView v) => v is CameraView.Hood or CameraView.Cockpit ? 0.05f : 0.3f;

    /// <summary>Camera mounts in model space: above the bonnet ahead of the screen, the driver's eye (right-hand drive: −X), the nose.</summary>
    public readonly record struct Mounts(Vector3 Hood, Vector3 Eye, Vector3 Nose, Vector3 Min, Vector3 Max);

    /// <summary>
    ///     Mounts from the shown body parts (<paramref name="shell"/>, model space), the glass <paramref name="wind"/> and the
    ///     wheel centre height: the front screen = glass triangles facing forward in the upper front quarter (HCAR's
    ///     <c>wind</c> part also holds headlamp covers, EK9); hood 10 cm above the
    ///     bonnet 15 cm ahead of the screen base; eye 14 cm under the screen top, 32 cm behind it, 42 % of the half width
    ///     to the right (the driver's side: the passenger silhouette <c>assi00</c> sits at +X). Clamped inside the body.
    /// </summary>
    public static Mounts Measure(IEnumerable<Mesh> shell, Mesh? wind, float wheelY)
    {
        var pts = shell.SelectMany(m => m.Materials).SelectMany(m => m.Triangles).Select(v => v.Position).ToArray();
        Vector3 min = pts.Aggregate(new Vector3(float.MaxValue), Vector3.Min), max = pts.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        var mid = (min + max) / 2;
        var front = new List<Vector3>();
        foreach (var t in wind?.Materials.Select(m => m.Triangles) ?? [])
            for (var i = 0; i + 2 < t.Count; i += 3)
            {
                Vector3 a = t[i].Position, b = t[i + 1].Position, c = t[i + 2].Position;
                var n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared() < 1e-10f) continue;
                n = Vector3.Normalize(n);
                if (MathF.Abs(n.Z) > 0.35f && MathF.Abs(n.X) < 0.5f && (a.Z + b.Z + c.Z) / 3 > mid.Z && (a.Y + b.Y + c.Y) / 3 > mid.Y + 0.12f * (max.Y - min.Y)) front.AddRange([a, b, c]);
            }
        float baseZ, baseY, topZ, topY;
        if (front.Count > 0) (baseZ, baseY, topZ, topY) = (front.Max(p => p.Z), front.Min(p => p.Y), front.Min(p => p.Z), front.Max(p => p.Y));
        else (baseZ, baseY, topZ, topY) = (mid.Z + 0.25f * (max.Z - min.Z), mid.Y + 0.1f * (max.Y - min.Y), mid.Z, max.Y - 0.05f);
        var bonnet = pts.Where(p => MathF.Abs(p.X) < 0.35f && p.Z > baseZ + 0.05f && p.Z < baseZ + 0.5f).Select(p => p.Y).DefaultIfEmpty(baseY).Max();
        var hood = new Vector3(0, MathF.Max(bonnet, baseY) + 0.1f, MathF.Min(baseZ + 0.15f, max.Z - 0.4f));
        var eye = new Vector3(-0.42f * max.X, MathF.Min(topY - 0.14f, max.Y - 0.1f), Math.Clamp(topZ - 0.32f, min.Z + 0.6f, baseZ - 0.3f));
        return new Mounts(hood, eye, new Vector3(0, wheelY + 0.22f, max.Z + 0.05f), min, max);
    }

    /// <summary>
    ///     The interior part <c>other00</c> without the driver: HCAR draws him as one textured card standing lengthwise
    ///     (normal along X) on the right-hand side, which a cockpit eye would sit in. Everything else (seats, dash panel, wheel,
    ///     mirror cards, the black cabin) stays.
    /// </summary>
    public static Mesh WithoutDriver(Mesh interior) => new()
    {
        Textures = interior.Textures, Nodes = interior.Nodes,
        Materials = [.. interior.Materials.Where(m => !IsDriver(m))],
    };

    /// <summary>
    ///     A part as the cockpit sees it: <c>other00</c> <see cref="WithoutDriver"/>; the door mirrors' glass (flag 0x200, light grey
    ///     with the paint's clear coat, meant to be glimpsed from outside) a dim grey, else it glows white in the corner of the view.
    /// </summary>
    public static Mesh CabinPart(string name, Mesh mesh) => name == "other00" ? WithoutDriver(mesh)
        : name.StartsWith("mirror") ? new Mesh
        {
            Textures = mesh.Textures, Nodes = mesh.Nodes,
            Materials = [.. mesh.Materials.Select(m => (m.Flags & 0x200) == 0 ? m : m with { Rgba = m.Rgba & 0xFF000000 | 0x2A2622 })],
        }
        : mesh;

    /// <summary>A textured card, every face's normal along X, on the driver's side (−X).</summary>
    public static bool IsDriver(Mesh.Material m)
    {
        if (m.Texture < 0 || m.Triangles.Count == 0 || m.Triangles.Average(v => v.Position.X) > -0.1f) return false;
        for (var i = 0; i + 2 < m.Triangles.Count; i += 3)
        {
            var n = Vector3.Cross(m.Triangles[i + 1].Position - m.Triangles[i].Position, m.Triangles[i + 2].Position - m.Triangles[i].Position);
            if (n.LengthSquared() > 1e-10f && MathF.Abs(Vector3.Normalize(n).X) < 0.9f) return false;
        }
        return true;
    }

    /// <summary>
    ///     State of the chase views between frames: the view's smoothed heading <see cref="Yaw"/> and <see cref="Pitch"/> (rad)
    ///     and their rates, where the car was, and <see cref="Reach"/> — how far from the pivot over the car the eye may stand (pulled in by the
    ///     course, <see cref="CameraHull.Reach"/>). Default = not set: the next frame snaps.
    /// </summary>
    public struct Follow
    {
        public float Yaw, Pitch;
        public Vector3 Turn, Car;
        public float Reach, ReachRate;
        public bool Set;
    }

    /// <summary>Chase view: metres behind and above the car's origin in the view's own frame (FORMATS.md „Fahrkameras“).</summary>
    private readonly record struct Rig(float Back, float Up);

    /// <summary>CHASE as the original (<c>0x16FFE0</c>: eye at (0, 1.7, 4.6) in the view frame); the original has no far view: FAR is CHASE 1.5× further.</summary>
    private static readonly Rig ChaseRig = new(4.6f, 1.7f), FarRig = new(6.9f, 2.55f);

    /// <summary>
    ///     Rate (1/s) the view turns to the car's heading and pitch: the original lerps 6.45 % of the angle per 60-Hz frame
    ///     (<c>0x170130</c>). Here a critically damped spring at twice the rate: the same lag behind a steady turn (rate / 4 s),
    ///     but it starts and stops the swing without a kink and filters the body's bounce (the original follows the ground's slope).
    /// </summary>
    public static readonly float TurnRate = -60 * MathF.Log(1 - 0.064516f);

    /// <summary>
    ///     Chase views' tan(½ vertical fov) over the setting's: the original's chase tan(½ H) 0.92502 (<c>0x170028</c>), vertical
    ///     0.75 of it, over the default 60° — so the default gives the original's 69.5° and other settings scale with it.
    /// </summary>
    public static readonly float Widen = 0.75f * 0.92502f / MathF.Tan(MathF.PI / 6);

    /// <summary>The car's pitch the view follows at most (rad): the original clamps it to ±30° (<c>0x154C40</c>).</summary>
    private const float MaxPitch = MathF.PI / 6;

    /// <summary>Height (m) of the pivot over the car's centre the eye is pulled in towards: under the roof.</summary>
    public const float Pivot = 0.9f;

    /// <summary>A pulled-in eye only goes back out once the course lets it out this much further (m): no in-out flicker.</summary>
    private const float Hold = 0.25f;

    /// <summary>
    ///     Eye, aim and field of view of <paramref name="view"/> for a car at physics pose <paramref name="pose"/> (CoG) with
    ///     model matrix <paramref name="body"/>; the on-board views are rigid. The chase views are the original's: the view's
    ///     heading and pitch turn to the car's at <see cref="TurnRate"/> (a lag of a quarter second, the yaw the short way round),
    ///     the eye stands 4.6 m back and 1.7 m up in that view frame from the model's origin (axle height) and looks along it —
    ///     no aim point, no velocity, nothing that grows with speed, no roll. The course (<paramref name="hull"/>) is only a
    ///     safety net: it pulls the eye in towards the pivot over the car (sooner where the view is turning to), never through
    ///     a surface, and lets it out slowly. <paramref name="snap"/> or a jump of the car (reset, another car) starts over.
    /// </summary>
    public static (Vector3 Pos, Vector3 Look, float Fov) Place(CameraView view, ref Follow f, bool snap, float dt,
        in Matrix4x4 pose, in Matrix4x4 body, in Mounts m, float fov, CameraHull? hull = null)
    {
        var fwd = Vector3.TransformNormal(Vector3.UnitZ, pose);
        var up = Vector3.TransformNormal(Vector3.UnitY, pose);
        switch (view)
        {
            case CameraView.Hood:
                var hood = Vector3.Transform(m.Hood, body);
                return (hood, hood + fwd * 10, fov);
            case CameraView.Cockpit:
                var eye = Vector3.Transform(m.Eye, body);
                return (eye, eye + fwd * 10 - up * 0.45f, fov + 0.1f); // a touch down to the road, a wider view like a seat
            case CameraView.Bumper:
                var nose = Vector3.Transform(m.Nose, body);
                return (nose, nose + fwd * 10, fov);
        }
        var r = view == CameraView.Far ? FarRig : ChaseRig;
        var car = body.Translation;
        float yaw = MathF.Atan2(fwd.X, fwd.Z), pitch = Math.Clamp(MathF.Asin(Math.Clamp(fwd.Y, -1, 1)), -MaxPitch, MaxPitch);
        snap |= !f.Set || Vector3.DistanceSquared(car, f.Car) > 8 * 8 || !float.IsFinite(f.Yaw + f.Pitch);
        if (snap) (f.Yaw, f.Pitch, f.Turn) = (yaw, pitch, Vector3.Zero);
        else
        {
            var x = new Vector3(f.Yaw, f.Pitch, 0);
            Spring(ref x, ref f.Turn, new Vector3(f.Yaw + Wrap(yaw - f.Yaw), pitch, 0), 2 * TurnRate, dt);
            (f.Yaw, f.Pitch) = (Wrap(x.X), x.Y);
        }
        (f.Car, f.Set) = (car, true);
        var want = Eye(car, f.Yaw, f.Pitch, r, out var dir);
        var look = want + dir * 10;
        var pivot = pose.Translation + Vector3.UnitY * Pivot;
        var full = Vector3.Distance(pivot, want);
        // hard limit: never through a surface; a wider sweep, also where the view is turning to, eases the eye in before (no jump)
        float hard = hull?.Reach(pivot, want) ?? full, soft = full;
        if (hull != null) soft = MathF.Min(hard, MathF.Min(hull.Reach(pivot, want, 1.2f), hull.Reach(pivot, Eye(car, yaw, pitch, r, out _), 1.2f)));
        // out again only past the hold (or all the way): a sweep grazing a surface does not pump the eye in and out
        var goal = soft < f.Reach || soft > f.Reach + Hold || soft >= full - 1e-3f ? soft : f.Reach;
        if (snap) (f.Reach, f.ReachRate) = (soft, 0);
        else
        {
            Vector3 x = new(f.Reach), v = new(f.ReachRate);
            Spring(ref x, ref v, new Vector3(goal), goal < f.Reach ? 12 : 3, dt); // in quickly, out slowly
            (f.Reach, f.ReachRate) = (MathF.Min(x.X, hard), x.X > hard ? 0 : v.X);
        }
        var pos = full > 1e-3f ? pivot + (want - pivot) * (MathF.Min(f.Reach, full) / full) : want;
        return (pos, look, 2 * MathF.Atan(MathF.Tan(fov / 2) * Widen));
    }

    /// <summary>The chase eye of rig <paramref name="r"/> for a view at <paramref name="yaw"/>/<paramref name="pitch"/> from <paramref name="car"/>, its direction <paramref name="dir"/>.</summary>
    private static Vector3 Eye(Vector3 car, float yaw, float pitch, Rig r, out Vector3 dir)
    {
        float sy = MathF.Sin(yaw), cy = MathF.Cos(yaw), sp = MathF.Sin(pitch), cp = MathF.Cos(pitch);
        dir = new Vector3(sy * cp, sp, cy * cp);
        return car - dir * r.Back + new Vector3(-sy * sp, cp, -cy * sp) * r.Up;
    }

    private static float Wrap(float a) => MathF.IEEERemainder(a, MathF.Tau);

    /// <summary>
    ///     Critically damped spring of <paramref name="x"/> (velocity <paramref name="v"/>) to <paramref name="target"/> at
    ///     <paramref name="omega"/> (1/s), implicit Euler (stable at any dt): a step of the target bends the path, it never kinks it.
    /// </summary>
    public static void Spring(ref Vector3 x, ref Vector3 v, Vector3 target, float omega, float dt)
    {
        float f = 1 + 2 * dt * omega, hoo = dt * omega * omega, hhoo = dt * hoo, inv = 1 / (f + hhoo);
        (x, v) = ((f * x + dt * v + hhoo * target) * inv, (v + hoo * (target - x)) * inv);
    }

    /// <summary>
    ///     The showcase orbit (car select, result sheet, story): <paramref name="distance"/> m around the car at
    ///     <paramref name="angle"/> (0 front, π/2 left), 1.3 m up per 5.5 m, looking at its middle; the course pulls it in.
    /// </summary>
    public static (Vector3 Eye, Vector3 Target) Orbit(in Matrix4x4 body, float angle, float distance, CameraHull? hull)
    {
        var target = Vector3.Transform(new Vector3(0, 0.4f, 0), body);
        var dir = Vector3.TransformNormal(new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)), body);
        return (Clip(target, target + dir * distance + new Vector3(0, 1.3f * distance / 5.5f, 0), hull), target);
    }

    /// <summary><paramref name="eye"/> pulled in towards <paramref name="target"/> to where the course allows (<see cref="CameraHull.Reach"/>).</summary>
    public static Vector3 Clip(Vector3 target, Vector3 eye, CameraHull? hull)
    {
        if (hull == null) return eye;
        var d = Vector3.Distance(target, eye);
        return d < 1e-3f ? eye : target + (eye - target) * (hull.Reach(target, eye) / d);
    }
}
