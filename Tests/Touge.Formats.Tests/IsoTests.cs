using System.Buffers.Binary;
using System.Text;
using Touge.Formats;

namespace Touge.Formats.Tests;

public class IsoTests
{
    /// <summary>Minimal image: PVD at sector 16, root dir at 18 with subdir "DATA" (19) holding "A.BIN;1" (20).</summary>
    [Fact]
    public void Finds_file_in_subdirectory_case_insensitive()
    {
        var img = new byte[21 * 2048];
        img[16 * 2048] = 1;
        "CD001"u8.CopyTo(img.AsSpan(16 * 2048 + 1));
        Record(img.AsSpan(16 * 2048 + 156), 18, 2048, "\0");
        Record(img.AsSpan(18 * 2048), 19, 2048, "DATA");
        Record(img.AsSpan(19 * 2048), 20, 5, "A.BIN;1");
        "hello"u8.CopyTo(img.AsSpan(20 * 2048));
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, img);

        using var iso = new Iso9660(path);
        Assert.Equal("hello"u8.ToArray(), iso.ReadFile("data/a.bin"));
        Assert.False(iso.Exists("DATA/B.BIN"));
    }

    private static void Record(Span<byte> r, int lba, int size, string name)
    {
        r[0] = (byte)(33 + name.Length);
        BinaryPrimitives.WriteInt32LittleEndian(r[2..], lba);
        BinaryPrimitives.WriteInt32LittleEndian(r[10..], size);
        r[32] = (byte)name.Length;
        Encoding.ASCII.GetBytes(name).CopyTo(r[33..]);
    }
}
