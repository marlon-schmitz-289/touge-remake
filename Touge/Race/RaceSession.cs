using System.Numerics;
using Kansei.Physics;
using Touge.Ui;

namespace Touge.Race;

/// <summary>
///     Who drives a car of a <see cref="RaceSession"/>: the local player (<see cref="ManualDriver"/>), the AI
///     (<see cref="AiDriver"/>), later a replay or a network peer — anything that turns the race state into an input per tick.
/// </summary>
public interface ICarDriver
{
    VehicleInput Drive(RaceSession race, RaceCar car, float dt);

    /// <summary>The car was put back onto the line (stuck): forget any state about where it was.</summary>
    void Reset() { }
}

/// <summary>
///     A car simulated elsewhere (network peer, <see cref="Touge.Net.RemoteDriver"/>): <see cref="ICarDriver.Drive"/> sets the
///     vehicle's state itself, the session runs no physics, auto-run or respawn for it (contacts still push the local cars).
/// </summary>
public interface IPuppet : ICarDriver;

/// <summary>Input set from outside every tick (keyboard/pad, or the autopilot of a test run).</summary>
public sealed class ManualDriver : ICarDriver
{
    public VehicleInput Input { get; set; }
    public VehicleInput Drive(RaceSession race, RaceCar car, float dt) => Input;
}

/// <summary>The AI: a <see cref="RivalPilot"/> fed with every other car's place on the line and the race's rubber band.</summary>
public sealed class AiDriver(RivalPilot pilot) : ICarDriver
{
    private readonly Opponent[] _others = new Opponent[16];
    public RivalPilot Pilot => pilot;

    public VehicleInput Drive(RaceSession race, RaceCar car, float dt)
    {
        var n = 0;
        foreach (var c in race.Cars)
            if (c != car && n < _others.Length) _others[n++] = new Opponent(c.Along, c.Lateral, c.Speed);
        pilot.RubberBand = race.RubberBand(car);
        pilot.NoPass = race.Battle is { Rule: BattleRule.LeadChase } b && b.Time < b.StartGrace; // no pass off the launch

        return pilot.Drive(car.Vehicle, race.Ground, _others.AsSpan(0, n), dt);
    }

    public void Reset() => pilot.Reset();
}

/// <summary>One car of a race: its body, driver, own tracker on the course line and race state.</summary>
public sealed class RaceCar(string name, Vehicle vehicle, ICarDriver driver, Vector3[] line)
{
    public string Name { get; } = name;
    public Vehicle Vehicle { get; set; } = vehicle;
    public ICarDriver Driver { get; set; } = driver;
    /// <summary>Own tracking state on the course line (each car needs its own local search).</summary>
    public LinePilot Track { get; } = new(line);
    /// <summary>Metres along / lateral offset (+ left) on the course line, after the last tick.</summary>
    public float Along { get; internal set; }
    public float Lateral { get; internal set; }
    /// <summary>Forward speed (m/s).</summary>
    public float Speed => Vector3.Dot(Vehicle.Velocity, Vector3.Transform(Vector3.UnitZ, Vehicle.Orientation));
    /// <summary>Input of the last tick (brake lights, engine sound).</summary>
    public VehicleInput Input { get; internal set; }
    /// <summary>Pose before the last tick (render interpolation, swept contact).</summary>
    public Vector3 PrevPosition { get; internal set; }
    public Quaternion PrevOrientation { get; internal set; }
    /// <summary>Race time at the goal and the finishing place (1 = first), null/0 before.</summary>
    public float? FinishedAt { get; internal set; }
    public int Place { get; internal set; }
    public int WallTicks { get; internal set; }
    public int Respawns { get; internal set; }
    /// <summary>Times its place on the line jumped (put somewhere, or a circuit's next lap): trackers of the same car (HUD) look for it again.</summary>
    public int Jumps { get; internal set; }
    /// <summary>Free play: stopped at the course end and handed back to its driver (<see cref="RaceSession.AtCourseEnd"/> left it there).</summary>
    public bool Parked { get; internal set; }
    /// <summary>Free play: times it reached the course end (a circuit: its two laps).</summary>
    public int CourseEnds { get; internal set; }
    /// <summary>
    ///     Free play online: seconds this car still passes through the others (a player driving in lands on the grid, maybe on a
    ///     car standing there); past them it stays a ghost until it is <see cref="RaceSession.ClearGap"/> m from every car.
    /// </summary>
    public float Protect { get; set; }
    internal float StuckFor;
    internal Coaster? Coast;
}

