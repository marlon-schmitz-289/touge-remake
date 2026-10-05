using System.Numerics;
using Xunit.Abstractions;

namespace Kansei.Physics.Tests;

public class RivalPilotTests(ITestOutputHelper log)
{
    const float Dt = 1f / 120;

    /// <summary>Straight line along +Z, 5 m spacing.</summary>
    static Vector3[] Straight(int points = 400) => [.. Enumerable.Range(0, points).Select(i => new Vector3(0, 0, i * 5))];

    /// <summary>Road x ∈ [−half, half] along the straight, walls beyond.</summary>
    static TriangleGround Road(float half) => WallTests.Grid([-half, half], [-20, 2100], (_, _) => false);

    /// <summary>
    ///     The AI behind a car driving the line at a steady <paramref name="leadSpeed"/> (scripted, on the line): returns the
    ///     AI's along minus the lead car's after <paramref name="seconds"/>, the closest centre distance while they overlapped
    ///     sideways, and the AI's widest lateral.
    /// </summary>
    static (float Ahead, float Closest, float MaxLat) Chase(float half, float leadSpeed, float seconds, RivalStyle style)
    {
        var line = Straight();
        var ground = Road(half);
        var ai = new RivalPilot(line, style);
        var car = new Vehicle(CarSpec.AE86);
        car.Reset(Vector3.Zero, 0);
        ai.Pilot.Nearest(car.Position);
        float lead = 30, closest = float.MaxValue, maxLat = 0;
        for (var t = 0f; t < seconds; t += Dt)
        {
            lead += leadSpeed * Dt;
            car.Step(ai.Drive(car, ground, [new Opponent(lead, 0, leadSpeed)], Dt), ground, Dt);
            var (s, lat) = ai.Pilot.Track(car.Position);
            if (MathF.Abs(lat) < RivalPilot.Alongside) closest = MathF.Min(closest, MathF.Abs(lead - s));
            maxLat = MathF.Max(maxLat, MathF.Abs(lat));
        }
        var (along, _) = ai.Pilot.Track(car.Position);
        return (along - lead, closest, maxLat);
    }

    [Fact]
    public void Passes_a_slower_car_on_a_wide_road_without_ramming_it()
    {
        var (ahead, closest, maxLat) = Chase(5, 15, 30, new RivalStyle(0.8f, 0.6f, 0));
        log.WriteLine($"ahead {ahead:F1} m, closest in line {closest:F1} m, max |lateral| {maxLat:F2} m");
        Assert.True(ahead > 20, "passed and pulled away");
        Assert.True(closest > 4.5f, "never nose to tail inside a car length"); // AE86 is 4.2 m long
        Assert.InRange(maxLat, RivalPilot.Alongside, 5 - RivalPilot.EdgeMargin + 0.3f); // out to pass, but kept off the walls
    }

    [Fact]
    public void Follows_where_the_road_is_too_narrow_to_pass()
    {
        // 5 m wide: no room for two side by side → it sits behind at the following gap, never inside a car length
        var (ahead, closest, _) = Chase(2.5f, 15, 30, new RivalStyle(0.8f, 1, 0));
        log.WriteLine($"ahead {ahead:F1} m, closest {closest:F1} m");
        Assert.True(ahead < -4.5f, "stays behind");
        Assert.True(closest > 4.5f);
    }

