using System.Buffers.Binary;
using System.Numerics;

namespace Touge.Formats;

/// <summary>
/// CRS_ROAD_&lt;KURS&gt;[_L|_R].BIN: Straßenmitte/-ränder, u32 n, u32 ? (0 oder 3, Loader ignoriert es), n × (f32 x, y, z), ~2 m Abstand.
/// ENV und FLR sind Tabellen pro ROAD-Punkt (Spiel liest mit Index mod n).
/// </summary>
public static class CourseRoad
{
    /// <summary>Env-Map-Texturen pro Punkt: Index für "ENV_TOP%02d", "ENV_BOTTOM%02d", "ENV_LEFT%02d", "ENV_RIGHT%02d" in ENV_TEX_&lt;KURS&gt;.</summary>
    public readonly record struct EnvMaps(sbyte Top, sbyte Bottom, sbyte Left, sbyte Right);

    public static Vector3[] Read(ReadOnlySpan<byte> d)
    {
        var n = BinaryPrimitives.ReadInt32LittleEndian(d);
        if (8 + n * 12 > d.Length) throw new InvalidDataException($"ROAD: {n} Punkte > {d.Length} Bytes");
        var p = new Vector3[n];
        for (var i = 0; i < n; i++) p[i] = DrivingLine.Vec3(d[(8 + i * 12)..]);
        return p;
    }

    /// <summary>CRS_ENV: 4 × s8 pro Punkt (Top, Bottom, Left, Right), kein Header. Spiel: 0x163E50.</summary>
    public static EnvMaps[] ReadEnv(ReadOnlySpan<byte> d)
    {
        var e = new EnvMaps[d.Length / 4];
        for (var i = 0; i < e.Length; i++)
            e[i] = new EnvMaps((sbyte)d[i * 4], (sbyte)d[i * 4 + 1], (sbyte)d[i * 4 + 2], (sbyte)d[i * 4 + 3]);
        return e;
    }

    /// <summary>
    /// CRS_FLR: 1 Byte pro Punkt, ≠ 0 = Sonnen-Lens-Flare sichtbar (Spiel 0x161F70 → Effekt 0x15, 11 Flare-Elemente
    /// zwischen Sonne und Kamera). Nur Kurse mit Tagesvariante haben FLR; ohne Datei ist Flare überall an.
    /// Manche Dateien sind kürzer als ROAD (Akina 3955 von 4089) – das Spiel liest dort über das Ende hinaus; hier: aus.
    /// </summary>
    public static bool[] ReadFlare(ReadOnlySpan<byte> d, int roadCount)
    {
        var f = new bool[roadCount];
        for (var i = 0; i < Math.Min(roadCount, d.Length); i++) f[i] = d[i] != 0;
        return f;
    }
}
