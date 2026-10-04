using System.Numerics;
using Kansei.Physics;
using Touge.Formats;
using Touge.Ui;

namespace Touge;

/// <summary>
///     The drivable part of a course: collision as ground, driving line + pilot, one car on it (AE86 by default).
///     <see cref="Reverse" /> = uphill/reverse direction like the original's flag 0x1D6 (FORMATS.md): CRS_COLI_&lt;COURSE&gt;_1
///     (falls back to _0 for the circuits MYOUGI0/USUI0, as the game does) and CRS_DRV_&lt;COURSE&gt;_O.
/// </summary>
public sealed class Drive
{
    public const float Dt = 1f / 120;

    private readonly string _courseTime;
    private float[] _grip = [];
    private LinePilot? _runOut;
    private float _coastDecel;

    public TriangleGround Ground { get; private set; } = null!;
    /// <summary>Driving line from the spawn to the goal (<see cref="CourseEnd"/>).</summary>
    public Vector3[] Line { get; private set; } = null!;
    /// <summary>Start line, m along <see cref="Line"/>: the car spawns behind it (0 on circuits).</summary>
    public float Start { get; private set; }
    public LinePilot Pilot { get; private set; } = null!;
    public bool Reverse { get; private set; }
    public Vehicle Car { get; private set; }
    /// <summary>Wall across the road at the end of the run-out behind the goal (<see cref="CourseEnd"/>), null on circuits.</summary>
    public TriangleGround.WallSegment? EndBarrier { get; private set; }
    /// <summary>Line from the goal to the end barrier (empty on circuits): where a finished car coasts to a stop.</summary>
    public Vector3[] RunOutLine { get; private set; } = [];

    public Drive(Iso9660 iso, string courseTime, bool reverse = false, CarSpec? spec = null)
    {
        _courseTime = courseTime;
        Car = new Vehicle(spec ?? CarSpec.AE86);
        SetDirection(iso, reverse);
        Car.SurfaceGrip = id => (uint)id < (uint)_grip.Length ? _grip[id] : 1; // reads _grip live, so direction changes apply
    }

