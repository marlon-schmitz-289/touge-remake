using System.Buffers.Binary;
using System.Text;

namespace Touge.Formats;

/// <summary>
///     Sega "PAC\0" container. Header 0x20: magic, count, ?, entry table offset, name[16].
///     Entry 0x20: name[16], offset, size, type, ?. Offsets relative to the PAC start.
/// </summary>
public static class Pac
{
    public readonly record struct Entry(string Name, int Offset, int Size, int Type);

    public static bool IsPac(ReadOnlySpan<byte> d) => d.Length >= 0x20 && d[..4].SequenceEqual("PAC\0"u8);

    public static string Name(ReadOnlySpan<byte> d) => CStr(d.Slice(0x10, 16));

    public static Entry[] Entries(ReadOnlySpan<byte> d)
    {
        if (!IsPac(d)) throw new InvalidDataException("no PAC magic");
        var count = BinaryPrimitives.ReadInt32LittleEndian(d[4..]);
        var table = BinaryPrimitives.ReadInt32LittleEndian(d[12..]);
        var entries = new Entry[count];
        for (var i = 0; i < count; i++)
        {
            var e = d.Slice(table + i * 0x20, 0x20);
            entries[i] = new Entry(CStr(e[..16]),
                BinaryPrimitives.ReadInt32LittleEndian(e[16..]),
                BinaryPrimitives.ReadInt32LittleEndian(e[20..]),
                BinaryPrimitives.ReadInt32LittleEndian(e[24..]));
        }
        return entries;
    }

    private static string CStr(ReadOnlySpan<byte> s)
    {
        var n = s.IndexOf((byte)0);
        return Encoding.ASCII.GetString(n < 0 ? s : s[..n]);
    }
}
