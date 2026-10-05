using System.Numerics;
using Kansei.Physics;
using Touge.Race;

namespace Touge.Tests;

public class BattleTests
{
    const float Dt = 1f / 120;

    /// <summary>Runs <paramref name="b"/> until decided or <paramref name="seconds"/>, the cars at along(t).</summary>
    static void Run(Battle b, Func<float, float> player, Func<float, float> rival, float seconds = 600)
    {
        for (var t = Dt; t <= seconds && b.Outcome == BattleOutcome.None; t += Dt) b.Update(Dt, player(t), rival(t));
    }

    [Fact]
    public void Time_gap_is_how_long_ago_the_leader_was_here()
    {
        // both at 20 m/s, the rival 2 s behind
        var b = new Battle(BattleRule.Race, 5000) { Breakaway = 0 };
        Run(b, t => 20 * t + 40, t => 20 * t, 10);
        Assert.True(b.PlayerLeads);
        Assert.InRange(b.Gap, 1.98f, 2.02f);
        Assert.InRange(b.GapMetres, 39.9f, 40.1f);
        Assert.Equal(BattleOutcome.None, b.Outcome);
    }

    [Fact]
    public void Race_first_to_the_goal_wins()
    {
        var b = new Battle(BattleRule.Race, 1000) { Breakaway = 0 };
        Run(b, t => 30 * t, t => 31 * t);
        Assert.Equal(BattleOutcome.Lose, b.Outcome);
        Assert.Equal("GOAL", b.Reason);
        Assert.InRange(b.DecidedAt, 1000 / 31f - 0.02f, 1000 / 31f + 0.02f);
    }

    [Fact]
    public void Race_ends_early_at_the_breakaway_gap()
    {
        // player 30 m/s, rival 25 m/s: the time gap grows by 1/6 s per second, 8 s after ~48 s
        var b = new Battle(BattleRule.Race, 100000);
        Run(b, t => 30 * t, t => 25 * t);
        Assert.Equal(BattleOutcome.Win, b.Outcome);
        Assert.Equal("BREAKAWAY", b.Reason);
        Assert.InRange(b.Gap, 8, 8.1f);
        Assert.InRange(b.DecidedAt, 47, 49);
    }

    [Fact]
    public void A_lead_change_counts_only_once_it_holds()
    {
        // the rival noses ahead for 0.05 s (less than the original's 6 frames): no overtake; then for good
        var b = new Battle(BattleRule.Race, 10000, startLeader: 0) { Breakaway = 0 };
        Run(b, t => 20 * t + 1, t => t is > 1 and < 1.05f ? 20 * t + 2 : t > 2 ? 21 * t : 20 * t, 1.5f);
        Assert.Equal(0, b.Overtakes);
        Assert.True(b.PlayerLeads);
        Run(b, t => 20 * t + 1, t => 21 * t, 4);
        Assert.Equal(1, b.Overtakes);
        Assert.False(b.PlayerLeads);
    }

    [Fact]
    public void Lead_chase_chaser_wins_by_passing_and_holding_the_lead()
    {
        // the player chases from 30 m behind and passes at 15 s; wins once the lead has held 1.5 s
        var b = new Battle(BattleRule.LeadChase, 10000, startLeader: 1);
        Run(b, t => 22 * t, t => 30 + 20 * t);
        Assert.Equal(BattleOutcome.Win, b.Outcome);
        Assert.Equal("OVERTAKE", b.Reason);
        Assert.InRange(b.DecidedAt, 16.5f - 0.05f, 16.5f + 0.05f);

        // a pass off the launch (at 5 s) does not decide before the start grace (10 s) + the hold
        var launch = new Battle(BattleRule.LeadChase, 10000, startLeader: 1);
        Run(launch, t => 22 * t, t => 10 + 20 * t);
        Assert.Equal("OVERTAKE", launch.Reason);
        Assert.InRange(launch.DecidedAt, launch.StartGrace + launch.PassHold - 0.05f, launch.StartGrace + launch.PassHold + 0.05f);
    }

    [Fact]
    public void Race_level_on_the_grid_counts_the_player_ahead()
    {
        var b = new Battle(BattleRule.Race, 1000);
        b.Update(Dt, 0, 0);
        Assert.True(b.PlayerLeads);
        Assert.Equal(0, b.Overtakes);
    }

