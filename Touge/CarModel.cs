using System.Numerics;
using Kansei.Graphics;
using Touge.Formats;

namespace Touge;

/// <summary>
///     A car from HCAR.AFS with paint from CAR_ENV.BIN: default body parts as one mesh, one wheel mesh
///     (tire + brake disk; calipers left out) and the four wheel transforms in car space (fr_l, fr_r, re_l, re_r).
/// </summary>
public sealed record CarModel(StaticMesh Body, StaticMesh Wheel, Matrix4x4[] Wheels, float WheelRadius) : IDisposable
{
    public static CarModel Load(Iso9660 iso, string car, int paint, WorldRenderer renderer)
    {
        var hcar = Afs.FromBytes(iso.ReadFile("CDVD/DATA/MODEL/HCAR.AFS"), iso.ReadFile("CDVD/DATA/MODEL/HCAR.TBL"));
        var pac = hcar.Read(hcar.Find(car + ".PAC") ?? throw new FileNotFoundException(car + ".PAC"));
        var colours = CarPaint.Parse(iso.ReadFile("CDVD/DATA/BINARY/CAR_ENV.BIN"))[Array.IndexOf(CarPaint.Cars, car)];
        var entries = Pac.Entries(pac);

        var textures = new Dictionary<string, int> { [""] = renderer.AddTexture(1, 1, [255, 255, 255, 255], "white") };
        foreach (var e in entries.Where(e => e.Type == 1))
        {
            var (w, h, rgba) = Gim.Decode(pac.AsSpan(e.Offset, e.Size));
            textures[e.Name] = renderer.AddTexture(w, h, rgba, e.Name, 0.5f); // car.frag alpha test
        }
        var parts = entries.Where(e => e.Type == 3 && Mesh.IsCmd(pac.AsSpan(e.Offset, e.Size)))
            .ToDictionary(e => e.Name[(car.Length + 1)..], e => CarPaint.Apply(Mesh.Parse(pac.AsSpan(e.Offset, e.Size)), colours[paint]));

        var tire = parts["tire00FL"];
        var radius = tire.Materials.SelectMany(m => m.Triangles).Max(v => v.Position.Y);
        return new CarModel(
            Build(renderer.Device, parts.Where(p => CarParts.IsDefaultBody(p.Key)).Select(p => (p.Key, p.Value)), textures),
            Build(renderer.Device, [("tire00FL", tire), ("Bdisk00", parts["Bdisk00"])], textures),
            CarParts.Wheels(parts["body00"]), radius);
    }

    /// <summary>
    ///     One batch per material in <see cref="Flatten"/> order, split into overlay layers where parts lie on each
    ///     other (<see cref="CourseLoader.Layered"/>: decals, emblems, lamps, stacked trim). Material RGB (0x80 = 1.0) is baked into the vertex
    ///     colour, alpha carries the kind for car.frag: paint 1 (flag 0x100, and the untextured 0x1000 parts =
    ///     the near-black lower body), windows (part <c>wind</c>) 0.5, rear lamps (part <c>Blamp</c>) 2, else 0 (matte).
    /// </summary>
    private static StaticMesh Build(Penelope.IPenelopeDevice device, IEnumerable<(string Name, Mesh Mesh)> meshes, Dictionary<string, int> textures)
    {
        var verts = new List<CarVertex>();
        var batches = new List<(int Texture, int First)>();
        foreach (var (name, mesh, m) in Flatten(meshes))
        {
            var tex = textures[m.Texture >= 0 && m.Texture < mesh.Textures.Length ? mesh.Textures[m.Texture] : ""];
            var rgb = new Vector3(m.Rgba & 0xFF, (m.Rgba >> 8) & 0xFF, (m.Rgba >> 16) & 0xFF) / 128f;
            var gloss = name.StartsWith("Blamp") ? 2f : name.StartsWith("wind") ? 0.5f
                : (m.Flags & CarPaint.PaintFlag) != 0 || m.Flags == 0x1000 && m.Texture < 0 && !name.StartsWith("tire") ? 1f : 0f;
            var first = verts.Count;
            foreach (var v in m.Triangles) verts.Add(new CarVertex(v.Position, v.Normal, v.Uv, new Vector4(rgb, gloss)));
            if (verts.Count > first) batches.Add((tex, first / 3));
        }
        var (indices, ranges) = CourseLoader.Layered([.. verts.Select(v => v.Position)], [.. batches.Select(b => b.First)], true);
        return new StaticMesh(device, verts.ToArray(), indices,
            [.. ranges.Select(r => new MeshBatch(batches[r.Batch].Texture, r.First * 3, r.Count * 3, r.Layer))]);
    }

    /// <summary>
    ///     Materials in draw order: all parts' normal materials, then the decal materials (flag 0x400, the game's
    ///     second pass in <c>0x1860E0</c>), each in part/file order.
    /// </summary>
    public static IEnumerable<(string Part, Mesh Mesh, Mesh.Material Material)> Flatten(IEnumerable<(string Name, Mesh Mesh)> meshes)
    {
        foreach (var decals in new[] { false, true })
        foreach (var (name, mesh) in meshes)
        foreach (var m in mesh.Materials.Where(m => (m.Flags & 0x400) != 0 == decals))
            yield return (name, mesh, m);
    }

    public void Dispose()
    {
        Body.Dispose();
        Wheel.Dispose();
    }
}
