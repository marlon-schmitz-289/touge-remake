using System.Numerics;
using Kansei.Graphics;
using Touge.Formats;

namespace Touge;

/// <summary>
///     A car from HCAR.AFS with paint from CAR_ENV.BIN: the parts the game shows for <see cref="Livery"/> (default the
///     character's car: stickers, rival livery, tuning parts; <see cref="CarParts.Body"/>) plus number plates
///     (NUM_TEX.PAC composed like the game) as one mesh, one wheel mesh (tire of the setup + brake disk; calipers left
///     out) and the four wheel transforms in car space (fr_l, fr_r, re_l, re_r). <see cref="Paints"/> = number of
///     CAR_ENV colours of the car. Its textures are the renderer's last ones (from <see cref="FirstTexture"/>), freed with the model.
/// </summary>
public sealed record CarModel(StaticMesh Body, StaticMesh Decals, StaticMesh Wheel, Matrix4x4[] Wheels, float WheelRadius, int Paints,
    WorldRenderer Renderer, int FirstTexture) : IDisposable
{
    public static CarModel Load(Iso9660 iso, string car, int paint, WorldRenderer renderer, Livery livery = Livery.Rival)
    {
        var hcar = iso.OpenAfs("CDVD/DATA/MODEL/HCAR.AFS");
        var pac = hcar.Read(hcar.Find(car + ".PAC") ?? throw new FileNotFoundException(car + ".PAC"));
        var colours = CarPaint.Parse(iso.ReadFile("CDVD/DATA/BINARY/CAR_ENV.BIN"))[Array.IndexOf(CarPaint.Cars, car)];
        paint = Math.Clamp(paint, 0, colours.Length - 1);
        var entries = Pac.Entries(pac);

        var firstTexture = renderer.TextureCount;
        var textures = new Dictionary<string, int> { [""] = renderer.AddTexture(1, 1, [255, 255, 255, 255], "white") };
        foreach (var e in entries.Where(e => e.Type == 1))
        {
            var (w, h, rgba) = Gim.Decode(pac.AsSpan(e.Offset, e.Size));
            textures[e.Name] = renderer.AddTexture(w, h, rgba, e.Name, 0.5f); // car.frag alpha test
        }
        var parts = entries.Where(e => e.Type == 3 && Mesh.IsCmd(pac.AsSpan(e.Offset, e.Size)))
            .ToDictionary(e => e.Name[(car.Length + 1)..], e => CarPaint.Apply(Mesh.Parse(pac.AsSpan(e.Offset, e.Size)), colours[paint]));

        var setup = CarParts.SetupOf(car, livery);
        if (CarParts.PlateNumber(car, setup.Driver) is { } number)
        {
            var tex = iso.OpenAfs("CDVD/DATA/MODEL/TEXTURE.AFS");
            var num = tex.Read(tex.Find("NUM_TEX.PAC")!.Value);
            var gims = Pac.Entries(num).ToDictionary(e => e.Name, e => Gim.Decode(num.AsSpan(e.Offset, e.Size)).Rgba);
            textures["plate"] = renderer.AddTexture(64, 32, CarParts.Plate(gims[car == "CAPPU" ? "NUM_PLATE_Y" : "NUM_PLATE_W"], gims["NUM_TEX"], number), "plate", 0.5f);
        }
        var tire = parts[CarParts.Tire(parts, setup)];
        var radius = tire.Materials.SelectMany(m => m.Triangles).Max(v => v.Position.Y);
        var (body, decals) = Build(renderer.Device, CarParts.Body(car, parts, livery, paint, textures.ContainsKey("plate") ? "plate" : null), textures, true);
        return new CarModel(body, decals!, Build(renderer.Device, [("tire", tire), ("Bdisk00", parts["Bdisk00"])], textures, false).Opaque,
            CarParts.Wheels(parts["body00"]), radius, colours.Length, renderer, firstTexture);
    }

    /// <summary>
    ///     One batch per material in <see cref="Flatten"/> order, split into overlay layers where parts lie on each
    ///     other (<see cref="CourseLoader.Layered"/>: decals, emblems, lamps, stacked trim). Material RGB (0x80 = 1.0) is baked into the vertex
    ///     colour, alpha carries the kind for car.frag: paint 1 (flag 0x100; body decals 0x400 too, so stickers get the same clear
    ///     coat as the paint under them, with their own colour; and the untextured 0x1000 parts =
    ///     the near-black lower body), windows (part <c>wind</c>) 0.5, rear lamps (part <c>Blamp</c>) 2, else 0 (matte).
    ///     <paramref name="split"/>: the decal batches (0x400 without 0x800, i.e. not the windows) go into a second mesh,
    ///     kind + 4, that <see cref="CarRenderer"/> alpha-blends like the PS2 instead of alpha-testing (soft sticker edges,
    ///     no edge shimmer); layers are computed over both together.
    /// </summary>
    private static (StaticMesh Opaque, StaticMesh? Decals) Build(Penelope.IPenelopeDevice device, IEnumerable<(string Name, Mesh Mesh)> meshes,
        Dictionary<string, int> textures, bool split)
    {
        var verts = new List<CarVertex>();
        var batches = new List<(int Texture, int First, bool Decal)>();
        foreach (var (name, mesh, m) in Flatten(meshes))
        {
            // a few parts name textures their PAC does not contain (S15: S15067_002): drawn untextured
            var tex = textures.GetValueOrDefault(m.Texture >= 0 && m.Texture < mesh.Textures.Length ? mesh.Textures[m.Texture] : "", textures[""]);
            var rgb = new Vector3(m.Rgba & 0xFF, (m.Rgba >> 8) & 0xFF, (m.Rgba >> 16) & 0xFF) / 128f;
            var gloss = name.StartsWith("Blamp") ? 2f : name.StartsWith("wind") ? 0.5f
                : (m.Flags & CarPaint.PaintFlag) != 0 || (m.Flags & 0x400) != 0 && name != "tire"
                  || m.Flags == 0x1000 && m.Texture < 0 && name != "tire" ? 1f : 0f;
            var decal = split && (m.Flags & 0xC00) == 0x400;
            var first = verts.Count;
            foreach (var v in m.Triangles) verts.Add(new CarVertex(v.Position, v.Normal, v.Uv, new Vector4(rgb, gloss + (decal ? 4 : 0))));
            if (verts.Count > first) batches.Add((tex, first / 3, decal));
        }
        var (indices, ranges) = CourseLoader.Layered([.. verts.Select(v => v.Position)], [.. batches.Select(b => b.First)], true);
        StaticMesh Part(bool decals) => new(device, verts.ToArray(), indices,
            [.. ranges.Where(r => batches[r.Batch].Decal == decals).Select(r => new MeshBatch(batches[r.Batch].Texture, r.First * 3, r.Count * 3, r.Layer))]);
        return (Part(false), split ? Part(true) : null);
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
        Decals.Dispose();
        Wheel.Dispose();
        Renderer.ReleaseTextures(FirstTexture);
    }
}
