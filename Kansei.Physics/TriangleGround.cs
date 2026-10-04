using System.Numerics;

namespace Kansei.Physics;

/// <summary>
///     <see cref="IGround" /> over a static 2.5D triangle surface (indexed triangle list, one surface id and
///     wall flag per triangle). Raycasts hit only drivable (non-wall) triangles and return the face normal
///     facing the ray. Walls are not geometry but the boundary of the drivable area: every edge of a drivable
///     triangle that borders a wall triangle or nothing becomes a vertical barrier from edge height
///     <see cref="WallBelow" /> to <see cref="WallAbove" />, its normal pointing toward the drivable side.
///     Both are bucketed in a uniform XZ grid (CSR layout), so queries touch only a few cells.
/// </summary>
public sealed class TriangleGround : IGround
{
    public const float WallBelow = 1f, WallAbove = 2f;

    /// <summary>Wall segment A→B (edge of a drivable triangle) with horizontal unit normal toward the drivable side.</summary>
    public readonly record struct WallSegment(Vector3 A, Vector3 B, Vector3 Normal);

    private readonly Vector3[] _a, _e1, _e2, _n; // per drivable triangle: corner, edges, unit normal
    private readonly int[] _surface;
    private readonly Grid _tris;
    private Grid _walls;
    private readonly Vector2 _min, _max;
    private readonly float _cell;

    public WallSegment[] Walls { get; private set; }

