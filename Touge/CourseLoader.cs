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
    /// <param name="Fog">The original's linear fog start/end in metres for this time of day (CRS_INFO), null if missing.</param>
    /// <param name="SunDirection">Towards the original's key light (CRS_INFO light 0, its car lighting), null if missing.</param>
    public sealed record Course(StaticMesh World, StaticMesh Sky, Vector3[] DrivingLine, Vector3[] Road, int[][]? Env, Vector3[] Lights, Vector3? FogColour,
        (float Start, float End)? Fog, Vector3? SunDirection)
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

        var course = courseTime[..courseTime.LastIndexOf('_')];
        var data = Afs.FromBytes(iso.ReadFile("CDVD/DATA/COURSE/CRS_DATA.AFS"), iso.ReadFile("CDVD/DATA/COURSE/CRS_DATA.TBL"));
        byte[]? Data(string name) => data.Find(name) is { } e ? data.Read(e) : null;
        var road = CourseRoad.Read(Data($"CRS_ROAD_{course}.BIN") ?? throw new FileNotFoundException($"CRS_ROAD_{course}.BIN"));

        // lod/shd are not drawn; the tree templates are placed from TREE_* (baked into the world)
        var world = Build(renderer.Device, [.. Meshes(pac, false), .. Trees(pac, course, Data, road)], textures, white, true);
        var sky = Build(renderer.Device, Meshes(pac, true), textures, white, false); // no depth: paint order stays file order

        var lights = Data($"CRS_LIGHT_{course}.BIN") is { } l ? CourseRoad.ReadLights(l) : [];
        var slot = CourseInfo.FogSlot(courseTime[(courseTime.LastIndexOf('_') + 1)..]);
        var cif = Data($"CRS_INFO_{course}.BIN");
        return new Course(world, sky, ReadDrivingLine(iso, course), road, LoadEnv(models, Data($"CRS_ENV_{course}.BIN"), courseTime, road.Length, renderer), lights,
            cif == null ? null : CourseInfo.FogColour(cif, slot), cif == null ? null : CourseInfo.FogRange(cif, slot),
            cif == null ? null : -CourseInfo.KeyLight(cif, slot).Direction);
    }

    /// <summary>
    ///     TREE_M/TREE_L_&lt;course&gt;_L/_R → the templates <c>treeMid|Lrg_L|R%02d</c> placed in world space
    ///     (<see cref="CourseTrees.Placement"/>, game 0x164F40), all copies of one template merged into one mesh (few batches).
    ///     <c>treelod*</c> is never used by that loader. Duplicate PAC names (Iroha): the first wins.
    /// </summary>
    private static IEnumerable<(string Name, Mesh Mesh)> Trees(byte[] pac, string course, Func<string, byte[]?> data, Vector3[] road)
    {
        var templates = new Dictionary<string, Mesh>();
        foreach (var e in Pac.Entries(pac).Where(e => e.Type == 3 && e.Name.StartsWith("tree")))
            if (!templates.ContainsKey(e.Name)) templates[e.Name] = Mesh.Parse(pac.AsSpan(e.Offset, e.Size));
        var placed = new Dictionary<string, Mesh>();
        foreach (var (file, size) in new[] { ("M", "Mid"), ("L", "Lrg") })
            foreach (var side in new[] { "L", "R" })
            {
                if (data($"TREE_{file}_{course}_{side}.BIN") is not { } bin) continue;
                foreach (var t in CourseTrees.Read(bin))
                {
                    var name = $"tree{size}_{side}{t.Template:D2}";
                    if (!templates.TryGetValue(name, out var mesh)) continue;
                    if (!placed.TryGetValue(name, out var into))
                        placed[name] = into = new Mesh { Textures = mesh.Textures, Nodes = [], Materials = [.. mesh.Materials.Select(x => x with { Triangles = [] })] };
                    var m = CourseTrees.Placement(t, road);
                    for (var k = 0; k < mesh.Materials.Length; k++)
                        into.Materials[k].Triangles.AddRange(mesh.Materials[k].Triangles.Select(v => v with { Position = Vector3.Transform(v.Position, m) }));
                }
            }
        return placed.Select(p => (p.Key, p.Value));
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
    ///     Flattens meshes into one vertex/index buffer, one batch per material (texture) in file order
    ///     (<see cref="Flatten"/>). Normals: <see cref="Normals.Smooth"/> over everything, so seams between
    ///     sections stay smooth. <paramref name="layered"/>: triangles lying on earlier coplanar ones (decals, overlapping
    ///     sections, <see cref="ZFight"/>) become overlay layers so the later one wins like on the PS2 (<see cref="Layered"/>).
    /// </summary>
    private static StaticMesh Build(Penelope.IPenelopeDevice device, IEnumerable<(string Name, Mesh Mesh)> meshes, Dictionary<string, int> textures, int white,
        bool layered)
    {
        var (corners, batches) = Flatten(meshes);
        var positions = corners.Select(v => v.Position).ToArray();
        var normals = Normals.Smooth(positions);
        var verts = corners.Select((v, i) => new WorldVertex(v.Position, v.Uv, v.Color, normals[i])).ToArray();
        var (indices, ranges) = Layered(positions, [.. batches.Select(b => b.First)], layered);
        return new StaticMesh(device, verts, indices,
            [.. ranges.Select(r => new MeshBatch(textures.GetValueOrDefault(batches[r.Batch].Texture, white), r.First * 3, r.Count * 3, r.Layer))]);
    }

    /// <summary>
    ///     Index buffer (triangle list over <paramref name="positions"/>) and draw ranges in triangles: each batch
    ///     (<paramref name="batchStart"/> = first triangle) split into its <see cref="ZFight.Layers"/>, layer 0 first,
    ///     file order within a layer. Without <paramref name="layered"/> everything stays layer 0 in file order.
    /// </summary>
    public static (uint[] Indices, List<(int Batch, int Layer, int First, int Count)> Ranges) Layered(Vector3[] positions, List<int> batchStart, bool layered)
    {
        var tris = positions.Length / 3;
        var layer = layered ? ZFight.Layers(tris, ZFight.Find(positions)) : new int[tris];
        var ranges = ZFight.Order(batchStart, layer, out var order);
        var indices = new uint[tris * 3];
        for (var i = 0; i < tris; i++)
            for (var k = 0; k < 3; k++)
                indices[i * 3 + k] = (uint)(order[i] * 3 + k);
        return (indices, ranges);
    }

    /// <summary>
    ///     Triangle corners in draw order (file order: mesh, material) and per batch its first triangle, texture name
    ///     and a label (mesh/material). Drops triangles whose corners coincide with an earlier one (either winding):
    ///     the data stores double-sided cards as two opposite triangles and repeats geometry at section seams — drawn
    ///     without culling they z-fight.
    /// </summary>
    public static (List<Mesh.Vertex> Corners, List<(int First, string Texture, string Label)> Batches) Flatten(IEnumerable<(string Name, Mesh Mesh)> meshes)
    {
        var batches = new List<(int, string, string)>();
        var seen = new HashSet<(Vector3, Vector3, Vector3)>();
        var corners = new List<Mesh.Vertex>();
        foreach (var (name, mesh) in meshes)
            for (var mi = 0; mi < mesh.Materials.Length; mi++)
            {
                var m = mesh.Materials[mi];
                var tex = m.Texture >= 0 && m.Texture < mesh.Textures.Length ? mesh.Textures[m.Texture] : "";
                var first = corners.Count;
                for (var i = 0; i < m.Triangles.Count; i += 3)
                {
                    if (!seen.Add(Key(m.Triangles[i].Position, m.Triangles[i + 1].Position, m.Triangles[i + 2].Position))) continue;
                    for (var k = 0; k < 3; k++) corners.Add(m.Triangles[i + k]);
                }
                if (corners.Count > first) batches.Add((first / 3, tex, $"{name}/m{mi}:{tex}"));
            }
        return (corners, batches);
    }

    /// <summary>Course meshes as drawn: no tree templates, LOD or shadow meshes; sky separate.</summary>
    public static List<(string Name, Mesh Mesh)> Meshes(byte[] pac, bool sky) =>
    [
        .. Pac.Entries(pac).Where(e => e.Type == 3 && !e.Name.Contains("lod") && !e.Name.StartsWith("shd") && !e.Name.StartsWith("tree")
                                       && e.Name.StartsWith("sky") == sky)
            .Select(e => (e.Name, Mesh.Parse(pac.AsSpan(e.Offset, e.Size)))),
    ];

    /// <summary>Order-independent key of a triangle's corners, rounded to 1 cm.</summary>
    private static (Vector3, Vector3, Vector3) Key(Vector3 a, Vector3 b, Vector3 c)
    {
        static Vector3 R(Vector3 v) => new(MathF.Round(v.X, 2), MathF.Round(v.Y, 2), MathF.Round(v.Z, 2));
        Span<Vector3> p = [R(a), R(b), R(c)];
        p.Sort((x, y) => x.X != y.X ? x.X.CompareTo(y.X) : x.Y != y.Y ? x.Y.CompareTo(y.Y) : x.Z.CompareTo(y.Z));
        return (p[0], p[1], p[2]);
    }
}
