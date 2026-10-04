using System.Buffers.Binary;
using System.Numerics;

namespace Touge.Formats;

/// <summary>
///     The original's replay TV cameras: <c>BINARY/REPLAY.AFS</c> → <c>REPCAM&lt;nn&gt;_&lt;d&gt;.BIN</c>, nn = course in the ELF
///     order (<see cref="Courses"/>), d = direction (0 along ROAD, 1 reverse). Fixed 26628 bytes: u32 count, 128 × 0xD0 records.
///     Record: u8 index, u8 kind (0 still, 1 fixed eye zooming, 2 fixed, 3 dolly, 5 short cut), u16 ?, u16 from, u16 to
///     (ROAD index of the car's progress in the run direction; reverse counts from the end), f32 0.01, f32 0.25 (?),
///     7 × f32, f32 1; then two keyframes of 0x50 bytes at +0x30 and +0x80: eye xyzw, target xyzw, unit view direction,
///     (pitch°, yaw°), (distance eye → target, ?, vertical FOV°). The camera goes from the first keyframe to the second
///     while the car runs from <c>from</c> to <c>to</c>.
/// </summary>
public static class ReplayCameras
{
    /// <summary>Courses in the ELF's course table order (= REPCAM number).</summary>
    public static readonly string[] Courses = ["MYOUGI0", "USUI0", "AKAGI", "AKINA", "HAPPOU", "IROHA", "MYOUGI", "USUI", "MOMIJI", "SHIONA", "SHOMARU"];

    public const int RecordSize = 0xD0;

    public readonly record struct Key(Vector3 Eye, Vector3 Target, float Fov);

    public readonly record struct Cam(int Kind, int From, int To, Key A, Key B);

    public static string FileName(string course, bool reverse) =>
        $"REPCAM{Array.IndexOf(Courses, course.ToUpperInvariant()):00}_{(reverse ? 1 : 0)}.BIN";

    public static Cam[] Read(ReadOnlySpan<byte> d)
    {
        var n = BinaryPrimitives.ReadInt32LittleEndian(d);
        if (n < 0 || 4 + n * RecordSize > d.Length) throw new InvalidDataException($"REPCAM: {n} Kameras > {d.Length} Bytes");
        var cams = new Cam[n];
        for (var i = 0; i < n; i++)
        {
            var r = d.Slice(4 + i * RecordSize, RecordSize);
            cams[i] = new Cam(r[1], BinaryPrimitives.ReadUInt16LittleEndian(r[4..]), BinaryPrimitives.ReadUInt16LittleEndian(r[6..]), KeyAt(r[0x30..]), KeyAt(r[0x80..]));
        }
        return cams;
    }

    private static Key KeyAt(ReadOnlySpan<byte> k) =>
        new(DrivingLine.Vec3(k), DrivingLine.Vec3(k[0x10..]), BinaryPrimitives.ReadSingleLittleEndian(k[0x48..]));

    /// <summary>The cameras of <paramref name="course"/> in one direction from the ISO, empty if the course has none.</summary>
    public static Cam[] Load(Iso9660 iso, string course, bool reverse)
    {
        if (Array.IndexOf(Courses, course.ToUpperInvariant()) < 0) return [];
        var afs = Afs.FromBytes(iso.ReadFile("CDVD/DATA/BINARY/REPLAY.AFS"), iso.ReadFile("CDVD/DATA/BINARY/REPLAY.TBL"));
        return afs.Find(FileName(course, reverse)) is { } e ? Read(afs.Read(e)) : [];
    }
}
