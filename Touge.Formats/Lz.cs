using System.Buffers.Binary;

namespace Touge.Formats;

/// <summary>
///     PAC entry compression (game func 0x1C54F0). Header: u32 0x01DA3D12 (byte 3: 1 = packed, 0 = stored, 2 = small
///     LZSS, see <see cref="Small" />), u32 unpacked size, u32 packed size. LZSS: 64 KB zeroed window starting at 0xFEFD, flag byte LSB first
///     (1 = literal), match = u16 absolute window position + u8 length-4.
/// </summary>
public static class Lz
{
    public static bool IsCompressed(ReadOnlySpan<byte> d) =>
        d.Length >= 12 && (BinaryPrimitives.ReadUInt32LittleEndian(d) & 0x00FFFFFF) == 0xDA3D12;

    public static byte[] Decompress(ReadOnlySpan<byte> d)
    {
        if (!IsCompressed(d)) throw new InvalidDataException("no LZ header");
        var size = BinaryPrimitives.ReadInt32LittleEndian(d[4..]);
        if (d[3] == 0) return d.Slice(12, size).ToArray();
        if (d[3] == 2) return Small(d[8..], size);

        var src = d[12..];
        var outp = new byte[size + 259]; // a final match may run past the end
        var win = new byte[0x10000];
        int wp = 0xFEFD, ip = 0, op = 0, flags = 0;
        while (op < size)
        {
            flags >>= 1;
            if ((flags & 0x100) == 0) flags = src[ip++] | 0xFF00;
            if ((flags & 1) != 0)
            {
                outp[op++] = win[wp] = src[ip++];
                wp = (wp + 1) & 0xFFFF;
            }
            else
            {
                int pos = src[ip] | (src[ip + 1] << 8), len = src[ip + 2] + 4;
                ip += 3;
                for (var i = 0; i < len; i++)
                {
                    outp[op++] = win[wp] = win[(pos + i) & 0xFFFF];
                    wp = (wp + 1) & 0xFFFF;
                }
            }
        }
        return outp.AsSpan(0, size).ToArray();
    }

    /// <summary>
    ///     Type 2 (manga pictures, FPK): no packed size, data at +8. Classic LZSS: 4 KB zeroed window starting at 0xFEE,
    ///     flag byte LSB first (1 = literal), match = 2 bytes: position lo | (hi &amp; 0xF0) &lt;&lt; 4, length (hi &amp; 0xF) + 3.
    /// </summary>
    private static byte[] Small(ReadOnlySpan<byte> src, int size)
    {
        var outp = new byte[size + 18];
        var win = new byte[0x1000];
        int wp = 0xFEE, ip = 0, op = 0, flags = 0;
        while (op < size)
        {
            flags >>= 1;
            if ((flags & 0x100) == 0) flags = src[ip++] | 0xFF00;
            if ((flags & 1) != 0)
            {
                outp[op++] = win[wp] = src[ip++];
                wp = (wp + 1) & 0xFFF;
            }
            else
            {
                int lo = src[ip], hi = src[ip + 1], pos = lo | ((hi & 0xF0) << 4), len = (hi & 0xF) + 3;
                ip += 2;
                for (var i = 0; i < len; i++)
                {
                    outp[op++] = win[wp] = win[(pos + i) & 0xFFF];
                    wp = (wp + 1) & 0xFFF;
                }
            }
        }
        return outp.AsSpan(0, size).ToArray();
    }
}
