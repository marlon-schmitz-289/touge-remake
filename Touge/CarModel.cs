using System.Numerics;
using Kansei.Graphics;
using Touge.Formats;

namespace Touge;

/// <summary>
///     A car from HCAR.AFS with paint from CAR_ENV.BIN: the parts the game shows for <see cref="Livery"/> (default the
///     character's car: stickers, rival livery, tuning parts; <see cref="CarParts.Body"/>) plus number plates
///     (NUM_TEX.PAC composed like the game) as one mesh per lamp state (<see cref="Day"/>; <see cref="Lit"/> with the night
///     lamp parts and their lit lenses, <see cref="CarParts.Lit"/>), one wheel mesh (tire of the setup + brake disk;
///     calipers left out), the four wheel transforms in car space (fr_l, fr_r, re_l, re_r) and the <see cref="Lamps"/>.
///     <see cref="Paints"/> = number of CAR_ENV colours of the car. Its textures are the renderer's last ones (from
///     <see cref="FirstTexture"/>), freed with the model.
/// </summary>
public sealed record CarModel(CarModel.Shell Day, CarModel.Shell Lit, StaticMesh Wheel, Matrix4x4[] Wheels, float WheelRadius, int Paints, CarModel.Lamps Lamp,
    WorldRenderer Renderer, int FirstTexture)
    : IDisposable
{
    /// <summary>Hood, cockpit and bumper camera mounts measured from this model (<see cref="CameraRig.Measure"/>).</summary>
    public CameraRig.Mounts Mounts { get; init; }

    /// <summary>The lit shell as the cockpit sees it (<see cref="CameraRig.CabinPart"/>: no driver card, dim mirror glass): the cockpit camera sits where he is drawn.</summary>
    public Shell? Cabin { get; init; }

    /// <summary>The shell to draw: <see cref="Cabin"/> with the camera inside, else by the light switch.</summary>
    public Shell ShellFor(bool lit, bool cockpit) => cockpit && Cabin != null ? Cabin : lit ? Lit : Day;

    /// <summary>Opaque body, its alpha-blended decals (<see cref="Build"/>) and the pop-up headlamp part of this lamp state.</summary>
    public sealed record Shell(StaticMesh Body, StaticMesh Decals, StaticMesh? PopUp);

    /// <summary>
    ///     Lamps in car space: pop-up headlamps or fixed ones (the part is in <see cref="Shell.PopUp"/>, modelled around its
    ///     hinge, drawn between the body00 nodes <c>fr_rk_close</c> and <c>fr_rk_open</c>) and the centres of the head and rear lamp lenses (left,
    ///     right; pop-ups open), from their textured materials.
    /// </summary>
    public sealed record Lamps(bool PopUp, Matrix4x4 Closed, Matrix4x4 Open, Vector3[] Head, Vector3[] Tail)
    {
        /// <summary>Pop-up part → car space between closed (0) and open (1), eased.</summary>
        public Matrix4x4 PopUpAt(float open)
        {
            Matrix4x4.Decompose(Closed, out _, out var a, out var pos);
            Matrix4x4.Decompose(Open, out _, out var b, out _);
            return Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(a, b, open * open * (3 - 2 * open))) * Matrix4x4.CreateTranslation(pos);
        }
    }

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
        var plate = textures.ContainsKey("plate") ? "plate" : null;
        var lit = CarParts.Body(car, parts, livery, paint, plate, true);

        // pop-ups: the lamp part leaves the shells and turns about its hinge
        var nodes = parts["body00"].Nodes;
        var closed = nodes.FirstOrDefault(n => n.Name == "fr_rk_close").Transform;
        var open = nodes.FirstOrDefault(n => n.Name == "fr_rk_open").Transform;
        var day = CarParts.Body(car, parts, livery, paint, plate);
        string? PopUpOf(List<(string Name, Mesh Mesh)> body) =>
            closed != default && open != default ? body.Select(p => p.Name).FirstOrDefault(n => n.StartsWith("Flight")) : null;
        var popUp = PopUpOf(lit);
        IEnumerable<Vector3> Lenses(string prefix) => lit.Where(p => p.Name.StartsWith(prefix))
            .SelectMany(p => (p.Name == popUp ? CarParts.Transformed(parts[p.Name], open) : p.Mesh).Materials)
            .Where(m => m.Texture >= 0).SelectMany(m => m.Triangles).Select(v => v.Position);
        var lamps = new Lamps(popUp != null, closed, open,
            Centres(Lenses("Flight"), new Vector3(0.6f, 0.35f, 1.9f), true), Centres(Lenses("Blamp"), new Vector3(0.6f, 0.4f, -2f), false));
        Shell Shell(List<(string Name, Mesh Mesh)> body)
        {
            var part = PopUpOf(body);
            var (opaque, decals) = Build(renderer.Device, body.Where(p => p.Name != part), textures, true);
            return new Shell(opaque, decals!, part == null ? null : Build(renderer.Device, [(part, parts[part])], textures, false).Opaque);
        }
        var wheels = CarParts.Wheels(parts["body00"]);
        return new CarModel(Shell(day), Shell(lit),
            Build(renderer.Device, [("tire", tire), ("Bdisk00", parts["Bdisk00"])], textures, false).Opaque,
            wheels, radius, colours.Length, lamps, renderer, firstTexture)
        {
            Cabin = Shell([.. lit.Select(p => (p.Name, CameraRig.CabinPart(p.Name, p.Mesh)))]),
            Mounts = CameraRig.Measure(day.Select(p => p.Mesh), day.FirstOrDefault(p => p.Name.StartsWith("wind")).Mesh, wheels.Average(w => w.Translation.Y)),
        };
    }

    /// <summary>
    ///     Centres of the points left (+x) and right of the middle, at their front- or rearmost z; <paramref name="fallback"/>
    ///     (left, mirrored) for a side without any.
    /// </summary>
    public static Vector3[] Centres(IEnumerable<Vector3> points, Vector3 fallback, bool front)
    {
        var all = points.ToArray();
        return
        [
            .. new[] { 1f, -1f }.Select(side => all.Where(p => p.X * side > 0.1f).ToArray() is { Length: > 0 } s
                ? new Vector3(s.Average(p => p.X), s.Average(p => p.Y), front ? s.Max(p => p.Z) : s.Min(p => p.Z))
                : fallback with { X = fallback.X * side }),
        ];
    }

    /// <summary>
    ///     One batch per material in <see cref="Flatten"/> order, split into overlay layers where parts lie on each
    ///     other (<see cref="CourseLoader.Layered"/>: decals, emblems, lamps, stacked trim). Material RGB (0x80 = 1.0) is baked into the vertex
    ///     colour, alpha carries the kind for car.frag: paint 1 (flag 0x100; body decals 0x400 too, so stickers get the same clear
    ///     coat as the paint under them, with their own colour; and the untextured 0x1000 parts =
    ///     the near-black lower body), windows (part <c>wind</c>) 0.5, rear lamps (part <c>Blamp</c>) 2, headlamp lenses
    ///     (textured, unpainted materials of <c>Flight</c>) 3, else 0 (matte).
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
                : name.StartsWith("Flight") && m.Texture >= 0 && (m.Flags & CarPaint.PaintFlag) == 0 ? 3f
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
        foreach (var s in new[] { Day, Lit, Cabin })
        {
            if (s == null) continue;
            s.Body.Dispose();
            s.Decals.Dispose();
            s.PopUp?.Dispose();
        }
        Wheel.Dispose();
        Renderer.ReleaseTextures(FirstTexture);
    }
}
