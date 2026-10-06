using System.Numerics;
using Kansei.Physics;
using Touge.Race;

namespace Touge.Net;

/// <summary>
///     Decides a multiplayer race (online: the host only; split screen: locally) from what each player's own simulation says:
///     goal times are the drivers' own (each peer is the authority over its car), so latency does not move the finish line.
///     <see cref="NetRule.Battle"/> (two players): the first goal time wins as soon as it is safe — the other has finished
///     too or reported a later moment without finishing — or a breakaway gap of <see cref="Battle.Breakaway"/> s.
///     <see cref="NetRule.Race"/>: places by goal time once everybody still connected has finished, the rest
///     <see cref="DnfAfter"/> s after the first finisher do not finish. Players that leave do not finish; the last one left wins.
/// </summary>
public sealed class Referee(NetRule rule, float goal)
{
    /// <summary>Seconds after the first goal time until the stragglers are out (did not finish).</summary>
    public const float DnfAfter = 30;

    /// <param name="FinishedAt">Goal time (race s) or null.</param>
    /// <param name="Along">Metres along the line now (remote: predicted).</param>
    /// <param name="InfoTime">Race time the information is from (remote: its newest state; local: now).</param>
    public readonly record struct Car(byte Id, bool Connected, float? FinishedAt, float Along, float InfoTime);

    private readonly Battle _battle = new(BattleRule.Race, goal);
    private float _time;

    public NetRule Rule => rule;
    /// <summary>The battle view (gap, leader) of a two-player battle; cars[0] is "the player" in it.</summary>
    public Battle BattleView => _battle;

    /// <summary>One tick at race time <paramref name="now"/>; the result once decided (call again: the same decision is not repeated, keep the first).</summary>
    public Result? Update(float now, float dt, IReadOnlyList<Car> cars, int raceId)
    {
        _time += dt;
        if (cars.Count == 0) return null;
        var connected = cars.Count(c => c.Connected);
        if (cars.Count >= 2 && connected <= 1) return Make(cars, raceId, "OPPONENTS LEFT", null);
        if (rule == NetRule.Battle && cars.Count == 2)
        {
            _battle.Update(dt, cars[0].Along, cars[1].Along);
            if (_battle is { Outcome: not BattleOutcome.None, Reason: "BREAKAWAY" })
                return Make(cars, raceId, "BREAKAWAY", cars[_battle.Outcome == BattleOutcome.Win ? 0 : 1].Id);
            var first = cars.Where(c => c.FinishedAt != null).OrderBy(c => c.FinishedAt).FirstOrDefault();
            if (first.FinishedAt is { } t)
            {
                var other = cars[0].Id == first.Id ? cars[1] : cars[0];
                // safe when the other is in (earlier than us would already be listed) or has told us about a later moment
                if (other.FinishedAt != null || other.InfoTime >= t || !other.Connected) return Make(cars, raceId, "GOAL", first.Id);
            }
            return null;
        }
        var finishers = cars.Where(c => c.FinishedAt != null).ToArray();
        if (finishers.Length == 0) return null;
        var firstAt = finishers.Min(c => c.FinishedAt!.Value);
        // every connected car finished, and nobody could still report an earlier goal time than the last one listed
        if (cars.All(c => !c.Connected || c.FinishedAt != null)) return Make(cars, raceId, "GOAL", null);
        if (now - firstAt > DnfAfter) return Make(cars, raceId, "TIME UP", null);
        return null;
    }

    /// <summary>Places: <paramref name="winner"/> first, then finishers by goal time, then the rest by how far they got (connected before gone).</summary>
    private static Result Make(IReadOnlyList<Car> cars, int raceId, string reason, byte? winner)
    {
        var order = cars.OrderBy(c => c.Id == winner ? 0 : 1).ThenBy(c => c.FinishedAt ?? float.MaxValue).ThenBy(c => c.Connected ? 0 : 1)
            .ThenByDescending(c => c.Along).ToArray();
        return new Result(raceId, reason, [.. order.Select((c, i) => new ResultEntry(c.Id, (byte)(i + 1), c.FinishedAt ?? -1, c.Along))]);
    }
}

