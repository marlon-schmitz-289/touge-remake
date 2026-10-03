using System.Numerics;
using Kansei.Graphics;
using Touge.Formats;

namespace Touge;

/// <summary>Loads a course (geometry, textures, driving line) straight from the user's ISO.</summary>
public static class CourseLoader
{
    public sealed record Course(StaticMesh World, StaticMesh Sky, Vector3[] DrivingLine);

    public static Course Load(Iso9660 iso, string courseTime, WorldRenderer renderer)
    {
        var models = Afs.FromBytes(iso.ReadFile("CDVD/DATA/MODEL/COURSE.AFS"), iso.ReadFile("CDVD/DATA/MODEL/COURSE.TBL"));
        var pac = models.Read(models.Find(courseTime + ".PAC") ?? throw new FileNotFoundException(courseTime + ".PAC"));
        var entries = Pac.Entries(pac);

        var white = renderer.AddTexture(1, 1, [255, 255, 255, 255], "white");
        var textures = new Dictionary<string, int>();
        foreach (var e in entries.Where(e => e.Type == 1))
        {
            var (w, h, rgba) = Gim.Decode(pac.AsSpan(e.Offset, e.Size));
            textures[e.Name] = renderer.AddTexture(w, h, rgba, e.Name);
        }

        // tree* are local-space templates (placement data not decoded yet), lod/shd are not drawn
        var meshes = entries.Where(e => e.Type == 3 && !e.Name.Contains("lod") && !e.Name.StartsWith("shd") && !e.Name.StartsWith("tree"))
            .Select(e => (e.Name, Mesh: Mesh.Parse(pac.AsSpan(e.Offset, e.Size)))).ToList();
        var world = Build(renderer.Device, meshes.Where(m => m.Name != "sky").Select(m => m.Mesh), textures, white);
        var sky = Build(renderer.Device, meshes.Where(m => m.Name == "sky").Select(m => m.Mesh), textures, white);

        var name = courseTime[..courseTime.LastIndexOf('_')];
        var data = Afs.FromBytes(iso.ReadFile("CDVD/DATA/COURSE/CRS_DATA.AFS"), iso.ReadFile("CDVD/DATA/COURSE/CRS_DATA.TBL"));
        var drv = data.Find($"CRS_DRV_{name}_I.BIN") ?? throw new FileNotFoundException($"CRS_DRV_{name}_I.BIN");
        var line = DrivingLine.Read(data.Read(drv), DrivingLine.PointCount(name));
        return new Course(world, sky, line);
    }

    /// <summary>
    ///     Flattens meshes into one vertex/index buffer, one batch per material (texture).
    ///     Drops triangles whose corners coincide with an earlier one (either winding): the data stores
    ///     double-sided cards as two opposite triangles and repeats geometry at section seams — drawn
    ///     without culling they z-fight.
    /// </summary>
    private static StaticMesh Build(Penelope.IPenelopeDevice device, IEnumerable<Mesh> meshes, Dictionary<string, int> textures, int white)
    {
        var verts = new List<WorldVertex>();
        var indices = new List<uint>();
        var batches = new List<MeshBatch>();
        var seen = new HashSet<(Vector3, Vector3, Vector3)>();
        foreach (var mesh in meshes)
        foreach (var m in mesh.Materials)
        {
            var tex = m.Texture >= 0 && m.Texture < mesh.Textures.Length && textures.TryGetValue(mesh.Textures[m.Texture], out var t) ? t : white;
            var first = indices.Count;
            for (var i = 0; i < m.Triangles.Count; i += 3)
            {
                if (!seen.Add(Key(m.Triangles[i].Position, m.Triangles[i + 1].Position, m.Triangles[i + 2].Position))) continue;
                for (var k = 0; k < 3; k++)
                {
                    var v = m.Triangles[i + k];
                    indices.Add((uint)verts.Count);
                    verts.Add(new WorldVertex(v.Position, v.Uv, v.Color));
                }
            }
            if (indices.Count > first) batches.Add(new MeshBatch(tex, first, indices.Count - first));
        }
        return new StaticMesh(device, verts.ToArray(), indices.ToArray(), batches);
    }

    /// <summary>Order-independent key of a triangle's corners, rounded to 1 cm.</summary>
    private static (Vector3, Vector3, Vector3) Key(Vector3 a, Vector3 b, Vector3 c)
    {
        static Vector3 R(Vector3 v) => new(MathF.Round(v.X, 2), MathF.Round(v.Y, 2), MathF.Round(v.Z, 2));
        Span<Vector3> p = [R(a), R(b), R(c)];
        p.Sort((x, y) => x.X != y.X ? x.X.CompareTo(y.X) : x.Y != y.Y ? x.Y.CompareTo(y.Y) : x.Z.CompareTo(y.Z));
        return (p[0], p[1], p[2]);
    }
}
