using System.Net;
using System.Numerics;
using Kansei.Physics;
using Touge.Net;
using Touge.Race;
using Touge.Ui;

namespace Touge.Tests;

public class FreePlayTests
{
    private const float Dt = 1f / 120;

    private sealed class Flat : IGround
    {
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out GroundHit hit)
        {
            hit = default;
            if (direction.Y >= 0 || origin.Y < 0) return false;
            var t = -origin.Y / direction.Y;
            if (t > maxDistance) return false;
            hit = new GroundHit(origin + direction * t, Vector3.UnitY, t, 0);
            return true;
        }

        public int CollideWalls(ReadOnlySpan<Vector3> probes, float radius, Span<WallContact> contacts) => 0;
    }

    /// <summary>A straight course: 400 m of line to the goal, 60 m of run-out behind it.</summary>
    private static RaceSession Straight(Func<RaceCar, bool>? atEnd)
    {
        Vector3[] line = [.. Enumerable.Range(0, 81).Select(i => new Vector3(0, 0, i * 5f))];
        Vector3[] runOut = [.. Enumerable.Range(0, 13).Select(i => new Vector3(0, 0, 400 + i * 5f))];
        return new RaceSession(new Flat(), line, runOut) { AtCourseEnd = atEnd };
    }

    /// <summary>
    ///     The course end: past the goal nobody finishes, the auto-run brings the car to a stop on the run-out (before its end),
    ///     only then the free play decides — once; back at the start the car is tracked there again and drives on.
    /// </summary>
    [Fact]
    public void CourseEnd_CoastsToAStopThenBackToStart()
    {
        var calls = new List<(float Along, float Speed, float Z)>();
        RaceSession race = null!;
        race = Straight(car =>
        {
            calls.Add((car.Along, car.Vehicle.Velocity.Length(), car.Vehicle.Position.Z));
            return race.BackToStart(car);
        });
        var me = new ManualDriver { Input = new VehicleInput(1, 0, 0) };
        var car = race.Add("YOU", new Vehicle(CarSpec.AE86), me);
        Assert.True(race.Place(car, 250, 0));
        var jumps = car.Jumps;
        float maxZ = 0;
        for (var t = 0; t < 120 * 40 && calls.Count == 0; t++)
        {
            race.Tick(Dt);
            maxZ = MathF.Max(maxZ, car.Vehicle.Position.Z);
        }
        var (along, speed, z) = Assert.Single(calls);
        Assert.Null(car.FinishedAt); // free play: no finish
        Assert.False(race.Over);
        Assert.True(along >= race.Goal, $"called at {along:F0} m, before the goal");
        Assert.True(speed <= 0.5f, $"called at {speed:F1} m/s, not stopped");
        Assert.InRange(z, 400, 460 - Drive.CoastGap + 0.5f); // on the run-out, short of its end
        Assert.True(maxZ < 460, "past the end of the run-out");
        Assert.Equal(1, car.CourseEnds);
        Assert.True(car.Jumps > jumps);
        Assert.True(car.Along < 10, $"back at {car.Along:F0} m");
        for (var t = 0; t < 120 * 3; t++) race.Tick(Dt); // the driver has it again: off down the course
        Assert.InRange(car.Along, 15, 200);
        Assert.Single(calls);
    }

    /// <summary>STOP: the car stays where it stopped, its driver's again (it drives off, no second auto-run) until it is back on the course.</summary>
    [Fact]
    public void CourseEnd_Stop_HandsTheCarBack()
    {
        var calls = 0;
        var race = Straight(_ =>
        {
            calls++;
            return false;
        });
        var me = new ManualDriver { Input = new VehicleInput(1, 0, 0) };
        var car = race.Add("YOU", new Vehicle(CarSpec.AE86), me);
        Assert.True(race.Place(car, 300, 0));
        for (var t = 0; t < 120 * 30 && calls == 0; t++) race.Tick(Dt);
        Assert.Equal(1, calls);
        Assert.True(car.Parked);
        var stoppedAt = car.Vehicle.Position.Z;
        me.Input = new VehicleInput(0.4f, 0, 0);
        for (var t = 0; t < 120 * 2; t++) race.Tick(Dt);
        Assert.True(car.Vehicle.Position.Z > stoppedAt + 3, "the driver cannot drive off");
        Assert.Equal(1, calls);
    }

    /// <summary>A circuit (line of two laps, no run-out): at the end of the second lap the tracking goes back to the first, nobody stops.</summary>
    [Fact]
    public void CourseEnd_CircuitGoesRound()
    {
        const float r = 80;
        Vector3[] line = [.. Enumerable.Range(0, 2 * 72 + 1).Select(i => new Vector3(r * MathF.Sin(i * MathF.PI / 36), 0, -r * MathF.Cos(i * MathF.PI / 36)))];
        var race = new RaceSession(new Flat(), line, []) { AtCourseEnd = _ => throw new InvalidOperationException("no stop on a circuit") };
        var car = race.Add("YOU", new Vehicle(CarSpec.AE86), new ManualDriver());
        Assert.True(race.Place(car, race.Goal + 1, 0));
        Assert.True(car.Along >= race.Goal);
        race.Tick(Dt);
        Assert.Equal(1, car.CourseEnds);
        Assert.True(car.Along < 20, $"still on the second lap at {car.Along:F0} m");
        Assert.Null(car.FinishedAt);
    }

    /// <summary>The ghost option: overlapping cars do not touch.</summary>
    [Fact]
    public void Ghost_NoContacts()
    {
        foreach (var ghost in new[] { false, true })
        {
            var race = Straight(_ => false);
            race.Ghost = ghost;
            var a = race.Add("A", new Vehicle(CarSpec.AE86), new ManualDriver { Input = new VehicleInput(0.5f, 0, 0) });
            var b = race.Add("B", new Vehicle(CarSpec.AE86), new ManualDriver());
            Assert.True(race.Place(a, 100, 0));
            Assert.True(race.Place(b, 106, 0));
            for (var t = 0; t < 120 * 3; t++) race.Tick(Dt);
            Assert.Equal(ghost, race.Contacts == 0);
        }
    }

    private static Catalog TestCatalog() => new(
        [
            new Catalog.Course("AKINA", "AKINA", ["DAY", "NIT", "RIN"], [Vector2.Zero, Vector2.One], 7400, 300, true, false),
            new Catalog.Course("MYOUGI0", "MYOGI CIRCUIT", ["DAY"], [Vector2.Zero, Vector2.One], 3000, 50, false, true),
        ],
        [new Catalog.Car("AE86T", "TOYOTA", "TRUENO", "FR", 130, 940, [1, 2, 3]), new Catalog.Car("FD3S", "MAZDA", "RX-7", "FR", 255, 1260, [4, 5])]);

    private static readonly (int, int, bool, bool) Ok = (0, 0, true, false), Back = (0, 0, false, true), Down = (0, 1, false, false), Right = (1, 0, false, false);

    /// <summary>
    ///     The lobby: the course end only on a point-to-point course, AI DRIVING only with AI cars, the cursor stays on a row when rows
    ///     come and go; values wrap; the field is the same for the same choice and as large as asked.
    /// </summary>
    [Fact]
    public void Lobby_RowsAndField()
    {
        var f = new FreePlay(TestCatalog());
        f.Open(new FreePlayChoice { Course = "NOPE_DAY", Cars = 9 }, "FD3S", 7, true);
        Assert.Equal(("AKINA_DAY", 3, 1, true), (f.Choice.Course, f.Choice.Cars, f.Paint, f.Manual)); // unknown course, too many cars, paint out of range
        Assert.Contains(FreePlay.Row.End, f.Rows);
        Assert.Contains(FreePlay.Row.Traffic, f.Rows);
        Assert.Equal(CourseEndAction.TurnAround, f.Choice.End);
        f.Update(Right, null); // MYOGI CIRCUIT: no course end
        Assert.Equal("MYOUGI0_DAY", f.Choice.Course);
        Assert.DoesNotContain(FreePlay.Row.End, f.Rows);
        Assert.Equal(FreePlay.Row.Course, f.Selected);
        f.Update(Right, null);
        for (var i = 0; i < 3; i++) f.Update(Down, null);
        Assert.Equal(FreePlay.Row.End, f.Selected);
        f.Update(Right, null);
        f.Update(Right, null);
        Assert.Equal(CourseEndAction.Stop, f.Choice.End);
        f.Update(Down, null);
        f.Update(Ok, null);
        Assert.False(f.Choice.Timer);
        f.Update(Down, null);
        f.Update(Right, null); // 3 → NONE: AI DRIVING goes, the cursor stays on AI CARS
        Assert.Equal((0, FreePlay.Row.Cars), (f.Choice.Cars, f.Selected));
        Assert.DoesNotContain(FreePlay.Row.Traffic, f.Rows);
        Assert.Empty(FreePlay.Field(f.Choice));
        f.Update(Right, null);
        f.Update(Right, null);
        var field = FreePlay.Field(f.Choice);
        Assert.Equal(2, field.Length);
        Assert.Equal(field.Select(r => r.Id), FreePlay.Field(f.Choice.Copy()).Select(r => r.Id));
        Assert.All(field, r => Assert.Equal((0f, 0.75f), (r.Style.Skill, r.Power))); // CRUISE
        f.Update(Down, null);
        f.Update(Right, null); // EASY: the free battle's level
        Assert.All(FreePlay.Field(f.Choice), r => Assert.InRange(r.Style.Skill, 0, 0.2f));
        f.Update(Down, null);
        Assert.Equal(FreePlay.Result.PickCar, f.Update(Ok, null));
        for (var i = 0; i < 5; i++) f.Update(Down, null);
        Assert.Equal(FreePlay.Result.Start, f.Update(Ok, null));
        Assert.Equal(FreePlay.Result.Back, f.Update(Back, null));
    }

    /// <summary>The versus lobby's RULE row: BATTLE → RACE → FREE RUN → FREE RUN with ghosts, and back round; the protocol carries both.</summary>
    [Fact]
    public void Rule_FreeAndGhost_RoundTrip()
    {
        var lobby = new Lobby(1, Phase.Race, 3, new RaceConfig("AKINA_NIT", false, true, NetRule.Free, true), [new PlayerInfo(0, "HOST", "FD3S", 1, true, 0, 3)], -42.5f, 9);
        var l = Assert.IsType<Lobby>(Protocol.Decode(Protocol.Encode(lobby)));
        Assert.Equal((lobby.Config, lobby.SecondsToGo), (l.Config, l.SecondsToGo));
        Assert.Equal("FREE RUN - GHOSTS", Versus.RuleName(l.Config));
        Assert.Equal("FREE RUN", Versus.RuleName(l.Config with { Ghost = false }));
    }

    /// <summary>
    ///     Online free play over loopback: the host starts alone, a guest joins while the run is on (not refused), its race clock
    ///     agrees with the host's, it drives in once ready and loaded, leaves (gone from the list at once, its id free again)
    ///     and a newcomer gets that id.
    /// </summary>
    [Fact]
    public void Session_JoinAndLeaveDuringTheRun()
    {
        using var host = NetSession.Host(0, "HOST");
        host.SetConfig(new RaceConfig("AKINA_DAY", Rule: NetRule.Free));
        Assert.True(host.CanStart); // alone
        host.StartRace();
        host.MarkLoaded();
        bool Run(Func<bool> until, NetSession? guest = null, double seconds = 5)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                host.Update();
                guest?.Update();
                if (until()) return true;
                Thread.Sleep(1);
            }
            return false;
        }
        var started = DateTime.UtcNow;
        Assert.True(Run(() => host.Phase == Phase.Race), "the host's run starts alone");
        Assert.True((DateTime.UtcNow - started).TotalSeconds < NetSession.FreeCountdown + 0.5, "the free play countdown is the short one");
        Thread.Sleep(300);
        var guest = NetSession.Join(new IPEndPoint(IPAddress.Loopback, host.Link.Port), "LATE");
        guest.SetLocal("R32", 0, false);
        Assert.True(Run(() => guest.Joined && guest.Phase == Phase.Race && host.Players.Count == 2, guest), "joins the run in progress");
        Assert.Null(guest.Ended);
        Assert.True(guest.Free);
        Assert.False(guest.CanDriveIn); // not ready yet: it stays in the lobby
        Run(() => false, guest, 1.5); // a few pings, the clock settles
        Assert.InRange(host.RaceTime - guest.RaceTime, -0.03f, 0.03f); // its clock is the run's, not a fresh one
        Assert.True(guest.RaceTime > 0.25f);
        guest.SetLocal("R32", 0, true);
        Assert.True(guest.CanDriveIn);
        guest.MarkLoaded();
        Assert.True(Run(() => host.Players[1].LoadedRace == host.RaceId, guest), "drives in");
        guest.LeaveRun(); // back to its lobby for another car: still in the session, its car off the course
        Assert.True(Run(() => host.Players[1].LoadedRace == -1, guest));
        guest.Leave();
        Assert.True(Run(() => host.Players.Count == 1, guest), "the leaver is gone at once");
        guest.Dispose();
        using var next = NetSession.Join(new IPEndPoint(IPAddress.Loopback, host.Link.Port), "NEXT");
        Assert.True(Run(() => next.Joined, next), "a newcomer");
        Assert.Equal((byte)1, next.Local.Id);
        Assert.Equal(Phase.Race, host.Phase);
    }
}
