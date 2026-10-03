using System.Numerics;

namespace Kansei.Graphics;

/// <summary>Vertex normals for meshes that come without them (prelit PS2 track geometry).</summary>
public static class Normals
{
    /// <summary>
    ///     Smooth normal per corner of a triangle list: face normals of all triangles touching the same position
    ///     (rounded to 1 cm, so seams between separately stored pieces close), weighted by the corner angle, but only
    ///     from faces within <paramref name="creaseDegrees"/> of this face — sharper edges stay hard. The data's
    ///     winding is not consistent (strips, two-sided cards), so neighbours are compared and summed sign-agnostic;
    ///     each result points to the side of its own face's winding (shaders flip normals towards the viewer).
    /// </summary>
    public static Vector3[] Smooth(ReadOnlySpan<Vector3> corners, float creaseDegrees = 60)
    {
        var tris = corners.Length / 3;
        var face = new Vector3[tris];
        var angle = new float[corners.Length];
        var groups = new Dictionary<(int, int, int), List<int>>();
        for (var t = 0; t < tris; t++)
        {
            Vector3 a = corners[t * 3], b = corners[t * 3 + 1], c = corners[t * 3 + 2];
            var n = Vector3.Cross(b - a, c - a);
            face[t] = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.Zero;
            angle[t * 3] = Angle(b - a, c - a);
            angle[t * 3 + 1] = Angle(c - b, a - b);
            angle[t * 3 + 2] = Angle(a - c, b - c);
            for (var k = 0; k < 3; k++)
            {
                var p = corners[t * 3 + k];
                var key = ((int)MathF.Round(p.X * 100), (int)MathF.Round(p.Y * 100), (int)MathF.Round(p.Z * 100));
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
                list.Add(t * 3 + k);
            }
        }

        var cos = MathF.Cos(creaseDegrees * MathF.PI / 180);
        var normals = new Vector3[corners.Length];
        foreach (var list in groups.Values)
        foreach (var i in list)
        {
            var own = face[i / 3];
            var sum = Vector3.Zero;
            foreach (var j in list)
            {
                var d = Vector3.Dot(own, face[j / 3]);
                if (MathF.Abs(d) >= cos) sum += face[j / 3] * (MathF.Sign(d) * angle[j]);
            }
            normals[i] = sum.LengthSquared() > 1e-12f ? Vector3.Normalize(sum) : own == Vector3.Zero ? Vector3.UnitY : own;
        }
        return normals;
    }

    private static float Angle(Vector3 u, Vector3 v)
    {
        var l = u.Length() * v.Length();
        return l > 1e-12f ? MathF.Acos(Math.Clamp(Vector3.Dot(u, v) / l, -1, 1)) : 0;
    }
}
