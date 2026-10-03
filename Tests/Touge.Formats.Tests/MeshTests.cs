using System.Buffers.Binary;
using Touge.Formats;

namespace Touge.Formats.Tests;

public class MeshTests
{
    /// <summary>One material, one batch of 4 strip vertices (first two with ADC) -> 2 triangles, alternating winding.</summary>
    [Fact]
    public void Parses_strip_into_triangles()
    {
        var vif = new List<byte>();
        void U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); vif.AddRange(b); }
        void F(float v) { var b = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); vif.AddRange(b); }
        U32(0x6C04_8001); // UNPACK V4-32, n=4, addr=1 (flg)
        int[] adc = [0x8000, 0x8000, 0, 0];
        for (var i = 0; i < 4; i++) { F(i); F(0); F(0); U32((uint)adc[i]); }
        U32(0x6804_8002); // UNPACK V3-32 normals
        for (var i = 0; i < 4; i++) { F(0); F(1); F(0); }
        U32(0x1400_0004); // MSCAL

        var c = new byte[0x70 + (vif.Count + 15) / 16 * 16];
        "CMD\0"u8.CopyTo(c);
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(0x18), 1);    // 1 material
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(0x20), 0x50); // tex table
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(0x24), 0x50); // nodes (none) -> materials at 0x50
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(0x50), 0x70);
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(0x54), (vif.Count + 15) / 16);
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(0x58), -1);
        vif.CopyTo(c, 0x70);

        var tris = Mesh.Parse(c).Materials.Single().Triangles;
        Assert.Equal([0f, 1, 2, 2, 1, 3], tris.Select(v => v.Position.X));
    }
}

public class LzTests
{
    [Fact]
    public void Decompresses_literals_and_window_match()
    {
        // "ABAB" + match(pos 0xFEFD, len 4) -> "ABABABAB"
        byte[] d = [0x12, 0x3D, 0xDA, 0x01, 8, 0, 0, 0, 8, 0, 0, 0,
                    0b0_1111, (byte)'A', (byte)'B', (byte)'A', (byte)'B', 0xFD, 0xFE, 0];
        Assert.Equal("ABABABAB"u8.ToArray(), Lz.Decompress(d));
    }
}

public class CarPaintTests
{
    /// <summary>Two CEB blocks; colour RGB at +0/+4/+8 of each 0x70 record; Apply only touches flag-0x100 materials.</summary>
    [Fact]
    public void Parses_blocks_and_paints_flagged_materials()
    {
        var d = new byte[2 * 0x10 + 3 * 0x70];
        void Block(int p, short car, short n)
        {
            "CEB\0"u8.CopyTo(d.AsSpan(p));
            BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(p + 4), car);
            BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(p + 6), n);
        }
        Block(0, 23, 2);
        d[0x10] = 0xD7; d[0x14] = 0xC3; d[0x18] = 0x12; // colour 0
        d[0x80] = 0xB9;                                 // colour 1
        Block(0xF0, 25, 1);
        d[0x100] = 0xDC; d[0x104] = 0xDC; d[0x108] = 0xDC;

        var cars = CarPaint.Parse(d);
        Assert.Equal([0x12C3D7u, 0xB9u], cars[23]);
        Assert.Equal([0xDCDCDCu], cars[25]);

        var m = new Mesh { Textures = [], Nodes = [], Materials = [new(-1, 0x1100, 0x40FFFFFF, []), new(-1, 0x3000, 0x80111111, [])] };
        Assert.Equal([0x4012C3D7u, 0x80111111u], CarPaint.Apply(m, cars[23][0]).Materials.Select(x => x.Rgba));
    }
}