    public TriangleGround(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices, ReadOnlySpan<int> surfaces,
        ReadOnlySpan<bool> isWall, float cellSize = 4f)
    {
        var triCount = indices.Length / 3;
        if (surfaces.Length != triCount || isWall.Length != triCount) throw new ArgumentException("one surface id and wall flag per triangle");

        // edge (lo, hi) → drivable-tri count, wall-tri count, opposite corner in a drivable tri (gives the side)
        // vertices at the same position count as one: material seams in the game's collision repeat their vertices
        // (MYOUGI 591, IROHA _0 99, USUI 6), which would otherwise turn into back-to-back walls across drivable ground
        var weld = new Dictionary<Vector3, int>();
        var same = new int[positions.Length];
        for (var v = 0; v < positions.Length; v++) same[v] = weld.TryAdd(positions[v], v) ? v : weld[positions[v]];
        var edges = new Dictionary<(int, int), (int Road, int Wall, int Apex)>();
        var drivable = new List<int>();
        for (var t = 0; t < triCount; t++)
        {
            if (!isWall[t]) drivable.Add(t);
            for (var k = 0; k < 3; k++)
            {
                int i = same[indices[t * 3 + k]], j = same[indices[t * 3 + (k + 1) % 3]], apex = indices[t * 3 + (k + 2) % 3];
                var key = i < j ? (i, j) : (j, i);
                edges.TryGetValue(key, out var e);
                edges[key] = isWall[t] ? e with { Wall = e.Wall + 1 } : (e.Road + 1, e.Wall, apex);
            }
        }

        var n = drivable.Count;
        (_a, _e1, _e2, _n, _surface) = (new Vector3[n], new Vector3[n], new Vector3[n], new Vector3[n], new int[n]);
        var triBoxes = new (Vector2 Min, Vector2 Max)[n];
        for (var i = 0; i < n; i++)
        {
            var t = drivable[i];
            Vector3 a = positions[indices[t * 3]], b = positions[indices[t * 3 + 1]], c = positions[indices[t * 3 + 2]];
            (_a[i], _e1[i], _e2[i], _surface[i]) = (a, b - a, c - a, surfaces[t]);
            var cross = Vector3.Cross(b - a, c - a);
            _n[i] = cross == Vector3.Zero ? Vector3.UnitY : Vector3.Normalize(cross);
            triBoxes[i] = (Xz(Vector3.Min(a, Vector3.Min(b, c))), Xz(Vector3.Max(a, Vector3.Max(b, c))));
        }

        var walls = new List<WallSegment>();
        foreach (var ((i, j), e) in edges)
        {
            // ponytail: edges with 2+ drivable owners never become walls, even if a wall tri also shares them (non-manifold)
            if (e.Road != 1) continue;
            Vector3 a = positions[i], b = positions[j];
            var d = Xz(b - a);
            if (d.LengthSquared() < 1e-8f) continue;
            var nrm = Vector2.Normalize(new Vector2(-d.Y, d.X));
            if (Vector2.Dot(nrm, Xz(positions[e.Apex] - a)) < 0) nrm = -nrm;
            walls.Add(new WallSegment(a, b, new Vector3(nrm.X, 0, nrm.Y)));
        }
        Walls = walls.ToArray();

        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var p in positions) (min, max) = (Vector2.Min(min, Xz(p)), Vector2.Max(max, Xz(p)));
        (_min, _max, _cell) = (min, max, cellSize);
        _tris = new Grid(min, max, cellSize, triBoxes);
        _walls = WallGrid();
    }

    /// <summary>Adds barriers that are no edge of the surface, e.g. across the road at a course end (same rules as edge walls).</summary>
    public void AddWalls(params ReadOnlySpan<WallSegment> extra)
    {
        Walls = [.. Walls, .. extra];
        _walls = WallGrid();
    }

    private Grid WallGrid() => new(_min, _max, _cell, Array.ConvertAll(Walls, w => (Vector2.Min(Xz(w.A), Xz(w.B)), Vector2.Max(Xz(w.A), Xz(w.B)))));

    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out GroundHit hit)
    {
        hit = default;
        var best = maxDistance;
        var found = -1;
        // 2D DDA over the cells the ray's XZ projection crosses, stopping once the best hit lies before the current cell
        var g = _tris;
        Vector2 o = (Xz(origin) - g.Min) / g.Cell, d = Xz(direction) / g.Cell;
        int cx = (int)MathF.Floor(o.X), cz = (int)MathF.Floor(o.Y);
        int sx = d.X > 0 ? 1 : -1, sz = d.Y > 0 ? 1 : -1;
        float dtx = d.X != 0 ? MathF.Abs(1 / d.X) : float.PositiveInfinity, dtz = d.Y != 0 ? MathF.Abs(1 / d.Y) : float.PositiveInfinity;
        float tx = d.X != 0 ? ((d.X > 0 ? cx + 1 - o.X : o.X - cx) * dtx) : float.PositiveInfinity;
        float tz = d.Y != 0 ? ((d.Y > 0 ? cz + 1 - o.Y : o.Y - cz) * dtz) : float.PositiveInfinity;
        while (true)
        {
            if (g.TryCell(cx, cz, out var start, out var end))
                for (var k = start; k < end; k++)
                {
                    var i = g.Items[k];
                    if (Intersect(i, origin, direction, best, out var t)) (best, found) = (t, i);
                }
            var exit = MathF.Min(tx, tz);
            if (exit >= best || !g.Overlaps(cx, cz, sx, sz)) break;
            if (tx < tz) (cx, tx) = (cx + sx, tx + dtx);
            else (cz, tz) = (cz + sz, tz + dtz);
        }
        if (found < 0) return false;
        var n = _n[found];
        hit = new GroundHit(origin + direction * best, Vector3.Dot(n, direction) > 0 ? -n : n, best, _surface[found]);
        return true;
    }

    /// <summary>Möller–Trumbore, two-sided.</summary>
    private bool Intersect(int i, Vector3 o, Vector3 dir, float maxT, out float t)
    {
        t = 0;
        Vector3 e1 = _e1[i], e2 = _e2[i];
        var p = Vector3.Cross(dir, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return false;
        var inv = 1 / det;
        var s = o - _a[i];
        var u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(s, e1);
        var v = Vector3.Dot(dir, q) * inv;
        if (v < 0 || u + v > 1) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t >= 0 && t < maxT;
    }

    /// <summary>
    ///     Sphere vs vertical wall quads at every probe, then wall end points vs the capsules between neighbouring
    ///     probes (closed loop, <see cref="CollideEdge" />). At most one contact per probe (the deepest) and one per edge.
    ///     A probe counts only while its centre is less than <paramref name="radius" /> behind the wall,
    ///     so a body that already tunnelled through is not pulled back across.
    /// </summary>
    public int CollideWalls(ReadOnlySpan<Vector3> probes, float radius, Span<WallContact> contacts)
    {
        var count = 0;
        var g = _walls;
        for (var pi = 0; pi < probes.Length && count < contacts.Length; pi++)
        {
            var p = probes[pi];
            var best = new WallContact(default, default, 0, -1);
            Vector2 lo = (Xz(p) - g.Min - new Vector2(radius)) / g.Cell, hi = (Xz(p) - g.Min + new Vector2(radius)) / g.Cell;
            for (var cz = (int)MathF.Floor(lo.Y); cz <= (int)MathF.Floor(hi.Y); cz++)
            for (var cx = (int)MathF.Floor(lo.X); cx <= (int)MathF.Floor(hi.X); cx++)
            {
                if (!g.TryCell(cx, cz, out var start, out var end)) continue;
                for (var k = start; k < end; k++)
                {
                    var w = Walls[g.Items[k]];
                    var ab = Xz(w.B - w.A);
                    var f = Math.Clamp(Vector2.Dot(Xz(p - w.A), ab) / ab.LengthSquared(), 0, 1);
                    var q = Vector3.Lerp(w.A, w.B, f);
                    if (p.Y < q.Y - WallBelow - radius || p.Y > q.Y + WallAbove + radius) continue;
                    var h = Xz(p - q);
                    var nrm = new Vector2(w.Normal.X, w.Normal.Z);
                    var side = Vector2.Dot(h, nrm);
                    float depth;
                    Vector2 dir;
                    if (f > 0 && f < 1) (depth, dir) = (radius - side, nrm);
                    else
                    {
                        // past an end: round cap on the drivable side only
                        var len = h.Length();
                        if (side < 0 || len < 1e-6f) continue;
                        (depth, dir) = (radius - len, h / len);
                    }
                    if (depth > 0 && depth < 2 * radius && depth > best.Depth)
                        best = new WallContact(new Vector3(q.X, p.Y, q.Z), new Vector3(dir.X, 0, dir.Y), depth, pi);
                }
            }
            if (best.ProbeIndex >= 0) contacts[count++] = best;
        }
        if (probes.Length < 3) return count;
        var centre = Vector2.Zero;
        foreach (var p in probes) centre += Xz(p) / probes.Length;
        for (var e = 0; e < probes.Length && count < contacts.Length; e++)
            if (CollideEdge(probes[e], probes[(e + 1) % probes.Length], centre, radius, probes.Length + e, out var c))
                contacts[count++] = c;
        return count;
    }

    /// <summary>
    ///     Wall end points (posts, guardrail ends, convex corners) inside the capsule <paramref name="p0" />→<paramref name="p1" />
    ///     that the probe spheres at its ends miss. Pushes along the edge's normal away from <paramref name="centre" />
    ///     (the body), deepest point only. Points past the capsule core (inside the body) are ignored, so a thin wall under
    ///     the car does not pin it between both sides; at 150 km/h the body moves 0.17 m per substep, less than the radius.
    /// </summary>
    private bool CollideEdge(Vector3 p0, Vector3 p1, Vector2 centre, float radius, int index, out WallContact contact)
    {
        contact = new WallContact(default, default, 0, index);
        var d = Xz(p1 - p0);
        var len = d.Length();
        if (len < 1e-4f) return false;
        var u = d / len;
        var outN = new Vector2(-u.Y, u.X);
        if (Vector2.Dot(outN, Xz(p0) - centre) < 0) outN = -outN;
        var g = _walls;
        Vector2 lo = (Vector2.Min(Xz(p0), Xz(p1)) - g.Min - new Vector2(radius)) / g.Cell,
            hi = (Vector2.Max(Xz(p0), Xz(p1)) - g.Min + new Vector2(radius)) / g.Cell;
        for (var cz = (int)MathF.Floor(lo.Y); cz <= (int)MathF.Floor(hi.Y); cz++)
        for (var cx = (int)MathF.Floor(lo.X); cx <= (int)MathF.Floor(hi.X); cx++)
        {
            if (!g.TryCell(cx, cz, out var start, out var end)) continue;
            for (var k = start; k < end; k++)
            {
                var w = Walls[g.Items[k]];
                if (w.Normal.X * outN.X + w.Normal.Z * outN.Y > 0.5f) continue; // wall faces away: the body is behind it
                for (var j = 0; j < 2; j++)
                {
                    var v = j == 0 ? w.A : w.B;
                    var rel = Xz(v - p0);
                    var t = Vector2.Dot(rel, u);
                    if (t <= 0 || t >= len) continue; // ends are the probes' job
                    var depth = radius - Vector2.Dot(rel, outN);
                    var y = p0.Y + (p1.Y - p0.Y) * (t / len);
                    if (depth <= contact.Depth || depth > radius || y < v.Y - WallBelow - radius || y > v.Y + WallAbove + radius) continue;
                    contact = new WallContact(new Vector3(v.X, y, v.Z), new Vector3(-outN.X, 0, -outN.Y), depth, index);
                }
            }
        }
        return contact.Depth > 0;
    }

    private static Vector2 Xz(Vector3 v) => new(v.X, v.Z);

    /// <summary>Uniform XZ grid; cell c holds Items[Start[c]..Start[c+1]] (indices of boxes overlapping it).</summary>
    private sealed class Grid
    {
        public readonly Vector2 Min;
        public readonly float Cell;
        public readonly int Width, Height;
        public readonly int[] Start, Items;

        public Grid(Vector2 min, Vector2 max, float cell, (Vector2 Min, Vector2 Max)[] boxes)
        {
            (Min, Cell) = (min, cell);
            Width = (int)((max.X - min.X) / cell) + 1;
            Height = (int)((max.Y - min.Y) / cell) + 1;
            Start = new int[Width * Height + 1];
            foreach (var b in boxes) Each(b, c => Start[c + 1]++);
            for (var c = 0; c < Width * Height; c++) Start[c + 1] += Start[c];
            Items = new int[Start[^1]];
            var fill = (int[])Start.Clone();
            for (var i = 0; i < boxes.Length; i++) Each(boxes[i], c => Items[fill[c]++] = i);
        }

        private void Each((Vector2 Min, Vector2 Max) b, Action<int> cell)
        {
            for (var z = CellZ(b.Min.Y); z <= CellZ(b.Max.Y); z++)
            for (var x = CellX(b.Min.X); x <= CellX(b.Max.X); x++)
                cell(z * Width + x);
        }

        private int CellX(float x) => Math.Clamp((int)((x - Min.X) / Cell), 0, Width - 1);
        private int CellZ(float z) => Math.Clamp((int)((z - Min.Y) / Cell), 0, Height - 1);

        public bool TryCell(int x, int z, out int start, out int end)
        {
            if ((uint)x >= (uint)Width || (uint)z >= (uint)Height) { start = end = 0; return false; }
            (start, end) = (Start[z * Width + x], Start[z * Width + x + 1]);
            return true;
        }

        /// <summary>False once stepping further in direction (sx, sz) can never reach the grid again.</summary>
        public bool Overlaps(int x, int z, int sx, int sz) =>
            !((x < 0 && sx < 0) || (x >= Width && sx > 0) || (z < 0 && sz < 0) || (z >= Height && sz > 0));
    }
}
