using System.Numerics;
using Touge.Formats;

namespace Touge;

/// <summary>
///     The street lamps of a course as lights. CRS_LIGHT lists the lit lamp heads of most night courses, but has errors
///     (AKINA's 3rd point has the z of the 2nd; MOMIJI's one point is a garbage float, USUI's sit 79 km off), so the night
///     geometry decides: a lit lamp head is a cluster of ≥ 8 near-white prelit triangles (mean RGB ≥ 0.7; the rest of a night
///     course is ~0.12) in a <c>crs</c> mesh 3–12 m above the nearest road point and within 15 m of it. Lamps the original
///     leaves dark are lit too where their housing is known (<see cref="DarkHeads"/>). A CRS_LIGHT point counts where a lamp
///     model stands (geometry within 1.5 m; IROHA has 3 with dark heads), one per 3 m, and wins over a head found within 3 m (its exact
///     light position). By day the course's lights are off anyway: CRS_LIGHT as it is.
/// </summary>
public static class CourseLamps
{
    /// <summary>Housing textures of lamps the original leaves dark: AKINA's 8 cobra heads along the road.</summary>
    public static readonly string[] DarkHeads = ["KINA_NIT117_015"];

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
        var lamps = new List<Vector3>();
        foreach (var p in crsLight.Where(Standing))
            if (lamps.All(l => Vector3.Distance(l, p) > 3)) lamps.Add(p); // one lamp, one light (AKINA's broken point hangs at the 2nd's pole)
        lamps.AddRange(heads.Where(h => lamps.All(l => Vector3.Distance(l, h) > 3)));
        return [.. lamps];
    }
}
