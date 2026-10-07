using System.Numerics;
using Kansei.Physics;

namespace Touge.Tests;

public class ChaseCameraTests
{
    [Fact]
    public void Snaps_behind_and_follows_x_z_at_once()
    {
        var c = new ChaseCamera();
        c.Update(new Vector3(3, 0.3f, 7), Vector3.UnitZ, Vector3.Zero, 1 / 60f);
        var (eye, target, fov) = c.Pose(1);
        Assert.True(Vector3.Distance(eye, new Vector3(3, 2f, 1.6f)) < 1e-4f, $"{eye}");
        Assert.True(Vector3.Distance(target, new Vector3(3, 1.1f, 11)) < 1e-4f, $"{target}");
        Assert.Equal(1, fov);
        // straight on at 30 m/s: the eye moves with the car exactly, frame after frame (no jitter), the fov 6° wider
        var car = new Vector3(3, 0.3f, 7);
        for (var i = 0; i < 100; i++)
        {
            car.Z += 30 / 144f;
            c.Update(car, Vector3.UnitZ, new Vector3(0, 0, 30), 1 / 144f);
            (eye, _, fov) = c.Pose(1);
            Assert.True(Vector3.Distance(eye - car, new Vector3(0, 1.7f, -5.4f)) < 1e-4f, $"{i}: {eye - car}");
        }
        Assert.Equal(1 + 6 * MathF.PI / 180, fov, 5);
    }

    [Fact]
    public void Heading_eases_the_same_at_30_60_and_144_fps()
    {
        var looks = new List<Vector3>();
        foreach (var fps in new[] { 30, 60, 144 })
        {
            var c = new ChaseCamera();
            c.Update(Vector3.Zero, Vector3.UnitZ, Vector3.Zero, 1f / fps);
            for (var i = 0; i < fps / 5; i++) c.Update(Vector3.Zero, Vector3.UnitX, Vector3.Zero, 1f / fps); // 0.2 s after a 90° turn
            var (eye, target, _) = c.Pose(1);
            looks.Add(Vector3.Normalize(target - eye));
        }
        Assert.True(Vector3.Dot(looks[0], looks[2]) > 0.999f && Vector3.Dot(looks[1], looks[2]) > 0.9995f);
        Assert.InRange(looks[2].X, 0.3f, 0.9f); // part of the way round, not snapped
    }

    [Fact]
    public void Drift_looks_into_the_slide_and_bumps_are_calmed()
    {
        // car points +Z, slides at 30° to the left (+X): the aim swings 35 % of the way towards the direction of travel
        var c = new ChaseCamera();
        var v = new Vector3(MathF.Sin(MathF.PI / 6), 0, MathF.Cos(MathF.PI / 6)) * 20;
        c.Update(Vector3.Zero, Vector3.UnitZ, v, 1 / 60f);
        var (eye, target, _) = c.Pose(1);
        Assert.True(MathF.Abs(eye.X) < 1e-4f && target.X > 0.6f, $"{eye} {target}");
        // under 3 m/s: along the car only
        c.Reset();
        c.Update(Vector3.Zero, Vector3.UnitZ, v / 10, 1 / 60f);
        Assert.Equal(0, c.Pose(1).Target.X, 4);
        // the car bouncing ±10 cm at 8 Hz: the eye moves under half of it; a 1 m step up is followed within 0.6 s
        c.Reset();
        float lo = 9, hi = -9;
        for (var i = 0; i < 288; i++)
        {
            c.Update(new Vector3(0, 0.1f * MathF.Sin(i / 144f * MathF.Tau * 8), 0), Vector3.UnitZ, Vector3.Zero, 1 / 144f);
            if (i > 144) (lo, hi) = (MathF.Min(lo, c.Pose(1).Position.Y), MathF.Max(hi, c.Pose(1).Position.Y));
        }
        Assert.InRange(hi - lo, 0, 0.1f);
        for (var i = 0; i < 86; i++) c.Update(Vector3.UnitY, Vector3.UnitZ, Vector3.Zero, 1 / 144f);
        Assert.Equal(2.7f, c.Pose(1).Position.Y, 1);
    }

    [Fact]
    public void Slopes_count_half()
    {
        var c = new ChaseCamera();
        var up = Vector3.Normalize(new Vector3(0, 0.2f, 1)); // nose up a 20 % grade
        c.Update(Vector3.Zero, up, Vector3.Zero, 1 / 60f);
        var (eye, _, _) = c.Pose(1);
        var flat = Vector3.Normalize(new Vector3(0, 0.1f, 1));
        Assert.True(Vector3.Distance(eye, -flat * 5.4f + Vector3.UnitY * 1.7f) < 1e-3f, $"{eye}");
    }

    [Fact]
    public void Body_pitching_on_its_springs_does_not_move_the_view()
    {
        var m = new CameraRig.Mounts(default, default, default, default, default, 0.3f);
        // wheel centres level 0.3 m over the road; the body nosed down (braking): its springs hold the wheels where they were
        static Vector3 Eye(float pitch, in CameraRig.Mounts m)
        {
            var pose = Matrix4x4.CreateRotationX(pitch) * Matrix4x4.CreateTranslation(0, 0.5f, 0);
            Matrix4x4.Invert(pose, out var inv);
            var wheels = new WheelState[4];
            for (var i = 0; i < 4; i++) wheels[i].LocalCenter = Vector3.Transform(new Vector3(i % 2 == 0 ? -0.7f : 0.7f, 0.3f, i < 2 ? 1.2f : -1.2f), inv);
            var c = new ChaseCamera();
            return CameraRig.Place(CameraView.Chase, ref c, true, 1 / 60f, pose, pose, Vector3.Zero, m, 1, wheels).Pos;
        }
        Assert.True(Vector3.Distance(Eye(0, m), new Vector3(0, 1.7f, -5.4f)) < 1e-4f, $"{Eye(0, m)}");
        Assert.True(Vector3.Distance(Eye(0.05f, m), Eye(0, m)) < 1e-4f, $"{Eye(0.05f, m)}");
    }
}
