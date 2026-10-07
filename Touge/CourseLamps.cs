using System.Numerics;
using Touge.Formats;

namespace Touge;

/// <summary>
///     The street lamps of a course as lights. CRS_LIGHT lists the lit lamp heads of most night courses, but has errors
///     (AKINA's 3rd point has the z of the 2nd; MOMIJI's one point is a garbage float, USUI's sit 79 km off), so the night
///     geometry decides: a lit lamp head is a cluster of ≥ 8 near-white prelit triangles (mean RGB ≥ 0.7; the rest of a night
///     course is ~0.12) in a <c>crs</c> mesh 3–12 m above the nearest road point and within 15 m of it. Lamps the original
///     leaves dark are lit too where their housing is known (<see cref="DarkHeads"/>). A CRS_LIGHT point counts where a lamp
///     model stands (geometry within 1.5 m; IROHA has 3 with dark heads) and no head was found within 3 m. By day the course's
///     lights are off anyway: CRS_LIGHT as it is.
/// </summary>
public static class CourseLamps
{
    /// <summary>Housing textures of lamps the original leaves dark: AKINA's 8 cobra heads along the road.</summary>
    public static readonly string[] DarkHeads = ["KINA_NIT117_015"];

    /// <summary>
    ///     Linear-ish HDR vertex colour (world.vert squares it ~×2.2) of a lit lamp glass: warm and well over white, so the bloom
    ///     draws the glare around it — the original's flat, ragged near-white card read as a matte paper patch. Tuned by eye.
    /// </summary>
    public static readonly Vector4 GlassColour = new(2.4f, 2.05f, 1.6f, 1);

    /// <summary>
    ///     Night: the glass of every lamp in <paramref name="lamps"/> glows (<see cref="GlassColour"/>): the near-white triangles
    ///     within 2 m of it, and on the dark heads (<see cref="DarkHeads"/>) the housing's downward faces.
    /// </summary>
    public static void Glow(IReadOnlyList<(string Name, Mesh Mesh)> meshes, Vector3[] lamps)
    {
        foreach (var (_, mesh) in meshes.Where(m => m.Name.StartsWith("crs")))
        foreach (var m in mesh.Materials)
        {
            var dark = m.Texture >= 0 && m.Texture < mesh.Textures.Length && DarkHeads.Contains(mesh.Textures[m.Texture]);
            var t = m.Triangles;
            for (var i = 0; i + 2 < t.Count; i += 3)
            {
                var p = (t[i].Position + t[i + 1].Position + t[i + 2].Position) / 3;
                if (!lamps.Any(l => Vector3.DistanceSquared(l, p) < 4)) continue;
                var c = (t[i].Color + t[i + 1].Color + t[i + 2].Color) / 3;
                var down = Vector3.Cross(t[i + 1].Position - t[i].Position, t[i + 2].Position - t[i].Position);
                if (dark ? Math.Abs(down.Y) < 0.7f * down.Length() : (c.X + c.Y + c.Z) / 3 < 0.7f) continue; // dark heads: the flat underside (either winding)
                for (var k = 0; k < 3; k++) t[i + k] = t[i + k] with { Color = GlassColour with { W = t[i + k].Color.W } };
            }
        }
    }

    public static Vector3[] Find(IReadOnlyList<(string Name, Mesh Mesh)> meshes, Vector3[] road, Vector3[] crsLight, bool night)
    {
        if (!night || road.Length == 0) return crsLight;
        var crs = meshes.Where(m => m.Name.StartsWith("crs")).ToArray();
        var seen = new HashSet<Vector3>();
        var points = new List<Vector3>();
        foreach (var (_, mesh) in crs)
        foreach (var m in mesh.Materials)
        {
            var dark = m.Texture >= 0 && m.Texture < mesh.Textures.Length && DarkHeads.Contains(mesh.Textures[m.Texture]);
            for (var i = 0; i + 2 < m.Triangles.Count; i += 3)
            {
                var c = (m.Triangles[i].Color + m.Triangles[i + 1].Color + m.Triangles[i + 2].Color) / 3;
                if (!dark && (c.X + c.Y + c.Z) / 3 < 0.7f) continue;
                var p = (m.Triangles[i].Position + m.Triangles[i + 1].Position + m.Triangles[i + 2].Position) / 3;
                if (!seen.Add(Vector3.Round(p * 100))) continue; // sections overlap at their seams
                var r = road.MinBy(q => Vector3.DistanceSquared(q, p));
                if (p.Y - r.Y is < 3 or > 12 || Vector2.Distance(new(r.X, r.Z), new(p.X, p.Z)) > 15) continue;
                points.Add(p);
            }
        }
        // single-link clusters within 2.5 m
        var groups = new List<List<Vector3>>();
        foreach (var p in points)
        {
            var hit = groups.Where(g => g.Any(q => Vector3.Distance(p, q) < 2.5f)).ToList();
            var into = hit.FirstOrDefault() ?? [];
            if (hit.Count == 0) groups.Add(into);
            foreach (var g in hit.Skip(1))
            {
                into.AddRange(g);
                groups.Remove(g);
            }
            into.Add(p);
        }
        var heads = groups.Where(g => g.Count >= 8).Select(g => g.Aggregate(Vector3.Zero, (a, q) => a + q) / g.Count).ToList();

        bool Standing(Vector3 p) => float.IsFinite(p.X + p.Y + p.Z) && crs.Any(m => m.Mesh.Materials.Any(x => x.Triangles.Any(v => Vector3.DistanceSquared(v.Position, p) < 1.5f * 1.5f)));
        // the heads first: the light sits at the lit glass (CRS_LIGHT points sit ~0.4 m above it, inside the housing, which then
        // shadowed everything under the lamp); a CRS_LIGHT point only where no head is found (IROHA's dark ones), one per 3 m
        var lamps = new List<Vector3>(heads);
        foreach (var p in crsLight.Where(Standing))
            if (lamps.All(l => Vector3.Distance(l, p) > 3)) lamps.Add(p);
        return [.. lamps];
    }
}
