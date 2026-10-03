using System.Text;
using Touge.Formats;

namespace Touge.Formats.Tests;

public class ContainerTests
{
    [Fact]
    public void Afs_reads_entries_and_tbl_names()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var afs = Path.Combine(dir, "X.AFS");
        // header(8) + 2 entries(16), data at 0x20 and 0x24
        var d = new byte[0x28];
        "AFS\0"u8.CopyTo(d);
        BitConverter.GetBytes(2).CopyTo(d, 4);
        BitConverter.GetBytes(0x20).CopyTo(d, 8); BitConverter.GetBytes(4).CopyTo(d, 12);
        BitConverter.GetBytes(0x24).CopyTo(d, 16); BitConverter.GetBytes(4).CopyTo(d, 20);
        "AAAABBBB"u8.CopyTo(d.AsSpan(0x20));
        File.WriteAllBytes(afs, d);
        // tbl: 2 offsets, then "A.PAC\0B.PAC\0"
        var tbl = new List<byte>();
        tbl.AddRange(BitConverter.GetBytes(8)); tbl.AddRange(BitConverter.GetBytes(14));
        tbl.AddRange(Encoding.ASCII.GetBytes("A.PAC\0B.PAC\0"));
        File.WriteAllBytes(Path.Combine(dir, "X.TBL"), tbl.ToArray());

        var a = Afs.Open(afs);
        Assert.Equal(["A.PAC", "B.PAC"], a.Entries.Select(e => e.Name));
        Assert.Equal("BBBB"u8.ToArray(), a.Read(a.Entries[1]));
    }

    [Fact]
    public void Pac_reads_entry_table()
    {
        var d = new byte[0x60];
        "PAC\0"u8.CopyTo(d);
        BitConverter.GetBytes(1).CopyTo(d, 4);
        BitConverter.GetBytes(0x20).CopyTo(d, 12);
        "CAR\0"u8.CopyTo(d.AsSpan(0x10));
        "bonnet00\0"u8.CopyTo(d.AsSpan(0x20));
        BitConverter.GetBytes(0x40).CopyTo(d, 0x30); BitConverter.GetBytes(0x20).CopyTo(d, 0x34);
        BitConverter.GetBytes(3).CopyTo(d, 0x38);

        Assert.Equal("CAR", Pac.Name(d));
        Assert.Equal(new Pac.Entry("bonnet00", 0x40, 0x20, 3), Assert.Single(Pac.Entries(d)));
    }
}