/// <summary>
///     After the finish (or once the battle is decided) the game takes the car like an arcade racer: steers along
///     <paramref name="pilot"/>, brakes at one constant rate (≥ <see cref="Drive.CoastDecel"/>) to a stop at
///     <paramref name="stopAt"/> m along it, holds it there with the handbrake (as <see cref="Drive.Coast"/>).
/// </summary>
internal sealed class Coaster(LinePilot pilot, float stopAt)
{
    private float _decel;

    public VehicleInput Drive(Vehicle car)
    {
        var steer = pilot.Drive(car).Steer;
        var v = car.Velocity.Length();
        var d = MathF.Max(stopAt - pilot.Track(car.Position).Along, 0);
        if (_decel == 0) _decel = MathF.Max(Touge.Drive.CoastDecel, v * v / (2 * MathF.Max(d, 0.5f)));
        car.LimitSpeed(MathF.Sqrt(2 * _decel * d));
        return v < 1 ? new VehicleInput(0, 0, steer, Handbrake: true) : new VehicleInput(0, 0.6f, steer);
    }
}

/// <summary>
///     A race of N cars on one course line: per tick every driver's input (or the auto-run once the car finished or the
///     battle is decided), physics, swept car-to-car contacts (<see cref="CarCollision"/>), each car's place on the line,
///     stuck AI cars put back, and the <see cref="Battle"/> rules on cars 0 (player) and 1 (rival). Grid side by side or
///     lead/follow (<see cref="Grid"/>). Contacts are counted per touch, with the hardest closing speed.
/// </summary>
public sealed class RaceSession
{
    /// <summary>Gap between the finishers' stopping points on the run-out.</summary>
    public const float StopSpacing = 8;

    /// <summary>Two cars must have been apart this long (s) before a contact counts as a new touch.</summary>
    public const float TouchGap = 0.25f;

    private readonly float _goal;
    private int _finished;
    private float[] _lastTouch = [];

    /// <param name="line">Course line from the spawn to the goal (<see cref="Drive.Line"/>).</param>
    /// <param name="runOut">Goal → end barrier (<see cref="Drive.RunOutLine"/>, empty on circuits).</param>
    public RaceSession(IGround ground, Vector3[] line, Vector3[] runOut, Battle? battle = null)
    {
        (Ground, Line, RunOut, Battle) = (ground, line, runOut, battle);
        _goal = new LinePilot(line).Length - LapTimer.Gate;
    }

    public IGround Ground { get; }
    public Vector3[] Line { get; }
    public Vector3[] RunOut { get; }
    public Battle? Battle { get; }
    public List<RaceCar> Cars { get; } = [];
    /// <summary>Metres along the line where a car has finished.</summary>
    public float Goal => _goal;
    public float Time { get; private set; }
    /// <summary>The AI eases/pushes against car 0 (a human is driving it).</summary>
    public bool RubberBanding { get; set; }
    /// <summary>Touches so far, ticks in contact, hardest closing speed (m/s), and this tick's contact (null: none).</summary>
    public int Contacts { get; private set; }
    public int ContactTicks { get; private set; }
    public float MaxImpact { get; private set; }
    public CarContact? LastContact { get; private set; }
    /// <summary>Decided from outside (a multiplayer referee): every car coasts to a stop.</summary>
    public bool Ended { get; set; }
    /// <summary>The battle is decided (or, without one, everybody finished), or <see cref="Ended"/>.</summary>
    public bool Over => Ended || Battle is { Outcome: not BattleOutcome.None } || (Cars.Count > 0 && _finished == Cars.Count);

    /// <summary>
    ///     Free play (null: a race): nobody finishes. Past the goal of a point-to-point course a car coasts to a stop on the
    ///     run-out, then this decides what happens to it (true: it was put somewhere, e.g. <see cref="BackToStart"/>; false: it
    ///     stays parked there and its driver drives on). On a circuit the car just goes round (its tracking back to the first lap).
    /// </summary>
    public Func<RaceCar, bool>? AtCourseEnd { get; set; }
    /// <summary>Cars pass through each other (free play's ghost option): no contacts.</summary>
    public bool Ghost { get; set; }
    /// <summary>Distance (m, centre to centre) at which a protected car (<see cref="RaceCar.Protect"/>) no longer overlaps another.</summary>
    public const float ClearGap = 5;

