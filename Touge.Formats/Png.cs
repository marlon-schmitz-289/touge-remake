using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Touge.Formats;

/// <summary>Minimal RGBA8 PNG writer (debug export only).</summary>
public static class Png
{
    private static readonly uint[] Crc = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static void Write(string path, int w, int h, byte[] rgba)
    {
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 6; // 8 bit, RGBA
        Chunk(fs, "IHDR", ihdr);

        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, true))
            for (var y = 0; y < h; y++)
            {
                z.WriteByte(0);
                z.Write(rgba, y * w * 4, w * 4);
            }
        Chunk(fs, "IDAT", raw.ToArray());
        Chunk(fs, "IEND", []);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, data.Length);
        s.Write(b);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var c = 0xFFFFFFFFu;
        foreach (var x in t.Concat(data)) c = Crc[(c ^ x) & 0xFF] ^ (c >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(b, ~c);
        s.Write(b);
    }
}
