using System.Numerics;
using Kansei.Physics;
using Touge.Formats;
using Touge.Replays;

namespace Touge.Tests;

/// <summary>The cameras against the course: pull-in, the edge of the course, the chase view's state, the TV director.</summary>
public class CameraHullTests
{
    /// <summary>Ground 0..<paramref name="length"/> m along +Z, 20 m wide (X ±10), and optionally a wall across Z = <paramref name="wallZ"/> (10 m high).</summary>
    private static CameraHull Course(float length = 200, float? wallZ = null)
    {
        List<Vector3> p = [new(-10, 0, 0), new(10, 0, 0), new(10, 0, length), new(-10, 0, 0), new(10, 0, length), new(-10, 0, length)];
        if (wallZ is { } z) p.AddRange([new(-10, 0, z), new(10, 0, z), new(10, 10, z), new(-10, 0, z), new(10, 10, z), new(-10, 10, z)]);
        var n = p.Count / 3;
        return new CameraHull(new TriangleGround(p.ToArray(), Enumerable.Range(0, p.Count).ToArray(), new int[n], new bool[n]));
    }

    [Fact]
    public void Reach_stops_short_of_a_wall_and_at_the_edge_of_the_course()
    {
        var open = Course();
        Assert.Equal(6, open.Reach(new Vector3(0, 1.5f, 50), new Vector3(0, 1.5f, 44)), 3); // nothing in the way
        var walled = Course(wallZ: 47);
        Assert.Equal(3 - CameraHull.Radius, walled.Reach(new Vector3(0, 1.5f, 50), new Vector3(0, 1.5f, 44)), 3);
        // a wall the centre ray just misses (0.2 m beside it) still stops the sphere
        var post = new CameraHull(new TriangleGround([new(-10, 0, 0), new(10, 0, 0), new(0, 0, 100), new(0.2f, 0, 47), new(5, 0, 47), new(0.2f, 5, 47)], [0, 1, 2, 3, 4, 5], [0, 0], [false, false]));
        Assert.True(post.Reach(new Vector3(0, 1.5f, 50), new Vector3(0, 1.5f, 44)) < 3.8f);
        // out over the end of the ground (Z < 0: the void): back to within a few cm of the edge
        var edge = open.Reach(new Vector3(0, 1.5f, 3), new Vector3(0, 1.5f, -5));
        Assert.InRange(edge, 2.9f, 3.05f);
        Assert.True(open.Floor(new Vector3(0, 1.5f, 3 - edge)));
        Assert.False(open.Floor(new Vector3(0, 1.5f, -1)));
        // a pivot already over the void (free camera started at a TV eye there) is not frozen: it may move, back towards the course too
        Assert.Equal(2, open.Reach(new Vector3(0, 5, -10), new Vector3(0, 5, -12)), 3);
        Assert.Equal(4, open.Reach(new Vector3(0, 5, -10), new Vector3(0, 5, -6)), 3);
    }

    private static Matrix4x4 PoseAt(Vector3 p, float yaw = 0) => Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(p);

    [Fact]
    public void Chase_view_never_goes_through_a_wall_and_eases_back_out()
    {
        var hull = Course(wallZ: 47);
        var f = new CameraRig.Follow();
        var mounts = new CameraRig.Mounts();
        var v = new Vector3(0, 0, 0.1f);
        // the car 2 m in front of the wall, facing away from it (+Z): the eye would stand 5 m behind, in the wall
        var (pos, look, _) = CameraRig.Place(CameraView.Chase, ref f, true, 1 / 60f, PoseAt(new(0, 0.5f, 49)), PoseAt(new(0, 0.5f, 49)), mounts, v, 1, hull);
        Assert.True(pos.Z > 47 + CameraHull.Radius - 1e-3f, $"{pos}");
        Assert.True(look.Z > pos.Z);
        // driving away from it: the eye eases out (no jump), back to the full distance
        var last = 49 - pos.Z;
        for (var i = 1; i <= 240; i++)
        {
            var car = new Vector3(0, 0.5f, 49 + i * 0.05f);
            (pos, _, _) = CameraRig.Place(CameraView.Chase, ref f, false, 1 / 60f, PoseAt(car), PoseAt(car), mounts, v, 1, hull);
            Assert.True(pos.Z > 47 + CameraHull.Radius - 1e-3f);
            var behind = car.Z - pos.Z;
            Assert.True(behind - last < 0.2f, $"frame {i}: {last:F2} -> {behind:F2}");
            last = behind;
        }
        Assert.InRange(last, 5, 6.2f);
    }

