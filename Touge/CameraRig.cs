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
    ///     State of the chase views between frames: the smoothed view direction <see cref="Dir"/> (unit, along the road's slope),
    ///     where the car was, and <see cref="Reach"/> — how far from the pivot over the car the eye may stand (pulled in by the
    ///     course, <see cref="CameraHull.Reach"/>). Default = not set: the next frame snaps.
    /// </summary>
    public struct Follow
    {
        public Vector3 Dir, Turn, Car;
        public float Reach, ReachRate;
        public bool Set;
    }

    /// <summary>Chase view: metres behind and above the car (perpendicular to the road), aim ahead and above it, turn rate (1/s), FOV widening at speed (rad).</summary>
    private readonly record struct Rig(float Back, float Height, float Ahead, float AimHeight, float Rate, float Widen, float Stretch);

    private static readonly Rig ChaseRig = new(5.2f, 1.45f, 7, 0.75f, 9, 0.08f, 0.6f), FarRig = new(7.8f, 2.3f, 10, 1, 6, 0.06f, 1);

    /// <summary>Height (m) of the pivot over the car's centre the eye is pulled in towards: under the roof.</summary>
    public const float Pivot = 0.9f;

    /// <summary>
    ///     Eye, aim and field of view of <paramref name="view"/> for a car at physics pose <paramref name="pose"/> (CoG) with
    ///     model matrix <paramref name="body"/>; the on-board views are rigid. The chase views keep their distance (no lag
    ///     that grows with speed): only the direction <see cref="Follow.Dir"/> follows — the heading blended with the direction
    ///     of travel (<paramref name="velocity"/>; in a spin the travel alone, so the eye does not swing around the car), with
    ///     the road's slope (the eye stays the same height over the road downhill, the aim goes down with it). The course
    ///     (<paramref name="hull"/>) pulls the eye in at once and lets it out at 6 m/s; <paramref name="snap"/> or a jump of the
    ///     car (reset, another car) starts over.
    /// </summary>
    public static (Vector3 Pos, Vector3 Look, float Fov) Place(CameraView view, ref Follow f, bool snap, float dt,
        in Matrix4x4 pose, in Matrix4x4 body, in Mounts m, Vector3 velocity, float fov, CameraHull? hull = null)
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
        var car = pose.Translation;
        snap |= !f.Set || Vector3.DistanceSquared(car, f.Car) > 8 * 8 || float.IsNaN(f.Dir.X);
        f.Car = car;
        var dir = Aim(fwd, velocity);
        if (snap) (f.Dir, f.Turn) = (dir, Vector3.Zero);
        else Spring(ref f.Dir, ref f.Turn, dir, r.Rate, dt);
        f.Dir = Vector3.Normalize(f.Dir + dir * 1e-4f);
        f.Set = true;
        // up across the view direction in its vertical plane: height over the road's slope, no roll from the body
        var lift = Vector3.UnitY - f.Dir * f.Dir.Y;
        lift = lift.LengthSquared() < 1e-4f ? Vector3.UnitY : Vector3.Normalize(lift);
        var speed = MathF.Min(velocity.Length() * 3.6f / 180, 1);
        var want = car - f.Dir * (r.Back + speed * r.Stretch) + lift * r.Height;
        var look = car + f.Dir * r.Ahead + lift * r.AimHeight;
        var pivot = car + Vector3.UnitY * Pivot;
        var full = Vector3.Distance(pivot, want);
        // hard limit: never through a surface; a wider sweep sees it coming and eases the eye in before (no jump)
        float hard = hull?.Reach(pivot, want) ?? full, soft = MathF.Min(hard, hull?.Reach(pivot, want, 1.2f) ?? full);
        if (snap) (f.Reach, f.ReachRate) = (soft, 0);
        else
        {
            Vector3 x = new(f.Reach), v = new(f.ReachRate);
            Spring(ref x, ref v, new Vector3(soft), soft < f.Reach ? 12 : 3, dt); // in quickly, out slowly
            (f.Reach, f.ReachRate) = (MathF.Min(x.X, hard), x.X > hard ? 0 : v.X);
        }
        var pos = full > 1e-3f ? pivot + (want - pivot) * (MathF.Min(f.Reach, full) / full) : want;
        return (pos, look, fov + speed * r.Widen);
    }

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
    ///     Where the chase views look: the heading <paramref name="fwd"/> blended half way to the direction of travel; the
    ///     further the car slides (slip over ~60°, a spin) the more the travel alone; at walking pace the heading.
    /// </summary>
    public static Vector3 Aim(Vector3 fwd, Vector3 velocity)
    {
        var v = velocity.Length();
        if (v < 2) return fwd;
        var travel = velocity / v;
        var w = float.Lerp(0.5f, 1, Math.Clamp((0.5f - Vector3.Dot(travel, fwd)) / 0.5f, 0, 1)) * Math.Clamp((v - 2) / 6, 0, 1);
        var d = Vector3.Lerp(fwd, travel, w);
        return d.LengthSquared() < 1e-4f ? travel : Vector3.Normalize(d);
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
