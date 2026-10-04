using System.Buffers.Binary;
using System.Text;

namespace Touge.Formats;

/// <summary>CRI AFS archive: "AFS\0", count, then (offset, size) per entry. Names come from the sibling .TBL.</summary>
public sealed class Afs
{
    public readonly record struct Entry(string Name, int Offset, int Size);

    public IReadOnlyList<Entry> Entries { get; }
    private readonly string? _path;
    private readonly long _base;
    private readonly byte[]? _data;

    private Afs(string? path, long baseOffset, byte[]? data, IReadOnlyList<Entry> entries) =>
        (_path, _base, _data, Entries) = (path, baseOffset, data, entries);

    /// <summary>AFS already in memory (e.g. read from the ISO); <paramref name="tbl"/> = sibling .TBL bytes or null.</summary>
    public static Afs FromBytes(byte[] data, byte[]? tbl)
    {
        if (!data.AsSpan(0, 4).SequenceEqual("AFS\0"u8)) throw new InvalidDataException("no AFS magic");
        var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        var names = tbl == null ? null : ParseTbl(tbl, count);
        var entries = new Entry[count];
        for (var i = 0; i < count; i++)
            entries[i] = new Entry(names?[i] ?? $"{i:D4}.bin",
                BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8 + i * 8)),
                BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12 + i * 8)));
        return new Afs(null, 0, data, entries);
    }

    public Entry? Find(string name) => Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } e ? e : null;

    public static Afs Open(string path)
    {
        var tbl = Path.ChangeExtension(path, ".TBL");
        return Open(path, 0, File.Exists(tbl) ? File.ReadAllBytes(tbl) : null);
    }

    /// <summary>AFS stored at <paramref name="baseOffset"/> inside <paramref name="path"/> (e.g. a file in the ISO image); entries are read on demand.</summary>
    public static Afs Open(string path, long baseOffset, byte[]? tbl)
    {
        using var fs = File.OpenRead(path);
        fs.Position = baseOffset;
        Span<byte> head = stackalloc byte[8];
        fs.ReadExactly(head);
        if (!head[..4].SequenceEqual("AFS\0"u8)) throw new InvalidDataException($"{path}@{baseOffset}: no AFS magic");
        var count = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);

        var table = new byte[count * 8];
        fs.ReadExactly(table);
        var names = tbl == null ? null : ParseTbl(tbl, count);

        var entries = new Entry[count];
        for (var i = 0; i < count; i++)
            entries[i] = new Entry(names?[i] ?? $"{i:D4}.bin",
                BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(i * 8)),
                BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(i * 8 + 4)));
        return new Afs(path, baseOffset, null, entries);
    }

    public byte[] Read(Entry e)
    {
        if (_data != null) return _data.AsSpan(e.Offset, e.Size).ToArray();
        using var fs = File.OpenRead(_path!);
        fs.Position = _base + e.Offset;
        var buf = new byte[e.Size];
        fs.ReadExactly(buf);
        return buf;
    }

    /// <summary>.TBL = count x u32 offsets into a NUL-terminated name table in the same file. Null if mismatched.</summary>
    public static string[]? ParseTbl(byte[] d, int count)
    {
        if (d.Length < count * 4 || BinaryPrimitives.ReadInt32LittleEndian(d) != count * 4) return null;
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            var o = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(i * 4));
            var end = Array.IndexOf(d, (byte)0, o);
            names[i] = Encoding.ASCII.GetString(d, o, (end < 0 ? d.Length : end) - o);
        }
        return names;
    }
}
