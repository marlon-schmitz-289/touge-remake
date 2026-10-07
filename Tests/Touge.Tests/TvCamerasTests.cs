using System.Buffers.Binary;
using System.Numerics;
using Touge.Formats;
using Touge.Replays;

namespace Touge.Tests;

public class TvCamerasTests
{
    /// <summary>A REPCAM file as on the disc: count + 128 records of 0xD0, two keyframes each.</summary>
    private static byte[] RepCam(params (int Kind, int From, int To, Vector3 EyeA, float FovA, Vector3 EyeB, float FovB)[] cams)
    {
        var d = new byte[4 + 128 * ReplayCameras.RecordSize];
        BinaryPrimitives.WriteInt32LittleEndian(d, cams.Length);
        for (var i = 0; i < cams.Length; i++)
        {
            var r = d.AsSpan(4 + i * ReplayCameras.RecordSize);
            var c = cams[i];
            (r[0], r[1]) = ((byte)i, (byte)c.Kind);
            BinaryPrimitives.WriteUInt16LittleEndian(r[4..], (ushort)c.From);
            BinaryPrimitives.WriteUInt16LittleEndian(r[6..], (ushort)c.To);
            foreach (var (at, eye, fov) in new[] { (0x30, c.EyeA, c.FovA), (0x80, c.EyeB, c.FovB) })
            {
                BinaryPrimitives.WriteSingleLittleEndian(r[at..], eye.X);
                BinaryPrimitives.WriteSingleLittleEndian(r[(at + 4)..], eye.Y);
                BinaryPrimitives.WriteSingleLittleEndian(r[(at + 8)..], eye.Z);
                BinaryPrimitives.WriteSingleLittleEndian(r[(at + 0x48)..], fov);
            }
        }
        return d;
    }

    private static readonly Vector3[] Road = [.. Enumerable.Range(0, 101).Select(i => new Vector3(0, 0, 2 * i))]; // 200 m straight, 2 m apart

    [Fact]
    public void Progress_runs_smoothly_through_the_road_points()
    {
        // a car driving 5 cm a frame, 1 m beside the road: the progress grows 0.025 a frame, never held at a point then jumping
        var tv = new TvCameras([], Road, false);
        var last = tv.Progress(new Vector3(1, 0, 10));
        for (var z = 10.05f; z < 30; z += 0.05f)
        {
            var p = tv.Progress(new Vector3(1, 0, z));
            Assert.InRange(p - last, 0.02f, 0.03f);
            last = p;
        }
    }

    [Fact]
    public void CamerasFollowTheProgressAndMoveBetweenTheirKeys()
    {
        var cams = ReplayCameras.Read(RepCam((3, 0, 50, new(10, 3, 0), 20, new(10, 3, 100), 10), (2, 50, 100, new(-8, 2, 150), 30, new(-8, 2, 150), 30)));
        Assert.Equal(2, cams.Length);
        Assert.Equal((3, 0, 50), (cams[0].Kind, cams[0].From, cams[0].To));

        var tv = new TvCameras(cams, Road, reverse: false);
        var (eye, fov, index) = tv.At(new Vector3(0.5f, 0, 50)); // road index 25: halfway through the dolly
        Assert.Equal(0, index);
        Assert.Equal(50, eye.Z, 3);
        Assert.Equal(15, fov, 3);
        Assert.Equal(1, tv.At(new Vector3(0, 0, 160)).Index);

        // the reverse table counts from the far end: the car at the far end is at progress 0
        var back = new TvCameras(cams, Road, reverse: true);
        Assert.Equal(0, back.At(new Vector3(0, 0, 199)).Index);
        Assert.Equal(1, back.At(new Vector3(0, 0, 20)).Index);
    }

    [Fact]
    public void WithoutATableCamerasArePlacedAlongTheRoad()
    {
        var cams = TvCameras.Auto(Road);
        Assert.Equal(2, cams.Length); // every 60 points
        Assert.All(cams, c => Assert.Equal(9, MathF.Abs(c.A.Eye.X), 3));
        Assert.Equal(0, cams[0].From);
        Assert.Equal(cams[0].To, cams[1].From);
        Assert.Equal(1, new TvCameras([], Road, false).At(new Vector3(0, 0, 150)).Index);
    }
}