    public RaceCar Add(string name, Vehicle vehicle, ICarDriver driver)
    {
        var car = new RaceCar(name, vehicle, driver, Line);
        Cars.Add(car);
        ResetTouches();
        return car;
    }

    /// <summary>A car leaves the session (free play online: its player left).</summary>
    public void Remove(RaceCar car)
    {
        if (Cars.Remove(car)) ResetTouches();
    }

    private void ResetTouches()
    {
        _lastTouch = new float[Cars.Count * Cars.Count];
        Array.Fill(_lastTouch, float.NegativeInfinity);
    }

    /// <summary>
    ///     Free play's default at the course end: back on the line at the spawn, or the first spot after it (8 m steps) with no
    ///     other car within 7 m, as at a fresh start; false if no spot was free.
    /// </summary>
    public bool BackToStart(RaceCar car)
    {
        for (var s = 0f; s < 120; s += 8)
            if (!Cars.Any(o => o != car && Vector3.Distance(o.Vehicle.Position, car.Track.PointAt(s)) < 7) && Place(car, s, 0))
                return true;
        return false;
    }

    /// <summary>
    ///     Puts <paramref name="car"/> at rest <paramref name="along"/> m along the line, <paramref name="lateral"/> m to its
    ///     left, facing along it; false where there is no ground under it, the body would touch a wall, or (beside the line) it
    ///     stands outside the full-grip road the AI considers drivable there (<see cref="CourseMap.Room"/>; a gutter is not).
    /// </summary>
    public bool Place(RaceCar car, float along, float lateral)
    {
        var t = car.Track;
        t.Nearest(t.PointAt(along), along);
        var p = t.PointAt(along) + t.LeftAt(along) * lateral;
        var dir = t.PointAt(along + 3) - t.PointAt(along - 3);
        // road under the spot at the line's height (beside the road the ray may reach a slope or valley far below)
        if (!Ground.Raycast(p + Vector3.UnitY * 5, -Vector3.UnitY, 20, out var hit) || MathF.Abs(hit.Point.Y - p.Y) > 1.5f) return false;
        var v = car.Vehicle;
        if (lateral != 0)
        {
            var (l, r) = CourseMap.Of(Line, Ground, v.SurfaceGrip).Room(along);
            if (lateral < RivalPilot.EdgeMargin - r || lateral > l - RivalPilot.EdgeMargin) return false;
        }
        v.Reset(hit.Point, MathF.Atan2(dir.X, dir.Z));
        Span<Vector3> probes = stackalloc Vector3[4];
        Span<WallContact> contacts = stackalloc WallContact[8];
        v.WallProbes(probes);
        if (Ground.CollideWalls(probes, Vehicle.ProbeRadius, contacts) > 0) return false;
        for (var i = 0; i < 60; i++) v.Step(new VehicleInput(0, 0, 0, Handbrake: true), Ground, 1f / 120); // settle on the springs
        if (v.Position.Y < hit.Point.Y || v.Velocity.Y < -1) return false; // the wheels find no road there (MYOUGI uphill, left of the line): it sinks
        t.Nearest(v.Position, along); // circuits: the line passes the grid twice
        (car.Along, car.Lateral) = t.Track(v.Position);
        (car.PrevPosition, car.PrevOrientation, car.StuckFor, car.Coast, car.Parked) = (v.Position, v.Orientation, 0, null, false);
        car.Jumps++;
        car.Driver.Reset();
        return true;
    }

