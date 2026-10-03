using Touge.Formats;

// idss list <file.AFS|file.PAC>          – Einträge auflisten (PACs in AFS werden mit aufgelöst)
// idss extract <file.AFS> <outDir>       – alle Einträge als Dateien schreiben
// idss textures <file.PAC> <outDir>      – alle GIM-Texturen als PNG
// idss check <PAC...>                   – alle Texturen und Meshes testweise dekodieren
// idss car <CAR.PAC> <outDir>            – Standard-Teile (…00) + Räder als OBJ, Texturen als PNG
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

    case ["car", var path, var outDir]:
        ExportCar(path, outDir);
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
                    else if (Cmd.IsCmd(d)) Cmd.Parse(d);
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
        Console.Error.WriteLine("usage: idss list <AFS|PAC> | extract <AFS> <outDir> | textures <PAC> <outDir> | car <PAC> <outDir> | check <PAC...>");
        return 1;
}
return 0;

static void PrintPac(byte[] d, string indent)
{
    foreach (var p in Pac.Entries(d))
    {
        var magic = System.Text.Encoding.ASCII.GetString(d, p.Offset, 4).TrimEnd('\0');
        Console.WriteLine($"{indent}{p.Name,-16} type={p.Type} 0x{p.Offset:X6} {p.Size,9} {magic}");
    }
}

static void ExportCar(string path, string outDir)
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

    var parts = entries.Where(e => e.Type == 3 && Cmd.IsCmd(pac.AsSpan(e.Offset, e.Size)))
        .ToDictionary(e => e.Name[(name.Length + 1)..], e => Cmd.Parse(pac.AsSpan(e.Offset, e.Size)));
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
