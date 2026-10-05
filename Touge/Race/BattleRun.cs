using Kansei.Physics;
using Touge.Ui;

namespace Touge.Race;

/// <summary>A quick battle as chosen on the command line (--battle &lt;rival&gt; [--rule race|chase] [--lead player|rival]) or a story chapter.</summary>
/// <param name="Leader">Lead/chase: who leads off, 0 the player, 1 the rival.</param>
/// <param name="Terms">Other limits than the rule's defaults (story chapters).</param>
public sealed record BattleSetup(Rivals.Rival Rival, BattleRule Rule, int Leader = 1, BattleTerms? Terms = null)
{
    /// <summary>The AI eases off ahead / pushes behind against a human player (free battle HARD: off).</summary>
    public bool RubberBand { get; init; } = true;
}

/// <summary>Limits of a <see cref="Battle"/> other than its rule's defaults (null = the default).</summary>
public sealed record BattleTerms(float? Breakaway = null, float? DrawGap = null, float TimeLimit = 0, int TimeLimitWinner = 1);

/// <summary>Builds a player-vs-rival <see cref="RaceSession"/> on a <see cref="Drive"/>, and runs one headless for --battle --autodrive.</summary>
public static class BattleRun
{
    /// <summary>Style of the autopilot that drives the player's car in test runs: a good, fairly clean driver.</summary>
    public static readonly RivalStyle Autopilot = new(0.8f, 0.5f, 0.3f);

    /// <summary>
    ///     The player's car (<see cref="Drive.Car"/>, driven by <paramref name="player"/>) and the rival's (its HCAR car, AI) on the
    ///     grid at the spawn; surfaces grip alike for both.
    /// </summary>
    public static RaceSession Create(Drive drive, BattleSetup setup, ICarDriver player, string playerName = "YOU")
    {
        var t = setup.Terms ?? new BattleTerms();
        var battle = new Battle(setup.Rule, drive.Pilot.Length - LapTimer.Gate, setup.Leader)
        {
            Breakaway = t.Breakaway ?? Battle.DefaultBreakaway(setup.Rule), DrawGap = t.DrawGap ?? Battle.DefaultDrawGap,
            TimeLimit = t.TimeLimit, TimeLimitWinner = t.TimeLimitWinner,
        };
        var race = new RaceSession(drive.Ground, drive.Line, drive.RunOutLine, battle);
        race.Add(playerName, drive.Car, player);
        var rival = new Vehicle(setup.Rival.Spec) { SurfaceGrip = drive.Car.SurfaceGrip };
        race.Add(setup.Rival.Name, rival, new AiDriver(new RivalPilot(drive.Line, setup.Rival.Style)));
        drive.ResetTo(0);
        var at = race.Cars[0].Track.Track(drive.Car.Position).Along;
        race.Grid(setup.Rule, at, setup.Leader);
        return race;
    }

    /// <summary>
    ///     --battle … --autodrive: the autopilot drives the player's car against the rival for <paramref name="seconds"/> (or until
    ///     both stopped after the result), one log line per second and a summary. False if the simulation blew up.
    /// </summary>
    public static bool Headless(Drive drive, BattleSetup setup, float seconds, string car)
    {
        var race = Create(drive, setup, new AiDriver(new RivalPilot(drive.Line, Autopilot)), car + " (AUTO)");
        return Run(race, seconds);
    }

