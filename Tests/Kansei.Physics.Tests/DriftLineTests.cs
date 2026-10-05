using System.Numerics;
using Xunit.Abstractions;

namespace Kansei.Physics.Tests;

/// <summary>
///     The AI's drift through a synthetic bend (flat road of a fixed width, no walls: leaving it shows as edge distance
///     below 0): straight, arc, straight. Measures the car against the road edges, body slip, time against grip.
/// </summary>
public class DriftLineTests(ITestOutputHelper log)
{
    const float Dt = 1f / 120, Deg = 180 / MathF.PI;
    static CarSpec LabCar => CarSpecs.All[Environment.GetEnvironmentVariable("DRIFT_CAR") ?? "AE86T"];
    static float LabDrift => float.Parse(Environment.GetEnvironmentVariable("DRIFT_STYLE") ?? "0.9", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Polyline with 5 m spacing: heading +Z, then each (straight, radius, angle) — + angle = left.</summary>
    internal static Vector3[] Line(params (float Straight, float Radius, float Angle)[] parts)
    {
        var pts = new List<Vector3> { Vector3.Zero };
        float heading = 0;
        var p = Vector3.Zero;
        foreach (var (straight, radius, angle) in parts)
        {
            for (var d = 5f; d <= straight; d += 5) pts.Add(p += new Vector3(MathF.Sin(heading), 0, MathF.Cos(heading)) * 5);
            var steps = (int)MathF.Ceiling(MathF.Abs(angle) * radius / 5);
            for (var i = 0; i < steps; i++)
            {
                heading += angle / steps;
                pts.Add(p += new Vector3(MathF.Sin(heading), 0, MathF.Cos(heading)) * (MathF.Abs(angle) * radius / steps));
            }
        }
        for (var d = 5f; d <= 150; d += 5) pts.Add(p += new Vector3(MathF.Sin(heading), 0, MathF.Cos(heading)) * 5);
        return [.. pts];
    }

    /// <summary>Flat road within <paramref name="half"/> m of the polyline, nothing beyond.</summary>
    internal sealed class Corridor(Vector3[] line, float half) : IGround
    {
        public float Distance(Vector3 p)
        {
            var best = float.MaxValue;
            for (var i = 0; i + 1 < line.Length; i++)
            {
                Vector2 a = new(line[i].X, line[i].Z), ab = new Vector2(line[i + 1].X, line[i + 1].Z) - a, ap = new Vector2(p.X, p.Z) - a;
                var t = Math.Clamp(Vector2.Dot(ap, ab) / MathF.Max(ab.LengthSquared(), 1e-6f), 0, 1);
                best = MathF.Min(best, (ap - ab * t).Length());
            }
            return best;
        }

        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out GroundHit hit)
        {
            hit = default;
            if (direction.Y >= 0 || origin.Y < 0) return false;
            var t = -origin.Y / direction.Y;
            if (t > maxDistance) return false;
            var at = origin + direction * t;
            if (Distance(at) > half) return false;
            hit = new GroundHit(at, Vector3.UnitY, t, 0);
            return true;
        }

        public int CollideWalls(ReadOnlySpan<Vector3> probes, float radius, Span<WallContact> contacts) => 0;
    }

    public sealed record Run(float Time, float MinEdge, float MaxBeta, float MeanBeta, float SlideTime, int Drifts, int Aborts, bool Spun);

