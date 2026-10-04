using System.Numerics;
using Kansei.Physics;
using Touge.Formats;

namespace Touge;

/// <summary>
///     The drivable part of a course: collision as ground, driving line + pilot, one car on it (AE86 by default).
///     <see cref="Reverse" /> = uphill/reverse direction like the original's flag 0x1D6 (FORMATS.md): CRS_COLI_&lt;COURSE&gt;_1
///     (falls back to _0 for the circuits MYOUGI0/USUI0, as the game does) and CRS_DRV_&lt;COURSE&gt;_O.
/// </summary>
public sealed class Drive
{
    public const float Dt = 1f / 120;

    private readonly string _course;
    private float[] _grip = [];

    public TriangleGround Ground { get; private set; } = null!;
    public Vector3[] Line { get; private set; } = null!;
    public LinePilot Pilot { get; private set; } = null!;
    public bool Reverse { get; private set; }
    public Vehicle Car { get; private set; }

    public Drive(Iso9660 iso, string courseTime, bool reverse = false, CarSpec? spec = null)
    {
        _course = courseTime[..courseTime.LastIndexOf('_')];
        Car = new Vehicle(spec ?? CarSpec.AE86);
        SetDirection(iso, reverse);
        Car.SurfaceGrip = id => (uint)id < (uint)_grip.Length ? _grip[id] : 1; // reads _grip live, so direction changes apply
    }

    /// <summary>Loads ground and driving line of a direction; the car stays where it is (call <see cref="ResetNearest" />).</summary>
    public void SetDirection(Iso9660 iso, bool reverse)
    {
        var (ground, collision) = CourseGround.Load(iso, _course, reverse ? 1 : 0);
        (Ground, Reverse, _grip) = (ground, reverse, Array.ConvertAll(collision.Materials, Grip));
        Line = CourseLoader.ReadDrivingLine(iso, _course, reverse);
        Pilot = new LinePilot(Line);
    }

    /// <summary>Swaps in a car with <paramref name="spec"/>, at rest on the driving line where the old one was.</summary>
    public void ChangeCar(CarSpec spec)
    {
        var old = Car;
        Car = new Vehicle(spec) { SurfaceGrip = old.SurfaceGrip, AutomaticGearbox = old.AutomaticGearbox };
        ResetTo(Pilot.Nearest(old.Position));
    }

    /// <summary>Grip factor per collision material name (R16road, R32gutter, R25r_grass, R21r_bump, …). Guessed, not from game data.</summary>
    public static float Grip(string material) =>
        material.Contains("grass") ? 0.6f
        : material.Contains("gutter") ? 0.85f
        : material.Contains("bump") || material.Contains("redline") ? 0.95f
        : 1f;

    /// <summary>Car at rest on driving-line point <paramref name="i"/> (or the next clear one, <see cref="LinePilot.Spawn" />), facing along the line.</summary>
    /// <remarks>Some lines start off the drivable faces (IROHA point 0 lies ~30 m before the road, on W faces).</remarks>
    public void ResetTo(int i)
    {
        var at = Pilot.Spawn(Car, Ground, i);
        if (at < 0) throw new InvalidOperationException($"Kein freier befahrbarer Boden an der Fahrlinie ab Punkt {i}");
        if (at != i) Console.WriteLine($"[Drive] Fahrlinie Punkt {i} ohne befahrbaren Boden oder an der Wand, starte bei Punkt {at}");
    }

    /// <summary>
    ///     R: back onto the driving line where the car is, facing the current direction. Local tracking keeps the place
    ///     along the course (no jump to the other leg of a hairpin); far off the line (beyond a wall) the global nearest point.
    /// </summary>
    public void ResetNearest()
    {
        var (_, lateral) = Pilot.Track(Car.Position);
        ResetTo(MathF.Abs(lateral) < 15 ? Pilot.Segment : Pilot.Nearest(Car.Position));
    }

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
