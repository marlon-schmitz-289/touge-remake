using System.Buffers.Binary;
using System.Numerics;
using Touge.Formats;

namespace Touge.Formats.Tests;

public class CourseDataTests
{
    [Fact]
    public void Road_Drv_Env_Flr_parse_synthetic_bytes()
    {
        // ROAD: n=2, ?=3, zwei Punkte
        var road = new byte[8 + 24];
        BinaryPrimitives.WriteInt32LittleEndian(road, 2);
        BinaryPrimitives.WriteInt32LittleEndian(road.AsSpan(4), 3);
        BinaryPrimitives.WriteSingleLittleEndian(road.AsSpan(8 + 12 + 8), -5f);
        Assert.Equal([Vector3.Zero, new Vector3(0, 0, -5)], CourseRoad.Read(road));

        // DRV: 3 Punkte, nur 2 gültig
        var drv = new byte[36];
        BinaryPrimitives.WriteSingleLittleEndian(drv.AsSpan(12), 10f);
        BinaryPrimitives.WriteSingleLittleEndian(drv.AsSpan(24), float.NaN);
        Assert.Equal([Vector3.Zero, new Vector3(10, 0, 0)], DrivingLine.Read(drv, 2));
        Assert.Equal(770, DrivingLine.PointCount(DrivingLine.CourseOf("CRS_DRV_AKINA_I.BIN")));

        Assert.Equal(new CourseRoad.EnvMaps(1, -1, 2, 0), CourseRoad.ReadEnv([1, 0xFF, 2, 0])[0]);
        Assert.Equal([true, false, false], CourseRoad.ReadFlare([1, 0], 3)); // zu kurz → aus
    }

    [Fact]
    public void Cif_fog_colour_per_slot()
    {
        var cif = new byte[0x240];
        "CIF\0"u8.CopyTo(cif);
        for (var c = 0; c < 3; c++) BinaryPrimitives.WriteSingleLittleEndian(cif.AsSpan(0x210 + c * 4), 51f * (c + 1)); // Slot 1
        Assert.Equal(new Vector3(0.2f, 0.4f, 0.6f), CourseInfo.FogColour(cif, CourseInfo.FogSlot("RIN")));
        Assert.Equal(Vector3.Zero, CourseInfo.FogColour(cif, 3)); // ab 3 → Slot 2 wie im Spiel
        Assert.Throws<InvalidDataException>(() => CourseInfo.FogColour(new byte[0x240], 0));
    }

    [Fact]
    public void Cif_fog_range_and_key_light()
    {
        var cif = new byte[0x240];
        "CIF\0"u8.CopyTo(cif);
        BinaryPrimitives.WriteInt32LittleEndian(cif.AsSpan(0x1E4), -400); // Regen: Start [1], Ende [1 + 4]
        BinaryPrimitives.WriteInt32LittleEndian(cif.AsSpan(0x1F4), 1300);
        Assert.Equal((-400f, 1300f), CourseInfo.FogRange(cif, 1));
        BinaryPrimitives.WriteSingleLittleEndian(cif.AsSpan(0x20), -2f); // Tag: Licht 0 (−2, −1, 0), Stärke 0,7
        BinaryPrimitives.WriteSingleLittleEndian(cif.AsSpan(0x24), -1f);
        BinaryPrimitives.WriteSingleLittleEndian(cif.AsSpan(0xE0), 0.7f);
        var (dir, intensity) = CourseInfo.KeyLight(cif, 0);
        Assert.Equal(Vector3.Normalize(new Vector3(-2, -1, 0)), dir);
        Assert.Equal(0.7f, intensity);
    }

    [Fact]
    public void Trees_parse_and_face_the_road()
    {
        // ein Baum: Vorlage 3, 10 m links (+X) einer Straße entlang +Z, Skalierung 2
        var bin = new byte[16 + 0x40];
        BinaryPrimitives.WriteInt32LittleEndian(bin, 1);
        bin[16] = 3;
        BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(16 + 0x10), 10f);
        BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(16 + 0x14), 5f);
        for (var k = 0; k < 3; k++) BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(16 + 0x30 + k * 4), 2f);
        var t = Assert.Single(CourseTrees.Read(bin));
        Assert.Equal((3, new Vector3(10, 5, 0), new Vector3(2)), (t.Template, t.Position, t.Scale));

        var m = CourseTrees.Placement(t, [new(0, 0, -2), new(0, 0, 2), new(0, 0, 6)]);
        Assert.Equal(new Vector3(10, 4.5f, 0), m.Translation); // y − Skalierung.y / 4
        var front = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, m)); // −Z der Vorlage zeigt zur Straße
        Assert.True(Vector3.Distance(front, -Vector3.UnitX) < 1e-5f, front.ToString());
        Assert.Throws<InvalidDataException>(() => CourseTrees.Read(bin.AsSpan(0, 40)));
    }
}