    /// <summary>Runs <paramref name="race"/> with logging; <paramref name="afterTick"/> after every tick.</summary>
    public static bool Run(RaceSession race, float seconds, Action? afterTick = null)
    {
        RaceCar p = race.Cars[0], r = race.Cars[1];
        var b = race.Battle!;
        Console.WriteLine($"[Battle] {p.Name} ({p.Vehicle.Spec.Mass:F0} kg) gegen {r.Name} ({r.Vehicle.Spec.Mass:F0} kg), Regel {b.Rule}, Ziel bei {race.Goal:F0} m");
        Console.WriteLine("   t   spieler_m  kmh   rivale_m  kmh  modus   versatz  abstand_s  abstand_m  führt  kontakte");
        var ticks = (int)(seconds / Drive.Dt);
        float decidedTime = -1, maxLateral = 0;
        for (var n = 1; n <= ticks; n++)
        {
            race.Tick(Drive.Dt);
            afterTick?.Invoke();
            foreach (var c in race.Cars)
            {
                var pos = c.Vehicle.Position;
                if (!float.IsFinite(pos.X + pos.Y + pos.Z + c.Vehicle.Velocity.X) || pos.Y < -500)
                {
                    Console.WriteLine($"[Battle] Abbruch nach {n * Drive.Dt:F2} s: {c.Name} ungültig, pos {pos}");
                    return false;
                }
                if (c.FinishedAt == null) maxLateral = MathF.Max(maxLateral, MathF.Abs(c.Lateral));
            }
            if (race.LastContact is { ImpactSpeed: > 0.3f } hit)
                Console.WriteLine($"[Battle]   Kontakt bei {race.Time:F2} s: {hit.ImpactSpeed * 3.6f:F1} km/h Aufprall, Tiefe {hit.Depth * 100:F0} cm");
            if (decidedTime < 0 && b.Outcome != BattleOutcome.None)
            {
                decidedTime = race.Time;
                Console.WriteLine($"[Battle] Entschieden nach {b.DecidedAt:F1} s: {b.Outcome} ({b.Reason}), Abstand {b.Gap:+0.00;-0.00} s / {b.GapMetres:+0;-0} m");
            }
            if (Environment.GetEnvironmentVariable("BATTLE_TRACE") is { } tr && race.Time < float.Parse(tr, System.Globalization.CultureInfo.InvariantCulture) && n % 15 == 0)
                Console.WriteLine($"[Trace] {race.Time:F2} P {p.Along:F1}/{p.Lateral:+0.00;-0.00} {((AiDriver)p.Driver).Pilot.State}  R {r.Along:F1}/{r.Lateral:+0.00;-0.00} {((AiDriver)r.Driver).Pilot.State} off {((AiDriver)r.Driver).Pilot.Offset:F2}");
            if (Environment.GetEnvironmentVariable("BATTLE_WALLS") != null && n % 30 == 0)
                foreach (var c in race.Cars)
                    if (c.Vehicle.WallContacts > 0)
                        Console.WriteLine($"[Wand] {race.Time:F1} {c.Name} bei {c.Along:F0} m quer {c.Lateral:+0.0;-0.0} {((AiDriver)c.Driver).Pilot.State} versatz {((AiDriver)c.Driver).Pilot.Offset:+0.0;-0.0} {c.Vehicle.SpeedKmh:F0} km/h, abstand {race.Cars[0].Along - race.Cars[1].Along:F0} m");
            if (n % 120 == 0)
            {
                var ai = (AiDriver)r.Driver;
                Console.WriteLine($"{race.Time,4:F0} {p.Along,10:F0} {p.Vehicle.SpeedKmh,4:F0} {r.Along,10:F0} {r.Vehicle.SpeedKmh,4:F0}  {ai.Pilot.State,-6} {ai.Pilot.Offset,7:+0.0;-0.0} " +
                                  $"{b.Gap,10:+0.00;-0.00} {b.GapMetres,10:+0;-0}  {(b.PlayerLeads ? "P" : "R"),5} {race.Contacts,9}");
            }
            // decided and both parked: done
            if (decidedTime >= 0 && race.Time > decidedTime + 3 && race.Cars.All(c => c.Vehicle.Velocity.Length() < 0.5f)) break;
        }
        Console.WriteLine($"[Battle] Ergebnis: {b.Outcome} ({b.Reason}) nach {b.DecidedAt:F1} s; Führungswechsel {b.Overtakes} (davon Spieler {b.PlayerPasses}); " +
                          $"Kontakte {race.Contacts} ({race.ContactTicks} Ticks, härtester {race.MaxImpact * 3.6f:F1} km/h); Wandticks Spieler {p.WallTicks}, Rivale {r.WallTicks}; " +
                          $"zurückgesetzt {r.Respawns}; max |quer| {maxLateral:F1} m; Ziel Spieler {Fmt(p.FinishedAt)}, Rivale {Fmt(r.FinishedAt)}");
        return true;
    }

    private static string Fmt(float? t) => t is { } x ? $"{x:F2} s" : "-";
}