    /// <summary>
    ///     Start positions from <paramref name="at"/> m along the line (the spawn): <see cref="BattleRule.Race"/> side by side,
    ///     car 0 on the left (1.6 m each side of the line; narrower or staggered where the road is not wide enough),
    ///     <see cref="BattleRule.LeadChase"/> one behind the other, the leader <paramref name="leader"/> 10 m ahead.
    /// </summary>
    public void Grid(BattleRule rule, float at, int leader = 1)
    {
        if (rule == BattleRule.LeadChase)
        {
            if (!Place(Cars[leader], at + 10, 0)) Place(Cars[leader], at + 14, 0);
            if (!Place(Cars[1 - leader], at, 0)) Place(Cars[1 - leader], at + 2, 0);
            return;
        }
        // the racing line may run near one edge at the start: the pair is shifted across the road until both fit
        foreach (var half in new[] { 1.6f, 1.3f, RivalPilot.PassGap / 2 })
        foreach (var centre in new[] { 0f, 0.5f, -0.5f, 1f, -1f, 1.5f, -1.5f, 2f, -2f })
            if (Place(Cars[0], at, centre + half) && Place(Cars[1], at, centre - half))
            {
                Console.WriteLine($"[Race] Startaufstellung nebeneinander bei {at:F0} m: {centre + half:+0.0;-0.0} / {centre - half:+0.0;-0.0} m neben der Linie");
                return;
            }
        Place(Cars[1], at + 8, 0); // staggered, the rival in front
        Place(Cars[0], at, 0);
        Console.WriteLine($"[Race] Startaufstellung versetzt: Straße bei {at:F0} m zu schmal für zwei nebeneinander");
    }

    /// <summary>
    ///     Rubber band for AI car <paramref name="ai"/> against car 0 (+ = the AI is behind): in a battle from the time gap, 0
    ///     within 1 s, full at 4 s, and off in a lead/chase and over the last 10 % of the course (the finish is fair); without
    ///     a battle by distance, 0 within 30 m, full at 150 m. It only moves the AI's planned grip (<see cref="RivalPilot.RubberBand"/>).
    /// </summary>
    public float RubberBand(RaceCar ai)
    {
        if (!RubberBanding || ai == Cars[0]) return 0;
        if (Battle is { } b)
        {
            if (b.Rule == BattleRule.LeadChase || MathF.Max(Cars[0].Along, ai.Along) > 0.9f * _goal) return 0;
            return Math.Clamp((MathF.Abs(b.Gap) - 1) / 3, 0, 1) * MathF.Sign(b.Gap);
        }
        var d = Cars[0].Along - ai.Along;
        return Math.Clamp((MathF.Abs(d) - 30) / 120, 0, 1) * MathF.Sign(d);
    }

    /// <summary>The auto-run for <paramref name="car"/>: on its side of the road (its lateral now), finishers one behind the other on the run-out.</summary>
    private Coaster MakeCoast(RaceCar car)
    {
        var side = Math.Clamp(car.Lateral, -2, 2);
        if ((car.FinishedAt != null || AtCourseEnd != null) && RunOut.Length > 1)
        {
            var runOut = new LinePilot(RunOut) { Offset = side };
            return new Coaster(runOut, runOut.Length - Touge.Drive.CoastGap - StopSpacing * Math.Max(car.Place - 1, 0));
        }
        var v = car.Speed;
        car.Track.Offset = side;
        return new Coaster(car.Track, car.Along + MathF.Max(v * v / (2 * Touge.Drive.CoastDecel), 3));
    }

