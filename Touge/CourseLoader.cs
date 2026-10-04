using System.Numerics;
using Kansei.Graphics;
using Touge.Formats;

namespace Touge;

/// <summary>Loads a course (geometry, textures, driving line, env maps, lights) straight from the user's ISO.</summary>
public static class CourseLoader
{
    /// <param name="Road">CRS_ROAD centre points.</param>
    /// <param name="Env">Per road point the renderer texture indices of ENV_TOP/BOTTOM/LEFT/RIGHT (null: course has no env maps).</param>
    /// <param name="Lights">CRS_LIGHT points (empty if none).</param>
    /// <param name="FogColour">The original's fog colour for this course and time of day (CRS_INFO, gamma 0..1), null if missing.</param>
    public sealed record Course(StaticMesh World, StaticMesh Sky, Vector3[] DrivingLine, Vector3[] Road, int[][]? Env, Vector3[] Lights, Vector3? FogColour)
    {
        /// <summary>Index of the road point nearest to <paramref name="p"/>.</summary>
        public int NearestRoadPoint(Vector3 p)
        {
            // ponytail: linear scan over ~4000 points per frame, a hint index if it ever shows in a profile
            var best = 0;
            for (var i = 1; i < Road.Length; i++)
                if (Vector3.DistanceSquared(Road[i], p) < Vector3.DistanceSquared(Road[best], p)) best = i;
            return best;
        }
    }

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
        var world = Build(renderer.Device, meshes.Where(m => !m.Name.StartsWith("sky")).Select(m => m.Mesh), textures, white);
        var sky = Build(renderer.Device, meshes.Where(m => m.Name.StartsWith("sky")).Select(m => m.Mesh), textures, white);

        var course = courseTime[..courseTime.LastIndexOf('_')];
        var data = Afs.FromBytes(iso.ReadFile("CDVD/DATA/COURSE/CRS_DATA.AFS"), iso.ReadFile("CDVD/DATA/COURSE/CRS_DATA.TBL"));
        byte[]? Data(string name) => data.Find(name) is { } e ? data.Read(e) : null;
        var road = CourseRoad.Read(Data($"CRS_ROAD_{course}.BIN") ?? throw new FileNotFoundException($"CRS_ROAD_{course}.BIN"));
        var lights = Data($"CRS_LIGHT_{course}.BIN") is { } l ? CourseRoad.ReadLights(l) : [];
        Vector3? fog = Data($"CRS_INFO_{course}.BIN") is { } cif ? CourseInfo.FogColour(cif, CourseInfo.FogSlot(courseTime[(courseTime.LastIndexOf('_') + 1)..])) : null;
        return new Course(world, sky, ReadDrivingLine(iso, course), road, LoadEnv(models, Data($"CRS_ENV_{course}.BIN"), courseTime, road.Length, renderer), lights, fog);
    }

    /// <summary>
    ///     ENV_TEX_&lt;course&gt;_&lt;time&gt;.PAC → renderer textures, CRS_ENV indices → per road point (index mod the ENV
    ///     length like the game) the four texture ids. Missing maps or table: null (renderer keeps white).
    /// </summary>
    private static int[][]? LoadEnv(Afs models, byte[]? table, string courseTime, int roadCount, WorldRenderer renderer)
    {
        if (table == null || models.Find($"ENV_TEX_{courseTime}.PAC") is not { } entry) return null;
        var pac = models.Read(entry);
        var tex = new Dictionary<string, int>();
        foreach (var e in Pac.Entries(pac).Where(e => e.Type == 1))
        {
            var (w, h, rgba) = Gim.Decode(pac.AsSpan(e.Offset, e.Size));
            tex[e.Name] = renderer.AddTexture(w, h, rgba, e.Name, 0);
        }
        var env = CourseRoad.ReadEnv(table);
        int Id(string kind, sbyte i) => tex.TryGetValue($"ENV_{kind}{Math.Max((int)i, 0):D2}", out var t) ? t : tex[$"ENV_{kind}00"];
        return
        [
            .. Enumerable.Range(0, roadCount).Select(i => env[i % env.Length])
                .Select(e => new[] { Id("TOP", e.Top), Id("BOTTOM", e.Bottom), Id("LEFT", e.Left), Id("RIGHT", e.Right) }),
        ];
    }

    /// <summary>CRS_DRV_&lt;course&gt;_I.BIN, valid points only.</summary>
    public static Vector3[] ReadDrivingLine(Iso9660 iso, string course)
    {
        var data = Afs.FromBytes(iso.ReadFile("CDVD/DATA/COURSE/CRS_DATA.AFS"), iso.ReadFile("CDVD/DATA/COURSE/CRS_DATA.TBL"));
        var drv = data.Find($"CRS_DRV_{course}_I.BIN") ?? throw new FileNotFoundException($"CRS_DRV_{course}_I.BIN");
        return DrivingLine.Read(data.Read(drv), DrivingLine.PointCount(course));
    }

    /// <summary>
    ///     Flattens meshes into one vertex/index buffer, one batch per material (texture).
    ///     Drops triangles whose corners coincide with an earlier one (either winding): the data stores
    ///     double-sided cards as two opposite triangles and repeats geometry at section seams — drawn
    ///     without culling they z-fight. Normals: <see cref="Normals.Smooth"/> over everything, so seams between
    ///     sections stay smooth.
    /// </summary>
    private static StaticMesh Build(Penelope.IPenelopeDevice device, IEnumerable<Mesh> meshes, Dictionary<string, int> textures, int white)
    {
        var indices = new List<uint>();
        var batches = new List<MeshBatch>();
        var seen = new HashSet<(Vector3, Vector3, Vector3)>();
        var corners = new List<Mesh.Vertex>();
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
                    indices.Add((uint)corners.Count);
                    corners.Add(v);
                }
            }
            if (indices.Count > first) batches.Add(new MeshBatch(tex, first, indices.Count - first));
        }
        var normals = Normals.Smooth(corners.Select(v => v.Position).ToArray());
        var verts = corners.Select((v, i) => new WorldVertex(v.Position, v.Uv, v.Color, normals[i])).ToArray();
        return new StaticMesh(device, verts, indices.ToArray(), batches);
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
