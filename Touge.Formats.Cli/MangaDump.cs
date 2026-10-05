using System.Text;
using Touge.Formats;

/// <summary><c>idss manga &lt;ISO&gt; &lt;outDir&gt; &lt;chapter&gt;</c>: everything the original shows in a story chapter, as PNG/WAV/TXT.</summary>
internal static class MangaDump
{
    private const string Dir = "CDVD/DATA/";

    public static void Run(string isoPath, string outDir, int chapter)
    {
        using var iso = new Iso9660(isoPath);
        var elf = iso.ReadFile(StoryScript.ElfPath);
        var ch = Manga.ReadChapters(elf)[chapter];
        Directory.CreateDirectory(outDir);
        var voices = iso.OpenAfs(Dir + "MANGAV/MG_KOMAS.AFS");
        Console.WriteLine(ch);

        foreach (var t in new[] { ch.Before, ch.After }.Where(t => t >= 0))
        {
            var bg = Manga.KomaBackground(elf, t);
            var dir = Path.Combine(outDir, $"koma{t:00}");
            Directory.CreateDirectory(dir);
            var tc = Fpk(iso, "MANGA/MG_KOMAM", "KOMABIN.FPK", $"KOMATC{t:00}.BIN");
            File.WriteAllLines(Path.Combine(dir, "timeline.txt"), Manga.Timeline(tc).Select(c => $"{c.Frame,6} {c.Frame / 60.0,7:0.00}s  {string.Join(' ', c.Tokens)}"));
            var komaf = iso.OpenAfs(Dir + "MANGA/MG_KOMAF.AFS");
            if (komaf.Find($"KOMA{bg:00}.FPK") is { } kf)
                Pictures(komaf.Read(kf), dir, null);
            var pac = komaf.Read(komaf.Find($"KOMABG{bg:00}.PAC") ?? throw new FileNotFoundException($"KOMABG{bg:00}.PAC"));
            Gims(pac, dir);
            Voice(voices, Manga.KomaVoice(t), dir);
        }

        if (!ch.Scene) return;
        var obj = iso.OpenAfs(Dir + "MANGA/MG_OBJ.AFS");
        var robj = obj.Read(obj.Find($"STR{chapter:00}.BIN") ?? throw new FileNotFoundException($"STR{chapter:00}.BIN"));
        var sdir = Path.Combine(outDir, "scene");
        Directory.CreateDirectory(sdir);
        var str = iso.OpenAfs(Dir + "MANGA/MG_STR.AFS");
        Pictures(str.Read(str.Find(Manga.Pictures(robj)) ?? throw new FileNotFoundException(Manga.Pictures(robj))), sdir, Manga.Faces(robj));
        var bgp = iso.OpenAfs(Dir + "MANGA/MG_BGP.AFS");
        Gims(bgp.Read(bgp.Find($"BGSTR{ch.Episode:00}.PAC") ?? throw new FileNotFoundException($"BGSTR{ch.Episode:00}.PAC")), sdir);

        var parts = StoryScript.ParseScript(robj);
        var lips = Manga.Lips(robj);
        var times = StoryScript.Times(robj, lips);
        var slots = Enumerable.Range(0, Manga.Slots).Where(s => lips[s].Length > 0).ToArray();
        var sb = new StringBuilder($"{Manga.Pictures(robj)} BGSTR{ch.Episode:00}.PAC\n");
        for (var p = 0; p < parts.Count; p++)
        {
            var slot = p < slots.Length ? slots[p] : -1;
            var voice = slot < 0 ? "?" : Manga.SceneVoice(chapter, slot);
            sb.Append($"\n# part {p}, slot {slot}, voice {voice}\n");
            for (var i = 0; i < parts[p].Count; i++) sb.Append($"{times[p][i],7:0.00}s  {parts[p][i].Speaker,-6} {parts[p][i].Text}\n");
            if (slot < 0) continue;
            foreach (var l in lips[slot]) sb.Append($"  lips {l.Length} frames: {l[..Math.Min(l.Length, 60)]}…\n");
            Voice(voices, voice, sdir);
        }
        File.WriteAllText(Path.Combine(sdir, "script.txt"), sb.ToString());
        Console.WriteLine($"-> {outDir}");
    }

    private static byte[] Fpk(Iso9660 iso, string afsName, string fpkName, string entry)
    {
        var afs = iso.OpenAfs(Dir + afsName + ".AFS");
        var fpk = afs.Read(afs.Find(fpkName) ?? throw new FileNotFoundException(fpkName));
        return Manga.Read(fpk, Manga.Fpk(fpk).First(e => e.Name == entry));
    }

    /// <summary>Every picture of an FPK; portraits also once with the face sprites drawn (mouth frame 3, eyes half shut) as a check.</summary>
    private static void Pictures(byte[] fpk, string dir, Manga.Face?[][]? faces)
    {
        foreach (var e in Manga.Fpk(fpk))
        {
            var (w, h, rgba) = Manga.Tim2(Manga.Read(fpk, e));
            var name = Path.GetFileNameWithoutExtension(e.Name);
            Png.Write(Path.Combine(dir, name + ".png"), w, h, rgba);
            if (faces == null || !int.TryParse(name, out var id) || id >= 32) continue;
            var drawn = (byte[])rgba.Clone();
            for (var k = 0; k < 3; k++)
                if (faces[k][id] is { } f)
                {
                    Blit(rgba, drawn, w, h, 3 * 80, 512 + 256 * k, f.MouthX, f.MouthY);
                    Blit(rgba, drawn, w, h, 1 * 80, 640 + 256 * k, f.Eye1X, f.Eye1Y);
                    Blit(rgba, drawn, w, h, 4 * 80, 640 + 256 * k, f.Eye2X, f.Eye2Y);
                }
            Png.Write(Path.Combine(dir, name + "_face.png"), w, h, drawn);
        }
    }

    /// <summary>An 80 × 128 sprite cell over the picture (alpha blend), skipped when x ≤ 0 (not used, like 0x1CEC5C).</summary>
    private static void Blit(byte[] src, byte[] dst, int w, int h, int sx, int sy, int dx, int dy)
    {
        if (dx <= 0 || sy + 128 > h) return;
        for (var y = 0; y < 128; y++)
            for (var x = 0; x < 80; x++)
            {
                int s = ((sy + y) * w + sx + x) * 4, d = ((dy + y) * w + dx + x) * 4;
                if (dx + x >= w || dy + y >= h) continue;
                var a = src[s + 3];
                for (var c = 0; c < 3; c++) dst[d + c] = (byte)((src[s + c] * a + dst[d + c] * dst[d + 3] / 255 * (255 - a)) / 255);
                dst[d + 3] = (byte)(a + dst[d + 3] * (255 - a) / 255); // the portrait has a hole where mouth and eyes go
            }
    }

    private static void Gims(byte[] pac, string dir)
    {
        foreach (var e in Pac.Entries(pac).Where(e => e.Type == 1))
        {
            var (w, h, rgba) = Gim.Decode(pac.AsSpan(e.Offset, e.Size));
            Png.Write(Path.Combine(dir, e.Name + ".png"), w, h, rgba);
        }
    }

    private static void Voice(Afs voices, string name, string dir)
    {
        if (voices.Find(name) is not { } e) return;
        var adx = new Adx(voices.Read(e));
        Wav.Write(Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + ".wav"), adx.DecodeAll(), adx.Channels, adx.SampleRate);
        Console.WriteLine($"{name}: {adx.SampleCount / (double)adx.SampleRate:0.0} s, {adx.Channels} ch, {adx.SampleRate} Hz");
    }
}
