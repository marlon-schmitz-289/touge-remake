using Touge.Formats;

// idss list <file.AFS|file.PAC>          – Einträge auflisten (PACs in AFS werden mit aufgelöst)
// idss extract <file.AFS> <outDir>       – alle Einträge als Dateien schreiben
// idss textures <file.PAC> <outDir>      – alle GIM-Texturen als PNG
// idss check <PAC...>                   – alle Texturen und Meshes testweise dekodieren
// idss course <COURSE.PAC> <outDir>     – Strecke (crs/tree/gate/mnt/sky, ohne LOD/Schatten) als OBJ
// idss car <CAR.PAC> <outDir> [<CAR_ENV.BIN> [n]] – Standard-Teile (…00) + Räder als OBJ, Texturen als PNG; Lack Nr. n (0 = Standard)
// idss coli <CRS_COLI_*.BIN> <out.obj>  – Kollisionsfläche als OBJ (Gruppe je Material), Histogramm
// idss drv <CRS_DRV_*.BIN>               – gültige Fahrlinienpunkte: i x y z
// idss road <CRS_ROAD_*.BIN> [CRS_ENV_*.BIN] [CRS_FLR_*.BIN] – i x y z [top bottom left right] [flare]
switch (args)
{
    case ["list", var path]:
        if (path.EndsWith(".AFS", StringComparison.OrdinalIgnoreCase))
        {
            var afs = Afs.Open(path);
            foreach (var e in afs.Entries)
            {
                Console.WriteLine($"{e.Name,-20} 0x{e.Offset:X8} {e.Size,10}");
                var d = afs.Read(e);
                if (Pac.IsPac(d)) PrintPac(d, "    ");
            }
        }
        else PrintPac(File.ReadAllBytes(path), "");
        break;

    case ["extract", var path, var outDir]:
        var a = Afs.Open(path);
        Directory.CreateDirectory(outDir);
        foreach (var e in a.Entries) File.WriteAllBytes(Path.Combine(outDir, e.Name), a.Read(e));
        Console.WriteLine($"{a.Entries.Count} Dateien -> {outDir}");
        break;

    case ["textures", var path, var outDir]:
        var pac = File.ReadAllBytes(path);
        Directory.CreateDirectory(outDir);
        int ok = 0, fail = 0;
        foreach (var e in Pac.Entries(pac).Where(e => e.Type == 1))
            try
            {
                var (w, h, rgba) = Gim.Decode(pac.AsSpan(e.Offset, e.Size));
                Png.Write(Path.Combine(outDir, e.Name + ".png"), w, h, rgba);
                ok++;
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
            {
                Console.Error.WriteLine($"{e.Name}: {ex.Message}");
                fail++;
            }
        Console.WriteLine($"{ok} PNG -> {outDir}, {fail} übersprungen");
        break;

    case ["course", var path, var outDir]:
        ExportCourse(path, outDir);
        break;

    case ["car", var path, var outDir, .. var paint] when paint.Length <= 2:
        ExportCar(path, outDir, paint.Length == 0 ? null : Paint(path, paint[0], paint.Length > 1 ? int.Parse(paint[1]) : 0));
        break;

    case ["coli", var path, var outObj]:
        ExportCollision(path, outObj);
        break;

    case ["drv", var path]:
        var line = DrivingLine.Read(File.ReadAllBytes(path), DrivingLine.PointCount(DrivingLine.CourseOf(path)));
        for (var i = 0; i < line.Length; i++) Console.WriteLine(F($"{i} {line[i].X} {line[i].Y} {line[i].Z}"));
        break;

    case ["road", var path, .. var extra]:
        var road = CourseRoad.Read(File.ReadAllBytes(path));
        var envPath = extra.FirstOrDefault(p => Path.GetFileName(p).StartsWith("CRS_ENV_", StringComparison.OrdinalIgnoreCase));
        var flrPath = extra.FirstOrDefault(p => Path.GetFileName(p).StartsWith("CRS_FLR_", StringComparison.OrdinalIgnoreCase));
        var env = envPath == null ? null : CourseRoad.ReadEnv(File.ReadAllBytes(envPath));
        var flr = flrPath == null ? null : CourseRoad.ReadFlare(File.ReadAllBytes(flrPath), road.Length);
        for (var i = 0; i < road.Length; i++)
        {
            var s = F($"{i} {road[i].X} {road[i].Y} {road[i].Z}");
            if (env != null) s += i < env.Length ? $" {env[i].Top} {env[i].Bottom} {env[i].Left} {env[i].Right}" : " - - - -";
            if (flr != null) s += flr[i] ? " 1" : " 0";
            Console.WriteLine(s);
        }
        break;

    case ["check", .. var paths]:
        int good = 0, bad = 0;
        foreach (var path in paths)
        {
            var file = File.ReadAllBytes(path);
            foreach (var e in Pac.Entries(file))
                try
                {
                    var d = file.AsSpan(e.Offset, e.Size);
                    if (e.Type == 1) Gim.Decode(d);
                    else if (Mesh.IsCmd(d) || Mesh.IsSmd(d) || Lz.IsCompressed(d)) Mesh.Parse(d);
                    else continue;
                    good++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{Path.GetFileName(path)}/{e.Name}: {ex.Message}");
                    bad++;
                }
        }
        Console.WriteLine($"{good} ok, {bad} Fehler");
        return bad == 0 ? 0 : 1;

    default:
        Console.Error.WriteLine("usage: idss list <AFS|PAC> | extract <AFS> <outDir> | textures <PAC> <outDir> | car <PAC> <outDir> [<CAR_ENV.BIN> [n]] | check <PAC...> | course <PAC> <outDir> | drv <CRS_DRV> | road <CRS_ROAD> [CRS_ENV] [CRS_FLR] | coli <BIN> <out.obj>");
        return 1;
}
return 0;

static string F(FormattableString s) => s.ToString(System.Globalization.CultureInfo.InvariantCulture);

static void PrintPac(byte[] d, string indent)
{
    foreach (var p in Pac.Entries(d))
    {
        var magic = System.Text.Encoding.ASCII.GetString(d, p.Offset, 4).TrimEnd('\0');
        Console.WriteLine($"{indent}{p.Name,-16} type={p.Type} 0x{p.Offset:X6} {p.Size,9} {magic}");
    }
}

static uint Paint(string carPac, string envBin, int index)
{
    var car = Array.IndexOf(CarPaint.Cars, Path.GetFileNameWithoutExtension(carPac).ToUpperInvariant());
    var cols = CarPaint.Parse(File.ReadAllBytes(envBin)).GetValueOrDefault(car)
               ?? throw new ArgumentException($"{carPac}: keine Lackfarben für dieses Auto");
    if ((uint)index >= cols.Length) throw new ArgumentException($"Lack {index}: nur 0–{cols.Length - 1}");
    return cols[index];
}

static void ExportCar(string path, string outDir, uint? paint)
{
    var pac = File.ReadAllBytes(path);
    var name = Path.GetFileNameWithoutExtension(path);
    Directory.CreateDirectory(outDir);
    var entries = Pac.Entries(pac);
    foreach (var e in entries.Where(e => e.Type == 1))
    {
        var (w, h, rgba) = Gim.Decode(pac.AsSpan(e.Offset, e.Size));
        Png.Write(Path.Combine(outDir, e.Name + ".png"), w, h, rgba);
    }

    var parts = entries.Where(e => e.Type == 3 && Mesh.IsCmd(pac.AsSpan(e.Offset, e.Size)))
        .ToDictionary(e => e.Name[(name.Length + 1)..], e => Mesh.Parse(pac.AsSpan(e.Offset, e.Size)))
        .ToDictionary(p => p.Key, p => paint is { } rgb ? CarPaint.Apply(p.Value, rgb) : p.Value);
    using var obj = new Obj(Path.Combine(outDir, name + ".obj"));
    foreach (var (part, cmd) in parts.Where(p => p.Key.EndsWith("00") && !p.Key.Contains("shd") && !p.Key.StartsWith("tire") && !p.Key.StartsWith("Bdisk")))
        obj.Add(part, cmd, System.Numerics.Matrix4x4.Identity);

    // Räder an den Achs-Knoten der Karosserie; rechte Seite um 180° gedreht
    var wheels = parts["body00"].Nodes.Where(n => n.Name is "fr_l" or "fr_r" or "re_l" or "re_r");
    foreach (var (node, m) in wheels)
    {
        var t = node.EndsWith("_r") ? System.Numerics.Matrix4x4.CreateRotationY(MathF.PI) * m : m;
        foreach (var w in new[] { "tire00FL", "Bdisk00" })
            if (parts.TryGetValue(w, out var c)) obj.Add($"{w}_{node}", c, t);
        var cali = "Bcali00" + node.ToUpperInvariant().Replace("_", "")[..2].Replace("FR", "F").Replace("RE", "R") + (node.EndsWith("_l") ? "L" : "R");
        if (parts.TryGetValue(cali, out var cc)) obj.Add(cali, cc, t);
    }
    Console.WriteLine($"{name}: {parts.Count} Teile -> {outDir}");
}

static void ExportCourse(string path, string outDir)
{
    var pac = File.ReadAllBytes(path);
    var name = Path.GetFileNameWithoutExtension(path);
    Directory.CreateDirectory(outDir);
    var entries = Pac.Entries(pac);
    foreach (var e in entries.Where(e => e.Type == 1))
    {
        var (w, h, rgba) = Gim.Decode(pac.AsSpan(e.Offset, e.Size));
        Png.Write(Path.Combine(outDir, e.Name + ".png"), w, h, rgba);
    }
    using var obj = new Obj(Path.Combine(outDir, name + ".obj"));
    var n = 0;
    foreach (var e in entries.Where(e => e.Type == 3 && !e.Name.Contains("lod") && !e.Name.StartsWith("shd")))
    {
        obj.Add(e.Name, Mesh.Parse(pac.AsSpan(e.Offset, e.Size)), System.Numerics.Matrix4x4.Identity);
        n++;
    }
    Console.WriteLine($"{name}: {n} Meshes -> {outDir}");
}

static void ExportCollision(string path, string outObj)
{
    var c = Collision.Parse(File.ReadAllBytes(path));
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    using var w = new StreamWriter(outObj);
    foreach (var p in c.Positions) w.WriteLine(string.Create(inv, $"v {p.X} {p.Y} {p.Z}"));
    foreach (var n in c.Normals) w.WriteLine(string.Create(inv, $"vn {n.X} {n.Y} {n.Z}"));
    Console.WriteLine($"{Path.GetFileName(path)}: {c.Positions.Length} Vertices, {c.Faces.Length} Dreiecke, {c.Sectors.Length} Sektoren");
    foreach (var g in c.Faces.GroupBy(f => f.Attribute).OrderBy(g => g.Key))
    {
        var name = c.Materials[g.First().Material];
        Console.WriteLine($"  0x{g.Key:X4} {name,-20} {g.Count(),6}");
        w.WriteLine($"g {name}");
        foreach (var f in g) w.WriteLine($"f {f.A + 1}//{f.A + 1} {f.B + 1}//{f.B + 1} {f.C + 1}//{f.C + 1}");
    }
}
