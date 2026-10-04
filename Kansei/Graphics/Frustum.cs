using System.Numerics;

namespace Kansei.Graphics;

/// <summary>Box-against-frustum test in clip space of a view-projection (row vectors, depth 0..1; reversed-Z with infinite far works too).</summary>
public static class Frustum
{
    /// <summary>
    ///     False only if the box <paramref name="min"/>..<paramref name="max"/> lies fully outside one of the planes
    ///     −w ≤ x ≤ w, −w ≤ y ≤ w, 0 ≤ z ≤ w of <paramref name="viewProj"/> (conservative: corner cases near edges stay visible).
    /// </summary>
    public static bool Visible(in Matrix4x4 viewProj, Vector3 min, Vector3 max)
    {
        var m = viewProj;
        var x = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var y = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var z = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var w = new Vector4(m.M14, m.M24, m.M34, m.M44);
        return Inside(w + x, min, max) && Inside(w - x, min, max) && Inside(w + y, min, max) && Inside(w - y, min, max)
               && Inside(z, min, max) && Inside(w - z, min, max);
    }

    /// <summary>The box corner furthest along the plane normal is on the inner side.</summary>
    private static bool Inside(Vector4 p, Vector3 min, Vector3 max) =>
        p.X * (p.X >= 0 ? max.X : min.X) + p.Y * (p.Y >= 0 ? max.Y : min.Y) + p.Z * (p.Z >= 0 ? max.Z : min.Z) + p.W >= 0;
}
