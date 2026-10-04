using System.Numerics;

namespace Touge.Formats;

/// <summary>
///     Z-fighting candidates in a triangle list (3 corners per triangle, in draw order): pairs of triangles that are
///     near-coplanar, overlap in area and lie within a few cm of each other. The PS2 resolves such layers by draw order
///     (later wins at equal Z); <see cref="Layers"/> turns the pairs into a per-triangle layer the renderer pulls
///     towards the camera so the later layer wins regardless of depth rounding.
/// </summary>
public static class ZFight
{
    /// <param name="A">Earlier triangle.</param>
    /// <param name="B">Later triangle (drawn on top).</param>
    /// <param name="Area">Overlap in m² (measured in A's plane).</param>
    /// <param name="Gap">Largest distance between the two planes over the overlap, along A's normal (m).</param>
    /// <param name="At">Centre of the overlap.</param>
    public readonly record struct Pair(int A, int B, float Area, float Gap, Vector3 At);

    /// <summary>
    ///     All overlapping near-coplanar pairs: normals within <paramref name="maxDegrees"/> (either side, nothing is
    ///     culled), planes within <paramref name="maxGap"/> over the overlap, overlap ≥ <paramref name="minArea"/>
    ///     (shared edges have none). Broad phase: XZ grid sized from the median triangle.
    /// </summary>
    public static List<Pair> Find(ReadOnlySpan<Vector3> corners, float maxGap = 0.03f, float maxDegrees = 3, float minArea = 1e-4f)
    {
        var n = corners.Length / 3;
        var lo = new Vector3[n];
        var hi = new Vector3[n];
        var extent = new float[n];
        for (var t = 0; t < n; t++)
        {
            lo[t] = Vector3.Min(Vector3.Min(corners[t * 3], corners[t * 3 + 1]), corners[t * 3 + 2]) - new Vector3(maxGap);
            hi[t] = Vector3.Max(Vector3.Max(corners[t * 3], corners[t * 3 + 1]), corners[t * 3 + 2]) + new Vector3(maxGap);
            extent[t] = MathF.Max(hi[t].X - lo[t].X, hi[t].Z - lo[t].Z);
        }
        var pairs = new List<Pair>();
        if (n == 0) return pairs;
        Array.Sort(extent);
        var cell = MathF.Max(extent[n / 2] * 2, 0.05f);

        var grid = new Dictionary<(int, int), List<int>>();
        for (var t = 0; t < n; t++)
            for (var x = (int)MathF.Floor(lo[t].X / cell); x <= (int)MathF.Floor(hi[t].X / cell); x++)
            for (var z = (int)MathF.Floor(lo[t].Z / cell); z <= (int)MathF.Floor(hi[t].Z / cell); z++)
            {
                if (!grid.TryGetValue((x, z), out var list)) grid[(x, z)] = list = [];
                list.Add(t);
            }

        var cos = MathF.Cos(maxDegrees * MathF.PI / 180);
        foreach (var ((cx, cz), list) in grid)
            for (var i = 0; i < list.Count; i++)
            for (var j = i + 1; j < list.Count; j++)
            {
                int a = list[i], b = list[j]; // lists are filled in triangle order: a < b
                var l = Vector3.Max(lo[a], lo[b]);
                var h = Vector3.Min(hi[a], hi[b]);
                if (l.X > h.X || l.Y > h.Y || l.Z > h.Z) continue;
                // test each pair once: in the cell holding the low corner of the box overlap
                if ((int)MathF.Floor(l.X / cell) != cx || (int)MathF.Floor(l.Z / cell) != cz) continue;
                if (Test(corners, a, b, cos, maxGap, minArea) is { } p) pairs.Add(p);
            }
        pairs.Sort((p, q) => p.B != q.B ? p.B.CompareTo(q.B) : p.A.CompareTo(q.A));
        return pairs;
    }

    /// <summary>Planes closer than this can fight: ~10× the depth noise of float world coordinates 1–2 km from the origin.</summary>
    public const float FightGap = 0.001f;

    /// <summary>
    ///     Per triangle: 0, or 1 + the highest layer of an earlier triangle it overlaps within <see cref="FightGap"/>
    ///     (<paramref name="pairs"/> from <see cref="Find"/>), capped at <paramref name="max"/>. Farther layers keep
    ///     their geometric order, so a pulled layer never jumps in front of something it really lies behind.
    /// </summary>
    public static int[] Layers(int triangles, List<Pair> pairs, int max = 15)
    {
        var layer = new int[triangles];
        foreach (var p in pairs) // sorted by B, and A < B: layer[A] is final when read
            if (p.Gap < FightGap)
                layer[p.B] = Math.Min(Math.Max(layer[p.B], layer[p.A] + 1), max);
        return layer;
    }

