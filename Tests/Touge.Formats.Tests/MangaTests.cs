using System.Buffers.Binary;
using System.Text;
using Touge.Formats;

namespace Touge.Formats.Tests;

public class MangaTests
{
    private static void I32(byte[] d, int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(at), v);

    /// <summary>Type 2: 8-byte header, flag byte LSB first, match = 12-bit window position (window starts at 0xFEE) + length − 3.</summary>
    [Fact]
    public void Lz_type2_is_classic_lzss()
    {
        byte[] packed = [0x12, 0x3D, 0xDA, 0x02, 9, 0, 0, 0, 0x07, (byte)'A', (byte)'B', (byte)'C', 0xEE, 0xF3];
        Assert.Equal("ABCABCABC"u8.ToArray(), Lz.Decompress(packed));
    }

    /// <summary>FPK entry (offset in 16-byte units) holding an 8-bit TIM2 whose CLUT is stored CSM1 (index bits 3 and 4 swapped).</summary>
    [Fact]
    public void Fpk_and_tim2_decode()
    {
        var tim = new byte[0x10 + 0x30 + 16 + 1024];
        "TIM2"u8.CopyTo(tim);
        var p = 0x10;
        I32(tim, p + 8, 16); // image size (2×1 padded)
        BinaryPrimitives.WriteUInt16LittleEndian(tim.AsSpan(p + 12), 0x30);
        BinaryPrimitives.WriteUInt16LittleEndian(tim.AsSpan(p + 14), 256);
        (tim[p + 18], tim[p + 19]) = (3, 5);
        BinaryPrimitives.WriteUInt16LittleEndian(tim.AsSpan(p + 20), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(tim.AsSpan(p + 22), 1);
        (tim[p + 0x30], tim[p + 0x31]) = (8, 1);
        var clut = p + 0x30 + 16;
        new byte[] { 255, 0, 0, 0x80 }.CopyTo(tim, clut + 16 * 4); // index 8 lives at slot 16
        new byte[] { 0, 0, 255, 0x40 }.CopyTo(tim, clut + 1 * 4);

        var fpk = new byte[0x30 + tim.Length];
        "01_00.ICP"u8.CopyTo(fpk);
        I32(fpk, 16, 3);
        I32(fpk, 20, tim.Length);
        tim.CopyTo(fpk, 0x30);

        var e = Assert.Single(Manga.Fpk(fpk));
        Assert.Equal(new Manga.FpkEntry("01_00.ICP", 0x30, tim.Length), e);
        var (w, h, rgba) = Manga.Tim2(Manga.Read(fpk, e));
        Assert.Equal((2, 1), (w, h));
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 128 }, rgba);
        Assert.Throws<InvalidDataException>(() => Manga.Tim2(new byte[0x40]));
    }

    [Fact]
    public void Timeline_reads_frames_and_commands()
    {
        var b = new List<byte>();
        void Block(int frame, params string[] toks)
        {
            b.AddRange(BitConverter.GetBytes(frame));
            foreach (var t in toks) b.AddRange(Encoding.ASCII.GetBytes(t + "\0"));
            b.Add(0);
            while (b.Count % 4 != 0) b.Add(0);
        }
        Block(0, "BG_summer_d", "P_(20,20)", "F_01_08");
        Block(234, "QUIT");
        b.AddRange(BitConverter.GetBytes(-1));
        var cues = Manga.Timeline(b.ToArray());
        Assert.Equal(2, cues.Count);
        Assert.Equal(["BG_summer_d", "P_(20,20)", "F_01_08"], cues[0].Tokens);
        Assert.Equal((234, "QUIT"), (cues[1].Frame, cues[1].Tokens[0]));
    }

    /// <summary>ROBJ resources: 4 = page lip lists per slot, 5 = file names, 6 = face sprite tables (32 pictures × 24 bytes each).</summary>
    [Fact]
    public void Robj_resources()
    {
        var d = new byte[0x1000];
        "ROBJ"u8.CopyTo(d);
        I32(d, 0x10, 0x100); // resource 4
        I32(d, 0x14, 0x200); // resource 5
        I32(d, 0x18, 0x300); // resource 6
        // slot 3: one page → lip record at 0x180 → digits at 0x190
        I32(d, 0x100 + 8 * 3, 0x170);
        I32(d, 0x100 + 8 * 3 + 4, 1);
        I32(d, 0x170 + 4, -1);
        I32(d, 0x170 + 8, 0x180);
        I32(d, 0x180 + 4, 4);
        I32(d, 0x180 + 8, 0x190);
        "0035"u8.CopyTo(d.AsSpan(0x190));
        I32(d, 0x200, 0x220);
        "STORY23.FPK"u8.CopyTo(d.AsSpan(0x220));
        I32(d, 0x300, 0x400); // face 0 table
        I32(d, 0x304, 0x700); // face 1 table, empty
        for (var n = 0; n < 6; n++) I32(d, 0x400 + 24 * 10 + 4 * n, 100 + n);

        Assert.Equal("STORY23.FPK", Manga.Pictures(d));
        var lips = Manga.Lips(d);
        Assert.Equal(["0035"], lips[3]);
        Assert.Empty(lips[0]);
        var faces = Manga.Faces(d);
        Assert.Equal(new Manga.Face(100, 101, 102, 103, 104, 105), faces[0][10]);
        Assert.Null(faces[0][11]);
        Assert.All(faces[1].Concat(faces[2]), Assert.Null);
    }

    [Fact]
    public void Chapters_and_voice_names_from_the_elf_tables()
    {
        int At(int address) => address - StoryScript.ElfBase;
        var elf = new byte[At(0x2CF400)];
        for (var c = 0; c < StoryScript.Chapters; c++)
        {
            elf[At(0x2CF380) + c] = (byte)c;
            elf[At(0x2CF3A0) + c] = c == 9 ? (byte)32 : (byte)0xFF;
            elf[At(0x2CF3C0) + c] = c < 2 ? (byte)0xFF : (byte)c;
            elf[At(0x299F30) + c] = c switch { 22 => 23, 23 => 22, _ => (byte)c };
        }
        var ch = Manga.ReadChapters(elf);
        Assert.Equal(new Manga.Chapter(0, 0, -1, false, 0), ch[0]);
        Assert.Equal(new Manga.Chapter(9, 9, 32, true, 9), ch[9]);
        Assert.Equal(23, ch[22].Episode);
        Assert.Equal("03_00.adx", Manga.KomaVoice(2));
        Assert.Equal("10_03.adx", Manga.KomaVoice(32));
        Assert.Equal(["03_01.adx", "03_02.adx", "31_03.adx"], [Manga.SceneVoice(2, 0), Manga.SceneVoice(2, 3), Manga.SceneVoice(30, 4)]);
        elf[At(0x299F30) + 5] = 6; // two chapters on one episode: not the table
        Assert.Throws<InvalidDataException>(() => Manga.ReadChapters(elf));
    }
}