    [Fact]
    public void Never_steers_into_a_car_alongside_where_the_road_room_ends()
    {
        // 7 m road (racing line bounds ±2.2 m), the AI starts 2.4 m right of a car that squeezes it, staying level at its
        // pace: the bounds pull towards that car — it holds its side and lifts instead (MYOUGI grid, review)
        var line = Straight();
        var ground = Road(3.5f);
        var ai = new RivalPilot(line, new RivalStyle(0.8f, 0.5f, 0));
        var car = new Vehicle(CarSpec.AE86);
        car.Reset(new Vector3(-2.4f, 0, 20), 0);
        ai.Pilot.Nearest(car.Position);
        float closest = float.MaxValue, aim = float.MaxValue;
        var lifted = false;
        for (var t = 0f; t < 5; t += Dt)
        {
            var (s, lat) = ai.Pilot.Track(car.Position);
            var v = MathF.Max(car.Velocity.Length(), 6);
            car.Step(ai.Drive(car, ground, [new Opponent(s + 0.5f, 0, v)], Dt), ground, Dt);
            closest = MathF.Min(closest, MathF.Abs(ai.Pilot.Track(car.Position).Lateral));
            aim = MathF.Min(aim, -ai.Offset);
            if (Environment.GetEnvironmentVariable("RP_DBG") != null) log.WriteLine($"t {t:F2} lat {ai.Pilot.Track(car.Position).Lateral:F2} off {ai.Offset:F2} state {ai.State} v {car.SpeedKmh:F0}");
            lifted |= ai.State == RivalPilot.Mode.Follow;
        }
        log.WriteLine($"closest lateral to the other car {closest:F2} m, offset {ai.Offset:F2}");
        Assert.True(aim > RivalPilot.PassGap - 0.05f, $"never aimed towards it: {aim:F2} m");
        Assert.True(closest > 2, $"kept its side: {closest:F2} m"); // bodies ~1.7 m wide
        Assert.True(lifted, "lifted to drop in behind");
    }

    [Fact]
    public void Defends_the_inside_against_a_car_close_behind_before_a_bend()
    {
        // straight, then a tight left-hander (radius 35 m, a braking zone before it); attacker 8 m behind on the left
        // (inside): with aggression 1 the AI covers the inside in ~80 % of the zones (the race seed decides), off its line
        var line = new List<Vector3>();
        for (var i = 0; i < 40; i++) line.Add(new Vector3(0, 0, i * 5));
        for (var i = 1; i < 30; i++)
        {
            var a = i * 5 / 35f;
            line.Add(new Vector3(35 - 35 * MathF.Cos(a), 0, 195 + 35 * MathF.Sin(a)));
        }
        var ground = WallTests.Grid([-10, 80], [-20, 400], (_, _) => false);
        var blocks = 0;
        for (var seed = 0; seed < 10; seed++)
        {
            var ai = new RivalPilot([.. line], new RivalStyle(0.8f, 1, 0)) { Seed = seed };
            var car = new Vehicle(CarSpec.AE86);
            car.Reset(Vector3.Zero, 0);
            ai.Pilot.Nearest(car.Position);
            var blocked = false;
            float moved = 0;
            for (var t = 0f; t < 15; t += Dt)
            {
                var (s, lat) = ai.Pilot.Track(car.Position);
                if (s > 190) break;
                // the attacker 8 m back, a metre and a half left of us (inside)
                car.Step(ai.Drive(car, ground, [new Opponent(s - 8, lat + 1.5f, car.Velocity.Length())], Dt), ground, Dt);
                blocked |= ai.State == RivalPilot.Mode.Block;
                if (blocked) moved = MathF.Max(moved, lat - ai.Racing!.OffsetAt(s)); // left of its own line
            }
            log.WriteLine($"seed {seed}: blocked {blocked}, moved left {moved:F2} m");
            if (blocked)
            {
                Assert.True(moved > 0.3f, "moved towards the inside (left)");
                blocks++;
            }
        }
        Assert.InRange(blocks, 5, 10);
    }

    [Fact]
    public void Pace_rises_with_skill_and_rubber_band_is_subtle()
    {
        var (c0, b0) = RivalPilot.Pace(0);
        var (c1, b1) = RivalPilot.Pace(1);
        Assert.True(c1 > c0 && b1 > b0);
        Assert.InRange(c0 / 9.81f, 0.6f, 1.0f); // a beginner: well below the tyres' grip
        Assert.InRange(c1 / 9.81f, 1.4f, 1.5f); // LEGEND: the AE86 at its limit (~1.45 g on the skid pad), like a good player
        Assert.InRange(b1 / 9.81f, 0.9f, 1.0f);
    }
}
