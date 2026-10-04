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
}
