using System.Buffers.Binary;
using System.Numerics;

namespace Touge.Formats;

/// <summary>
///     CRS_DATA <c>TREE_M_&lt;KURS&gt;_L/_R.BIN</c> (Vorlagen <c>treeMid_L/R%02d</c>) und <c>TREE_L_…</c> (<c>treeLrg_…</c>),
///     Loader 0x164F40: u32 n, 12 B 0, n × 0x40: u8 Vorlage, 3 × u8 (≈ 0xF0–0xFE, vom Spiel nicht benutzt), 12 B 0,
///     f32 x, y, z, 1, f32 Drehung xyz in Grad (nur y ≠ 0, wird beim Laden ersetzt), 0, f32 Skalierung xyz, 1.
/// </summary>
public static class CourseTrees
{
    public readonly record struct Tree(int Template, Vector3 Position, Vector3 Scale);

    public static Tree[] Read(ReadOnlySpan<byte> d)
    {
        var n = BinaryPrimitives.ReadInt32LittleEndian(d);
        if (n < 0 || 16 + n * 0x40 > d.Length) throw new InvalidDataException($"TREE: {n} Bäume > {d.Length} Bytes");
        var t = new Tree[n];
        for (var i = 0; i < n; i++)
        {
            var r = d.Slice(16 + i * 0x40, 0x40);
            t[i] = new Tree(r[0], DrivingLine.Vec3(r[0x10..]), DrivingLine.Vec3(r[0x30..]));
        }
        return t;
    }

    /// <summary>
    ///     Weltmatrix wie im Spiel (0x164F40): nächster ROAD-Punkt i (0x163840, 3D), Lot auf das Stück i → i+1 (0x173CE0, nur
    ///     xz), Drehung y = atan2(−v.z, v.x) − 90° mit v = Lotpunkt − Baum (Vorlage schaut zur Straße), y −= Skalierung.y / 4,
    ///     dann Skalierung · Drehung · Verschiebung (Zeilenvektoren, Drehung wie sceVu0RotMatrixY = <see cref="Matrix4x4.CreateRotationY(float)"/>).
    /// </summary>
    public static Matrix4x4 Placement(Tree t, Vector3[] road)
    {
        var i = 0;
        for (var k = 1; k < road.Length; k++)
            if (Vector3.DistanceSquared(road[k], t.Position) < Vector3.DistanceSquared(road[i], t.Position)) i = k;
        var a = road[i];
        var dir = road[(i + 1) % road.Length] - a;
        var s = Vector3.Dot(dir, t.Position - a) / MathF.Max(dir.LengthSquared(), 1e-6f);
        var v = new Vector2(a.X + dir.X * s - t.Position.X, a.Z + dir.Z * s - t.Position.Z);
        var yaw = MathF.Atan2(-v.Y, v.X) - MathF.PI / 2;
        var pos = t.Position with { Y = t.Position.Y - t.Scale.Y / 4 };
        return Matrix4x4.CreateScale(t.Scale) * Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(pos);
    }
}
