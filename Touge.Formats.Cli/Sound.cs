using Touge.Formats;

/// <summary>Audio assets of the disc for <c>idss sound</c> / <c>idss wav</c>.</summary>
internal static class Sound
{
    public sealed record Asset(string Id, string Format, int Channels, int Rate, int Samples, (int, int)? Loop, Func<short[]> Pcm);

    private static readonly string[] AdxArchives =
    [
        "SOUND/BGM", "SOUND/RACEBGM", "SOUND/ST_BGM_N", "SOUND/IKETANI", "SOUND/RACEVOIC", "MANGA/MG_BGM",
        .. Enumerable.Range(0, 8).Select(i => $"MANGAV/MG_VC{i:D2}"),
    ];

    public static IEnumerable<Asset> Assets(Iso9660 iso)
    {
        foreach (var arc in AdxArchives)
        {
            var afs = iso.OpenAfs($"CDVD/DATA/{arc}.AFS");
            var tag = arc[(arc.IndexOf('/') + 1)..];
            foreach (var e in afs.Entries)
            {
                var d = afs.Read(e);
                if (!Adx.IsAdx(d)) { yield return new Asset($"{tag}/{e.Name}", "?", 0, 1, 0, null, () => []); continue; }
                var adx = new Adx(d);
                yield return new Asset($"{tag}/{e.Name}", "ADX", adx.Channels, adx.SampleRate, adx.SampleCount, adx.Loop, adx.DecodeAll);
            }
        }

        var carse = iso.OpenAfs("CDVD/DATA/SOUND/CARSE.AFS");
        foreach (var e in carse.Entries.Where(e => e.Name.EndsWith(".MRG", StringComparison.OrdinalIgnoreCase)))
        {
            var (hd, bd) = Vag.Mrg(carse.Read(e));
            var samples = Vag.HdSamples(hd, bd.Length);
            for (var i = 0; i < samples.Length; i++)
            {
                var s = samples[i];
                var v = Vag.Decode(bd.AsSpan(s.Offset, s.Size), s.Rate);
                yield return new Asset($"CARSE/{Path.GetFileNameWithoutExtension(e.Name)}#{i}", "VAG", 1, v.Rate, v.Pcm.Length, v.Loop, () => v.Pcm);
            }
        }

        var sysse = iso.ReadFile("CDVD/DATA/SOUND/SYSSE.BIN");
        var bank = Vag.SysSe(sysse);
        var names = Afs.ParseTbl(iso.ReadFile("CDVD/DATA/SOUND/SYSSE.TBL"), bank.Length) ?? [];
        for (var i = 0; i < bank.Length; i++)
        {
            var v = Vag.Decode(sysse.AsSpan(bank[i].Offset, bank[i].Size), bank[i].Rate);
            yield return new Asset($"SYSSE/{(i < names.Length ? names[i] : $"{i:D2}")}", "VAG", 1, v.Rate, v.Pcm.Length, v.Loop, () => v.Pcm);
        }
    }

    /// <summary>Best-effort role from the file name (names are the developers'; meaning of a few is a guess, marked "?").</summary>
    public static string Role(string id)
    {
        var (arc, name) = (id[..id.IndexOf('/')], id[(id.IndexOf('/') + 1)..].ToUpperInvariant());
        var stem = Path.GetFileNameWithoutExtension(name);
        return arc switch
        {
            "BGM" => "Menü/Ergebnis-Musik",
            "RACEBGM" => $"Renn-BGM \"{stem.Replace('_', ' ')}\"",
            "ST_BGM_N" or "MG_BGM" => "Story-Musik",
            "IKETANI" => stem.StartsWith("INTRO_") ? $"Ansage Auto {stem[6..]} (Iketani?)" : "-",
            "RACEVOIC" => name.Split('_') is [_, var who, var what, ..] ? $"Rennstimme {who} ({what})" : "Rennstimme",
            "MG_VC00" or "MG_VC01" or "MG_VC02" or "MG_VC03" or "MG_VC04" or "MG_VC05" or "MG_VC06" or "MG_VC07" => "Story-Stimme",
            "CARSE" when stem.Contains("SRIP") => stem.StartsWith("RAIN") ? "Reifen quietschen (nass)" : "Reifen quietschen",
            "CARSE" when stem.StartsWith("TURBO") => "Turbo",
            "CARSE" => $"Motor {stem.Split('#')[0]} Schicht {stem.Split('#')[1]} (_U Last / _D Schub?)",
            "SYSSE" when stem.Contains("BACKFIRE") || stem == "POPOFF" || stem == "BLOW" => "Fehlzündung/Abblasen",
            "SYSSE" when stem.StartsWith("CR0") => "Crash?",
            "SYSSE" when stem is "RAIN" or "WATER" or "STEAM" => "Umgebung",
            "SYSSE" => "System/UI",
            _ => "-",
        };
    }

    public static void WriteWav(string path, short[] pcm, int channels, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8);
        w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * channels * 2);
        w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length * 2);
        foreach (var s in pcm) w.Write(s);
    }
}