    /// <summary>
    ///     Loads ground and driving line of a direction, the line running from the spawn to the goal and both course ends walled off
    ///     (<see cref="CourseEnd"/>); the car stays where it is (call <see cref="ResetNearest" />).
    /// </summary>
    public void SetDirection(Iso9660 iso, bool reverse)
    {
        var (ground, collision) = CourseGround.Load(iso, _courseTime[.._courseTime.LastIndexOf('_')], reverse ? 1 : 0);
        var ends = CourseEnd.Close(iso, _courseTime, reverse, ground);
        (Ground, Reverse, _grip, Line, Start, EndBarrier) = (ground, reverse, Array.ConvertAll(collision.Materials, Grip), ends.Line, ends.Start, ends.Barrier);
        Pilot = new LinePilot(Line);
        _runOut = ends.RunOut.Length > 1 ? new LinePilot(ends.RunOut) : null;
        RunOutLine = ends.RunOut.Length > 1 ? ends.RunOut : [];
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
        _coastDecel = 0;
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

    /// <summary>Deceleration (m/s²) the auto-run after the finish brakes with at least, and the gap (m) between car centre and end barrier it stops at.</summary>
    public const float CoastDecel = 6, CoastGap = 4.5f;

    /// <summary>
    ///     After the finish, like an arcade racer's auto-run: no throttle, brakes, steers along the run-out and slows at one
    ///     constant rate (fixed at the first call: <see cref="CoastDecel"/>, or more if the barrier is closer) to a stop
    ///     <see cref="CoastGap"/> m before <see cref="EndBarrier"/>; at a standstill the handbrake holds it (the brake would
    ///     engage reverse). The tyres brake what they can, <see cref="Vehicle.LimitSpeed"/> the rest; harder where the pilot
    ///     brakes for a corner of the run-out (SHOMARU downhill: a hairpin 50 m past the goal).
    /// </summary>
    public VehicleInput Coast()
    {
        var pilot = (_runOut ?? Pilot).Drive(Car);
        var v = Car.Velocity.Length();
        if (EndBarrier != null && _runOut != null)
        {
            var d = MathF.Max(_runOut.Length - _runOut.Track(Car.Position).Along - CoastGap, 0); // along the run-out: it may bend
            if (_coastDecel == 0) _coastDecel = MathF.Max(CoastDecel, v * v / (2 * MathF.Max(d, 0.5f)));
            Car.LimitSpeed(MathF.Sqrt(2 * _coastDecel * d)); // also when parked: no creeping downhill into the barrier
        }
        return v < 1 ? new VehicleInput(0, 0, pilot.Steer, Handbrake: true) : new VehicleInput(0, MathF.Max(0.6f, pilot.Brake), pilot.Steer);
    }

    /// <summary>--ram: after the goal <see cref="AutoDrive"/> keeps full throttle along the run-out into the end barrier instead of <see cref="Coast"/>.</summary>
    public bool Ram { get; set; }

    /// <summary>
    ///     --autodrive: the pilot drives for <paramref name="seconds"/> from the current position, one log line per
    ///     second and a summary, <paramref name="afterTick"/> after every tick. Returns false if the simulation blew
    ///     up (NaN or car fell off the world).
    /// </summary>
    public bool AutoDrive(float seconds, Action? afterTick = null)
    {
        var (s0, _) = Pilot.Track(Car.Position);
        int ticks = (int)(seconds / Dt), before = 0, inside = 0, wallTicks = 0, wallTicksSecond = 0, goalWall = 0;
        float maxLat = 0, goalKmh = 0, goalY = 0, past = 0, gap = float.MaxValue, minY = float.MaxValue;
        float? goalAt = null;
        Console.WriteLine("   t  km/h  soll  gang    rpm  strecke_m  quer_m  wand_ticks  schräg_°");
        for (var n = 1; n <= ticks; n++)
        {
            // past the goal: the auto-run (Coast), or with --ram full throttle along the run-out into the end barrier
            var input = goalAt == null ? PilotInput(n * Dt) : Ram ? (_runOut ?? Pilot).Drive(Car) with { Throttle = 1, Brake = 0 } : Coast();
            Car.Step(input, Ground, Dt);
            afterTick?.Invoke();
            var (s, lat) = Pilot.Track(Car.Position);
            if (!float.IsFinite(Car.Position.X + Car.Position.Y + Car.Position.Z + Car.Velocity.X) || Car.Position.Y < -500)
            {
                Console.WriteLine($"[AutoDrive] Abbruch nach {n * Dt:F2} s: Zustand ungültig, pos {Car.Position}");
                return false;
            }
            if (goalAt == null && s >= Pilot.Length - LapTimer.Gate) (goalAt, goalKmh, goalY) = (n * Dt, Car.SpeedKmh, Car.Position.Y);
            if (goalAt != null)
            {
                past = MathF.Max(past, _runOut?.Track(Car.Position).Along ?? Vector2.Distance(new(Car.Position.X, Car.Position.Z), new(Line[^1].X, Line[^1].Z)));
                if (EndBarrier != null && _runOut != null) gap = MathF.Min(gap, _runOut.Length - _runOut.Track(Car.Position).Along - Car.Spec.Length / 2);
                minY = MathF.Min(minY, Car.Position.Y);
                if (Car.WallContacts > 0) goalWall++;
            }
            else
            {
                before++;
                if (MathF.Abs(lat) <= 6) inside++;
                maxLat = MathF.Max(maxLat, MathF.Abs(lat));
            }
            if (Car.WallContacts > 0) (wallTicks, wallTicksSecond) = (wallTicks + 1, wallTicksSecond + 1);
            if (n % 120 == 0)
            {
                Console.WriteLine($"{n * Dt,4:F0} {Car.SpeedKmh,5:F0} {Pilot.TargetSpeed * 3.6f,5:F0} {Gear(Car),5} {Car.Rpm,6:F0} {s - s0,10:F0} {lat,7:F1} {wallTicksSecond,11} {Car.SlipAngle * 180 / MathF.PI,9:F1}");
                wallTicksSecond = 0;
            }
        }
        var (end, _) = Pilot.Track(Car.Position);
        Console.WriteLine($"[AutoDrive] {seconds:F0} s: {end - s0:F0} m entlang der Linie (von {Pilot.Length:F0} m), |quer| ≤ 6 m in {100f * inside / Math.Max(before, 1):F1} % der Ticks bis zum Ziel, max |quer| {maxLat:F1} m, Ticks mit Wandkontakt {wallTicks} ({100f * wallTicks / Math.Max(ticks, 1):F1} %)");
        if (goalAt != null)
            Console.WriteLine($"[AutoDrive] Ziel nach {goalAt:F1} s mit {goalKmh:F0} km/h, danach {(Ram ? "Vollgas weiter" : "Auslauf")}: bis {past:F1} m hinter dem Ziel " +
                              $"(Endsperre {(_runOut == null ? "keine" : $"bei {_runOut.Length:F1} m")}), Front min {(gap < float.MaxValue ? $"{gap:F2} m vor der Sperre" : "-")}, " +
                              $"{goalWall} Ticks Wandkontakt, Höhe min {minY - goalY:+0.0;-0.0} m, jetzt {Car.SpeedKmh:F0} km/h" +
                              (Ram ? "" : $", Verzögerung {_coastDecel:F1} m/s²"));
        return true;
    }

    public static string Gear(Vehicle car) => (car.Gear switch { < 0 => "R", 0 => "N", var g => g.ToString() }) + (car.AutomaticGearbox ? "A" : "M");
}