/// <summary>
///     A remote player's car in the <see cref="RaceSession"/> (<see cref="IPuppet"/>): each tick its snapshots are sampled at our
///     race clock (dead reckoning past the newest one: on our screen the car is where it is now, not 100 ms ago, so side by side
///     looks side by side), and the shown pose follows that target with a short exponential blend (<see cref="Smoothing"/>), so a
///     correction slides in instead of jumping; beyond <see cref="SnapDistance"/> it jumps (respawn, reset to the road). Contacts
///     push our car off it and its own peer does the same with ours (local authority per car). Gone players park out of reach.
/// </summary>
public sealed class RemoteDriver(NetPlayer player, Func<float> clock) : IPuppet
{
    public const float Smoothing = 0.1f, SnapDistance = 6;
    private Vector3 _pos;
    private Quaternion _rot;
    private bool _has;
    private readonly float[] _comp = new float[4], _slide = new float[4];

    public NetPlayer Player => player;
    public SampleKind LastKind { get; private set; }
    /// <summary>Distance between the shown pose and the sampled one (m), after this tick's blend.</summary>
    public float LastError { get; private set; }
    public int Interpolated, Extrapolated, Held;
    public Headlights.Mode Lights { get; private set; }

    public VehicleInput Drive(RaceSession race, RaceCar car, float dt)
    {
        var v = car.Vehicle;
        if (!player.Connected)
        {
            v.SetRemote(new Vector3(0, -1000, 0), Quaternion.Identity, Vector3.Zero, Vector3.Zero, 0, 0, 0, 0, _comp, _slide, false, dt);
            return default;
        }
        var kind = LastKind = player.Snapshots.Sample(clock(), out var s);
        switch (kind)
        {
            case SampleKind.None: return default; // nothing heard yet: it stays where the grid put it
            case SampleKind.Interpolated: Interpolated++; break;
            case SampleKind.Extrapolated: Extrapolated++; break;
            default: Held++; break;
        }
        if (!_has || Vector3.Distance(_pos, s.Position) > SnapDistance) (_pos, _rot, _has) = (s.Position, s.Orientation, true);
        else
        {
            var k = 1 - MathF.Exp(-dt / Smoothing);
            _pos = Vector3.Lerp(_pos + s.Velocity * dt, s.Position, k);
            _rot = Quaternion.Normalize(Quaternion.Slerp(_rot, s.Orientation, k));
        }
        LastError = Vector3.Distance(_pos, s.Position);
        CarStates.Unpack(s, _comp, _slide);
        Lights = s.Has(CarState.LightsHigh) ? Headlights.Mode.High : s.Has(CarState.LightsLow) ? Headlights.Mode.Low : Headlights.Mode.Off;
        v.SetRemote(_pos, _rot, s.Velocity, s.AngularVelocity, s.Rpm, s.Gear, s.Throttle, s.Steer, _comp, _slide, s.Has(CarState.Wall), dt);
        return new VehicleInput(s.Throttle, s.Brake, s.Steer, s.Has(CarState.Handbrake));
    }

    public void Reset() => _has = false;
}

/// <summary>Our car → <see cref="CarState"/> and back (wheels packed one byte each).</summary>
public static class CarStates
{
    /// <summary>Slide speed (m/s) per byte step: 0.25 m/s up to 63 m/s.</summary>
    private const float SlideStep = 0.25f;

    public static CarState Of(Vehicle v, VehicleInput input, float time, float along, float? finishedAt, Headlights.Mode lights)
    {
        uint wheels = 0, slides = 0;
        var speed = MathF.Max(v.Velocity.Length(), 3);
        for (var i = 0; i < 4; i++)
        {
            var w = v.Wheels[i];
            var comp = w.Contact ? Math.Clamp(w.Compression / v.Spec.Travel, 1 / 255f, 1) : 0;
            var tan = MathF.Tan(w.SlipAngle);
            var slide = w.Contact ? speed * MathF.Sqrt(w.SlipRatio * w.SlipRatio + tan * tan) : 0;
            wheels |= (uint)MathF.Round(comp * 255) << (8 * i);
            slides |= (uint)Math.Clamp(MathF.Round(slide / SlideStep), 0, 255) << (8 * i);
        }
        var flags = (byte)((input.Handbrake ? CarState.Handbrake : 0) | (lights == Headlights.Mode.Low ? CarState.LightsLow : 0)
                           | (lights == Headlights.Mode.High ? CarState.LightsHigh : 0) | (v.WallContacts > 0 ? CarState.Wall : 0)
                           | (finishedAt != null ? CarState.Finished : 0));
        return new CarState(0, 0, time, v.Position, v.Orientation, v.Velocity, v.AngularVelocity, input.Steer, input.Throttle, input.Brake, v.Rpm,
            (sbyte)v.Gear, flags, wheels, slides, along, finishedAt ?? -1);
    }

