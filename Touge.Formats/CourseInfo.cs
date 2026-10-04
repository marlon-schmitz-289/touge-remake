using System.Buffers.Binary;
using System.Numerics;

namespace Touge.Formats;

/// <summary>
/// CRS_INFO_&lt;KURS&gt;.BIN (Magic "CIF\0", Version 0x40000). Gelesen wird bisher nur der Nebel (Spiel: Loader 0x162420
/// → 0x162290 mit Datei+0x1E0 und Slot = Byte 0x1D7 × 2 + Byte 0x1D8 im Kursobjekt, ab 3 auf 2 begrenzt):
/// 4 × f32-RGBA (0..255) ab 0x200 je Slot, das Spiel halbiert sie zum GS-Farbwort (Kursobjekt +0x160).
/// </summary>
public static class CourseInfo
{
    /// <summary>Slot je Tageszeit: Tag 0, Regen 1, Nacht 2 (Farben weiß-grau / grau / schwarz in allen Kursen).</summary>
    public static int FogSlot(string time) => time switch { "RIN" => 1, "NIT" => 2, _ => 0 };

    /// <summary>Nebelfarbe des Slots, 0..1 (Gamma wie die Vertexfarben).</summary>
    public static Vector3 FogColour(ReadOnlySpan<byte> cif, int slot)
    {
        if (cif.Length < 0x240 || !cif[..4].SequenceEqual("CIF\0"u8)) throw new InvalidDataException("CRS_INFO: kein CIF");
        var c = cif[(0x200 + Math.Clamp(slot, 0, 2) * 16)..];
        return new Vector3(BinaryPrimitives.ReadSingleLittleEndian(c), BinaryPrimitives.ReadSingleLittleEndian(c[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(c[8..])) / 255;
    }
}