    /// <summary>Drives <paramref name="line"/> on a road of half width <paramref name="half"/> with <paramref name="style"/>; edge distance = road half width − |offset from the road's centre| − half the body.</summary>
    internal static Run Drive(Vector3[] line, float half, RivalStyle style, CarSpec spec, ITestOutputHelper? trace = null)
    {
        var ground = new Corridor(line, half);
        var pilot = new RivalPilot(line, style);
        var car = new Vehicle(spec);
        car.Reset(Vector3.Zero, 0);
        pilot.Pilot.Nearest(car.Position);
        float minEdge = float.MaxValue, maxBeta = 0, sumBeta = 0, slide = 0, t = 0;
        var n = 0;
        var spun = false;
        var goal = pilot.Pilot.Length - 20;
        for (; t < 120; t += Dt)
        {
            var input = pilot.Drive(car, ground, [], Dt);
            car.Step(input, ground, Dt);
            var (s, lat) = pilot.Pilot.Track(car.Position);
            if (s >= goal) break;
            minEdge = MathF.Min(minEdge, half - MathF.Abs(lat) - spec.Width / 2);
            var b = MathF.Abs(car.SlipAngle) * Deg;
            maxBeta = MathF.Max(maxBeta, b);
            spun |= b > 70;
            if (b > 10) { slide += Dt; sumBeta += b; n++; }
            if (trace != null && (int)(t / Dt) % 12 == 0)
                trace.WriteLine($"t {t:F1} s {s:F0} lat {lat:+0.0;-0.0} plan {pilot.Racing!.OffsetAt(s):+0.0;-0.0} {car.SpeedKmh:F0} km/h tgt {pilot.Pilot.TargetSpeed * 3.6f:F0} β {car.SlipAngle * Deg:+0;-0} " +
                                $"thr {input.Throttle:F2} brk {input.Brake:F2} str {input.Steer:+0.00;-0.00} hb {(input.Handbrake ? 1 : 0)} {(pilot.Drifting ? $"{pilot.Drift.State} {pilot.Drift.Debug}" : "")}");
        }
        if (Environment.GetEnvironmentVariable("DRIFT_LAB") == "3" && style.Drift > 0)
            for (var i = 0; i < pilot.Map!.Corners.Count; i++)
            {
                var c = pilot.Map.Corners[i];
                Console.WriteLine($"corner {i} {c.From:F0}-{c.To:F0} r {c.Radius:F1} plan {pilot.DriftAt(i)} apex v {pilot.Racing!.SpeedAt(c.Apex) * 3.6f:F0} holds {GripLimit.DriftSpeed(spec) * 3.6f * 1.15f:F0}");
            }
        return new Run(t, minEdge, maxBeta, n > 0 ? sumBeta / n : 0, slide, pilot.Drift.Held, pilot.Drift.Aborted, spun);
    }

    /// <summary>Tuning lab (DRIFT_LAB=1): every bend of a set, drift against grip, one summary line.</summary>
    [Fact]
    public void Lab()
    {
        if (Environment.GetEnvironmentVariable("DRIFT_LAB") == null) return;
        var rows = new List<(Run G, Run D)>();
        foreach (var r in new[] { 12f, 15f, 20f, 25f })
        foreach (var a in new[] { 100f, 150f, 180f })
        foreach (var half in new[] { 3.5f, 4.5f })
        {
            var line = Line((150, r, a / Deg), (60, r, -a / Deg));
            rows.Add((Drive(line, half, new RivalStyle(0.8f, 0.5f, 0, 0), LabCar), Drive(line, half, new RivalStyle(0.8f, 0.5f, LabDrift, 0), LabCar)));
            if (Environment.GetEnvironmentVariable("DRIFT_LAB") == "2") log.WriteLine($"LAB   R {r} {a}° ±{half}: {rows[^1].D} vs grip {rows[^1].G.Time:F2}");
        }
        var off = rows.Count(x => x.D.MinEdge < 0);
        log.WriteLine($"LAB {Environment.GetEnvironmentVariable("DRIFT_P")}: off {off}/{rows.Count}, min edge {rows.Average(x => MathF.Max(x.D.MinEdge, -3)):F2}, " +
                      $"time {100 * rows.Where(x => x.D.MinEdge >= 0).Select(x => x.D.Time / x.G.Time - 1).DefaultIfEmpty(float.NaN).Average():+0.0;-0.0}%, " +
                      $"β {rows.Average(x => x.D.MeanBeta):F1} (max {rows.Average(x => x.D.MaxBeta):F1}), slide {rows.Average(x => x.D.SlideTime):F1} s, drifts {rows.Sum(x => x.D.Drifts)} aborts {rows.Sum(x => x.D.Aborts)} spins {rows.Count(x => x.D.Spun)}");
    }

    [Theory]
    [InlineData(15f, 180f, 4f)]
    [InlineData(20f, 120f, 4f)]
    [InlineData(30f, 90f, 4f)]
    public void Probe(float radius, float angle, float half)
    {
        if (Environment.GetEnvironmentVariable("DRIFT_CASE") is { } dc && dc.Split(',') is [var r0, var a0, var h0])
            (radius, angle, half) = (float.Parse(r0), float.Parse(a0), float.Parse(h0, System.Globalization.CultureInfo.InvariantCulture));
        var line = Line((150, radius, angle / Deg), (60, radius, -angle / Deg));
        var grip = Drive(line, half, new RivalStyle(0.8f, 0.5f, 0, 0), LabCar);
        var drift = Drive(line, half, new RivalStyle(0.8f, 0.5f, LabDrift, 0), LabCar, Environment.GetEnvironmentVariable("DRIFT_TRACE") == radius.ToString(System.Globalization.CultureInfo.InvariantCulture) ? log : null);
        log.WriteLine($"R {radius} {angle}°: grip {grip}");
        log.WriteLine($"R {radius} {angle}°: drift {drift}");
    }
}
