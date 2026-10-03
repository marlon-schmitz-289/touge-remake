using System.Numerics;
using Kansei.Physics;
using Touge.Formats;

namespace Touge;

/// <summary>The drivable part of a course: collision as ground, driving line + pilot, one AE86 on it.</summary>
public sealed class Drive
{
    public const float Dt = 1f / 120;

    public TriangleGround Ground { get; }
    public Vector3[] Line { get; }
    public LinePilot Pilot { get; }
    public Vehicle Car { get; } = new(CarSpec.AE86);

    public Drive(Iso9660 iso, string courseTime)
    {
        var course = courseTime[..courseTime.LastIndexOf('_')];
        var (ground, collision) = CourseGround.Load(iso, course);
        Ground = ground;
        Line = CourseLoader.ReadDrivingLine(iso, course);
        Pilot = new LinePilot(Line);
        var grip = Array.ConvertAll(collision.Materials, Grip);
        Car.SurfaceGrip = id => (uint)id < (uint)grip.Length ? grip[id] : 1;
    }

    /// <summary>Grip factor per collision material name (R16road, R32gutter, R25r_grass, R21r_bump, …). Guessed, not from game data.</summary>
    public static float Grip(string material) =>
        material.Contains("grass") ? 0.6f
        : material.Contains("gutter") ? 0.85f
        : material.Contains("bump") || material.Contains("redline") ? 0.95f
        : 1f;

    /// <summary>Car at rest on driving-line point <paramref name="i"/>, facing along the line, settled on its springs.</summary>
    public void ResetTo(int i)
    {
        i = Math.Clamp(i, 0, Line.Length - 2);
        Vector3 a = Line[i], d = Line[i + 1] - a;
        var ground = Ground.Raycast(a + Vector3.UnitY * 5, -Vector3.UnitY, 20, out var hit) ? hit.Point : a;
        Car.Reset(ground, MathF.Atan2(d.X, d.Z));
        for (var t = 0; t < 60; t++) Car.Step(new VehicleInput(0, 1, 0), Ground, Dt);
        Pilot.Nearest(Car.Position);
    }

    public void ResetNearest() => ResetTo(Pilot.Nearest(Car.Position));

    /// <summary>
    ///     --autodrive: the pilot drives for <paramref name="seconds"/> from the current position, one log line per
    ///     second and a summary. Returns false if the simulation blew up (NaN or car fell off the world).
    /// </summary>
    public bool AutoDrive(float seconds)
    {
        var (s0, _) = Pilot.Track(Car.Position);
        int ticks = (int)(seconds / Dt), inside = 0, wallTicks = 0, wallTicksSecond = 0;
        float maxLat = 0;
        Console.WriteLine("   t  km/h  gang    rpm  strecke_m  quer_m  wand_ticks  schräg_°");
        for (var n = 1; n <= ticks; n++)
        {
            Car.Step(Pilot.Drive(Car), Ground, Dt);
            var (s, lat) = Pilot.Track(Car.Position);
            if (!float.IsFinite(Car.Position.X + Car.Position.Y + Car.Position.Z + Car.Velocity.X) || Car.Position.Y < -500)
            {
                Console.WriteLine($"[AutoDrive] Abbruch nach {n * Dt:F2} s: Zustand ungültig, pos {Car.Position}");
                return false;
            }
            if (MathF.Abs(lat) <= 6) inside++;
            maxLat = MathF.Max(maxLat, MathF.Abs(lat));
            if (Car.WallContacts > 0) (wallTicks, wallTicksSecond) = (wallTicks + 1, wallTicksSecond + 1);
            if (n % 120 == 0)
            {
                Console.WriteLine($"{n * Dt,4:F0} {Car.SpeedKmh,5:F0} {Gear(Car),5} {Car.Rpm,6:F0} {s - s0,10:F0} {lat,7:F1} {wallTicksSecond,11} {Car.SlipAngle * 180 / MathF.PI,9:F1}");
                wallTicksSecond = 0;
            }
        }
        var (end, _) = Pilot.Track(Car.Position);
        Console.WriteLine($"[AutoDrive] {seconds:F0} s: {end - s0:F0} m entlang der Linie (von {Pilot.Length:F0} m), |quer| ≤ 6 m in {100f * inside / Math.Max(ticks, 1):F1} % der Ticks, max |quer| {maxLat:F1} m, Ticks mit Wandkontakt {wallTicks} ({100f * wallTicks / Math.Max(ticks, 1):F1} %)");
        return true;
    }

    public static string Gear(Vehicle car) => (car.Gear switch { < 0 => "R", 0 => "N", var g => g.ToString() }) + (car.AutomaticGearbox ? "A" : "M");
}