    /// <summary>
    ///     Draw ranges for triangles grouped in batches (<paramref name="batchStart"/>: first triangle of each batch,
    ///     ascending, triangles of a batch are contiguous): each batch split by layer, layer 0 first, within a layer
    ///     file order. <paramref name="order"/> receives the triangle order to put into the index buffer.
    /// </summary>
    public static List<(int Batch, int Layer, int First, int Count)> Order(IReadOnlyList<int> batchStart, int[] layer, out int[] order)
    {
        var ranges = new List<(int, int, int, int)>();
        order = new int[layer.Length];
        var o = 0;
        var top = layer.Length == 0 ? 0 : layer.Max();
        for (var l = 0; l <= top; l++)
            for (var b = 0; b < batchStart.Count; b++)
            {
                var end = b + 1 < batchStart.Count ? batchStart[b + 1] : layer.Length;
                var first = o;
                for (var t = batchStart[b]; t < end; t++)
                    if (layer[t] == l) order[o++] = t;
                if (o > first) ranges.Add((b, l, first, o - first));
            }
        return ranges;
    }

    private static Pair? Test(ReadOnlySpan<Vector3> c, int a, int b, float cos, float maxGap, float minArea)
    {
        Vector3 a0 = c[a * 3], a1 = c[a * 3 + 1], a2 = c[a * 3 + 2], b0 = c[b * 3], b1 = c[b * 3 + 1], b2 = c[b * 3 + 2];
        var na = Vector3.Cross(a1 - a0, a2 - a0);
        var nb = Vector3.Cross(b1 - b0, b2 - b0);
        if (na.LengthSquared() < 1e-12f || nb.LengthSquared() < 1e-12f) return null;
        na = Vector3.Normalize(na);
        nb = Vector3.Normalize(nb);
        var d = Vector3.Dot(na, nb);
        if (MathF.Abs(d) < cos) return null;

        // 2D frame in A's plane, A counter-clockwise
        var u = Vector3.Normalize(a1 - a0);
        var v = Vector3.Cross(na, u);
        Vector2 P(Vector3 p) => new(Vector3.Dot(p - a0, u), Vector3.Dot(p - a0, v));
        Span<Vector2> tri = [P(a0), P(a1), P(a2)];
        Span<Vector2> poly = stackalloc Vector2[9];
        Span<Vector2> tmp = stackalloc Vector2[9];
        poly[0] = P(b0);
        poly[1] = P(b1);
        poly[2] = P(b2);
        var count = 3;
        if (Cross(poly[1] - poly[0], poly[2] - poly[0]) < 0) (poly[1], poly[2]) = (poly[2], poly[1]);
        // Sutherland–Hodgman against A's three edges
        for (var e = 0; e < 3 && count > 0; e++)
        {
            Vector2 p0 = tri[e], p1 = tri[(e + 1) % 3];
            var m = 0;
            for (var i = 0; i < count; i++)
            {
                Vector2 s = poly[i], f = poly[(i + 1) % count];
                float ds = Cross(p1 - p0, s - p0), df = Cross(p1 - p0, f - p0);
                if (ds >= 0) tmp[m++] = s;
                if (ds >= 0 != df >= 0 && m < tmp.Length) tmp[m++] = s + (f - s) * (ds / (ds - df));
            }
            tmp[..m].CopyTo(poly);
            count = m;
        }
        if (count < 3) return null;
        float area = 0;
        var centre = Vector2.Zero;
        for (var i = 0; i < count; i++)
        {
            area += Cross(poly[i], poly[(i + 1) % count]) / 2;
            centre += poly[i] / count;
        }
        if (area < minArea) return null;

        // distance from A's plane to B's plane along A's normal at each overlap corner
        float gap = 0;
        for (var i = 0; i < count; i++)
        {
            var p = a0 + u * poly[i].X + v * poly[i].Y;
            gap = MathF.Max(gap, MathF.Abs(Vector3.Dot(nb, b0 - p) / d));
        }
        return gap > maxGap ? null : new Pair(a, b, area, gap, a0 + u * centre.X + v * centre.Y);
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
