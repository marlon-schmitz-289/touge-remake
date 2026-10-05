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
    ///     Eye, aim and field of view of <paramref name="view"/> for a car at physics pose <paramref name="pose"/> (CoG) with
    ///     model matrix <paramref name="body"/>; the chase views spring from <paramref name="pos"/>/<paramref name="look"/>
    ///     (<paramref name="snap"/>: jump), widening with speed; the on-board ones are rigid.
    /// </summary>
    public static (Vector3 Pos, Vector3 Look, float Fov) Place(CameraView view, Vector3 pos, Vector3 look, bool snap, float dt,
        in Matrix4x4 pose, in Matrix4x4 body, in Mounts m, float speedKmh, float fov)
    {
        var fwd = Vector3.TransformNormal(Vector3.UnitZ, pose);
        var up = Vector3.TransformNormal(Vector3.UnitY, pose);
        var speed = MathF.Min(speedKmh / 180, 1);
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
        var car = pose.Translation;
        var flat = Vector3.Normalize(fwd with { Y = 0 } + new Vector3(0, 0, 1e-6f));
        var far = view == CameraView.Far;
        var (back, height, ahead, rate) = far ? (9.5f, 3.1f, 6f, 2.6f) : (5.8f, 1.9f, 4f, 6f);
        float a = snap ? 1 : 1 - MathF.Exp(-rate * dt), b = snap ? 1 : 1 - MathF.Exp(-2 * rate * dt);
        return (Vector3.Lerp(pos, car - flat * back + Vector3.UnitY * height, a), Vector3.Lerp(look, car + flat * ahead + Vector3.UnitY * (far ? 0.9f : 0.6f), b),
            fov + speed * (far ? 0.12f : 0.2f));
    }
}
