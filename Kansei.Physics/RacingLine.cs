using System.Numerics;

namespace Kansei.Physics;

/// <summary>
///     A car's own line over a <see cref="CourseMap"/>: a lateral offset per station (+ left of the course line) inside the
///     road room less a margin (<see cref="EdgeMargin"/>, wider in drift corners for the swung-out tail), found as the
///     elastic band of least squared curvature (outside–inside–outside), the apex of tight bends moved later; plus the
///     speed plan along it (<see cref="Plan"/>): corner speed √(a_lat·r), forward pass on the car's own drive force less
///     drag and grade, backward pass on the braking deceleration (friction ellipse with the cornering).
/// </summary>
public sealed class RacingLine
{
    private const float G = 9.81f;

    /// <summary>Distance kept between the car's centre and the end of the drivable road (half a body plus elbow room).</summary>
    public const float EdgeMargin = 1.3f;

    public CourseMap Map { get; }
    /// <summary>Per station: lateral offset (+ left), its bounds, the line's signed curvature (1/m, + left), distance to the next station along it.</summary>
    public float[] Offset { get; }
    public float[] Lo { get; }
    public float[] Hi { get; }
    public float[] Curvature { get; }
    public float[] Ds { get; }
    /// <summary>Planned speed (m/s) per station and time (s) from the first station (after <see cref="Plan"/>).</summary>
    public float[] Speed { get; }
    public float[] Time { get; }

    /// <param name="margin">Edge margin per station (null = <see cref="EdgeMargin"/> everywhere).</param>
    /// <param name="lateApex">Extra weight of the curvature from the apex of hairpins/tight bends to 25 m past their end (late apex).</param>
    public RacingLine(CourseMap map, float[]? margin = null, float lateApex = 2)
    {
        Map = map;
        var n = map.Count;
        (Offset, Lo, Hi, Curvature, Ds, Speed, Time) = (new float[n], new float[n], new float[n], new float[n], new float[n], new float[n], new float[n]);
        // bounds: the narrowest room within ±2 stations (the probe is noisy), less the margin
        for (var i = 0; i < n; i++)
        {
            float l = float.MaxValue, r = float.MaxValue;
            for (var j = Math.Max(i - 2, 0); j <= Math.Min(i + 2, n - 1); j++) (l, r) = (MathF.Min(l, map.RoomLeft[j]), MathF.Min(r, map.RoomRight[j]));
            var m = margin?[i] ?? EdgeMargin;
            (Lo[i], Hi[i]) = (-(r - m), l - m);
            if (Lo[i] > Hi[i]) Lo[i] = Hi[i] = (Lo[i] + Hi[i]) / 2;
        }
        for (var i = 0; i < n; i++) Offset[i] = Math.Clamp(0, Lo[i], Hi[i]);
        Smooth(lateApex);
        Geometry();
    }

    /// <summary>Point of the line at station <paramref name="i"/>.</summary>
    public Vector3 PointAt(int i) => Map.Point[i] + Map.Left[i] * Offset[i];

    /// <summary>
    ///     Least weighted squared curvature: projected Gauss–Seidel on the (weighted) 4th difference, coarse to fine
    ///     (neighbours 16 … 1 stations apart, 60 sweeps each), each point moved only along the course line's normal, clamped.
    ///     The exits of hairpins and tight bends weigh <paramref name="exitWeight"/> × more: the line turns in tighter and
    ///     opens the exit early — a late apex.
    /// </summary>
    private void Smooth(float exitWeight)
    {
        var n = Map.Count;
        var w = new float[n];
        Array.Fill(w, 1);
        foreach (var c in Map.Corners)
            if (c.Slow)
                for (var i = Map.Index(c.Apex); i <= Map.Index(c.To + 25) && i < n; i++) w[i] = 1 + exitWeight;
        foreach (var k in (ReadOnlySpan<int>)[16, 8, 4, 2, 1])
        for (var it = 0; it < 60; it++)
        for (var i = 2 * k; i < n - 2 * k; i++)
        {
            float wa = w[i - k], wb = w[i], wc = w[i + k];
            Vector3 a = PointAt(i - 2 * k), b = PointAt(i - k), c = PointAt(i + k), d = PointAt(i + 2 * k);
            var want = (wa * (2 * b - a) + 2 * wb * (b + c) + wc * (2 * c - d)) / (wa + 4 * wb + wc);
            Offset[i] = Math.Clamp(Vector3.Dot(want - Map.Point[i], Map.Left[i]), Lo[i], Hi[i]);
        }
    }

    private void Geometry()
    {
        var n = Map.Count;
        for (var i = 0; i < n; i++)
        {
            Curvature[i] = CourseMap.Menger(PointAt(Math.Max(i - 3, 0)), PointAt(i), PointAt(Math.Min(i + 3, n - 1)));
            var d = i + 1 < n ? PointAt(i + 1) - PointAt(i) : Vector3.Zero;
            Ds[i] = MathF.Max(new Vector2(d.X, d.Z).Length(), 0.1f);
        }
    }

