using System.Buffers.Binary;
using System.Text;

namespace Touge.Formats;

/// <summary>
///     Story mode pictures and timelines of the original (FORMATS.md "Story – Manga"): FPK archives of TIM2 pictures (manga
///     panels MG_KOMAF <c>KOMAnn.FPK</c>, portraits MG_STR <c>STORYnn.FPK</c>), the panel timelines MG_KOMAM <c>KOMATCnn.BIN</c>
///     and the resources of a ROBJ scene (MG_OBJ <c>STRnn.BIN</c>: picture archive, face sprite positions, lip-sync strings).
/// </summary>
public static class Manga
{
    public readonly record struct FpkEntry(string Name, int Offset, int Size);

    /// <summary>FPK: 24-byte entries name[16], u32 offset / 16, u32 size up to an empty name; data LZ type 2 (pictures) or raw.</summary>
    public static FpkEntry[] Fpk(ReadOnlySpan<byte> d)
    {
        var list = new List<FpkEntry>();
        for (var at = 0; at + 24 <= d.Length && d[at] != 0; at += 24)
        {
            var name = Encoding.ASCII.GetString(d.Slice(at, 16)).TrimEnd('\0');
            int off = BinaryPrimitives.ReadInt32LittleEndian(d[(at + 16)..]) * 16, size = BinaryPrimitives.ReadInt32LittleEndian(d[(at + 20)..]);
            if (off + size > d.Length) throw new InvalidDataException($"FPK entry {name} out of range");
            list.Add(new FpkEntry(name, off, size));
        }
        return [.. list];
    }

    public static byte[] Read(ReadOnlySpan<byte> fpk, FpkEntry e)
    {
        var s = fpk.Slice(e.Offset, e.Size);
        return Lz.IsCompressed(s) ? Lz.Decompress(s) : s.ToArray();
    }

