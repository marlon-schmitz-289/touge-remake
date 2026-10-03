using System.Buffers.Binary;
using System.Text;

namespace Touge.Formats;

/// <summary>CRI AFS archive: "AFS\0", count, then (offset, size) per entry. Names come from the sibling .TBL.</summary>
public sealed class Afs
{
    public readonly record struct Entry(string Name, int Offset, int Size);

    public IReadOnlyList<Entry> Entries { get; }
    private readonly string _path;

    private Afs(string path, IReadOnlyList<Entry> entries) => (_path, Entries) = (path, entries);

    public static Afs Open(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[8];
        fs.ReadExactly(head);
        if (!head[..4].SequenceEqual("AFS\0"u8)) throw new InvalidDataException($"{path}: no AFS magic");
        var count = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);

        var table = new byte[count * 8];
        fs.ReadExactly(table);
        var names = ReadTbl(Path.ChangeExtension(path, ".TBL"), count);

        var entries = new Entry[count];
        for (var i = 0; i < count; i++)
            entries[i] = new Entry(names?[i] ?? $"{i:D4}.bin",
                BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(i * 8)),
                BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(i * 8 + 4)));
        return new Afs(path, entries);
    }

    public byte[] Read(Entry e)
    {
        using var fs = File.OpenRead(_path);
        fs.Position = e.Offset;
        var buf = new byte[e.Size];
        fs.ReadExactly(buf);
        return buf;
    }

    /// <summary>.TBL = count x u32 offsets into a NUL-terminated name table in the same file. Null if missing/mismatched.</summary>
    private static string[]? ReadTbl(string path, int count)
    {
        if (!File.Exists(path)) return null;
        var d = File.ReadAllBytes(path);
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