    /// <summary>One physics tick of every car.</summary>
    public void Tick(float dt)
    {
        foreach (var car in Cars)
        {
            var v = car.Vehicle;
            (car.PrevPosition, car.PrevOrientation) = (v.Position, v.Orientation);
            if (car.Driver is IPuppet)
            {
                car.Input = car.Driver.Drive(this, car, dt);
                continue;
            }
            if (AtCourseEnd != null) FreeEnd(car);
            else if (car.Coast == null && (car.FinishedAt != null || Over)) car.Coast = MakeCoast(car);
            var input = car.Coast?.Drive(v) ?? car.Driver.Drive(this, car, dt);
            if (car.Coast != null) KeepBehind(car);
            car.Input = input;
            v.Step(input, Ground, dt);
        }
        LastContact = null;
        for (var i = 0; i < Cars.Count && !Ghost; i++)
        for (var j = i + 1; j < Cars.Count; j++)
        {
            RaceCar a = Cars[i], b = Cars[j];
            if (a.Protect > 0 || b.Protect > 0) continue;
            var c = CarCollision.Resolve(a.Vehicle, b.Vehicle, a.PrevPosition, a.PrevOrientation, b.PrevPosition, b.PrevOrientation);
            if (c is { } contact)
            {
                ref var last = ref _lastTouch[i * Cars.Count + j];
                if (Time - last > TouchGap) Contacts++; // a rub (separate, touch again next tick) is one touch
                last = Time;
                ContactTicks++;
                MaxImpact = MathF.Max(MaxImpact, contact.ImpactSpeed);
                if (LastContact is not { } hardest || contact.ImpactSpeed > hardest.ImpactSpeed) LastContact = contact;
            }
        }
        Time += dt;
        foreach (var car in Cars)
            if (car.Protect > 0 && (car.Protect -= dt) <= 0 && Cars.Any(o => o != car && Vector3.Distance(o.Vehicle.Position, car.Vehicle.Position) < ClearGap))
                car.Protect = dt; // still on top of someone: ghost until apart
        foreach (var car in Cars)
        {
            var v = car.Vehicle;
            (car.Along, car.Lateral) = car.Track.Track(v.Position);
            if (v.WallContacts > 0) car.WallTicks++;
            if (car.FinishedAt == null && car.Along >= _goal && AtCourseEnd == null) (car.FinishedAt, car.Place) = (Time, ++_finished);
            // an AI car stuck (standing, far off the line or on its roof) for 3 s is put back where it was
            var upright = Vector3.Transform(Vector3.UnitY, v.Orientation).Y > 0.3f;
            var stuck = car.Driver is AiDriver && car.FinishedAt == null && !Over && car.Coast == null && !car.Parked
                        && (v.Velocity.Length() < 1 || MathF.Abs(car.Lateral) > 12 || !upright);
            car.StuckFor = stuck ? car.StuckFor + dt : 0;
            if (car.StuckFor > 3) Respawn(car);
        }
        if (Battle != null && Cars.Count >= 2) Battle.Update(dt, Cars[0].Along, Cars[1].Along);
    }

    /// <summary>
    ///     Free play at the course end (<see cref="AtCourseEnd"/>): a circuit's car goes on into its next lap; past the goal of a
    ///     point-to-point course the auto-run takes it to a stop on the run-out, then <see cref="AtCourseEnd"/> decides. A parked
    ///     car is its driver's again until it is back on the course (it may turn and drive back up).
    /// </summary>
    private void FreeEnd(RaceCar car)
    {
        if (car.Along < _goal)
        {
            if (car.Along < _goal - 10) car.Parked = false;
            return;
        }
        var v = car.Vehicle;
        if (RunOut.Length <= 1)
        {
            car.Track.Nearest(v.Position, 0); // a circuit's line runs two laps: back onto the first
            (car.Along, car.Lateral) = car.Track.Track(v.Position);
            (car.Jumps, car.CourseEnds) = (car.Jumps + 1, car.CourseEnds + 1);
            return;
        }
        if (car.Parked) return;
        car.Coast ??= MakeCoast(car);
        if (v.Velocity.Length() > 0.5f) return;
        (car.Coast, car.CourseEnds) = (null, car.CourseEnds + 1);
        if (!AtCourseEnd!(car)) car.Parked = true;
    }

    /// <summary>
    ///     The auto-run never runs into a car stopping ahead (a finisher braking on a short run-out): speed held to where a
    ///     braking of 8 m/s² stops it 6 m behind that car.
    /// </summary>
    private void KeepBehind(RaceCar car)
    {
        var v = car.Vehicle;
        var fwd = Vector3.Transform(Vector3.UnitZ, v.Orientation);
        foreach (var o in Cars)
        {
            if (o == car) continue;
            var rel = o.Vehicle.Position - v.Position;
            var ahead = Vector3.Dot(rel, fwd);
            if (ahead <= 0 || ahead > 60 || (rel - fwd * ahead).Length() > 2.5f) continue;
            var vo = MathF.Max(Vector3.Dot(o.Vehicle.Velocity, fwd), 0);
            v.LimitSpeed(MathF.Sqrt(vo * vo + 2 * 8 * MathF.Max(ahead - 6, 0)));
        }
    }

    /// <summary>Back onto the line where the car was, a little further back if that spot is taken (another car) or walled.</summary>
    private void Respawn(RaceCar car)
    {
        var at = car.Along;
        for (var back = 0f; back < 60; back += 6)
        {
            var s = at - back;
            if (Cars.Any(o => o != car && MathF.Abs(o.Along - s) < 7)) continue;
            if (Place(car, s, 0))
            {
                car.Respawns++;
                Console.WriteLine($"[Race] {car.Name} festgefahren, zurück auf die Linie bei {s:F0} m");
                return;
            }
        }
    }
}
