using System.Buffers.Binary;
using Touge.Formats;

namespace Touge.Formats.Tests;

public class CollisionTests
{
    /// <summary>Header + 1 material + 3 vertices + 1 wall face + 1 sector, laid out like CRS_COLI_*.BIN.</summary>
    [Fact]
    public void Parses_faces_attributes_and_sectors()
    {
        const int mat = 0x30, vtx = mat + 0x24, face = vtx + 3 * 0x20, sec = face + 0x10;
        var d = new byte[sec + 0x38];
        void I(int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(o), v);
        void F(int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(o), v);
        void S(int o, int v) => BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(o), (short)v);
        "1LCR"u8.CopyTo(d);
        int[] hdr = [1, 1, mat, 3, vtx, 1, face, 0, sec, 1, sec];
        for (var i = 0; i < hdr.Length; i++) I(4 + i * 4, hdr[i]);
        "W14hard"u8.CopyTo(d.AsSpan(mat));
        for (var i = 0; i < 3; i++) { F(vtx + i * 0x20, i); F(vtx + i * 0x20 + 8, 10 * i); F(vtx + i * 0x20 + 16, 1); }
        S(face, 2); S(face + 2, 1); S(face + 4, 0); S(face + 6, -1); S(face + 8, 7); S(face + 10, -1); S(face + 12, -1); S(face + 14, 0x8000);
        F(sec + 4, 1); F(sec + 12, -5);

        var c = Collision.Parse(d);
        Assert.Equal(["W14hard"], c.Materials);
        Assert.Equal(20f, c.Positions[2].Z);
        Assert.Equal(1f, c.Normals[0].Y);
        var f = Assert.Single(c.Faces);
        Assert.Equal((2, 1, 0, -1, 7, -1), (f.A, f.B, f.C, f.NCA, f.NAB, f.NBC));
        Assert.True(f.IsWall);
        Assert.Equal(0, f.Material);
        Assert.Equal(-5f, Assert.Single(c.Sectors).Plane.D);
    }
}