    public static void Unpack(in CarState s, Span<float> compression, Span<float> slide)
    {
        for (var i = 0; i < 4; i++)
        {
            compression[i] = (s.Wheels >> (8 * i) & 0xFF) / 255f;
            slide[i] = (s.Slides >> (8 * i) & 0xFF) * SlideStep;
        }
    }
}

/// <summary>
///     An online race on one peer: a <see cref="RaceSession"/> with our car (index 0, our driver) and every other player's car as
///     a <see cref="RemoteDriver"/> puppet, all on the same grid (slots by player id, rows of two, 8 m apart). Per tick: the
///     session steps (contacts included), our state goes out at <see cref="StateRate"/> Hz, the host referees
///     (<see cref="Referee"/>, publishes the result), a client stops racing when the host's result is in.
/// </summary>
public sealed class NetRace
{
    public const float StateRate = 30, RowGap = 10;
    private float _sendDebt;
    private readonly Referee? _referee;

    public RaceSession Race { get; }
    public NetSession Net { get; }
    /// <summary>Race car per player id (ours included).</summary>
    public Dictionary<byte, RaceCar> ByPlayer { get; } = [];
    /// <summary>Our car's lamps (sent along).</summary>
    public Headlights.Mode Lights { get; set; }
    public RaceCar Local => Race.Cars[0];
    /// <summary>Our goal time on the shared race clock (<see cref="NetSession.RaceTime"/>; the session's own tick count can lag it after a hitch).</summary>
    public float? LocalFinish { get; private set; }

    private NetRace(RaceSession race, NetSession net)
    {
        (Race, Net) = (race, net);
        if (net.Free) (race.AtCourseEnd, race.Ghost) = (race.BackToStart, net.Config.Ghost); // no result: round and round
        else if (net.IsHost) _referee = new Referee(net.Config.Rule, race.Goal);
    }

    /// <summary>Free play: seconds a car that drives in passes through the others (<see cref="RaceCar.Protect"/>).</summary>
    public const float SpawnGhost = 2;

    /// <summary>The race on <paramref name="drive"/> (its car is ours, driven by <paramref name="local"/>) with everybody of <paramref name="net"/>.</summary>
    public static NetRace Create(Drive drive, NetSession net, ICarDriver local)
    {
        var race = new RaceSession(drive.Ground, drive.Line, drive.RunOutLine);
        var r = new NetRace(race, net);
        r.ByPlayer[net.Local.Id] = race.Add(net.Local.Name, drive.Car, local);
        if (net.Free) race.Cars[0].Protect = SpawnGhost; // a guest driving in lands on the grid, maybe on someone standing there
        foreach (var p in net.Players.Where(p => !p.IsLocal && (!net.Free || r.Drives(p)))) r.AddRemote(p);
        drive.ResetTo(0);
        var at = race.Cars[0].Track.Track(drive.Car.Position).Along;
        var ids = r.ByPlayer.Keys.Order().ToList();
        Grid(race, at, [.. race.Cars.Select(c => ids.IndexOf(r.ByPlayer.First(kv => kv.Value == c).Key))]);
        return r;
    }

    /// <summary>
    ///     Grid of <paramref name="race"/>'s cars from <paramref name="at"/> m (the spawn): car i in slot <paramref name="slots"/>[i], rows of
    ///     two side by side (even slots left); with more than one row the first is <see cref="RowGap"/> m further up the road than the next
    ///     (behind the spawn is the wall closing the course), the last stands at the spawn. A row is shifted across the road until both fit
    ///     (as <see cref="RaceSession.Grid"/>), else staggered.
    /// </summary>
    public static void Grid(RaceSession race, float at, IReadOnlyList<int> slots)
    {
        var rows = (race.Cars.Count + 1) / 2;
        for (var row = 0; row < rows; row++)
        {
            var along = at + (rows - 1 - row) * RowGap;
            var left = race.Cars.Where((_, i) => slots[i] == row * 2).FirstOrDefault();
            var right = race.Cars.Where((_, i) => slots[i] == row * 2 + 1).FirstOrDefault();
            var placed = false;
            foreach (var half in new[] { 1.6f, 1.3f, RivalPilot.PassGap / 2 })
            {
                foreach (var centre in new[] { 0f, 0.5f, -0.5f, 1f, -1f, 1.5f, -1.5f })
                    if ((left == null || race.Place(left, along, centre + half)) && (right == null || race.Place(right, along, centre - half)))
                    {
                        placed = true;
                        break;
                    }
                if (placed) break;
            }
            if (placed) continue;
            // too narrow for two abreast: one behind the other, half a row apart (further up the road until the spot is free)
            if (left != null) PlaceAhead(race, left, along);
            if (right != null) PlaceAhead(race, right, along + RowGap / 2);
        }
    }