    /// <summary>
    ///     TIM2 ("TIM2", v4, 1 picture) as on the disc: 8-bit indices, 256-colour CT32 CLUT stored CSM1 (index bits 3/4
    ///     swapped), header 0x30 at +0x10 (u32 total, clut size, image size, u16 header size, colours, u8 ?, mips, clut type,
    ///     image type, u16 w, h), pixels then CLUT. Alpha 0x80 = opaque.
    /// </summary>
    public static (int W, int H, byte[] Rgba) Tim2(ReadOnlySpan<byte> t)
    {
        if (t.Length < 0x40 || !t[..4].SequenceEqual("TIM2"u8)) throw new InvalidDataException("no TIM2");
        var p = t[0x10..];
        int imgSize = BinaryPrimitives.ReadInt32LittleEndian(p[8..]), hdr = BinaryPrimitives.ReadUInt16LittleEndian(p[12..]);
        int colours = BinaryPrimitives.ReadUInt16LittleEndian(p[14..]), clutType = p[18], imgType = p[19];
        int w = BinaryPrimitives.ReadUInt16LittleEndian(p[20..]), h = BinaryPrimitives.ReadUInt16LittleEndian(p[22..]);
        if (imgType != 5 || clutType != 3 || colours != 256) throw new NotSupportedException($"TIM2 image {imgType} clut 0x{clutType:X} × {colours}");
        var idx = p.Slice(hdr, w * h);
        var clut = p.Slice(hdr + imgSize, 1024);
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < idx.Length; i++)
        {
            var c = clut.Slice(Gs.ClutIndex(idx[i]) * 4, 4);
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (c[0], c[1], c[2], (byte)Math.Min(255, c[3] * 2));
        }
        return (w, h, rgba);
    }

    /// <summary>One block of a panel timeline: from ADX time <see cref="Frame" /> (60 Hz) on, the commands.</summary>
    public sealed record Cue(int Frame, string[] Tokens);

    /// <summary>KOMATC: blocks of i32 frame (−1 = end), NUL-terminated ASCII commands, an empty one, padding to 4 bytes.</summary>
    public static List<Cue> Timeline(ReadOnlySpan<byte> t)
    {
        var list = new List<Cue>();
        for (var at = 0; at + 4 <= t.Length;)
        {
            var frame = BinaryPrimitives.ReadInt32LittleEndian(t[at..]);
            at += 4;
            if (frame < 0) break;
            var toks = new List<string>();
            while (true)
            {
                var len = t[at..].IndexOf((byte)0);
                if (len < 0) throw new InvalidDataException("KOMATC: unterminated command");
                at += len + 1;
                if (len == 0) break;
                toks.Add(Encoding.ASCII.GetString(t.Slice(at - len - 1, len)));
            }
            at = (at + 3) & ~3;
            list.Add(new Cue(frame, [.. toks]));
        }
        return list;
    }

    /// <summary>Scene slots of a ROBJ (resource 1, 24 × 32 bytes): 0 before the race, 3 after a win, 4 epilogue (chapter 30).</summary>
    public const int Slots = 24;

    /// <summary>Resource n of a ROBJ: u32 offset at 4·n (the game relocates them at load, 0x1D7100).</summary>
    private static int Res(ReadOnlySpan<byte> robj, int n)
    {
        if (robj.Length < 0x1C || !robj[..4].SequenceEqual("ROBJ"u8)) throw new InvalidDataException("no ROBJ");
        return BinaryPrimitives.ReadInt32LittleEndian(robj[(4 * n)..]);
    }

    private static string CStr(ReadOnlySpan<byte> d, int at) => at <= 0 ? "" : Encoding.ASCII.GetString(d[at..][..d[at..].IndexOf((byte)0)]);

    /// <summary>Resource 5[0]: the picture archive of the scene (MG_STR, e.g. "STORY23.FPK" for STR22 – episode numbering).</summary>
    public static string Pictures(ReadOnlySpan<byte> robj) => CStr(robj, BinaryPrimitives.ReadInt32LittleEndian(robj[Res(robj, 5)..]));

    /// <summary>Where the animated parts go on a portrait (top-left of the 80 × 128 cells; 0/0 = not drawn).</summary>
    public readonly record struct Face(int MouthX, int MouthY, int Eye1X, int Eye1Y, int Eye2X, int Eye2Y);

    /// <summary>
    ///     Resource 6: up to 3 faces per picture, each a table of 32 × 24 bytes indexed by the picture number (P_n). Face k takes
    ///     its sprites from rows 512 + 256·k (mouth, frame f at x = 80·f, 6 frames) and 640 + 256·k (eyes: frames 0–2 first
    ///     eye, 3–5 second eye; 0 open … 2 shut). Missing face/picture = null.
    /// </summary>
    public static Face?[][] Faces(ReadOnlySpan<byte> robj)
    {
        var at = Res(robj, 6);
        var faces = new Face?[3][];
        for (var k = 0; k < 3; k++)
        {
            faces[k] = new Face?[32];
            var tab = BinaryPrimitives.ReadInt32LittleEndian(robj[(at + 4 * k)..]);
            if (tab == 0) continue;
            for (var i = 0; i < 32; i++)
            {
                var v = new int[6];
                for (var n = 0; n < 6; n++) v[n] = BinaryPrimitives.ReadInt32LittleEndian(robj[(tab + 24 * i + 4 * n)..]);
                var f = new Face(v[0], v[1], v[2], v[3], v[4], v[5]);
                if (f != default) faces[k][i] = f;
            }
        }
        return faces;
    }

    /// <summary>
    ///     Resource 4: per slot (u32 list, u32 count) a list of pages (u32 page, i32 −1, u32 → lip record (u32 0, u32 length,
    ///     u32 → digits)). One digit per 60-Hz frame from the page's first balloon on = mouth frame 0–5 of the speaking face
    ///     (C_n). Indexed by page number (pages counted by N in the slot; pages without speech have none: "").
    /// </summary>
    public static string[][] Lips(ReadOnlySpan<byte> robj)
    {
        var at = Res(robj, 4);
        var slots = new string[Slots][];
        for (var s = 0; s < Slots; s++)
        {
            int list = BinaryPrimitives.ReadInt32LittleEndian(robj[(at + 8 * s)..]), n = BinaryPrimitives.ReadInt32LittleEndian(robj[(at + 8 * s + 4)..]);
            var pages = new SortedDictionary<int, string>();
            for (var i = 0; list != 0 && i < n; i++)
            {
                int page = BinaryPrimitives.ReadInt32LittleEndian(robj[(list + 12 * i)..]), rec = BinaryPrimitives.ReadInt32LittleEndian(robj[(list + 12 * i + 8)..]);
                if (page is >= 0 and < 1000) pages[page] = CStr(robj, BinaryPrimitives.ReadInt32LittleEndian(robj[(rec + 8)..]));
            }
            slots[s] = new string[pages.Count == 0 ? 0 : pages.Keys.Max() + 1];
            Array.Fill(slots[s], "");
            foreach (var (page, digits) in pages) slots[s][page] = digits;
        }
        return slots;
    }

    /// <summary>Voice/drama track (MANGAV/MG_KOMAS) of story chapter c's scene slot (0 → _01, 3 → _02, 4 → _03).</summary>
    public static string SceneVoice(int chapter, int slot) => $"{chapter + 1:00}_{(slot == 0 ? 1 : slot == 3 ? 2 : 3):00}.adx";

    /// <summary>Drama track of panel timeline KOMATCn (n + 1)_00, KOMATC32 = 10_03 (KOMATC27 has none on the disc).</summary>
    public static string KomaVoice(int timeline) => timeline == 32 ? "10_03.adx" : $"{timeline + 1:00}_00.adx";

    /// <param name="Before">Panel timeline (KOMATCn) played when the chapter starts, −1 = none.</param>
    /// <param name="After">Panel timeline after the won race (only chapter 9: 32), −1 = none.</param>
    /// <param name="Scene">Chapter has a ROBJ scene STRnn (2–30).</param>
    /// <param name="Episode">Number of the manga episode = BGSTRnn/KOMABGnn/MTnn and the scene's FPK (22↔23, 25–29 permuted).</param>
    public sealed record Chapter(int Index, int Before, int After, bool Scene, int Episode);

    /// <summary>
    ///     From the ELF (sub_1CC4B0/sub_1CC960, sub_1D55B0): 0x2CF380 timeline before, 0x2CF3A0 after, 0x2CF3C0 scene, 0x299F30
    ///     BGSTR number, per chapter one signed byte.
    /// </summary>
    public static Chapter[] ReadChapters(ReadOnlySpan<byte> elf)
    {
        var list = new Chapter[StoryScript.Chapters];
        for (var c = 0; c < list.Length; c++)
        {
            sbyte B(ReadOnlySpan<byte> e, int address) => (sbyte)e[address - StoryScript.ElfBase + c];
            list[c] = new Chapter(c, B(elf, 0x2CF380), B(elf, 0x2CF3A0), B(elf, 0x2CF3C0) == c, B(elf, 0x299F30));
        }
        if (list.Select(c => c.Episode).Order().SequenceEqual(Enumerable.Range(0, list.Length)) is false)
            throw new InvalidDataException("manga chapter table looks wrong");
        return list;
    }

    /// <summary>KOMABG number of panel timeline n (0x299FD0, 33 signed bytes; −1 = no timeline).</summary>
    public static int KomaBackground(ReadOnlySpan<byte> elf, int timeline) => (sbyte)elf[0x299FD0 - StoryScript.ElfBase + timeline];
}
