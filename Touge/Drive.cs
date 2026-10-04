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
    /// <remarks>
    ///     Some lines start off the drivable faces (IROHA point 0 lies ~30 m before the road, on W faces): the first
    ///     point from <paramref name="i" /> on with drivable ground under both axles is used instead, with a warning.
    /// </remarks>
    public void ResetTo(int i)
    {
        var want = i = Math.Clamp(i, 0, Line.Length - 2);
        GroundHit hit = default;
        while (i < Line.Length - 1 && !OnRoad(i, out hit)) i++;
        if (i == Line.Length - 1) throw new InvalidOperationException($"Kein befahrbarer Boden unter der Fahrlinie ab Punkt {want}");
        if (i != want) Console.WriteLine($"[Drive] Fahrlinie Punkt {want} ohne befahrbaren Boden, starte bei Punkt {i}");
        var d = Line[i + 1] - Line[i];
        Car.Reset(hit.Point, MathF.Atan2(d.X, d.Z));
        for (var t = 0; t < 60; t++) Car.Step(new VehicleInput(0, 0, 0, Handbrake: true), Ground, Dt); // handbrake, not brake: brake at standstill engages reverse
        Pilot.Nearest(Car.Position);
    }

    private bool OnRoad(int i, out GroundHit hit)
    {
        var axle = Vector3.Normalize(Line[i + 1] - Line[i]) * 3;
        return Ground.Raycast(Line[i] + Vector3.UnitY * 5, -Vector3.UnitY, 20, out hit)
               && Ground.Raycast(Line[i] - axle + Vector3.UnitY * 5, -Vector3.UnitY, 20, out _)
               && Ground.Raycast(Line[i] + axle + Vector3.UnitY * 5, -Vector3.UnitY, 20, out _);
    }

    public void ResetNearest() => ResetTo(Pilot.Nearest(Car.Position));

    /// <summary>--drift: every 7 s (from 4.5 s on) the pilot's input becomes a 2.5 s handbrake-flick drift, for effect tests.</summary>
    public bool ForceDrift { get; set; }

    /// <summary>Pilot input at simulation time <paramref name="time"/> (s), with the scripted drift of <see cref="ForceDrift"/>.</summary>
    public VehicleInput PilotInput(float time)
    {
        var input = Pilot.Drive(Car);
        var phase = time % 7;
        if (!ForceDrift || phase < 4.5f) return input;
        // full lock towards the pilot's steering, full throttle, handbrake for the first 0.5 s
        return new VehicleInput(1, 0, input.Steer < 0 ? -1 : 1, phase < 5.0f);
    }

    /// <summary>
    ///     --autodrive: the pilot drives for <paramref name="seconds"/> from the current position, one log line per
    ///     second and a summary, <paramref name="afterTick"/> after every tick. Returns false if the simulation blew
    ///     up (NaN or car fell off the world).
    /// </summary>
    public bool AutoDrive(float seconds, Action? afterTick = null)
    {
        var (s0, _) = Pilot.Track(Car.Position);
        int ticks = (int)(seconds / Dt), inside = 0, wallTicks = 0, wallTicksSecond = 0;
        float maxLat = 0;
        Console.WriteLine("   t  km/h  soll  gang    rpm  strecke_m  quer_m  wand_ticks  schräg_°");
        for (var n = 1; n <= ticks; n++)
        {
            Car.Step(PilotInput(n * Dt), Ground, Dt);
            afterTick?.Invoke();
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
                Console.WriteLine($"{n * Dt,4:F0} {Car.SpeedKmh,5:F0} {Pilot.TargetSpeed * 3.6f,5:F0} {Gear(Car),5} {Car.Rpm,6:F0} {s - s0,10:F0} {lat,7:F1} {wallTicksSecond,11} {Car.SlipAngle * 180 / MathF.PI,9:F1}");
                wallTicksSecond = 0;
            }
        }
        var (end, _) = Pilot.Track(Car.Position);
        Console.WriteLine($"[AutoDrive] {seconds:F0} s: {end - s0:F0} m entlang der Linie (von {Pilot.Length:F0} m), |quer| ≤ 6 m in {100f * inside / Math.Max(ticks, 1):F1} % der Ticks, max |quer| {maxLat:F1} m, Ticks mit Wandkontakt {wallTicks} ({100f * wallTicks / Math.Max(ticks, 1):F1} %)");
        return true;
    }

    public static string Gear(Vehicle car) => (car.Gear switch { < 0 => "R", 0 => "N", var g => g.ToString() }) + (car.AutomaticGearbox ? "A" : "M");
}
