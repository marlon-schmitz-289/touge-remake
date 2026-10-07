using System.Net;
using System.Numerics;
using Touge.Net;

namespace Touge.Tests;

public class NetTests
{
    private static CarState Car(uint seq, float t, Vector3 p, Vector3 v, float yaw = 0, float yawRate = 0) =>
        new(1, seq, t, p, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw), v, new Vector3(0, yawRate, 0), 0.1f, 0.8f, 0, 6000, 3, CarState.LightsLow,
            0x80808080, 0x01020304, 123.5f, -1);

    /// <summary>Every message survives encode → decode unchanged.</summary>
    [Fact]
    public void Protocol_RoundTrip()
    {
        INetMessage[] all =
        [
            new Discover(), new Announce("TAKUMI", 2, 4, "AKINA_NIT", Phase.Lobby, 0xC0FFEE), new Hello(0xDEADBEEF, "ITSUKI", "AE85", 2, true, 7),
            new Ping(12.5), new Pong(99.25), new StateMsg(7, Car(42, 3.5f, new Vector3(1, 2, 3), new Vector3(20, 0, 5), 0.3f, 0.2f)),
            new Result(3, "GOAL", [new ResultEntry(0, 1, 201.5f, 7400), new ResultEntry(2, 2, -1, 6800)]), new Bye(2, "LEFT"),
        ];
        foreach (var m in all)
        {
            var back = Protocol.Decode(Protocol.Encode(m));
            Assert.NotNull(back);
            Assert.Equal(m.Type, back.Type);
            if (m is not (Result or Discover)) Assert.Equal(m, back); // records with arrays compare by reference
        }
        var lobby = new Lobby(1, Phase.Countdown, 4, new RaceConfig("IROHA_RIN", true, false, NetRule.Race),
            [new PlayerInfo(0, "HOST", "FD3S", 1, true, 0, 4), new PlayerInfo(1, "YOU", "R32", 0, false, 35, 3)], 2.25f, 77);
        var l = Assert.IsType<Lobby>(Protocol.Decode(Protocol.Encode(lobby)));
        Assert.Equal((lobby.YouId, lobby.Phase, lobby.RaceId, lobby.Config, lobby.SecondsToGo, lobby.Seq), (l.YouId, l.Phase, l.RaceId, l.Config, l.SecondsToGo, l.Seq));
        Assert.Equal(lobby.Players, l.Players);
        var r = Assert.IsType<Result>(Protocol.Decode(Protocol.Encode(all[6])));
        Assert.Equal(((Result)all[6]).Entries, r.Entries);
        Assert.True(Protocol.Encode(new StateMsg(1, Car(1, 0, Vector3.Zero, Vector3.Zero))).Length < 128); // 30 Hz × 4 players stays tiny
    }

    /// <summary>The trust boundary: truncated, foreign, oversized or poisoned packets decode to null; names are clipped and cleaned.</summary>
    [Fact]
    public void Protocol_RejectsMalformed()
    {
        var good = Protocol.Encode(new StateMsg(1, Car(1, 0, Vector3.One, Vector3.Zero)));
        for (var n = 0; n < good.Length; n++) Assert.Null(Protocol.Decode(good.AsSpan(0, n)));
        Assert.Null(Protocol.Decode([.. good, 0])); // trailing garbage
        var version = (byte[])good.Clone();
        version[2] = 99;
        Assert.Null(Protocol.Decode(version));
        var nan = (byte[])good.Clone();
        BitConverter.GetBytes(float.NaN).CopyTo(nan, 4 + 4 + 1 + 4 + 4); // position.x
        Assert.Null(Protocol.Decode(nan));
        var badId = (byte[])good.Clone();
        badId[8] = 9;
        Assert.Null(Protocol.Decode(badId));
        Assert.Null(Protocol.Decode(new byte[2000]));
        var h = Assert.IsType<Hello>(Protocol.Decode(Protocol.Encode(new Hello(1, "A\u0001VERY LONG NAME THAT GOES ON AND ON AND ON", "AE86T", 0, false, -1))));
        Assert.Equal(Protocol.MaxString, h.Name.Length);
        Assert.DoesNotContain('\u0001', h.Name);
    }

    [Fact]
    public void NetSim_Parse()
    {
        Assert.Equal(new NetSim(80, 0.05f, 10), NetSim.Parse("80:5%:10"));
        Assert.Equal(new NetSim(40, 0.1f), NetSim.Parse("40:0.1"));
        Assert.Equal(new NetSim(25), NetSim.Parse("25"));
    }

    /// <summary>Out of order, duplicated and lost states: kept sorted, counted; Hermite through the velocities beats a straight line on a curve.</summary>
    [Fact]
    public void Snapshots_OrderLossAndInterpolation()
    {
        // a car drifting round a 15 m hairpin at 25 m/s
        const float r = 15, speed = 25;
        CarState At(uint seq, float t)
        {
            var a = speed * t / r;
            var p = new Vector3(r * MathF.Sin(a), 0, r * (1 - MathF.Cos(a)));
            var v = new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * speed;
            return Car(seq, t, p, v, MathF.PI / 2 - a, -speed / r);
        }
        var b = new SnapshotBuffer();
        b.Add(At(1, 0));
        b.Add(At(3, 2 / 30f)); // 2 arrives before…
        b.Add(At(2, 1 / 30f)); // …its predecessor
        b.Add(At(2, 1 / 30f)); // and once more
        b.Add(At(11, 10 / 30f)); // 4 … 10 lost
        Assert.Equal((4, 1, 1, 7L), (b.Received, b.Late, b.Duplicates, b.Lost));
        Assert.Equal(SampleKind.Interpolated, b.Sample(6 / 30f, out var mid));
        var truth = At(0, 6 / 30f).Position;
        var linear = Vector3.Lerp(At(3, 2 / 30f).Position, At(11, 10 / 30f).Position, 0.5f);
        Assert.True(Vector3.Distance(mid.Position, truth) < 0.03f, $"hermite error {Vector3.Distance(mid.Position, truth)}");
        Assert.True(Vector3.Distance(linear, truth) > 0.3f, $"linear error {Vector3.Distance(linear, truth)}");
        // past the newest: dead reckoning, then held after MaxExtrapolation
        Assert.Equal(SampleKind.Extrapolated, b.Sample(10 / 30f + 0.1f, out var ahead));
        Assert.True(Vector3.Distance(At(11, 10 / 30f).Position + At(11, 10 / 30f).Velocity * 0.1f, ahead.Position) < 1e-4f);
        Assert.Equal(SampleKind.Held, b.Sample(10 / 30f + 3, out var held));
        Assert.Equal(10 / 30f + SnapshotBuffer.MaxExtrapolation, held.Time, 4);
        Assert.Equal(SampleKind.Held, b.Sample(-1, out _));
    }

    /// <summary>
    ///     Packet loss and jitter: a car weaving at 30 m/s sends 30 states/s, 20 % are lost, the rest arrive 40–80 ms late (and
    ///     out of order). Sampled at our race clock every 1/120 s (dead reckoning, as the game does) and blended like
    ///     <see cref="RemoteDriver"/>, the shown car stays within a few dm of the truth and never jumps.
    /// </summary>
    [Fact]
    public void Snapshots_SurviveLossAndJitter()
    {
        static Vector3 P(float t) => new(30 * t, 0, 4 * MathF.Sin(t * 1.3f));
        static Vector3 V(float t) => new(30, 0, 5.2f * MathF.Cos(t * 1.3f));
        var rng = new Random(7);
        var b = new SnapshotBuffer();
        var inFlight = new List<(float Arrive, CarState S)>();
        uint seq = 0;
        float maxErr = 0, sumErr = 0, maxStep = 0;
        var n = 0;
        Vector3 shown = default, prevShown = default;
        var has = false;
        for (var tick = 0; tick < 120 * 20; tick++)
        {
            var t = tick / 120f;
            if (tick % 4 == 0 && rng.NextDouble() >= 0.2) inFlight.Add((t + 0.04f + 0.04f * rng.NextSingle(), new CarState(1, ++seq, t, P(t), Quaternion.Identity, V(t), Vector3.Zero, 0, 1, 0, 7000, 4, 0, 0, 0, 0, -1)));
            else if (tick % 4 == 0) seq++;
            foreach (var f in inFlight.Where(f => f.Arrive <= t).ToArray())
            {
                b.Add(f.S);
                inFlight.Remove(f);
            }
            if (b.Sample(t, out var s) == SampleKind.None) continue;
            const float dt = 1 / 120f;
            shown = has ? Vector3.Lerp(shown + s.Velocity * dt, s.Position, 1 - MathF.Exp(-dt / RemoteDriver.Smoothing)) : s.Position;
            if (has) maxStep = MathF.Max(maxStep, Vector3.Distance(shown, prevShown));
            (prevShown, has) = (shown, true);
            if (t < 1) continue;
            var err = Vector3.Distance(shown, P(t));
            (maxErr, sumErr, n) = (MathF.Max(maxErr, err), sumErr + err, n + 1);
        }
        Assert.InRange(b.Lost, 80, 160); // ~20 % of 600
        Assert.True(sumErr / n < 0.15f, $"mean error {sumErr / n:F3} m");
        Assert.True(maxErr < 0.6f, $"max error {maxErr:F3} m");
        Assert.True(maxStep < 30 / 120f * 1.6f, $"largest per-tick move {maxStep:F3} m"); // no visible jumps
    }

    private static Referee.Car C(byte id, float? fin, float along, float info, bool on = true) => new(id, on, fin, along, info);

    /// <summary>
    ///     Battle: the first goal time wins, but only once the other side has reported a moment after it (its packets are late);
    ///     a breakaway ends it early; the last player left wins.
    /// </summary>
    [Fact]
    public void Referee_BattleWaitsForLateNews()
    {
        var r = new Referee(NetRule.Battle, 1000);
        Assert.Null(r.Update(60, 0.01f, [C(0, null, 900, 60), C(1, null, 905, 59.9f)], 1));
        // we cross at 70.0; the rival's newest state is from 69.95 and it has not finished: it might still have crossed earlier
        Assert.Null(r.Update(70.01f, 0.01f, [C(0, 70f, 1000, 70.01f), C(1, null, 999, 69.95f)], 1));
        // it reports 69.98: finished before us after all
        var res = r.Update(70.06f, 0.01f, [C(0, 70f, 1001, 70.06f), C(1, 69.98f, 1001, 70.02f)], 1);
        Assert.NotNull(res);
        Assert.Equal(("GOAL", (byte)1), (res.Reason, res.Entries.Single(e => e.Place == 1).Id));
        // the other way: its news from after our goal time without a finish → we won
        var r2 = new Referee(NetRule.Battle, 1000);
        var res2 = r2.Update(70.1f, 0.01f, [C(0, 70f, 1001, 70.1f), C(1, null, 995, 70.05f)], 1)!;
        Assert.Equal((byte)0, res2.Entries.Single(e => e.Place == 1).Id);
        Assert.Equal(-1, res2.Entries.Single(e => e.Place == 2).Time);
        // breakaway: 8 s ahead at 30 m/s
        var r3 = new Referee(NetRule.Battle, 10000);
        Result? b = null;
        for (var t = 0f; t < 40 && b == null; t += 0.01f) b = r3.Update(t, 0.01f, [C(0, null, 30 * t, t), C(1, null, 20 * t, t)], 1);
        Assert.Equal(("BREAKAWAY", (byte)0), (b!.Reason, b.Entries[0].Id));
        var left = new Referee(NetRule.Battle, 1000).Update(5, 0.01f, [C(0, null, 100, 5), C(1, null, 120, 4, on: false)], 1)!;
        Assert.Equal(("OPPONENTS LEFT", (byte)0), (left.Reason, left.Entries[0].Id));
    }

    /// <summary>Race: places by goal time once everybody still connected is in; stragglers are out 30 s after the first; leavers last.</summary>
    [Fact]
    public void Referee_RaceOrderAndTimeUp()
    {
        var r = new Referee(NetRule.Race, 1000);
        Assert.Null(r.Update(80, 0.01f, [C(0, 79f, 1002, 80), C(1, null, 990, 80), C(2, 78.5f, 1003, 80), C(3, null, 400, 50, on: false)], 2));
        var res = r.Update(81, 0.01f, [C(0, 79f, 1002, 81), C(1, 80.7f, 1001, 81), C(2, 78.5f, 1003, 81), C(3, null, 400, 50, on: false)], 2)!;
        Assert.Equal([2, 0, 1, 3], res.Entries.OrderBy(e => e.Place).Select(e => (int)e.Id));
        var slow = new Referee(NetRule.Race, 1000);
        Assert.Null(slow.Update(100, 0.01f, [C(0, 75f, 1001, 100), C(1, null, 600, 100)], 1));
        var up = slow.Update(105.1f, 0.01f, [C(0, 75f, 1001, 105), C(1, null, 700, 105)], 1)!;
        Assert.Equal(("TIME UP", -1f), (up.Reason, up.Entries.Single(e => e.Id == 1).Time));
    }

    /// <summary>
    ///     A real session over loopback UDP, the client's link losing 20 % each way with 30 ± 10 ms delay: it joins, gets the
    ///     host's choice, both load, the countdown puts GO at the same moment on both clocks (within the jitter), car states and
    ///     the result arrive despite the loss, and a client that leaves is dropped by the host.
    /// </summary>
    [Fact]
    public void Session_LoopbackWithLoss()
    {
        using var host = NetSession.Host(0, "HOST");
        host.Countdown = 1;
        host.SetConfig(new RaceConfig("IROHA_NIT", true, false, NetRule.Race));
        using var client = NetSession.Join(new IPEndPoint(IPAddress.Loopback, host.Link.Port), "GUEST", new NetSim(30, 0.2f, 10));
        client.SetLocal("R32", 1, true);
        bool Run(Func<bool> until, double seconds = 5)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                host.Update();
                client.Update();
                if (until()) return true;
                Thread.Sleep(1);
            }
            return false;
        }
        Assert.True(Run(() => client.Joined && host.CanStart), "join + ready");
        Assert.Equal(("IROHA_NIT", true, NetRule.Race), (client.Config.CourseTime, client.Config.Reverse, client.Config.Rule));
        Assert.Equal((byte)1, client.Local.Id);
        Assert.Equal(("R32", (byte)1), (host.Players[1].Car, host.Players[1].Paint));
        host.StartRace();
        Assert.True(Run(() => client.Phase == Phase.Loading), "loading");
        host.MarkLoaded();
        client.MarkLoaded();
        Assert.True(Run(() => host.Phase == Phase.Race && client.Phase == Phase.Race), "countdown → race");
        // both race clocks: GO at the same moment within the simulated delay/jitter
        Assert.InRange(host.RaceTime - client.RaceTime, -0.06f, 0.06f);
        // a late, reordered lobby packet of an older phase must not take the client back (it would clear GO)
        var stale = Protocol.Encode(new Lobby(1, Phase.Lobby, host.RaceId - 1, host.Config, [], 0, 1));
        for (var i = 0; i < 20; i++) host.Link.Send(stale, new IPEndPoint(IPAddress.Loopback, client.Link.Port));
        Assert.False(Run(() => client.Phase != Phase.Race, 0.5), "stale lobby ignored");
        Assert.True(Run(() =>
        {
            host.SendState(new CarState(0, 0, host.RaceTime, new Vector3(1, 0, host.RaceTime), Quaternion.Identity, Vector3.UnitZ, Vector3.Zero, 0, 1, 0, 5000, 2, 0, 0, 0, 1, -1));
            client.SendState(new CarState(0, 0, client.RaceTime, new Vector3(-1, 0, client.RaceTime), Quaternion.Identity, Vector3.UnitZ, Vector3.Zero, 0, 1, 0, 5000, 2, 0, 0, 0, 1, -1));
            return host.Players[1].Snapshots.Received > 20 && client.Players.First(p => p.Id == 0).Snapshots.Received > 20;
        }), "states both ways");
        Assert.Equal((byte)1, host.Players[1].Snapshots.Latest!.Value.Id);
        host.Publish(new Result(host.RaceId, "GOAL", [new ResultEntry(1, 1, 60, 1000), new ResultEntry(0, 2, 61, 1000)]));
        Assert.True(Run(() => client.Result != null && client.Phase == Phase.Results), "result");
        Assert.Equal((byte)1, client.Result!.Entries[0].Id);
        Assert.True(client.Local.PingMs is > 40 and < 200, $"ping {client.Local.PingMs}");
        client.Leave();
        Assert.True(Run(() => host.Players.Count == 1), "host drops the leaver");
    }

    /// <summary>A client whose host goes silent ends the session after the timeout; a full session refuses newcomers.</summary>
    [Fact]
    public void Session_TimeoutAndFull()
    {
        var t = 0.0;
        double Clock() => t;
        using var host = NetSession.Host(0, "HOST", clock: Clock);
        var guests = new List<NetSession>();
        for (var i = 0; i < 4; i++) guests.Add(NetSession.Join(new IPEndPoint(IPAddress.Loopback, host.Link.Port), $"G{i}", clock: Clock));
        for (var k = 0; k < 400; k++)
        {
            t += 0.01;
            host.Update();
            foreach (var g in guests) g.Update();
            Thread.Sleep(k % 10 == 0 ? 1 : 0);
        }
        Assert.Equal(4, host.Players.Count);
        Assert.Single(guests, g => g.Ended == "SESSION FULL");
        // host goes quiet (no more Update): the joined guests give up after TimeoutSeconds
        for (var k = 0; k < 700; k++)
        {
            t += 0.01;
            foreach (var g in guests) g.Update();
        }
        Assert.Equal(3, guests.Count(g => g.Ended == "CONNECTION LOST"));
        foreach (var g in guests) g.Dispose();
    }

    /// <summary>LAN discovery: a broadcast on the game port finds a host on this machine, with its name, players and course.</summary>
    [Fact]
    public void Discovery_FindsHostOnThisMachine()
    {
        using var host = NetSession.Host(0, "TAKUMI");
        host.SetConfig(new RaceConfig("AKINA_NIT"));
        using var lan = new NetDiscovery(host.Link.Port);
        var end = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < end && lan.Games.Count == 0)
        {
            lan.Update();
            host.Update();
            Thread.Sleep(2);
        }
        var g = Assert.Single(lan.Games);
        Assert.Equal(("TAKUMI", 1, NetSession.MaxPlayers, "AKINA_NIT", host.Link.Port), (g.Host, g.Players, g.Max, g.CourseTime, g.EndPoint.Port));
    }
}
