using System.Text;
using Kansei.Input;
using Touge.Ui;

namespace Touge.Tests;

/// <summary>Launcher: disc check on tiny synthetic ISO 9660 images, the shallow scan, last-disc.txt, the file browser and the screen's pick.</summary>
public sealed class LauncherTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("launcher-test").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>
    ///     A minimal PS2-like disc: PVD at 16, root at 18 with SYSTEM.CNF (BOOT2 = <paramref name="serial"/>), the ELF and
    ///     CDVD/DATA/MODEL/COURSE.AFS (without <paramref name="data"/> CDVD is empty); <paramref name="sectors"/> = claimed volume size.
    /// </summary>
    private string Iso(string name, string serial = "SLPM_652.68", bool data = true, int? sectors = null)
    {
        const int S = 2048;
        var img = new byte[26 * S];
        void Dir(int lba, params (string Name, int Lba, int Size, bool IsDir)[] entries)
        {
            var o = lba * S;
            foreach (var (n, l, size, isDir) in new[] { ("\0", lba, S, true), ("\u0001", 18, S, true) }.Concat(entries))
            {
                var nb = Encoding.ASCII.GetBytes(n);
                var len = 33 + nb.Length + (nb.Length % 2 == 0 ? 1 : 0);
                img[o] = (byte)len;
                BitConverter.TryWriteBytes(img.AsSpan(o + 2), l);
                BitConverter.TryWriteBytes(img.AsSpan(o + 10), size);
                img[o + 25] = (byte)(isDir ? 2 : 0);
                img[o + 32] = (byte)nb.Length;
                nb.CopyTo(img, o + 33);
                o += len;
            }
        }
        var pvd = 16 * S;
        img[pvd] = 1;
        "CD001"u8.CopyTo(img.AsSpan(pvd + 1));
        BitConverter.TryWriteBytes(img.AsSpan(pvd + 80), sectors ?? 26);
        img[pvd + 156] = 34;
        BitConverter.TryWriteBytes(img.AsSpan(pvd + 156 + 2), 18);
        BitConverter.TryWriteBytes(img.AsSpan(pvd + 156 + 10), S);
        var cnf = Encoding.ASCII.GetBytes($"BOOT2 = cdrom0:\\{serial};1\r\nVER = 2.00\r\nVMODE = NTSC\r\n");
        cnf.CopyTo(img, 22 * S);
        Dir(18, ("SYSTEM.CNF;1", 22, cnf.Length, false), (serial + ";1", 23, 16, false), ("CDVD", 19, S, true));
        if (data)
        {
            Dir(19, ("DATA", 20, S, true));
            Dir(20, ("MODEL", 21, S, true));
            Dir(21, ("COURSE.AFS;1", 24, 16, false));
        }
        else Dir(19);
        var path = Path.Combine(_dir, name.Replace('/', Path.DirectorySeparatorChar)); // the paths Windows hands back
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, img);
        return path;
    }

    [Fact]
    public void GoodDiscPassesWithVersion()
    {
        var d = Disc.Check(Iso("good.iso"));
        Assert.True(d.Ok, d.Error);
        Assert.Equal("2.00", d.Version);
        Assert.Contains("VER 2.00", d.Info);
    }

    [Fact]
    public void WrongDiscsSayWhy()
    {
        Assert.Contains("SLUS-12345", Disc.Check(Iso("other.iso", "SLUS_123.45")).Error);
        Assert.Contains("WRONG GAME", Disc.Check(Iso("other2.iso", "SLUS_123.45")).Error);
        Assert.Contains("INCOMPLETE DISC", Disc.Check(Iso("nodata.iso", data: false)).Error);
        Assert.Contains("CUT OFF", Disc.Check(Iso("short.iso", sectors: 900_000)).Error);
        var zeros = Path.Combine(_dir, "zeros.iso");
        File.WriteAllBytes(zeros, new byte[100_000]);
        Assert.Contains("NO ISO 9660", Disc.Check(zeros).Error);
        Assert.Contains("NOT FOUND", Disc.Check(Path.Combine(_dir, "gone.iso")).Error);
        Assert.Contains("FOLDER", Disc.Check(_dir).Error);
    }

    [Fact]
    public void SymlinkedDiscPassesAndPastedPathsResolve()
    {
        var good = Iso("Spiele Ä/Initial D – Kopie ü.iso");
        var link = Path.Combine(_dir, "link.iso");
        File.CreateSymbolicLink(link, good);
        Assert.True(Disc.Check(link).Ok, Disc.Check(link).Error); // the target's size, not the link's
        var b = new Browser(Path.GetDirectoryName(good));
        Assert.Equal(new FileInfo(good).Length, new Browser(_dir).Entries.Single(e => e.Name == "link.iso").Size);
        Assert.Equal(Browser.Outcome.Pick, b.Submit("Initial D – Kopie ü.iso")); // Finder's Cmd+C: the name only
        Assert.Equal(good, b.Result);
        if (!OperatingSystem.IsWindows()) // Terminal: Initial\ D\ –\ Kopie\ ü.iso
            Assert.Equal(good, Disc.Resolve(good.Replace(" ", "\\ ")));
        Assert.Equal(good, Disc.Resolve("file://" + new Uri(good).AbsolutePath));
    }

    [Fact]
    public void MissingGlyphsShowAsQuestionMark()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "InitialDRemake.slnx"))) dir = Path.GetDirectoryName(dir)!;
        var font = new Kansei.Graphics.SdfFont(File.ReadAllBytes(Path.Combine(dir, "Touge/Assets/Fonts/Rajdhani-Bold.ttf")), Style.Glyphs);
        Assert.True(font.TryGet('ü', out var u));
        Assert.True(font.TryGet('–', out _));
        Assert.True(font.TryGet('?', out var q));
        Assert.NotEqual(q, u);
        Assert.True(font.TryGet('漢', out var missing));
        Assert.Equal(q, missing);
        Assert.False(font.TryGet('\n', out _));
        // with the Japanese fallback: its own glyph, Latin still from Rajdhani
        var both = new Kansei.Graphics.SdfFont(File.ReadAllBytes(Path.Combine(dir, "Touge/Assets/Fonts/Rajdhani-Bold.ttf")), Style.Glyphs + "漢字ア",
            File.ReadAllBytes(Path.Combine(dir, "Touge/Assets/Fonts/MPLUS1p-Bold.ttf")));
        Assert.True(both.TryGet('漢', out var kan));
        Assert.True(both.TryGet('?', out var q2));
        Assert.NotEqual(q2, kan);
        Assert.True(both.TryGet('H', out var h));
        Assert.True(font.TryGet('H', out var h0));
        Assert.Equal(h0.Advance, h.Advance);
    }

    [Fact]
    public void ScanFindsGoodDiscsOneLevelDeep()
    {
        var top = Iso("GOOD2.ISO");
        var sub = Iso("games/good.iso");
        Iso("games/deeper/deep.iso"); // two levels down: not scanned
        Iso("games/other.iso", "SLUS_123.45"); // wrong game: not listed
        var scan = new DiscScan();
        scan.Run([_dir], DateTime.UtcNow.AddSeconds(30));
        Assert.True(scan.Done);
        Assert.Equal(new[] { top, sub }.Order(StringComparer.OrdinalIgnoreCase), scan.Found.Select(d => d.Path));
    }

    [Fact]
    public void ScanStopsAtItsDeadline()
    {
        Iso("good.iso");
        var scan = new DiscScan();
        scan.Run([_dir], DateTime.UtcNow.AddSeconds(-1));
        Assert.True(scan.Done);
        Assert.Empty(scan.Found);
    }

    [Fact]
    public void LastDiscRoundTrips()
    {
        var file = Path.Combine(_dir, "profile", "last-disc.txt");
        Assert.Null(LastDisc.Load(file));
        LastDisc.Save("/some/where/game.iso", file);
        Assert.Equal("/some/where/game.iso", LastDisc.Load(file));
    }

    [Fact]
    public void BrowserListsFoldersAndIsosOnly()
    {
        Iso("a.iso");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_dir, "Sub"));
        Directory.CreateDirectory(Path.Combine(_dir, ".hidden"));
        var b = new Browser(_dir);
        Assert.Equal([("..", Browser.Kind.Up), ("Sub", Browser.Kind.Folder), ("a.iso", Browser.Kind.Iso)], b.Entries.Select(e => (e.Name, e.Kind)));
        Assert.Equal(Browser.Outcome.None, b.Submit(Path.Combine(_dir, "Sub")));
        Assert.Equal(Path.Combine(_dir, "Sub"), b.Dir);
        Assert.Equal(Browser.Outcome.None, b.Activate()); // "..": back up, the cursor on Sub
        Assert.Equal(_dir, b.Dir);
        Assert.Equal("Sub", b.Entries[b.Selected].Name);
        Assert.Equal(Browser.Outcome.Pick, b.Submit($"\"{Path.Combine(_dir, "a.iso")}\""));
        Assert.Equal(Path.Combine(_dir, "a.iso"), b.Result);
        Assert.Contains(Browser.Top(), e => e.Name == "HOME");
    }

    [Fact]
    public void ScreenRejectsWrongDiscAndStartsWithGoodOne()
    {
        var screen = new LauncherScreen(new DiscScan());
        var input = new InputSnapshot();
        screen.Picked = Iso("other.iso", "SLUS_123.45");
        Assert.Equal(LauncherScreen.Result.None, screen.Update(input, 0.016f, (0, 0)));
        Assert.True(screen.Message is (var text, true) && text.StartsWith("other.iso: WRONG GAME"));
        screen.Picked = Iso("good.iso");
        Assert.Equal(LauncherScreen.Result.Start, screen.Update(input, 0.016f, (0, 0)));
        Assert.Equal(Path.Combine(_dir, "good.iso"), screen.Chosen!.Path);
    }
}
