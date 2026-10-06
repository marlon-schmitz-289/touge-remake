using System.Numerics;
using Kansei.Physics;
using Touge.Formats;

namespace Touge;

/// <summary>
///     What the cameras may not pass: the course as drawn (opaque triangles, no foliage/fence cutouts: hills, rock faces,
///     banks, buildings, tree trunks, the road) as a ray-cast <see cref="TriangleGround"/> with every triangle solid. The collision
///     file alone has only the drivable faces and low walls, not the slopes the chase camera swung into.
/// </summary>
public sealed class CameraHull(TriangleGround solid)
{
    /// <summary>Clearance (m) kept between the eye and a surface: more than the 0.3 m near plane.</summary>
    public const float Radius = 0.35f;

    /// <summary>How far down (m) the ground must be found below the eye; beyond the course's edge there is nothing (the void).</summary>
    public const float FloorDepth = 150;

    public TriangleGround Solid => solid;

    /// <summary>Course triangles of <paramref name="meshes"/> that hide what is behind them (texture not in <paramref name="cutout"/>, vertex alpha 1).</summary>
    public static CameraHull Of(IEnumerable<(string Name, Mesh Mesh)> meshes, IReadOnlySet<string> cutout)
    {
        var pts = new List<Vector3>();
        foreach (var (_, mesh) in meshes)
            foreach (var m in mesh.Materials)
            {
                if (m.Texture >= 0 && m.Texture < mesh.Textures.Length && cutout.Contains(mesh.Textures[m.Texture])) continue;
                var t = m.Triangles;
                for (var i = 0; i + 2 < t.Count; i += 3)
                    if (t[i].Color.W >= 0.99f && t[i + 1].Color.W >= 0.99f && t[i + 2].Color.W >= 0.99f)
                        pts.AddRange([t[i].Position, t[i + 1].Position, t[i + 2].Position]);
            }
        var n = pts.Count / 3;
        return new CameraHull(new TriangleGround(pts.ToArray(), Enumerable.Range(0, n * 3).ToArray(), new int[n], new bool[n]));
    }

    /// <summary>First surface on the segment <paramref name="from"/> → <paramref name="to"/> (distance from <paramref name="from"/>), null when clear.</summary>
    public float? Hit(Vector3 from, Vector3 to)
    {
        var d = to - from;
        var len = d.Length();
        return len > 1e-4f && solid.Raycast(from, d / len, len, out var h) ? h.Distance : null;
    }

    /// <summary>Ground below <paramref name="p"/> within <see cref="FloorDepth"/> (false: out over the void beyond the course's edge).</summary>
    public bool Floor(Vector3 p) => solid.Raycast(p + Vector3.UnitY * 0.5f, -Vector3.UnitY, FloorDepth, out _);

    /// <summary>
    ///     How far (m) from <paramref name="pivot"/> towards <paramref name="eye"/> the camera may go: a thin sphere sweep (the
    ///     centre ray and four rays <paramref name="radius"/> around it) stops <paramref name="radius"/> before the first surface,
    ///     then it backs off to where there is ground below (bisection: the edge of the course). At most the full distance.
    /// </summary>
    public float Reach(Vector3 pivot, Vector3 eye, float radius = Radius)
    {
        var d = eye - pivot;
        var len = d.Length();
        if (len < 1e-3f) return len;
        d /= len;
        var side = Vector3.Cross(d, Vector3.UnitY);
        side = side.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(side);
        var up = Vector3.Cross(side, d);
        var reach = len;
        ReadOnlySpan<Vector3> offsets = [Vector3.Zero, side * radius, -side * radius, up * radius, -up * radius];
        foreach (var o in offsets)
            if (solid.Raycast(pivot + o, d, len + radius, out var h)) reach = MathF.Min(reach, h.Distance - radius);
        reach = MathF.Max(reach, 0);
        if (reach == 0 || Floor(pivot + d * reach)) return reach;
        float lo = 0, hi = reach; // the pivot stands over the course
        for (var i = 0; i < 8; i++)
        {
            var mid = (lo + hi) / 2;
            if (Floor(pivot + d * mid)) lo = mid;
            else hi = mid;
        }
        return lo;
    }
}
