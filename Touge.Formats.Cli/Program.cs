using Touge.Formats;

// idss list <file.AFS|file.PAC>          – Einträge auflisten (PACs in AFS werden mit aufgelöst)
// idss extract <file.AFS> <outDir>       – alle Einträge als Dateien schreiben
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

    default:
        Console.Error.WriteLine("usage: idss list <AFS|PAC> | idss extract <AFS> <outDir>");
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
