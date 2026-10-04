using System.Numerics;
using Kansei.Physics;
using Touge.Net;
using Touge.Race;

namespace Touge.Tests;

public class NetRaceTests
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

    /// <summary>
    ///     Local authority per car: our car steers into a remote car that its own peer drives straight (its states arrive
    ///     30 Hz, 60 ms late, some lost). The contact is counted and keeps our car beside it (our share of each contact, every
    ///     tick it lasts — the remote car does not yield until its own peer, resolving the contact from its side, says so);
    ///     the remote car is shown where its peer says it is (the push does not move it for good).
    /// </summary>
    [Fact]
    public void Contact_PushesUs_RemoteStaysWhereItsPeerSays()
    {
        var ground = new Flat();
        Vector3[] line = [.. Enumerable.Range(0, 400).Select(i => new Vector3(0, 0, i * 5f))];
        var race = new RaceSession(ground, line, []);
        var me = new ManualDriver();
        race.Add("ME", new Vehicle(CarSpec.AE86), me);
        var player = new NetPlayer(1, "THEM", false);
        var t = 0f;
        var them = race.Add("THEM", new Vehicle(CarSpec.AE86), new RemoteDriver(player, () => t));
        Assert.True(race.Place(race.Cars[0], 50, 1.1f));
        Assert.True(race.Place(them, 50, -1.1f));
        // their peer's own simulation of their car: straight on at part throttle
        var truth = new Vehicle(CarSpec.AE86);
        truth.Reset(new Vector3(them.Vehicle.Position.X, 0, them.Vehicle.Position.Z), 0);
        for (var i = 0; i < 60; i++) truth.Step(new VehicleInput(0, 0, 0, Handbrake: true), ground, Dt);
        var rng = new Random(3);
        var inFlight = new List<(float At, CarState S)>();
        uint seq = 0;
        float maxShownErr = 0, minGap = float.MaxValue;
        for (var tick = 0; tick < 120 * 8; tick++)
        {
            t = tick * Dt;
            truth.Step(new VehicleInput(0.5f, 0, 0), ground, Dt);
            if (tick % 4 == 0 && rng.NextDouble() > 0.1)
                inFlight.Add((t + 0.06f, CarStates.Of(truth, new VehicleInput(0.5f, 0, 0), t, truth.Position.Z, null, Headlights.Mode.Off) with { Seq = ++seq, Id = 1 }));
            foreach (var f in inFlight.Where(f => f.At <= t).ToArray())
            {
                player.Snapshots.Add(f.S);
                inFlight.Remove(f);
            }
            // we keep pace, then turn right into them for a moment
            me.Input = new VehicleInput(0.5f, 0, t is > 3 and < 3.6f ? 0.35f : 0);
            race.Tick(Dt);
            // left = +X: we are on the left and steer right into them; without them we would end up well right of their line
            if (t > 1) maxShownErr = MathF.Max(maxShownErr, Vector3.Distance(them.Vehicle.Position, truth.Position));
            minGap = MathF.Min(minGap, race.Cars[0].Vehicle.Position.X - truth.Position.X);
        }
        Assert.True(race.Contacts >= 1, "no contact");
        Assert.True(minGap > CarSpec.AE86.Width - 0.1f, $"overlap: centres {minGap:F2} m apart");
        Assert.True(maxShownErr < 0.6f, $"remote shown {maxShownErr:F2} m off its peer's car");
    }

    /// <summary>The split keyboard: player 2's arrows are gone from player 1's bindings, player 2 drives on the arrow half only.</summary>
    [Fact]
    public void SplitKeyboard_TwoHalves()
    {
        var cfg = new ControlSettings();
        var p1 = SplitKeys.WithoutP2(cfg);
        var p2 = SplitKeys.P2Keyboard();
        Assert.Contains(p1[Control.SteerLeft], b => b.Code == (int)Kansei.Input.Key.A);
        foreach (var binds in p1.Values)
            Assert.DoesNotContain(binds, b => b.Source == Source.Key && SplitKeys.P2.Contains((Kansei.Input.Key)b.Code));
        foreach (var binds in p2.Values)
            Assert.All(binds, b => Assert.True(b.Source == Source.None || SplitKeys.P2.Contains((Kansei.Input.Key)b.Code)));
        Assert.Equal((int)Kansei.Input.Key.Up, p2[Control.Throttle][0].Code);
    }
}
