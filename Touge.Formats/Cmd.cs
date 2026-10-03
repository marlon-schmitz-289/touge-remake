using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Touge.Formats;

/// <summary>
///     Mesh inside PAC (type 3, "CMD\0" "1.02V"). Header:
///     0x10 texture count, 0x14 node count, 0x18 material count, 0x20 texture-name table, 0x24 node table,
///     0x30 bbox (min xyz, max xyz). Texture names 16 B each; node = name[16] + row-major 4x4 matrix (0x50 B);
///     materials follow the nodes, 0x20 B each: data offset, qword count, texture index (-1 none), ?, flags,
///     RGBA (alpha 0x80 = opaque), triangle count, vertex count.
///     Material data = VIF stream of batches: UNPACK V4-32 to VU addr 1 (xyz + ADC flag in w), V3-32 addr 2 (normal),
///     V2-32 addr 3 (uv), V3-32 addr 4 (unused here), then MSCAL. Vertices form triangle strips; w bit 15 = no kick.
/// </summary>
public sealed class Cmd
{
    public readonly record struct Vertex(Vector3 Position, Vector3 Normal, Vector2 Uv);

    public sealed record Material(int Texture, int Flags, uint Rgba, List<Vertex> Triangles);

    public required string[] Textures { get; init; }
    public required (string Name, Matrix4x4 Transform)[] Nodes { get; init; }
    public required Material[] Materials { get; init; }

    public static bool IsCmd(ReadOnlySpan<byte> d) => d.Length >= 0x50 && d[..4].SequenceEqual("CMD\0"u8);

    public static Cmd Parse(ReadOnlySpan<byte> c)
    {
        if (!IsCmd(c)) throw new InvalidDataException("no CMD magic");
        int nTex = I32(c, 0x10), nNodes = I32(c, 0x14), nMats = I32(c, 0x18), texOff = I32(c, 0x20), nodeOff = I32(c, 0x24);

        var tex = new string[nTex];
        for (var i = 0; i < nTex; i++) tex[i] = CStr(c.Slice(texOff + i * 16, 16));

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
            mats[i] = new Material(I32(r, 8), I32(r, 16), (uint)I32(r, 20), Strips(c.Slice(off, qwc * 16)));
        }
        return new Cmd { Textures = tex, Nodes = nodes, Materials = mats };
    }

    /// <summary>Walks the VIF stream; each MSCAL flushes the collected batch as a triangle strip.</summary>
    private static List<Vertex> Strips(ReadOnlySpan<byte> v)
    {
        var tris = new List<Vertex>();
        Vector4[] pos = [], nrm = [], uv = [];
        var p = 0;
        while (p + 4 <= v.Length)
        {
            int imm = BinaryPrimitives.ReadUInt16LittleEndian(v[p..]), num = v[p + 2], cmd = v[p + 3] & 0x7F;
            p += 4;
            if (cmd >= 0x60)
            {
                var n = num == 0 ? 256 : num;
                var comps = ((cmd >> 2) & 3) + 1;
                if ((cmd & 3) != 0) throw new NotSupportedException($"VIF unpack 0x{cmd:X2} (only 32-bit)");
                var data = new Vector4[n];
                for (var i = 0; i < n; i++)
                {
                    Span<float> f = stackalloc float[4];
                    for (var k = 0; k < comps; k++) f[k] = BinaryPrimitives.ReadSingleLittleEndian(v[(p + (i * comps + k) * 4)..]);
                    if (comps == 4) f[3] = BinaryPrimitives.ReadInt32LittleEndian(v[(p + (i * 4 + 3) * 4)..]);
                    data[i] = new Vector4(f[0], f[1], f[2], f[3]);
                }
                p += n * comps * 4;
                switch (imm & 0x3FF)
                {
                    case 1: pos = data; break;
                    case 2: nrm = data; break;
                    case 3: uv = data; break;
                }
            }
            else if (cmd == 0x14 || cmd == 0x15 || cmd == 0x17) // MSCAL/MSCALF/MSCNT -> draw batch
            {
                for (var i = 2; i < pos.Length; i++)
                {
                    if (((int)pos[i].W & 0x8000) != 0) continue;
                    var (a, b) = (i & 1) == 0 ? (i - 2, i - 1) : (i - 1, i - 2);
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
            i < uv.Length ? new Vector2(uv[i].X, uv[i].Y) : Vector2.Zero);
    }

    private static int I32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d[o..]);

    private static string CStr(ReadOnlySpan<byte> s)
    {
        var n = s.IndexOf((byte)0);
        return Encoding.ASCII.GetString(n < 0 ? s : s[..n]);
    }
}
