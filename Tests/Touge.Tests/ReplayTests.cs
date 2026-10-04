using System.Numerics;
using Kansei.Physics;
using Touge.Replays;

namespace Touge.Tests;

public class ReplayTests
{
    /// <summary>Plane y = 0 with a wall at x = ±12 (the cars scrape it).</summary>
    private sealed class Lane : IGround
    {
        public bool Raycast(Vector3 o, Vector3 d, float max, out GroundHit hit)
        {
            hit = default;
            if (d.Y >= 0 || o.Y < 0 || -o.Y / d.Y > max) return false;
            var t = -o.Y / d.Y;
            hit = new GroundHit(o + d * t, Vector3.UnitY, t, o.X > 0 ? 1 : 0);
            return true;
        }

        public int CollideWalls(ReadOnlySpan<Vector3> probes, float radius, Span<WallContact> contacts)
        {
            var n = 0;
            foreach (var p in probes)
                if (MathF.Abs(p.X) + radius > 12 && n < contacts.Length)
                    contacts[n++] = new WallContact(p with { X = 12 * MathF.Sign(p.X) }, -Vector3.UnitX * MathF.Sign(p.X), MathF.Abs(p.X) + radius - 12, 0);
            return n;
        }
    }

    private static VehicleInput Script(int car, int t) => new(1, t % 400 > 350 ? 0.6f : 0, MathF.Sin(t * 0.013f + car) * (car == 0 ? 1 : 0.7f), t % 500 is > 200 and < 215);

    private static Vehicle[] Cars()
    {
        Vehicle a = new(CarSpecs.All["AE86T"]), b = new(CarSpecs.All["FD3S"]);
        a.Reset(new Vector3(-1.6f, 0, 0), 0);
        b.Reset(new Vector3(1.6f, 0, 2), 0.05f);
        return [a, b];
    }

    /// <summary>Records 30 s of two cars side by side (contacts, walls, drifts), with a teleport of car 1 halfway.</summary>
    private static (Replay Replay, byte[][] Final, byte[] Mid) Record(IGround ground)
    {
        var cars = Cars();
        var replay = new Replay { Info = { Course = "AKINA_DAY", Cars = [new("YOU", "AE86T", 0), new("RIVAL", "FD3S", 1, 2, 1)] } };
        var rec = new ReplayRecorder(replay, cars);
        Span<VehicleInput> inputs = stackalloc VehicleInput[2];
        Span<Vector3> prev = stackalloc Vector3[2];
        Span<Quaternion> rot = stackalloc Quaternion[2];
        byte[] mid = [];
        for (var t = 0; t < 3600; t++)
        {
            if (t == 1800)
            {
                cars[1].Reset(cars[1].Position + new Vector3(0, 0, 30), 0); // a respawn
                rec.Mark();
            }
            if (t == 2000) mid = cars[0].SaveState();
            rec.Before(cars);
            for (var c = 0; c < 2; c++)
            {
                (prev[c], rot[c]) = (cars[c].Position, cars[c].Orientation);
                inputs[c] = Script(c, t);
                cars[c].Step(inputs[c], ground, Drive.Dt);
            }
            CarCollision.Resolve(cars[0], cars[1], prev[0], rot[0], prev[1], rot[1]);
            rec.After(inputs);
        }
        return (replay, [.. cars.Select(c => c.SaveState())], mid);
    }

    [Fact]
    public void FileRoundTripReplaysTheRunExactly()
    {
        var ground = new Lane();
        var (replay, final, mid) = Record(ground);
        Assert.Equal(3600, replay.Ticks);
        Assert.True(replay.Keys.ContainsKey(1800), "teleport keyframe");
        var ms = new MemoryStream();
        replay.Write(ms);
        ms.Position = 0;
        Assert.Equal("FD3S", Replay.ReadInfo(ms).Cars[1].Car);
        ms.Position = 0;
        var back = Replay.Read(ms);
        Assert.Equal(replay.Inputs, back.Inputs);
        Assert.Equal(replay.Keys.Keys, back.Keys.Keys);

        var player = new ReplayPlayer(back, Cars(), ground);
        player.Seek(0);
        while (player.Step()) { }
        for (var c = 0; c < 2; c++) Assert.Equal(final[c], player.Cars[c].SaveState());

        // inputs alone: exact up to the teleport, which only the keyframe carries
        var drift = new ReplayPlayer(back, Cars(), ground).MeasureDrift();
        Assert.True(drift.Max > 1, "the teleport shows without keyframes");
        Assert.Equal(1800 * Drive.Dt, drift.FirstAt!.Value, 3); // exact up to there

        player.Seek(2000);
        Assert.Equal(mid, player.Cars[0].SaveState());
    }

    [Fact]
    public void ARunWithoutTeleportsHasNoDrift()
    {
        var ground = new Lane();
        var cars = Cars()[..1];
        var replay = new Replay { Info = { Cars = [new("YOU", "AE86T", 0)] } };
        var rec = new ReplayRecorder(replay, cars);
        for (var t = 0; t < 2400; t++)
        {
            rec.Before(cars);
            var i = Script(0, t);
            cars[0].Step(i, ground, Drive.Dt);
            rec.After([i]);
        }
        var drift = new ReplayPlayer(replay, Cars()[..1], ground).MeasureDrift();
        Assert.Equal(0, drift.Max);
        Assert.Equal(2400 / ReplayRecorder.KeyEvery - 1, drift.Samples);
    }

    [Fact]
    public void ACarSwapDropsTheRecording()
    {
        var cars = Cars()[..1];
        var rec = new ReplayRecorder(new Replay { Info = { Cars = [new("YOU", "AE86T", 0)] } }, cars);
        rec.Before(cars);
        rec.Before([new Vehicle(CarSpecs.All["R32"])]);
        Assert.False(rec.Valid);
    }

    [Fact]
    public void ForeignFilesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => Replay.Read(new MemoryStream([1, 2, 3])));
        var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, true)) gz.Write("XXXX"u8);
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => Replay.Read(ms));
    }
}