    [Fact]
    public void Chase_view_keeps_its_distance_at_speed_and_follows_the_slope()
    {
        var f = new CameraRig.Follow();
        var mounts = new CameraRig.Mounts();
        var speed = new Vector3(0, 0, 40); // 144 km/h along +Z
        Vector3 pos = default, car = default;
        for (var i = 0; i < 120; i++)
        {
            car = new Vector3(0, 0.5f, i * 40 / 60f);
            (pos, _, _) = CameraRig.Place(CameraView.Chase, ref f, i == 0, 1 / 60f, PoseAt(car), PoseAt(car), mounts, speed, 1);
        }
        Assert.InRange(car.Z - pos.Z, 5, 6.2f); // no lag that grows with speed (it was ~12 m)
        // 10 % downhill: the eye stands as high over the road behind as on the flat, the aim goes down the slope
        var down = Vector3.Normalize(new Vector3(0, -0.1f, 1));
        var pose = Matrix4x4.CreateWorld(new Vector3(0, 0.5f, 0), down, Vector3.Cross(down, Vector3.UnitX) * -1);
        var g = new CameraRig.Follow();
        var (eye, look, _) = CameraRig.Place(CameraView.Chase, ref g, true, 1 / 60f, pose, pose, mounts, down * 20, 1);
        var roadBehind = 0.5f + 0.1f * -eye.Z;
        Assert.InRange(eye.Y - roadBehind, 1.3f, 1.6f);
        Assert.True(look.Y < 0.5f + 0.75f); // below the car's height plus the aim: down the slope
    }

    [Fact]
    public void Chase_view_follows_the_travel_in_a_spin_and_snaps_after_a_jump()
    {
        var fwd = Vector3.UnitZ;
        Assert.True(Vector3.Dot(CameraRig.Aim(fwd, new Vector3(1, 0, 1) * 10), Vector3.Normalize(fwd + Vector3.Normalize(new Vector3(1, 0, 1)))) > 0.999f); // 45° drift: half way between
        Assert.True(Vector3.Dot(CameraRig.Aim(fwd, new Vector3(0, 0, -20)), -fwd) > 0.99f); // sliding backwards: the travel
        Assert.Equal(fwd, CameraRig.Aim(fwd, new Vector3(0, 0, -1))); // reversing slowly: the heading
        // the car put 50 m away (a reset, another car in the replay): the view starts over there
        var f = new CameraRig.Follow();
        var mounts = new CameraRig.Mounts();
        CameraRig.Place(CameraView.Chase, ref f, true, 1 / 60f, PoseAt(Vector3.Zero), PoseAt(Vector3.Zero), mounts, default, 1);
        var moved = PoseAt(new Vector3(50, 0, 0), MathF.PI / 2);
        var (pos, _, _) = CameraRig.Place(CameraView.Chase, ref f, false, 1 / 60f, moved, moved, mounts, default, 1);
        Assert.InRange(Vector3.Distance(pos, moved.Translation), 5, 6.5f);
        Assert.True(pos.X < 50 - 4.5f); // behind the turned car (it faces +X)
    }

    [Fact]
    public void Spring_bends_a_step_without_overshoot()
    {
        Vector3 x = Vector3.Zero, v = Vector3.Zero, prev = Vector3.Zero;
        float lastStep = 0;
        for (var i = 0; i < 300; i++)
        {
            CameraRig.Spring(ref x, ref v, Vector3.One, 9, 1 / 60f);
            Assert.True(x.X <= 1 + 1e-5f);
            var step = x.X - prev.X;
            if (i == 0) Assert.True(step < 0.03f); // starts gently (a lerp would jump 14 %)
            lastStep = step;
            prev = x;
        }
        Assert.Equal(1, x.X, 3);
        Assert.True(lastStep >= 0);
    }

