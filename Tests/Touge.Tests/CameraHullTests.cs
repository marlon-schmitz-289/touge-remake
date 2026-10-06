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
        // the car 2 m in front of the wall, facing away from it (+Z): the eye would stand 4.6 m behind, in the wall
        var (pos, look, _) = CameraRig.Place(CameraView.Chase, ref f, true, 1 / 60f, PoseAt(new(0, 0.5f, 49)), PoseAt(new(0, 0.5f, 49)), mounts, 1, hull);
        Assert.True(pos.Z > 47 + CameraHull.Radius - 1e-3f, $"{pos}");
        Assert.True(look.Z > pos.Z);
        // standing there: the pulled-in eye holds still (no pumping in and out)
        var held = pos;
        for (var i = 0; i < 120; i++)
            (pos, _, _) = CameraRig.Place(CameraView.Chase, ref f, false, 1 / 60f, PoseAt(new(0, 0.5f, 49)), PoseAt(new(0, 0.5f, 49)), mounts, 1, hull);
        Assert.True(Vector3.Distance(pos, held) < 0.02f, $"{held} -> {pos}");
        // driving away from it: the eye eases out (no jump), back to the full distance
        var last = 49 - pos.Z;
        for (var i = 1; i <= 240; i++)
        {
            var car = new Vector3(0, 0.5f, 49 + i * 0.05f);
            (pos, _, _) = CameraRig.Place(CameraView.Chase, ref f, false, 1 / 60f, PoseAt(car), PoseAt(car), mounts, 1, hull);
            Assert.True(pos.Z > 47 + CameraHull.Radius - 1e-3f);
            var behind = car.Z - pos.Z;
            Assert.True(behind - last < 0.2f, $"frame {i}: {last:F2} -> {behind:F2}");
            last = behind;
        }
        Assert.Equal(4.6f, last, 2);
    }

    [Fact]
    public void Chase_view_is_the_originals_rig_and_lags_a_turn_by_a_quarter_second_at_full_smoothing()
    {
        var mounts = new CameraRig.Mounts();
        // snapped on the flat: 4.6 m behind and 1.7 m over the car's origin, looking straight along the car, the chase fov
        var car = PoseAt(new Vector3(3, 0.3f, 7));
        var f = new CameraRig.Follow();
        var (eye, look, fov) = CameraRig.Place(CameraView.Chase, ref f, true, 1 / 60f, car, car, mounts, 1);
        Assert.True(Vector3.Distance(eye, new Vector3(3, 2, 2.4f)) < 1e-4f, $"{eye}");
        Assert.True(Vector3.Dot(Vector3.Normalize(look - eye), Vector3.UnitZ) > 0.9999f);
        Assert.Equal(MathF.Tan(0.5f) * CameraRig.Widen, MathF.Tan(fov / 2), 4);
        // the default 60° setting is the original's chase: tan ½ H 0.92502 at 4:3, vertical 0.75 of it (69.5°)
        (_, _, fov) = CameraRig.Place(CameraView.Chase, ref f, true, 1 / 60f, car, car, mounts, MathF.PI / 3);
        Assert.Equal(0.75f * 0.92502f, MathF.Tan(fov / 2), 4);
        // a steady turn at 1 rad/s at full smoothing: the view trails the heading by rate / TurnRate (0.25 rad); the car turns
        // on the spot so no slide turns the view
        var g = new CameraRig.Follow();
        var heading = 0f;
        for (var i = 0; i <= 360; i++, heading += 1 / 120f)
        {
            var p = PoseAt(new Vector3(3, 0, 4), heading);
            (eye, look, _) = CameraRig.Place(CameraView.Chase, ref g, i == 0, 1 / 120f, p, p, mounts, 1, null, 1);
        }
        var dir = Vector3.Normalize(look - eye);
        Assert.Equal(1 / CameraRig.TurnRate, heading - 1 / 120f - MathF.Atan2(dir.X, dir.Z), 2);
        Assert.InRange(1 / CameraRig.TurnRate, 0.24f, 0.26f); // 6.45 % per 60-Hz frame
        // 10 % downhill: the view pitches down the slope, the eye stays 1.7 m over the car across the slope (higher behind)
        var pose = Matrix4x4.CreateRotationX(MathF.Atan(0.1f)) * Matrix4x4.CreateTranslation(0, 0.5f, 0); // nose down
        var h = new CameraRig.Follow();
        (eye, look, _) = CameraRig.Place(CameraView.Chase, ref h, true, 1 / 60f, pose, pose, mounts, 1);
        Assert.Equal(-0.1f / MathF.Sqrt(1.01f), Vector3.Normalize(look - eye).Y, 3);
        Assert.InRange(eye.Y, 0.5f + 0.46f + 1.69f - 0.01f, 0.5f + 0.46f + 1.69f + 0.01f); // 4.6 m back up the slope + 1.7 m across it
    }

    [Fact]
    public void Chase_view_swings_round_the_short_way_and_snaps_after_a_jump()
    {
        var mounts = new CameraRig.Mounts();
        // spun round 200° at full smoothing: the view turns the short way (-160°) and is behind the car again within 2 s
        var f = new CameraRig.Follow();
        CameraRig.Place(CameraView.Chase, ref f, true, 1 / 60f, PoseAt(Vector3.Zero), PoseAt(Vector3.Zero), mounts, 1, null, 1);
        var spun = PoseAt(Vector3.Zero, 200 * MathF.PI / 180);
        var (eye, look, _) = CameraRig.Place(CameraView.Chase, ref f, false, 1 / 60f, spun, spun, mounts, 1, null, 1);
        var first = Vector3.Normalize(look - eye);
        Assert.True(first.X < 0, $"{first}"); // turning towards -X (the short way to -160°)
        for (var i = 0; i < 120; i++) (eye, look, _) = CameraRig.Place(CameraView.Chase, ref f, false, 1 / 60f, spun, spun, mounts, 1, null, 1);
        Assert.True(Vector3.Dot(Vector3.Normalize(look - eye), Vector3.TransformNormal(Vector3.UnitZ, spun)) > 0.99f);
        // the car put 50 m away (a reset, another car in the replay): the view starts over there
        var moved = PoseAt(new Vector3(50, 0, 0), MathF.PI / 2);
        (eye, _, _) = CameraRig.Place(CameraView.Chase, ref f, false, 1 / 60f, moved, moved, mounts, 1);
        Assert.True(MathF.Abs(eye.X - (50 - 4.6f)) < 1e-3f && MathF.Abs(eye.Z) < 1e-3f, $"{eye}"); // behind the turned car (it faces +X)
    }

    [Fact]
    public void Chase_view_is_fixed_to_the_car_through_accelerations_and_turns()
    {
        var mounts = new CameraRig.Mounts();
        foreach (var view in new[] { CameraView.Chase, CameraView.Far })
        {
            var f = new CameraRig.Follow();
            Vector3 p = new(10, 0.5f, 20), first = default;
            float heading = 0.3f, speed = 0, t = 0;
            for (var i = 0; i < 144 * 20; i++, t += 1 / 144f)
            {
                // flooring it, braking, weaving left and right (up to 2.5 rad/s), all without slip: the car goes where it points
                speed = Math.Clamp(speed + (t % 8 < 5 ? 9 : -14) / 144f, 0, 60);
                heading += 2.5f * MathF.Sin(t * 1.7f) / 144f;
                p += new Vector3(MathF.Sin(heading), 0, MathF.Cos(heading)) * speed / 144f;
                var pose = PoseAt(p, heading);
                var (eye, look, fov) = CameraRig.Place(view, ref f, i == 0, 1 / 144f, pose, pose, mounts, 1);
                Matrix4x4.Invert(pose, out var inv);
                var local = Vector3.Transform(eye, inv);
                if (i == 0) first = local;
                Assert.True(Vector3.Distance(local, first) < 1e-3f, $"{view} {t:F2} s: {first} -> {local}");
                Assert.True(Vector3.Dot(Vector3.Normalize(look - eye), Vector3.TransformNormal(Vector3.UnitZ, pose)) > 0.99999f);
            }
            Assert.True(MathF.Abs(first.X) < 1e-4f && first.Z < -4);
        }
    }

    [Fact]
    public void Chase_view_turns_part_way_into_a_slide_quickly()
    {
        var mounts = new CameraRig.Mounts();
        float View(float slip, float speed, int frames = 144)
        {
            var f = new CameraRig.Follow();
            var heading = 0.5f;
            Vector3 eye = default, look = default;
            for (var i = 0; i <= frames; i++)
            {
                var pose = PoseAt(new Vector3(MathF.Sin(heading + slip), 0, MathF.Cos(heading + slip)) * speed * i / 144f, heading);
                (eye, look, _) = CameraRig.Place(CameraView.Chase, ref f, i == 0, 1 / 144f, pose, pose, mounts, 1);
            }
            var d = Vector3.Normalize(look - eye);
            return MathF.Atan2(d.X, d.Z) - heading;
        }
        Assert.InRange(View(0.6f, 20, 22) / (CameraRig.DriftShare * MathF.Sin(0.6f)), 0.75f, 1); // three quarters there after 0.15 s
        Assert.Equal(CameraRig.DriftShare * MathF.Sin(0.6f), View(0.6f, 20), 3); // sliding left of the nose: the view turns that way
        Assert.Equal(-CameraRig.DriftShare * MathF.Sin(0.6f), View(-0.6f, 20), 3);
        Assert.Equal(0, View(0, 20), 4); // grip: on the body's axis
        Assert.Equal(0, View(MathF.PI, 20), 3); // reversing: no turn
        Assert.Equal(0, View(0.6f, 1.5f), 4); // walking pace: the motion is too small to read
    }

    [Fact]
    public void Chase_view_calms_the_bounce_and_follows_a_slope()
    {
        var mounts = new CameraRig.Mounts();
        var f = new CameraRig.Follow();
        float max = 0, pitch = 0;
        for (var i = 0; i < 144 * 4; i++)
        {
            // the body bouncing ±2° at 2 Hz on a level road, standing still
            var body = MathF.Sin(i / 144f * MathF.Tau * 2) * 2 * MathF.PI / 180;
            var pose = Matrix4x4.CreateRotationX(-body) * Matrix4x4.CreateTranslation(0, 0.5f, 0);
            var (eye, look, _) = CameraRig.Place(CameraView.Chase, ref f, i == 0, 1 / 144f, pose, pose, mounts, 1);
            pitch = MathF.Asin(Vector3.Normalize(look - eye).Y);
            if (i > 144) max = MathF.Max(max, MathF.Abs(pitch));
        }
        Assert.InRange(max, 0, 0.6f * 2 * MathF.PI / 180);
        // onto a 10 % slope at once (a step; real slopes ramp in): the view is down it within a second
        var slope = Matrix4x4.CreateRotationX(MathF.Atan(0.1f)) * Matrix4x4.CreateTranslation(0, 0.5f, 0);
        for (var i = 0; i < 144; i++) CameraRig.Place(CameraView.Chase, ref f, false, 1 / 144f, slope, slope, mounts, 1);
        var (e, l, _) = CameraRig.Place(CameraView.Chase, ref f, false, 1 / 144f, slope, slope, mounts, 1);
        Assert.Equal(-MathF.Atan(0.1f), MathF.Asin(Vector3.Normalize(l - e).Y), 2);
    }

    [Fact]
    public void Smoothing_scales_the_lag_up_to_the_originals()
    {
        var mounts = new CameraRig.Mounts();
        foreach (var smoothing in new[] { 0f, 0.5f, 1f })
        {
            var f = new CameraRig.Follow();
            var heading = 0f;
            Vector3 eye = default, look = default;
            for (var i = 0; i <= 360; i++, heading += 1 / 120f)
            {
                var p = PoseAt(Vector3.Zero, heading);
                (eye, look, _) = CameraRig.Place(CameraView.Chase, ref f, i == 0, 1 / 120f, p, p, mounts, 1, null, smoothing);
            }
            var d = Vector3.Normalize(look - eye);
            Assert.Equal(smoothing / CameraRig.TurnRate, heading - 1 / 120f - MathF.Atan2(d.X, d.Z), 2);
        }
        var s = new Ui.Settings { CameraSmoothing = 3 };
        s.Sanitize();
        Assert.Equal(1, s.CameraSmoothing);
        Assert.Equal(0, new Ui.Settings().CameraSmoothing);
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
