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

        var meshes = entries.Where(e => e.Type == 3 && !e.Name.Contains("lod") && !e.Name.StartsWith("shd"))
            .Select(e => (e.Name, Mesh: Mesh.Parse(pac.AsSpan(e.Offset, e.Size)))).ToList();
        var world = Build(renderer.Device, meshes.Where(m => m.Name != "sky").Select(m => m.Mesh), textures, white);
        var sky = Build(renderer.Device, meshes.Where(m => m.Name == "sky").Select(m => m.Mesh), textures, white);

        var name = courseTime[..courseTime.LastIndexOf('_')];
        var data = Afs.FromBytes(iso.ReadFile("CDVD/DATA/COURSE/CRS_DATA.AFS"), iso.ReadFile("CDVD/DATA/COURSE/CRS_DATA.TBL"));
        var drv = data.Find($"CRS_DRV_{name}_I.BIN") ?? throw new FileNotFoundException($"CRS_DRV_{name}_I.BIN");
        var line = DrivingLine.Read(data.Read(drv), DrivingLine.PointCount(name));
        return new Course(world, sky, line);
    }

    /// <summary>Flattens meshes into one vertex/index buffer, one batch per material (texture).</summary>
    private static StaticMesh Build(Penelope.IPenelopeDevice device, IEnumerable<Mesh> meshes, Dictionary<string, int> textures, int white)
    {
        var verts = new List<WorldVertex>();
        var indices = new List<uint>();
        var batches = new List<MeshBatch>();
        foreach (var mesh in meshes)
        foreach (var m in mesh.Materials)
        {
            if (m.Triangles.Count == 0) continue;
            var tex = m.Texture >= 0 && m.Texture < mesh.Textures.Length && textures.TryGetValue(mesh.Textures[m.Texture], out var t) ? t : white;
            batches.Add(new MeshBatch(tex, indices.Count, m.Triangles.Count));
            foreach (var v in m.Triangles)
            {
                indices.Add((uint)verts.Count);
                verts.Add(new WorldVertex(v.Position, v.Uv, v.Color));
            }
        }
        return new StaticMesh(device, verts.ToArray(), indices.ToArray(), batches);
    }
}
