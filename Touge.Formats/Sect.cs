using System.Buffers.Binary;

namespace Touge.Formats;

/// <summary>
///     CARSE <c>.DAT</c> ("SECTver0.502"): per bank 16 entries (one per HD program/voice; the game drives the first 8),
///     u32 offsets at 0x20. Each entry = 3 curves × 6 points (i32 x 0…255, i32 y 0…127): pitch (64 = centre, sent as
///     pitch bend y·128), volume, pan. The game expands them piecewise-linearly into a 256-row table
///     (sub_001981A0) and indexes it with <see cref="EngineIndex"/> (sub_0018A170 → sub_00178D20, read in sub_00178800).
///     Default curve: y = 64 everywhere.
/// </summary>
public sealed class Sect
{
    public const int Entries = 16, Pitch = 0, Volume = 1, Pan = 2;

    /// <summary>
    ///     Pitch-bend range of the CARSE programs: HD split bendRangeLow/High = 0x0100 in 1/128 semitone (MODHSYN.IRX
    ///     0x45C0: bend·range ≫ 13 into the voice's fine pitch, 128 per semitone) → curve value 0/127 = −2/+1.97 semitones.
    /// </summary>
    public const float BendSemitones = 2;

    /// <summary>
    ///     The engine voices are keyed on note 64 (sub_00178800 → sub_0017F380, t0 = 0x40) against sampleBaseNote 60 of
    ///     every CARSE sample (Smpl chunk byte 11): all layers play 4 semitones above their sample rate.
    /// </summary>
    public const int KeyTranspose = 4;

    // (a, b, c) per profile, ELF 0x24EAD0; the game picks it by tuning level (sub_00189B00: stock 0, tuned 1–2)
    private static readonly (float A, float B, float C)[] Profiles = [(3, 5, 16), (2.6f, 4.6f, 10), (2.1f, 4.1f, 7)];

    /// <summary>
    ///     Curve index of the engine sound, sub_0018A170 (soft-float doubles): with x = rpm / rev limit (0…1) and
    ///     g = (gear − 1) / (gears − 1), <c>5 + 250·(0.8·x^(a + (b − a)·g) + 0.2·√x + 3·(1 − x)·x^c)</c> — 5 at standstill,
    ///     255 at the limit, rising late in low gears (high exponent) and earlier in top gear.
    /// </summary>
    public static float EngineIndex(float x, float gear, int profile)
    {
        var (a, b, c) = Profiles[profile];
        x = Math.Clamp(x, 0, 1);
        return 5 + 250 * (0.8f * MathF.Pow(x, a + (b - a) * gear) + 0.2f * MathF.Sqrt(x) + 3 * (1 - x) * MathF.Pow(x, c));
    }
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
