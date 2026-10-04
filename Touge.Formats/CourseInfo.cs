using System.Buffers.Binary;
using System.Numerics;

namespace Touge.Formats;

/// <summary>
/// CRS_INFO_&lt;KURS&gt;.BIN (Magic "CIF\0", Version 0x40000, Loader 0x162420). Slot = Byte 0x1D7 (Nacht) × 2 + Byte 0x1D8
/// (Regen) im Kursobjekt: Tag 0, Regen 1, Nacht 2. Gelesen: Autolicht (0x10…0x14F), Nebel (0x1E0…0x23F).
/// Abschnittstabelle ab 0x2C0 siehe FORMATS.md (kein Reader).
/// </summary>
public static class CourseInfo
{
    /// <summary>Slot je Tageszeit: Tag 0, Regen 1, Nacht 2 (Farben weiß-grau / grau / schwarz in allen Kursen).</summary>
    public static int FogSlot(string time) => time switch { "RIN" => 1, "NIT" => 2, _ => 0 };

    /// <summary>
    ///     Linearer Nebel des Slots in Metern Kameratiefe: Start = s32 [0x1E0 + 4·slot], Ende = s32 [0x1F0 + 4·slot]
    ///     (0x1628A0 → Kursobjekt +0x158/+0x15C → 0x1815D0 → 0x148460: GS-Fog = (Ende − z) · 255 / (Ende − Start)).
    ///     Start darf negativ sein (Dunst schon an der Kamera). Erst ab Version 0x40000 so; ältere Dateien (0x162290) hätten
    ///     3 + 3 Werte, auf der Disc gibt es keine.
    /// </summary>
    public static (float Start, float End) FogRange(ReadOnlySpan<byte> cif, int slot)
    {
        Check(cif);
        return (BinaryPrimitives.ReadInt32LittleEndian(cif[(0x1E0 + slot * 4)..]), BinaryPrimitives.ReadInt32LittleEndian(cif[(0x1F0 + slot * 4)..]));
    }

    /// <summary>
    ///     Hauptlicht des Autos im Slot: u32-Anzahl je Nacht-Flag ab 0x10, dann je Slot 3 × (f32 x, y, z, 1) Richtung ab
    ///     0x20 + 48·slot und 3 × RGBA (0..1) ab 0xE0 + 48·slot, Licht 0 = Sonne (0x162580 → 0x1769B0 "light%d").
    ///     Richtung = wohin das Licht fällt (Akina (−1, −0,5, 0): Sonne bei +X wie das Sonnensprite).
    /// </summary>
    public static (Vector3 Direction, float Intensity) KeyLight(ReadOnlySpan<byte> cif, int slot)
    {
        Check(cif);
        var d = cif[(0x20 + slot * 48)..];
        return (Vector3.Normalize(new Vector3(F(d), F(d[4..]), F(d[8..]))), F(cif[(0xE0 + slot * 48)..]));
    }

    private static float F(ReadOnlySpan<byte> d) => BinaryPrimitives.ReadSingleLittleEndian(d);

    private static void Check(ReadOnlySpan<byte> cif)
    {
        if (cif.Length < 0x240 || !cif[..4].SequenceEqual("CIF\0"u8)) throw new InvalidDataException("CRS_INFO: kein CIF");
    }

    /// <summary>Nebelfarbe des Slots, 0..1 (Gamma wie die Vertexfarben).</summary>
    public static Vector3 FogColour(ReadOnlySpan<byte> cif, int slot)
    {
        Check(cif);
        var c = cif[(0x200 + Math.Clamp(slot, 0, 2) * 16)..];
        return new Vector3(BinaryPrimitives.ReadSingleLittleEndian(c), BinaryPrimitives.ReadSingleLittleEndian(c[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(c[8..])) / 255;
    }
}
