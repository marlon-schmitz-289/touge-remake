using System.Numerics;
using Xunit.Abstractions;

namespace Kansei.Physics.Tests;

/// <summary>
///     The AI's course knowledge and line on synthetic roads (flat, a fixed width either side of the course line, no
///     walls: <see cref="DriftLineTests.Corridor"/>): bends and road room (<see cref="CourseMap"/>), the racing line inside
///     the margins (<see cref="RacingLine"/>), a drift through a hairpin (<see cref="DriftController"/>), seeded mistakes.
/// </summary>
public class RacingLineTests(ITestOutputHelper log)
{
    const float Dt = 1f / 120, Deg = 180 / MathF.PI, Half = 4;

    /// <summary>150 m straight, a left hairpin (r 15 m, 180°), 120 m, a right-hander (r 60 m, 90°), then 150 m.</summary>
    static Vector3[] Road() => DriftLineTests.Line((150, 15, MathF.PI), (120, 60, -MathF.PI / 2));

    static CourseMap Map(Vector3[] line) => new(line, new DriftLineTests.Corridor(line, Half), null);

    [Fact]
    public void Map_finds_bends_room_and_overtaking_zones()
    {
        var map = Map(Road());
        Assert.Equal(2, map.Corners.Count);
        Assert.Equal(("hairpin", 1), (map.Corners[0].Kind, map.Corners[0].Dir));
        Assert.Equal(("medium", -1), (map.Corners[1].Kind, map.Corners[1].Dir));
        // free road either side of the line: the road's half width, to the probe's 0.25 m steps
        for (var s = 10f; s < map.Length - 10; s += 7)
        {
            var (l, r) = map.Room(s);
            Assert.InRange(l, Half - 0.3f, Half);
            Assert.InRange(r, Half - 0.3f, Half);
        }
        // the braking zone before the hairpin (8 m of road), and the first straight
        Assert.Contains(map.Zones, z => z.Corner == 0 && z.To == map.Corners[0].From);
        Assert.Contains(map.Zones, z => z.Corner < 0 && z.To - z.From >= 80);
        Assert.Equal(0, map.ZoneAt(map.Corners[0].From - 10)!.Value.Corner); // a braking zone wins over the straight it overlaps
    }

    [Fact]
    public void Line_keeps_the_margin_goes_wide_in_and_out_and_plans_the_speed()
    {
        var map = Map(Road());
        var line = new RacingLine(map);
        for (var i = 0; i < map.Count; i++)
            Assert.InRange(line.Offset[i], -(Half - RacingLine.EdgeMargin) - 0.01f, Half - RacingLine.EdgeMargin + 0.01f);
        // left hairpin: outside (right) before it, inside (left) at its apex
        var c = map.Corners[0];
        Assert.True(line.OffsetAt(c.From - 15) < -1, $"turn-in from the outside: {line.OffsetAt(c.From - 15):F2}");
        Assert.True(line.OffsetAt(c.Apex) > 1, $"inside at the apex: {line.OffsetAt(c.Apex):F2}");
        // speed plan: slow in the hairpin, at most √(a·r) of the line there, faster on the straights, time adds up
        line.Plan(_ => 12, 8, CarSpec.AE86, 70);
        var apex = line.SpeedAt(c.Apex);
        Assert.InRange(apex, 10, MathF.Sqrt(12 / MathF.Abs(line.CurvatureAt(c.Apex))) + 0.5f);
        Assert.True(line.SpeedAt(c.From - 60) > apex + 5);
        Assert.True(line.TimeBetween(0, map.Length - 1) > 0);
        log.WriteLine($"hairpin r {c.Radius:F1} m: line r {1 / MathF.Abs(line.CurvatureAt(c.Apex)):F1} m, apex {apex * 3.6f:F0} km/h");
    }

    [Fact]
    public void Drift_through_a_hairpin_holds_the_slip_on_the_road()
    {
        var line = DriftLineTests.Line((150, 18, MathF.PI), (60, 18, -MathF.PI));
        var drift = DriftLineTests.Drive(line, 4.5f, new RivalStyle(0.8f, 0.5f, 0.9f, 0), CarSpecs.All["AE86T"]);
        var grip = DriftLineTests.Drive(line, 4.5f, new RivalStyle(0.8f, 0.5f, 0, 0), CarSpecs.All["AE86T"]);
        log.WriteLine($"drift {drift}");
        log.WriteLine($"grip  {grip}");
        Assert.True(drift.Drifts >= 1, "drifted");
        Assert.InRange(drift.MaxBeta, 15, 45);
        Assert.False(drift.Spun);
        Assert.True(drift.MinEdge > 0, $"stayed on the road: {drift.MinEdge:F2} m");
        Assert.True(grip.MaxBeta < 10, $"grip stays in grip: {grip.MaxBeta:F1}°");
        Assert.InRange(drift.Time / grip.Time, 0.95f, 1.05f); // about as fast as grip
    }

    [Fact]
    public void Mistakes_follow_the_seed()
    {
        // a weak driver over a few bends: the same seed drives the same way, another seed differently
        var line = DriftLineTests.Line((120, 25, MathF.PI / 2), (80, 30, -MathF.PI / 2), (80, 20, MathF.PI));
        static float[] Run(Vector3[] line, int seed)
        {
            var ground = new DriftLineTests.Corridor(line, Half);
            var pilot = new RivalPilot(line, new RivalStyle(0.1f, 0.5f, 0.3f)) { Seed = seed };
            var car = new Vehicle(CarSpec.AE86);
            car.Reset(Vector3.Zero, 0);
            pilot.Pilot.Nearest(car.Position);
            var path = new List<float>();
            for (var t = 0f; t < 40; t += Dt)
            {
                car.Step(pilot.Drive(car, ground, [], Dt), ground, Dt);
                path.Add(car.Position.X + car.Position.Z);
            }
            return [.. path];
        }
        float[] a = Run(line, 1), b = Run(line, 1), c = Run(line, 2);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
