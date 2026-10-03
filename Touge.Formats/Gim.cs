using System.Buffers.Binary;

namespace Touge.Formats;

/// <summary>
///     Texture inside PAC (type 1). Header: "GIM\0"[16], name[16], u16 1, u16 1, u32 ?,
///     0x30 image: u16 w, h, psm, upload w, upload h, upload psm, u32 data offset,
///     0x40 clut:  same layout. Offsets relative to the GIM start.
/// </summary>
public static class Gim
{
    private const int PsmCt32 = 0x00, PsmT8 = 0x13, PsmT4 = 0x14;

    /// <summary>Decodes to RGBA8 (alpha scaled from PS2 0..128 to 0..255).</summary>
    public static (int W, int H, byte[] Rgba) Decode(ReadOnlySpan<byte> g)
    {
        if (!g[..4].SequenceEqual("GIM\0"u8)) throw new InvalidDataException("no GIM magic");
        int w = U16(g, 0x30), h = U16(g, 0x32), psm = U16(g, 0x34), tw = U16(g, 0x36), th = U16(g, 0x38), tpsm = U16(g, 0x3A);
        var dataOff = BinaryPrimitives.ReadInt32LittleEndian(g[0x3C..]);
        int cpsm = U16(g, 0x44), clutOff = BinaryPrimitives.ReadInt32LittleEndian(g[0x4C..]);
        if (psm is not (PsmT8 or PsmT4) || cpsm != PsmCt32)
            throw new NotSupportedException($"psm 0x{psm:X} / clut psm 0x{cpsm:X}");
        var bits = psm == PsmT8 ? 8 : 4;

        byte[] idx;
        if (tpsm == psm)
        {
            idx = new byte[w * h];
            for (var i = 0; i < w * h; i++)
                idx[i] = bits == 8 ? g[dataOff + i] : (byte)((g[dataOff + i / 2] >> ((i & 1) * 4)) & 0xF);
        }
        else if (tpsm == PsmCt32)
            idx = Gs.UnswizzleIndices(g.Slice(dataOff, tw * th * 4), tw, th, w, h, bits);
        else throw new NotSupportedException($"upload psm 0x{tpsm:X}");

        var clut = g[clutOff..];
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < idx.Length; i++)
        {
            var c = clut.Slice((bits == 8 ? Gs.ClutIndex(idx[i]) : idx[i]) * 4, 4);
            rgba[i * 4] = c[0];
            rgba[i * 4 + 1] = c[1];
            rgba[i * 4 + 2] = c[2];
            rgba[i * 4 + 3] = (byte)Math.Min(255, c[3] * 2);
        }
        return (w, h, rgba);
    }

    private static int U16(ReadOnlySpan<byte> g, int o) => BinaryPrimitives.ReadUInt16LittleEndian(g[o..]);
}
