using System.Buffers.Binary;

namespace Touge.Formats;

/// <summary>
///     Sony SPU2 ADPCM ("VAG" body, no header): 16-byte frames = u8 predictor&lt;&lt;4|shift, u8 flags, 14 bytes = 28 nibbles (low first).
///     Flags: 4 = loop start, 1 = last frame (with 2 = jump back to the loop start, else stop).
///     Plus the two banks the game stores it in: <c>SYSSE.BIN</c> and Sony HD/BD (in CARSE.AFS <c>.MRG</c>).
/// </summary>
public static class Vag
{
    public sealed record Sound(short[] Pcm, int Rate, (int Start, int End)? Loop);

    private static readonly int[,] Filter = { { 0, 0 }, { 60, 0 }, { 115, -52 }, { 98, -55 }, { 122, -60 } };

    /// <summary>Decodes mono samples up to the first end-flag frame (or the end of the span). Loop in samples, end exclusive.</summary>
    public static Sound Decode(ReadOnlySpan<byte> d, int rate)
    {
        var frames = d.Length / 16;
        var pcm = new List<short>(frames * 28);
        int h1 = 0, h2 = 0, loopStart = -1;
        (int, int)? loop = null;
        for (var f = 0; f < frames; f++)
        {
            var fr = d.Slice(f * 16, 16);
            int pred = Math.Min(fr[0] >> 4, 4), shift = fr[0] & 15, flags = fr[1];
            if ((flags & 4) != 0) loopStart = pcm.Count;
            for (var i = 0; i < 28; i++)
            {
                var b = fr[2 + i / 2];
                var nib = (short)(((i & 1) == 0 ? b & 15 : b >> 4) << 12) >> shift;
                var s = Math.Clamp(nib + ((h1 * Filter[pred, 0] + h2 * Filter[pred, 1] + 32) >> 6), short.MinValue, short.MaxValue);
                pcm.Add((short)s);
                (h2, h1) = (h1, s);
            }
            if ((flags & 1) == 0) continue;
            if ((flags & 2) != 0 && loopStart >= 0) loop = (loopStart, pcm.Count);
            break;
        }
        return new Sound(pcm.ToArray(), rate, loop);
    }

    /// <summary>SPU pitch register → sample rate (0x1000 = 48 kHz).</summary>
    public static int PitchToRate(int pitch) => (int)Math.Round(pitch * 48000.0 / 4096);

    /// <summary>
    ///     <c>SOUND/SYSSE.BIN</c>: u32 count, u32 offset table pos, u32 pitch table pos, u32 data pos;
    ///     count × u32 offset (from data pos), count × u32 SPU pitch. Names in <c>SYSSE.TBL</c> (same layout as AFS .TBL).
    /// </summary>
    public static (int Offset, int Size, int Rate)[] SysSe(ReadOnlySpan<byte> d)
    {
        var n = BinaryPrimitives.ReadInt32LittleEndian(d);
        int offs = BinaryPrimitives.ReadInt32LittleEndian(d[4..]), pitch = BinaryPrimitives.ReadInt32LittleEndian(d[8..]),
            data = BinaryPrimitives.ReadInt32LittleEndian(d[12..]);
        var r = new (int, int, int)[n];
        for (var i = 0; i < n; i++)
        {
            var o = data + BinaryPrimitives.ReadInt32LittleEndian(d[(offs + i * 4)..]);
            var end = i + 1 < n ? data + BinaryPrimitives.ReadInt32LittleEndian(d[(offs + i * 4 + 4)..]) : d.Length;
            r[i] = (o, end - o, PitchToRate(BinaryPrimitives.ReadInt32LittleEndian(d[(pitch + i * 4)..])));
        }
        return r;
    }

    /// <summary>
    ///     CARSE <c>.MRG</c>: u32 count, count × u32 offset; each part has its u32 size just before it.
    ///     Parts: <c>mrg.lst</c> (text: names of the next parts), <c>*.bd</c>, <c>*.hd</c>.
    /// </summary>
    public static (byte[] Hd, byte[] Bd) Mrg(ReadOnlySpan<byte> d)
    {
        if (BinaryPrimitives.ReadInt32LittleEndian(d) != 3) throw new InvalidDataException("MRG: expected 3 parts (lst, bd, hd)");
        ReadOnlySpan<byte> Part(ReadOnlySpan<byte> m, int i)
        {
            var o = BinaryPrimitives.ReadInt32LittleEndian(m[(4 + i * 4)..]);
            return m.Slice(o, BinaryPrimitives.ReadInt32LittleEndian(m[(o - 4)..]));
        }
        return (Part(d, 2).ToArray(), Part(d, 1).ToArray());
    }

    /// <summary>
    ///     Sony HD (<c>"IECSsreV"</c> …): the <c>"IECSigaV"</c> chunk = u32 size, u32 last index, offsets (from chunk start)
    ///     to 8-byte entries u32 BD offset, u16 rate in Hz, u8 loop, u8 0xFF. Size of a sample = gap to the next one.
    /// </summary>
    public static (int Offset, int Size, int Rate, bool Loop)[] HdSamples(ReadOnlySpan<byte> hd, int bdLength)
    {
        var c = hd.IndexOf("IECSigaV"u8);
        if (c < 0) throw new InvalidDataException("HD without Vagi chunk");
        var n = BinaryPrimitives.ReadInt32LittleEndian(hd[(c + 12)..]) + 1;
        var r = new (int Offset, int Size, int Rate, bool Loop)[n];
        for (var i = 0; i < n; i++)
        {
            var e = hd[(c + BinaryPrimitives.ReadInt32LittleEndian(hd[(c + 16 + i * 4)..]))..];
            r[i] = (BinaryPrimitives.ReadInt32LittleEndian(e), 0, BinaryPrimitives.ReadUInt16LittleEndian(e[4..]), e[6] != 0);
        }
        for (var i = 0; i < n; i++) r[i].Size = (i + 1 < n ? r[i + 1].Offset : bdLength) - r[i].Offset;
        return r;
    }
}
