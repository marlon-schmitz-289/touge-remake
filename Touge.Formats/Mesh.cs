using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Touge.Formats;

/// <summary>
///     Mesh inside PAC (type 3). Two variants share the VIF payload:
///     "CMD\0" "1.02V" (cars) and "SMD\0" "0.00" (courses, LZ-compressed in the PAC, see <see cref="Lz" />).
///     CMD header:
///     0x10 texture count, 0x14 node count, 0x18 material count, 0x20 texture-name table, 0x24 node table,
///     0x30 bbox (min xyz, max xyz). Texture names 16 B each; node = name[16] + row-major 4x4 matrix (0x50 B);
///     materials follow the nodes, 0x20 B each: data offset, qword count, texture index (-1 none), ?, flags,
///     RGBA (alpha 0x80 = opaque), triangle count, vertex count.
///     Material data = VIF stream of batches: UNPACK V4-32 to VU addr 1 (xyz + ADC flag in w), V3-32 addr 2 (normal),
///     V2-32 addr 3 (uv), V3-32 addr 4 (unused here), then MSCAL. Vertices form triangle strips; w bit 15 = no kick, w bit 2 = winding.
///     SMD header: 0x08 #triangles, 0x0C #vertices, 0x10 #textures, 0x14 #materials, 0x18 texture table, 0x1C material
///     table, 0x20 bbox. Materials as in CMD (flags/RGBA zero). Payload: addr 1 xyz+ADC, addr 2 uv, addr 3 V4-8 vertex
///     colour (prelit, 0x80 = 1.0); positions are world space in metres.
/// </summary>
public sealed class Mesh
{
    public readonly record struct Vertex(Vector3 Position, Vector3 Normal, Vector2 Uv, Vector4 Color);

    public sealed record Material(int Texture, int Flags, uint Rgba, List<Vertex> Triangles);

    public required string[] Textures { get; init; }
    public required (string Name, Matrix4x4 Transform)[] Nodes { get; init; }
    public required Material[] Materials { get; init; }

    public static bool IsCmd(ReadOnlySpan<byte> d) => d.Length >= 0x50 && d[..4].SequenceEqual("CMD\0"u8);
    public static bool IsSmd(ReadOnlySpan<byte> d) => d.Length >= 0x40 && d[..4].SequenceEqual("SMD\0"u8);

