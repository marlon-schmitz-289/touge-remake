using System.Diagnostics;
using System.Numerics;
using Kansei.Physics;
using Touge.Formats;

namespace Touge;

/// <summary>Course collision (CRS_COLI_&lt;COURSE&gt;_&lt;dir&gt;.BIN) as <see cref="IGround" />. Surface = material index (see <see cref="Collision.Materials" />).</summary>
public static class CourseGround
{
    /// <remarks>Direction 1 without its own file (circuits MYOUGI0/USUI0) uses _0, like the game (0x1649C0).</remarks>
    public static (TriangleGround Ground, Collision Collision) Load(Iso9660 iso, string course, int direction = 0)
    {
        var data = Afs.FromBytes(iso.ReadFile("CDVD/DATA/COURSE/CRS_DATA.AFS"), iso.ReadFile("CDVD/DATA/COURSE/CRS_DATA.TBL"));
        var name = $"CRS_COLI_{course}_{direction}.BIN";
        var c = Collision.Parse(data.Read(data.Find(name) ?? data.Find($"CRS_COLI_{course}_0.BIN") ?? throw new FileNotFoundException(name)));
        var f = c.Faces;
        var indices = new int[f.Length * 3];
        for (var i = 0; i < f.Length; i++) (indices[i * 3], indices[i * 3 + 1], indices[i * 3 + 2]) = (f[i].A, f[i].B, f[i].C);
        return (new TriangleGround(c.Positions, indices, Array.ConvertAll(f, x => x.Material), Array.ConvertAll(f, x => x.IsWall)), c);
    }

    /// <summary>
    ///     --ground &lt;png&gt;: times 1e6 downward raycasts around the driving line and writes a top-down
    ///     image (road grey, other drivable green, wall faces dark, derived wall segments red with blue
    ///     normal ticks) of the whole course plus a 200 m detail around driving-line point <paramref name="at" />.
    /// </summary>
    public static void Proof(Iso9660 iso, string course, string png, int at, bool reverse = false)
    {
        var sw = Stopwatch.StartNew();
        var (g, c) = Load(iso, course, reverse ? 1 : 0);
        Console.WriteLine($"[Ground] {course}: {c.Faces.Length} Dreiecke, {g.Walls.Length} Wandsegmente, gebaut in {sw.ElapsedMilliseconds} ms");

        var line = CourseLoader.ReadDrivingLine(iso, course, reverse);
        const int n = 1_000_000;
        var rng = new Random(1);
        var origins = new Vector3[n];
        for (var i = 0; i < n; i++)
        {
            var k = rng.Next(line.Length - 1);
            var p = Vector3.Lerp(line[k], line[k + 1], rng.NextSingle());
            origins[i] = p + new Vector3(rng.NextSingle() * 16 - 8, 5, rng.NextSingle() * 16 - 8);
        }
        for (var i = 0; i < 10_000; i++) g.Raycast(origins[i], -Vector3.UnitY, 20, out _); // warm-up
        var hits = 0;
        sw.Restart();
        foreach (var o in origins)
            if (g.Raycast(o, -Vector3.UnitY, 20, out _)) hits++;
        var ns = sw.Elapsed.TotalNanoseconds / n;
        Console.WriteLine($"[Ground] {n} Raycasts (Fahrlinie ±8 m, 5 m darüber, nach unten, max 20 m): Trefferquote {100.0 * hits / n:F1} %, {ns:F0} ns/Ray");

        // top-down rasters, +X right, +Z down (seen from above): whole course, then 200 m around driving-line point `at`
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var p in c.Positions) (min, max) = (Vector2.Min(min, new(p.X, p.Z)), Vector2.Max(max, new(p.X, p.Z)));
        var scale = 3000 / MathF.Max(max.X - min.X, max.Y - min.Y);
        Raster(png, g, c, min, scale, (int)((max.X - min.X) * scale) + 1, (int)((max.Y - min.Y) * scale) + 1);
        var centre = line[Math.Clamp(at, 0, line.Length - 1)];
        Raster(Path.ChangeExtension(png, null) + $"_at{at}.png", g, c, new Vector2(centre.X, centre.Z) - new Vector2(100), 6, 1200, 1200);
    }

    private static void Raster(string png, TriangleGround g, Collision c, Vector2 min, float scale, int w, int h)
    {
        var rgba = new byte[w * h * 4];
        Array.Fill(rgba, (byte)255);
        Vector2 Px(Vector3 v) => new((v.X - min.X) * scale, (v.Z - min.Y) * scale);
        void Set(int x, int y, (byte R, byte G, byte B) col)
        {
            if ((uint)x >= w || (uint)y >= h) return;
            var o = (y * w + x) * 4;
            (rgba[o], rgba[o + 1], rgba[o + 2]) = col;
        }
        void Line(Vector2 a, Vector2 b, (byte, byte, byte) col)
        {
            var steps = (int)Vector2.Distance(a, b) * 2 + 1;
            for (var i = 0; i <= steps; i++)
            {
                var p = Vector2.Lerp(a, b, (float)i / steps);
                for (var dy = 0; dy < 2; dy++)
                for (var dx = 0; dx < 2; dx++)
                    Set((int)p.X + dx, (int)p.Y + dy, col);
            }
        }
        foreach (var f in c.Faces)
        {
            var col = f.IsWall ? ((byte)70, (byte)70, (byte)80) : c.Materials[f.Material].Contains("road") ? ((byte)150, (byte)150, (byte)150) : ((byte)120, (byte)170, (byte)110);
            Vector2 a = Px(c.Positions[f.A]), b = Px(c.Positions[f.B]), d = Px(c.Positions[f.C]);
            var area = Cross(b - a, d - a);
            if (MathF.Abs(area) < 1e-6f) continue;
            for (var y = (int)MathF.Max(0, MathF.Min(a.Y, MathF.Min(b.Y, d.Y))); y <= (int)MathF.Min(h - 1, MathF.Max(a.Y, MathF.Max(b.Y, d.Y))); y++)
            for (var x = (int)MathF.Max(0, MathF.Min(a.X, MathF.Min(b.X, d.X))); x <= (int)MathF.Min(w - 1, MathF.Max(a.X, MathF.Max(b.X, d.X))); x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float u = Cross(b - p, d - p) / area, v = Cross(d - p, a - p) / area;
                if (u >= 0 && v >= 0 && u + v <= 1) Set(x, y, col);
            }
        }
        foreach (var s in g.Walls)
        {
            Vector2 a = Px(s.A), b = Px(s.B), m = (a + b) / 2;
            Line(a, b, (230, 30, 30));
            Line(m, m + new Vector2(s.Normal.X, s.Normal.Z) * scale, (40, 60, 230)); // 1 m tick toward the drivable side
        }
        Png.Write(png, w, h, rgba);
        Console.WriteLine($"[Ground] Draufsicht {w}×{h} -> {png}");
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
