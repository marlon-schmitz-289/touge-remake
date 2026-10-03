using Touge.Formats;

// idss list <file.AFS|file.PAC>          – Einträge auflisten (PACs in AFS werden mit aufgelöst)
// idss extract <file.AFS> <outDir>       – alle Einträge als Dateien schreiben
// idss textures <file.PAC> <outDir>      – alle GIM-Texturen als PNG
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

    default:
        Console.Error.WriteLine("usage: idss list <AFS|PAC> | extract <AFS> <outDir> | textures <PAC> <outDir>");
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
