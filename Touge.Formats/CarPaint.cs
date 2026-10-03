using System.Buffers.Binary;

namespace Touge.Formats;

/// <summary>
///     Body paint from BINARY/CAR_ENV.BIN (loaded at 0x15CBB0, applied at 0x15D060). 32 blocks back to back:
///     "CEB\0", s16 car id, s16 colour count, 8 B zero, then count x 0x70 colour records:
///     u32 R, G, B, A (0xFF) + 0x60 B reflection table (12 lighting rows x 2 x 2 x byte pair; row 11 = 0x52 default).
///     Colour 0 is the car's default (anime) colour. The game writes RGB into every part of the car; only CMD
///     materials with flag 0x100 take it (0x186xxx), their own RGB is ignored.
/// </summary>
public static class CarPaint
{
    public const int PaintFlag = 0x100;

    /// <summary>Car ids as used by the game (name table at 0x2C4978); also the HCAR/CARPARTS file names.</summary>
    public static readonly string[] Cars =
    [
        "AE86T", "AE86L", "AE85", "MR2", "MRS", "ALTEZ", "GT-4", "R32", "R34", "ER34", "S13", "S14Q", "S14", "S15",
        "ONE80", "SIL80", "EK9", "EG6", "INTGR", "S2000", "EVO3", "EVO4", "EVO7", "FD3S", "FD3SA", "FC3S", "NA6C",
        "NB8C", "IMP", "IMP2", "IMP3", "CAPPU",
    ];

    /// <summary>Paint colours per car id, as 0xBBGGRR (same byte order as the CMD material RGBA).</summary>
    public static Dictionary<int, uint[]> Parse(ReadOnlySpan<byte> d)
    {
        var cars = new Dictionary<int, uint[]>();
        for (var p = 0; p + 0x10 <= d.Length;)
        {
            if (!d.Slice(p, 4).SequenceEqual("CEB\0"u8)) throw new InvalidDataException($"no CEB magic at 0x{p:X}");
            int car = BinaryPrimitives.ReadInt16LittleEndian(d[(p + 4)..]), n = BinaryPrimitives.ReadInt16LittleEndian(d[(p + 6)..]);
            var cols = new uint[n];
            for (var i = 0; i < n; i++)
            {
                var r = d[(p + 0x10 + i * 0x70)..];
                cols[i] = r[0] | (uint)r[4] << 8 | (uint)r[8] << 16;
            }
            cars[car] = cols;
            p += 0x10 + n * 0x70;
        }
        return cars;
    }

    /// <summary>Copy of the mesh with RGB of all paint materials replaced (alpha kept).</summary>
    /// <summary>Paint is 0xFF = full colour; material RGBA uses the PS2 scale 0x80 = 1.0, so it is halved.</summary>
    public static Mesh Apply(Mesh m, uint rgb)
    {
        var half = (rgb >> 1) & 0x7F7F7F;
        return new Mesh
        {
            Textures = m.Textures, Nodes = m.Nodes,
            Materials = [.. m.Materials.Select(x => (x.Flags & PaintFlag) != 0 ? x with { Rgba = x.Rgba & 0xFF000000 | half } : x)],
        };
    }
}