    /// <summary>Parses CMD or SMD; LZ-compressed entries are unpacked first.</summary>
    public static Mesh Parse(ReadOnlySpan<byte> c)
    {
        if (Lz.IsCompressed(c)) return Parse(Lz.Decompress(c));
        if (IsSmd(c)) return ParseSmd(c);
        if (!IsCmd(c)) throw new InvalidDataException("no CMD/SMD magic");
        int nTex = I32(c, 0x10), nNodes = I32(c, 0x14), nMats = I32(c, 0x18), texOff = I32(c, 0x20), nodeOff = I32(c, 0x24);

        var tex = Names(c, texOff, nTex);

        var nodes = new (string, Matrix4x4)[nNodes];
        for (var i = 0; i < nNodes; i++)
        {
            var n = c.Slice(nodeOff + i * 0x50, 0x50);
            var m = new float[16];
            for (var k = 0; k < 16; k++) m[k] = BinaryPrimitives.ReadSingleLittleEndian(n[(16 + k * 4)..]);
            nodes[i] = (CStr(n[..16]), new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]));
        }

        var matOff = nodeOff + nNodes * 0x50;
        var mats = new Material[nMats];
        for (var i = 0; i < nMats; i++)
        {
            var r = c.Slice(matOff + i * 0x20, 0x20);
            int off = I32(r, 0), qwc = I32(r, 4);
            mats[i] = new Material(I32(r, 8), I32(r, 16), (uint)I32(r, 20), Strips(c.Slice(off, qwc * 16), uvAddr: 3, normalAddr: 2, colorAddr: -1));
        }
        return new Mesh { Textures = tex, Nodes = nodes, Materials = mats };
    }

    private static Mesh ParseSmd(ReadOnlySpan<byte> c)
    {
        int nTex = I32(c, 0x10), nMats = I32(c, 0x14), texOff = I32(c, 0x18), matOff = I32(c, 0x1C);
        var mats = new Material[nMats];
        for (var i = 0; i < nMats; i++)
        {
            var r = c.Slice(matOff + i * 0x20, 0x20);
            mats[i] = new Material(I32(r, 8), I32(r, 16), 0x80808080, Strips(c.Slice(I32(r, 0), I32(r, 4) * 16), uvAddr: 2, normalAddr: -1, colorAddr: 3));
        }
        return new Mesh { Textures = Names(c, texOff, nTex), Nodes = [], Materials = mats };
    }

    private static string[] Names(ReadOnlySpan<byte> c, int off, int n)
    {
        var names = new string[n];
        for (var i = 0; i < n; i++) names[i] = CStr(c.Slice(off + i * 16, 16));
        return names;
    }

    /// <summary>Walks the VIF stream; each MSCAL flushes the collected batch as a triangle strip.</summary>
    private static List<Vertex> Strips(ReadOnlySpan<byte> v, int uvAddr, int normalAddr, int colorAddr)
    {
        var tris = new List<Vertex>();
        Vector4[] pos = [], nrm = [], uv = [], col = [];
        Span<float> f = stackalloc float[4];
        var p = 0;
        while (p + 4 <= v.Length)
        {
            int imm = BinaryPrimitives.ReadUInt16LittleEndian(v[p..]), num = v[p + 2], cmd = v[p + 3] & 0x7F;
            p += 4;
            if (cmd >= 0x60)
            {
                var n = num == 0 ? 256 : num;
                var comps = ((cmd >> 2) & 3) + 1;
                var size = 4 >> (cmd & 3); // 32/16/8 bit
                if ((cmd & 3) == 3) throw new NotSupportedException("VIF unpack V4-5");
                var unsigned = (imm & 0x4000) != 0;
                var data = new Vector4[n];
                for (var i = 0; i < n; i++)
                {
                    f.Clear();
                    for (var k = 0; k < comps; k++)
                    {
                        var o = p + (i * comps + k) * size;
                        f[k] = size switch
                        {
                            4 when !(comps == 4 && k == 3) => BinaryPrimitives.ReadSingleLittleEndian(v[o..]),
                            4 => BinaryPrimitives.ReadInt32LittleEndian(v[o..]), // w carries flags
                            2 => unsigned ? BinaryPrimitives.ReadUInt16LittleEndian(v[o..]) : BinaryPrimitives.ReadInt16LittleEndian(v[o..]),
                            _ => unsigned ? v[o] : (sbyte)v[o],
                        };
                    }
                    data[i] = new Vector4(f[0], f[1], f[2], f[3]);
                }
                p += (n * comps * size + 3) & ~3;
                var addr = imm & 0x3FF;
                if (addr == 1) pos = data;
                else if (addr == normalAddr) nrm = data;
                else if (addr == uvAddr) uv = data;
                else if (addr == colorAddr) col = data;
            }
            else if (cmd == 0x14 || cmd == 0x15 || cmd == 0x17) // MSCAL/MSCALF/MSCNT -> draw batch
            {
                for (var i = 2; i < pos.Length; i++)
                {
                    if (((int)pos[i].W & 0x8000) != 0) continue;
                    var (a, b) = ((int)pos[i].W & 4) != 0 ? (i - 2, i - 1) : (i - 1, i - 2); // w bit 2 = strip winding, survives restarts
                    tris.Add(Vert(a)); tris.Add(Vert(b)); tris.Add(Vert(i));
                }
                pos = [];
            }
            else if (cmd is 0x20) p += 4;           // STMASK
            else if (cmd is 0x30 or 0x31) p += 16; // STROW/STCOL
        }
        return tris;

        Vertex Vert(int i) => new(new Vector3(pos[i].X, pos[i].Y, pos[i].Z),
            i < nrm.Length ? new Vector3(nrm[i].X, nrm[i].Y, nrm[i].Z) : Vector3.UnitY,
            i < uv.Length ? new Vector2(uv[i].X, uv[i].Y) : Vector2.Zero,
            i < col.Length ? col[i] / 128f : Vector4.One);
    }

    private static int I32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d[o..]);

    private static string CStr(ReadOnlySpan<byte> s)
    {
        var n = s.IndexOf((byte)0);
        return Encoding.ASCII.GetString(n < 0 ? s : s[..n]);
    }
}