    [Fact]
    public void Tv_holds_a_shot_and_cuts_away_from_a_blocked_camera()
    {
        Vector3[] road = [.. Enumerable.Range(0, 101).Select(i => new Vector3(0, 0, 2 * i))];
        // camera 0 for road points 0–10, camera 1 for 10–100 behind a wall (X = 5) that hides the road
        ReplayCameras.Cam Cam(int from, int to, Vector3 eye) => new(2, from, to, new(eye, default, 20), new(eye, default, 20));
        var cams = new[] { Cam(0, 10, new(8, 3, 10)), Cam(10, 100, new(12, 3, 100)) };
        List<Vector3> p = [new(-10, 0, -5), new(10, 0, -5), new(10, 0, 205), new(-10, 0, -5), new(10, 0, 205), new(-10, 0, 205)];
        p.AddRange([new(5, 0, 60), new(5, 0, 205), new(5, 9, 205), new(5, 0, 60), new(5, 9, 205), new(5, 9, 60)]);
        var hull = new CameraHull(new TriangleGround(p.ToArray(), Enumerable.Range(0, p.Count).ToArray(), new int[4], new bool[4]));
        var tv = new TvCameras(cams, road, false, hull);
        var cuts = new List<float>();
        Vector3 eye = default;
        int hidden = 0, longest = 0;
        for (var t = 0f; t < 12; t += 1 / 60f)
        {
            var car = new Vector3(0, 0.5f, 5 + t * 15); // 54 km/h along the road
            (eye, _, _, var cut) = tv.Update(car, 1 / 60f, t == 0);
            if (cut) cuts.Add(t);
            hidden = hull.Hit(eye, car + Vector3.UnitY) != null ? hidden + 1 : 0;
            longest = Math.Max(longest, hidden);
        }
        Assert.Equal(0, cuts[0]);
        Assert.True(longest <= TvCameras.Blind * 60 + 2, $"hidden {longest} frames in a row"); // the wall never hides the car for long
        Assert.All(cuts.Zip(cuts.Skip(1)), c => Assert.True(c.Second - c.First >= TvCameras.Blind, $"cuts {string.Join(", ", cuts)}"));
        Assert.True(cuts.Count <= 3, $"cuts {string.Join(", ", cuts)}");
        Assert.True(eye.X < 5); // camera 1 stands behind the wall: a trackside camera on the road side took over
    }

    [Fact]
    public void Tv_zoom_keeps_the_car_whole_up_close_and_visible_far_away()
    {
        static float Height(float fov, float d) => 2 * d * MathF.Tan(fov / 2 * MathF.PI / 180); // picture height at the car
        Assert.InRange(Height(TvCameras.Frame(40, 4), 4), 5 - 1e-3f, 40); // 4 m off: widened so the car fits
        Assert.Equal(75, TvCameras.Frame(40, 1)); // closer still: no fisheye beyond 75°
        Assert.InRange(Height(TvCameras.Frame(50, 120), 120), 5, 40 + 1e-3f); // 120 m off at 50°: zoomed in
        Assert.Equal(20, TvCameras.Frame(20, 30)); // a sensible table shot is kept
    }

    [Fact]
    public void Fast_replay_glides_over_all_ticks_of_a_game_tick()
    {
        var replay = new Replay { Info = { Course = "AKINA_DAY", Cars = [new ReplayCar("YOU", "AE86T", 0)] } };
        var car = new Vehicle(CarSpec.AE86);
        var ground = new TriangleGround([new(-50, 0, -50), new(50, 0, -50), new(50, 0, 500), new(-50, 0, 500)], [0, 1, 2, 0, 2, 3], [0, 0], [false, false]);
        car.Reset(Vector3.Zero, 0);
        var rec = new ReplayRecorder(replay, [car]);
        for (var i = 0; i < 240; i++)
        {
            rec.Before([car]);
            var input = new VehicleInput(1, 0, 0, false);
            car.Step(input, ground, Drive.Dt);
            rec.After([input]);
        }
        var player = new ReplayPlayer(replay, [new Vehicle(CarSpec.AE86)], ground);
        player.Seek(100);
        var before = player.Cars[0].Position;
        player.Step();
        player.KeepPrev = true;
        player.Step();
        player.KeepPrev = false;
        Assert.Equal(before, player.PrevPosition[0]); // 2×: from before the first of the two ticks
        player.Step();
        Assert.NotEqual(before, player.PrevPosition[0]);
    }
}