    [Fact]
    public void Lead_chase_leader_wins_with_a_gap_and_a_glued_chaser_draws()
    {
        var pull = new Battle(BattleRule.LeadChase, 10000, startLeader: 0);
        Run(pull, t => 10 + 25 * t, t => 20 * t);
        Assert.Equal((BattleOutcome.Win, "BREAKAWAY"), (pull.Outcome, pull.Reason));
        Assert.InRange(pull.Gap, 4, 4.1f);

        // the chaser stays 0.5 s behind to the goal: no winner
        var glued = new Battle(BattleRule.LeadChase, 1000, startLeader: 1);
        Run(glued, t => 20 * t, t => 10 + 20 * t);
        Assert.Equal((BattleOutcome.Draw, "NO GAP"), (glued.Outcome, glued.Reason));

        // 2 s in hand at the goal: the leader wins
        var held = new Battle(BattleRule.LeadChase, 1000, startLeader: 1);
        Run(held, t => 20 * t, t => 40 + 20 * t);
        Assert.Equal((BattleOutcome.Lose, "GOAL"), (held.Outcome, held.Reason));
    }

    [Fact]
    public void Rivals_are_found_by_id_car_or_name()
    {
        Assert.Equal("FD3S", Rivals.Find("keisuke").Car);
        Assert.Equal("KEISUKE TAKAHASHI", Rivals.Find("FD3S").Name);
        Assert.Equal("R32", Rivals.Find("Nakazato").Car);
        Assert.Equal("S2000", Rivals.Find("s2000").Car); // nobody's car: a nameless street racer
        Assert.Throws<ArgumentException>(() => Rivals.Find("nobody"));
        Assert.All(Rivals.All, r => Assert.Contains(r.Car, Touge.Formats.CarPaint.Cars));
    }

    /// <summary>Flat plane y = 0, no walls.</summary>
    sealed class Plane : IGround
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

    [Fact]
    public void Session_two_ai_cars_race_side_by_side_to_the_goal_and_stop_on_the_run_out()
    {
        // 1.5 km straight, 200 m run-out: grid side by side, both AI, decided at the goal, both parked before its end
        Vector3[] line = [.. Enumerable.Range(0, 301).Select(i => new Vector3(0, 0, i * 5))];
        Vector3[] runOut = [.. Enumerable.Range(0, 41).Select(i => new Vector3(0, 0, 1500 + i * 5))];
        var race = new RaceSession(new Plane(), line, runOut, new Battle(BattleRule.Race, 1500 - Touge.Ui.LapTimer.Gate) { Breakaway = 0 });
        var style = new RivalStyle(0.8f, 0.5f, 0);
        race.Add("A", new Vehicle(CarSpec.AE86), new AiDriver(new RivalPilot(line, style)));
        race.Add("B", new Vehicle(CarSpec.AE86), new AiDriver(new RivalPilot(line, style)));
        race.Grid(BattleRule.Race, 0);
        Assert.InRange(race.Cars[0].Lateral, 1.5f, 1.7f);
        Assert.InRange(race.Cars[1].Lateral, -1.7f, -1.5f);
        for (var t = 0f; t < 120; t += Dt) race.Tick(Dt);
        Assert.NotEqual(BattleOutcome.None, race.Battle!.Outcome);
        Assert.Equal(0, race.Contacts); // equal cars, each on its side
        Assert.All(race.Cars, c => Assert.NotNull(c.FinishedAt));
        Assert.All(race.Cars, c => Assert.True(c.Vehicle.Velocity.Length() < 0.5f && c.Along < 1700, $"{c.Name} parked at {c.Along:F0} m"));
    }

    [Fact]
    public void Session_auto_run_never_runs_into_a_finisher_stopping_on_a_short_run_out()
    {
        // lead/chase on a 1 km straight with a 25 m run-out: the leader stops hard behind the goal, the chaser right
        // behind it coasts after the decision — it is held behind the stopping car (SHIONA/SHOMARU: 23–33 km/h knocks, here 148 km/h without)
        Vector3[] line = [.. Enumerable.Range(0, 201).Select(i => new Vector3(0, 0, i * 5))];
        Vector3[] runOut = [.. Enumerable.Range(0, 6).Select(i => new Vector3(0, 0, 1000 + i * 5))];
        var race = new RaceSession(new Plane(), line, runOut, new Battle(BattleRule.LeadChase, 1000 - Touge.Ui.LapTimer.Gate, 0) { Breakaway = 1000 });
        var style = new RivalStyle(0.8f, 1, 0);
        race.Add("A", new Vehicle(CarSpec.AE86), new AiDriver(new RivalPilot(line, style)));
        race.Add("B", new Vehicle(CarSpec.AE86), new AiDriver(new RivalPilot(line, style)));
        race.Grid(BattleRule.LeadChase, 0, 0);
        for (var t = 0f; t < 90; t += Dt) race.Tick(Dt);
        Assert.NotEqual(BattleOutcome.None, race.Battle!.Outcome);
        Assert.True(race.MaxImpact * 3.6f < 5, $"hardest contact {race.MaxImpact * 3.6f:F1} km/h");
        Assert.All(race.Cars, c => Assert.True(c.Vehicle.Velocity.Length() < 0.5f, $"{c.Name} parked"));
    }
}
