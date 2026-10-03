using System.Buffers.Binary;

namespace Touge.Formats;

/// <summary>
///     CARSE <c>.DAT</c> ("SECTver0.502"): per bank 16 entries (one per HD program/voice; the game drives the first 8),
///     u32 offsets at 0x20. Each entry = 3 curves × 6 points (i32 x 0…255, i32 y 0…127): pitch (64 = centre, sent as
///     pitch bend y·128), volume, pan. The game expands them piecewise-linearly into a 256-row table
///     (sub_001981A0) and indexes it with an rpm-like value 0…255 (sub_00178800). Default curve: y = 64 everywhere.
/// </summary>
public sealed class Sect
{
    public const int Entries = 16, Pitch = 0, Volume = 1, Pan = 2;
    private readonly byte[] _table = new byte[Entries * 3 * 256]; // [entry][curve][x]

    public Sect(ReadOnlySpan<byte> d)
    {
        if (!d.StartsWith("SECTver"u8)) throw new InvalidDataException("no SECT curve file");
        for (var e = 0; e < Entries; e++)
        {
            var o = BinaryPrimitives.ReadInt32LittleEndian(d[(0x20 + e * 4)..]);
            for (var c = 0; c < 3; c++)
            {
                var row = _table.AsSpan((e * 3 + c) * 256, 256);
                var pts = d.Slice(o + c * 0x30, 0x30);
                for (var k = 0; k < 5; k++)
                {
                    int x0 = Read(pts, 2 * k), y0 = Read(pts, 2 * k + 1), x1 = Read(pts, 2 * k + 2), y1 = Read(pts, 2 * k + 3);
                    for (var x = Math.Max(x0, 0); x <= Math.Min(x1, 255); x++)
                        row[x] = (byte)Math.Clamp(x1 == x0 ? y1 : (int)(y0 + (y1 - y0) * (float)(x - x0) / (x1 - x0)), 0, 127);
                }
            }
        }
    }

    private static int Read(ReadOnlySpan<byte> pts, int i) => BinaryPrimitives.ReadInt32LittleEndian(pts[(i * 4)..]);

    /// <summary>Curve value 0…127 of <paramref name="entry"/> at <paramref name="x"/> (clamped to 0…255).</summary>
    public int Value(int entry, int curve, int x) => _table[(entry * 3 + curve) * 256 + Math.Clamp(x, 0, 255)];
}