    private static void PlaceAhead(RaceSession race, RaceCar car, float along)
    {
        for (var k = 0; k < 6 && !race.Place(car, along + 2 * k, 0); k++) { }
    }

    /// <summary>Free play: a remote player whose car is out on the course (in the session, loaded this run).</summary>
    private bool Drives(NetPlayer p) => p.Connected && p.LoadedRace == Net.RaceId && Net.Players.Contains(p);

    private void AddRemote(NetPlayer p)
    {
        var spec = CarSpecs.All.TryGetValue(p.Car, out var s) ? s : CarSpec.AE86;
        var car = ByPlayer[p.Id] = Race.Add(p.Name, new Vehicle(spec) { SurfaceGrip = Local.Vehicle.SurfaceGrip }, new RemoteDriver(p, () => Net.RaceTime));
        if (Net.Free) car.Protect = SpawnGhost; // the same on everybody else's machine
    }

    /// <summary>
    ///     Free play: the cars follow the session — a player who drives in gets a car, one who left (or went back to the lobby
    ///     for another car) loses it; a newcomer on a freed id is a new player. True when the field changed (the game reloads models).
    /// </summary>
    public bool Sync()
    {
        if (!Net.Free) return false;
        var changed = false;
        foreach (var (id, car) in ByPlayer.ToArray())
            if (car.Driver is RemoteDriver rd && !Drives(rd.Player))
            {
                Race.Remove(car);
                ByPlayer.Remove(id);
                Net.Log?.Invoke($"[Netz] {Net.RaceTime:F1} #{id} {rd.Player.Name} nicht mehr auf der Strecke");
                changed = true;
            }
        foreach (var p in Net.Players)
            if (!p.IsLocal && !ByPlayer.ContainsKey(p.Id) && Drives(p))
            {
                AddRemote(p);
                Net.Log?.Invoke($"[Netz] {Net.RaceTime:F1} #{p.Id} {p.Name} fährt mit ({p.Car})");
                changed = true;
            }
        return changed;
    }

    /// <summary>One physics tick once the race runs (race clock ≥ 0).</summary>
    public void Tick(float dt)
    {
        Race.Tick(dt);
        if (Local.FinishedAt != null && LocalFinish == null) LocalFinish = Net.RaceTime;
        _sendDebt += dt * StateRate;
        if (_sendDebt >= 1)
        {
            _sendDebt = MathF.Min(_sendDebt - 1, 1);
            Net.SendState(CarStates.Of(Local.Vehicle, Local.Input, Net.RaceTime, Local.Along, LocalFinish, Lights));
        }
        if (_referee != null && Net.Phase == Phase.Race && Net.Result == null)
        {
            var now = Net.RaceTime;
            var cars = ByPlayer.OrderBy(kv => kv.Key == Net.Local.Id ? -1 : kv.Key).Select(kv =>
            {
                var (id, car) = (kv.Key, kv.Value);
                if (car == Local) return new Referee.Car(id, true, LocalFinish, car.Along, now);
                var p = Net.Players.First(x => x.Id == id);
                var last = p.Snapshots.Latest;
                return new Referee.Car(id, p.Connected, last is { FinishedAt: >= 0 } l ? l.FinishedAt : null, car.Along, last?.Time ?? float.NegativeInfinity);
            }).ToList();
            if (_referee.Update(now, dt, cars, Net.RaceId) is { } result) Net.Publish(result);
        }
        if (Net.Result != null) Race.Ended = true;
    }

    /// <summary>The battle view of a two-player battle (host: the referee's; null otherwise).</summary>
    public Battle? BattleView => _referee is { Rule: NetRule.Battle } r && Race.Cars.Count == 2 ? r.BattleView : null;
}
