using System.Buffers.Binary;
using System.Numerics;

namespace Touge.Formats;

/// <summary>
/// CRS_DRV_&lt;KURS&gt;_I/O.BIN: Fahrlinie, n × (f32 x, y, z), ~10 m Abstand, sonst nichts pro Punkt.
/// _I = Fahrtrichtung entlang ROAD, _O = Gegenrichtung. Die Datei ist fest 1000 Punkte groß (USUI0 916),
/// die gültige Anzahl steht nicht darin, sondern in einer Tabelle im ELF (0x2C4920, je Kurs u32 + u32 0,
/// genutzt vom Nächster-Punkt-Suchlauf 0x157170; I und O gleich). Dahinter: Auslauf hinter dem Ziel, dann Speichermüll.
/// </summary>
public static class DrivingLine
{
    /// <summary>Gültige Punktanzahl je Kurs, Reihenfolge wie die Kurstabelle im ELF (MYOUGI0 = 0).</summary>
    public static int PointCount(string course) => course.ToUpperInvariant() switch
    {
        "MYOUGI0" => 952, "USUI0" => 832, "AKAGI" => 583, "AKINA" => 770, "HAPPOU" => 560, "IROHA" => 512,
        "MYOUGI" => 493, "USUI" => 735, "MOMIJI" => 525, "SHIONA" => 510, "SHOMARU" => 595,
        _ => throw new ArgumentException($"unbekannter Kurs {course}", nameof(course)),
    };

    /// <summary>Kursname aus "CRS_DRV_AKINA_I.BIN".</summary>
    public static string CourseOf(string fileName) => Path.GetFileNameWithoutExtension(fileName)[8..^2];

    public static Vector3[] Read(ReadOnlySpan<byte> d, int count)
    {
        if (count * 12 > d.Length) throw new InvalidDataException($"DRV: {count} Punkte > {d.Length} Bytes");
        var p = new Vector3[count];
        for (var i = 0; i < count; i++) p[i] = Vec3(d[(i * 12)..]);
        return p;
    }

    internal static Vector3 Vec3(ReadOnlySpan<byte> d) => new(
        BinaryPrimitives.ReadSingleLittleEndian(d), BinaryPrimitives.ReadSingleLittleEndian(d[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(d[8..]));
}
