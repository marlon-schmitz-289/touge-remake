using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Touge.Formats;

/// <summary>
///     Course collision "CRS_COLI_&lt;COURSE&gt;_0/1.BIN" (loader 0x15F340). Header 0x30, u32 each:
///     "1LCR", version 1, #materials, material offset, #vertices, vertex offset, #faces, face offset,
///     #u32 list, list offset (always empty), #sectors, sector offset.
///     Material 0x24: name[16] ("R16road", "W14hard" – index = number in the name), 20 B zero; unused slots empty.
///     Vertex 0x20: position xyz, unit normal xyz, 2 × 0.
///     Face 0x10: s16 vertex[3], s16 neighbour face across edge v2v0/v0v1/v1v2 (-1 = open border), u16 0xFFFF,
///     u16 attribute = material index | 0x8000 for walls ("W…"). One connected 2.5D surface: the game locates
///     the car by walking neighbours in XZ (0x15F7D0) and stops at wall faces (0x15F960).
///     Sector 0x38: plane (n xyz, d), triangle xyz[3], s32 start face – coarse ribbon along the track, below the
///     road, used to find the walk's start face (0x15F600).
///     _0 / _1 = course direction (car byte 0x1D6); same surface, only end zones differ (e.g. Akina R80btm vs R64top).
/// </summary>
public sealed class Collision
{
    public readonly record struct Face(int A, int B, int C, int NCA, int NAB, int NBC, ushort Attribute)
    {
        public int Material => Attribute & 0x7FFF;
        public bool IsWall => (Attribute & 0x8000) != 0;
    }

    public readonly record struct Sector(Plane Plane, Vector3 A, Vector3 B, Vector3 C, int StartFace);

    public required string[] Materials { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Face[] Faces { get; init; }
    public required Sector[] Sectors { get; init; }

    public static Collision Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 0x30 || !d[..4].SequenceEqual("1LCR"u8)) throw new InvalidDataException("no 1LCR magic");
        int nMat = I32(d, 8), matOff = I32(d, 12), nV = I32(d, 16), vOff = I32(d, 20), nF = I32(d, 24), fOff = I32(d, 28);
        int nSec = I32(d, 40), secOff = I32(d, 44);

        var mats = new string[nMat];
        for (var i = 0; i < nMat; i++)
        {
            var s = d.Slice(matOff + i * 0x24, 16);
            var n = s.IndexOf((byte)0);
            mats[i] = Encoding.ASCII.GetString(n < 0 ? s : s[..n]);
        }

        var pos = new Vector3[nV];
        var nrm = new Vector3[nV];
        for (var i = 0; i < nV; i++)
        {
            pos[i] = V3(d, vOff + i * 0x20);
            nrm[i] = V3(d, vOff + i * 0x20 + 12);
        }

        var faces = new Face[nF];
        for (var i = 0; i < nF; i++)
        {
            var f = fOff + i * 0x10;
            faces[i] = new Face(S16(d, f), S16(d, f + 2), S16(d, f + 4), S16(d, f + 6), S16(d, f + 8), S16(d, f + 10),
                BinaryPrimitives.ReadUInt16LittleEndian(d[(f + 14)..]));
        }

        var secs = new Sector[nSec];
        for (var i = 0; i < nSec; i++)
        {
            var o = secOff + i * 0x38;
            secs[i] = new Sector(new Plane(V3(d, o), F(d, o + 12)), V3(d, o + 16), V3(d, o + 28), V3(d, o + 40), I32(d, o + 52));
        }
        return new Collision { Materials = mats, Positions = pos, Normals = nrm, Faces = faces, Sectors = secs };
    }

    private static int I32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d[o..]);
    private static short S16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt16LittleEndian(d[o..]);
    private static float F(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadSingleLittleEndian(d[o..]);
    private static Vector3 V3(ReadOnlySpan<byte> d, int o) => new(F(d, o), F(d, o + 4), F(d, o + 8));
}