    /// <summary>Point of the line at <paramref name="s"/> m along the course line.</summary>
    public Vector3 PositionAt(float s)
    {
        var x = Math.Clamp(s / CourseMap.Step, 0, Map.Count - 1);
        var i = Math.Min((int)x, Map.Count - 2);
        return Vector3.Lerp(PointAt(i), PointAt(i + 1), x - i);
    }

    /// <summary>Offset at <paramref name="s"/> m along the course line.</summary>
    public float OffsetAt(float s) => Map.At(Offset, s);
    public float SpeedAt(float s) => Map.At(Speed, s);
    public float CurvatureAt(float s) => Map.At(Curvature, s);
    public (float Lo, float Hi) BoundsAt(float s) => (Map.At(Lo, s), Map.At(Hi, s));
    /// <summary>Planned time (s) from <paramref name="from"/> to <paramref name="to"/> m along.</summary>
    public float TimeBetween(float from, float to) => Map.At(Time, to) - Map.At(Time, from);

    /// <summary>
    ///     The speed plan: <paramref name="aLat"/>(station) = planned lateral acceleration (m/s²), <paramref name="aBrake"/> =
    ///     braking deceleration (m/s²), <paramref name="spec"/> the car (drive force, drag), <paramref name="top"/> speed cap.
    /// </summary>
    public void Plan(Func<int, float> aLat, float aBrake, CarSpec spec, float top)
    {
        var n = Map.Count;
        var drive = DriveAccel(spec);
        for (var i = 0; i < n; i++) Speed[i] = MathF.Min(top, MathF.Sqrt(aLat(i) / MathF.Max(MathF.Abs(Curvature[i]), 1e-4f)));
        // forward: what the car can reach (grade from the line's height)
        var fwd = new float[n];
        fwd[0] = Speed[0];
        for (var i = 0; i + 1 < n; i++)
        {
            var grade = (Map.Point[i + 1].Y - Map.Point[i].Y) / Ds[i];
            var v = fwd[i];
            var a = drive[Math.Clamp((int)v, 0, drive.Length - 1)] - G * grade;
            fwd[i + 1] = MathF.Min(Speed[i + 1], MathF.Sqrt(MathF.Max(v * v + 2 * a * Ds[i], 0)));
        }
        // backward: braking in time for every corner, less braking where the tyres are busy cornering
        for (var i = n - 2; i >= 0; i--)
        {
            var v = fwd[i + 1];
            var grade = (Map.Point[i + 1].Y - Map.Point[i].Y) / Ds[i];
            var lat = v * v * MathF.Abs(Curvature[i + 1]) / MathF.Max(aLat(i + 1), 0.1f);
            var a = aBrake * MathF.Sqrt(MathF.Max(1 - lat * lat, 0.15f)) + G * grade;
            fwd[i] = MathF.Min(fwd[i], MathF.Sqrt(v * v + 2 * MathF.Max(a, 1) * Ds[i]));
        }
        Array.Copy(fwd, Speed, n);
        Time[0] = 0;
        for (var i = 0; i + 1 < n; i++) Time[i + 1] = Time[i] + Ds[i] / MathF.Max((Speed[i] + Speed[i + 1]) / 2, 1);
    }

    /// <summary>Full-throttle acceleration (m/s²) of <paramref name="spec"/> on the flat per 1 m/s of speed, best gear, less drag/rolling resistance, traction-limited.</summary>
    public static float[] DriveAccel(CarSpec spec)
    {
        var a = new float[100];
        var traction = spec.Grip * G * (spec.DriveFront >= 1 ? spec.FrontWeight : spec.DriveFront > 0 ? 1 : 1 - spec.FrontWeight) * 0.8f;
        for (var v = 0; v < a.Length; v++)
        {
            float best = 0;
            foreach (var gear in spec.Gears)
            {
                var ratio = gear * spec.FinalDrive;
                var rpm = MathF.Max(v / spec.WheelRadius * ratio * 60 / MathF.Tau, spec.LaunchRpm);
                if (rpm > spec.RevLimit) continue;
                best = MathF.Max(best, Torque(spec, rpm) * ratio * spec.DrivetrainEfficiency / spec.WheelRadius / spec.Mass);
            }
            a[v] = MathF.Min(best, traction) - 0.5f * 1.225f * spec.DragArea * v * v / spec.Mass - G * spec.RollingResistance;
        }
        return a;
    }

    private static float Torque(CarSpec s, float rpm)
    {
        var r = s.TorqueRpm;
        if (rpm <= r[0]) return s.TorqueNm[0];
        for (var i = 1; i < r.Length; i++)
            if (rpm <= r[i]) return float.Lerp(s.TorqueNm[i - 1], s.TorqueNm[i], (rpm - r[i - 1]) / (r[i] - r[i - 1]));
        return s.TorqueNm[^1];
    }
}
