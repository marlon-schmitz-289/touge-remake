using System.Buffers.Binary;
using System.Text;

namespace Touge.Formats;

/// <summary>
///     Read-only ISO 9660 image access (no Joliet/Rock Ridge — PS2 discs use plain 8.3 names with ";1").
///     Paths are case-insensitive, '/' or '\' separated, e.g. "CDVD/DATA/MODEL/COURSE.AFS".
/// </summary>
public sealed class Iso9660 : IDisposable
{
    private const int Sector = 2048;
    private readonly FileStream _fs;
    private readonly (int Lba, int Size) _root;

    public Iso9660(string path)
    {
        _fs = File.OpenRead(path);
        var pvd = ReadSectors(16, Sector);
        if (pvd[0] != 1 || !pvd.AsSpan(1, 5).SequenceEqual("CD001"u8)) throw new InvalidDataException($"{path}: no ISO 9660 volume");
        _root = Extent(pvd.AsSpan(156));
    }

    public bool Exists(string path) => Find(path) != null;

    public byte[] ReadFile(string path)
    {
        var (lba, size) = Find(path) ?? throw new FileNotFoundException(path);
        return ReadSectors(lba, size);
    }

    /// <summary>AFS inside the image, entries read on demand (no full copy of e.g. the 355 MB RACEBGM.AFS); names from the sibling .TBL.</summary>
    public Afs OpenAfs(string path)
    {
        var (lba, _) = Find(path) ?? throw new FileNotFoundException(path);
        var tbl = Path.ChangeExtension(path, ".TBL");
        return Afs.Open(_fs.Name, (long)lba * Sector, Exists(tbl) ? ReadFile(tbl) : null);
    }

    private (int Lba, int Size)? Find(string path)
    {
        var cur = _root;
        foreach (var part in path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = ReadSectors(cur.Lba, cur.Size);
            (int, int)? next = null;
            for (var o = 0; o < dir.Length;)
            {
                int len = dir[o];
                if (len == 0) { o = (o / Sector + 1) * Sector; continue; } // records never cross sectors
                var name = Encoding.ASCII.GetString(dir, o + 33, dir[o + 32]).Split(';')[0];
                if (name.Equals(part, StringComparison.OrdinalIgnoreCase)) { next = Extent(dir.AsSpan(o)); break; }
                o += len;
            }
            if (next == null) return null;
            cur = next.Value;
        }
        return cur;
    }

    private static (int, int) Extent(ReadOnlySpan<byte> record) =>
        (BinaryPrimitives.ReadInt32LittleEndian(record[2..]), BinaryPrimitives.ReadInt32LittleEndian(record[10..]));

    private byte[] ReadSectors(int lba, int size)
    {
        var buf = new byte[size];
        _fs.Position = (long)lba * Sector;
        _fs.ReadExactly(buf);
        return buf;
    }

    public void Dispose() => _fs.Dispose();
}
