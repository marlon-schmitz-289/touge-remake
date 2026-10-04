using System.Diagnostics;
using System.Net;
using Kansei.Physics;
using Touge.Formats;
using Touge.Race;

namespace Touge.Net;

/// <summary>
///     A multiplayer peer without a window (--headless): host (--host) or client (--join host:port) of a real UDP session, the car
///     driven by the autopilot (--bot; without it the car stands), ready in the lobby at once; the host starts as soon as
///     <see cref="Options.MinPlayers"/> are in and ready. Runs in real time at the physics rate, logs the session, once a
///     second every car (place on the line, speed, how the remote one was sampled and its correction), ping, packets and
///     losses, and a summary per race. Tests online play with one game window (or none) on one machine.
/// </summary>
public static class Headless
{
    public sealed record Options(string Iso, IPEndPoint? Join, int Port, bool Bot, string Name, string Car, int Paint, RaceConfig Config,
        int MinPlayers, float Seconds, NetSim? Sim, int Races);

    public static int Run(Options o)
    {
        using var iso = new Iso9660(o.Iso);
        NetSession Open()
        {
            var s = o.Join == null ? NetSession.Host(o.Port, o.Name, o.Sim, log: Console.WriteLine) : NetSession.Join(o.Join, o.Name, o.Sim, log: Console.WriteLine);
            s.SetLocal(o.Car, (byte)o.Paint, o.Bot);
            s.SetConfig(o.Config);
            return s;
        }
        var net = Open();
        try
        {
            var who = net.IsHost ? "Host" : "Bot";
            Console.WriteLine($"[{who}] {o.Name} mit {o.Car}{(o.Sim != null ? $", Netzsimulation {o.Sim.LatencyMs:F0} ms ±{o.Sim.JitterMs:F0} ms, Verlust {o.Sim.Loss:P0} je Richtung" : "")}");
            var clock = Stopwatch.StartNew();
            double last = 0, acc = 0, nextLog = 0, resultAt = -1;
            Drive? drive = null;
            NetRace? race = null;
            int loaded = -1, races = 0, logged = -1;
            var stats = new Dictionary<byte, (double Err, double Max, int N)>();
            while (clock.Elapsed.TotalSeconds < o.Seconds)
            {
                var now = clock.Elapsed.TotalSeconds;
                acc += Math.Min(now - last, 0.25);
                last = now;
                net.Update();
                if (net.Ended == "NO ANSWER" && races == 0)
                {
                    // the host is not up yet (its window may wait for another): try again until the time limit
                    net.Dispose();
                    net = Open();
                    continue;
                }
                if (net.Ended != null)
                {
                    Console.WriteLine($"[{who}] Sitzung beendet: {net.Ended}");
                    return races > 0 ? 0 : 2;
                }
                if (net.IsHost && net.Phase == Phase.Lobby && net.Players.Count >= o.MinPlayers && net.CanStart) net.StartRace();
                if (net.Phase == Phase.Loading && loaded != net.RaceId)
                {
                    var c = net.Config;
                    var sw = Stopwatch.StartNew();
                    drive = new Drive(iso, c.CourseTime, c.Reverse, CarSpecs.All.TryGetValue(o.Car, out var spec) ? spec : CarSpec.AE86);
                    (loaded, race) = (net.RaceId, null);
                    net.MarkLoaded();
                    stats.Clear();
                    Console.WriteLine($"[{who}] Rennen {net.RaceId}: {c.CourseTime}{(c.Reverse ? " rückwärts" : "")}, Regel {c.Rule}, geladen in {sw.ElapsedMilliseconds} ms");
                }
                if (net.Phase is Phase.Countdown or Phase.Race or Phase.Results && race == null && drive != null && loaded == net.RaceId)
                {
                    ICarDriver driver = o.Bot ? new AiDriver(new RivalPilot(drive.Line, BattleRun.Autopilot)) : new ManualDriver();
                    race = NetRace.Create(drive, net, driver);
                    Console.WriteLine($"[{who}] Startaufstellung: {string.Join(", ", race.ByPlayer.Select(kv => $"#{kv.Key} {net.NameOf(kv.Key)} bei {kv.Value.Along:F0} m / {kv.Value.Lateral:+0.0;-0.0}"))}");
                    Console.WriteLine($"[{who}]    t   eigene_m  km/h  | je Gegner: m  km/h  Abtastung  Korrektur_cm  Alter_ms  | ping_ms  gesendet/empfangen  verloren");
                }
                if (race != null && net.RaceTime >= 0)
                    for (; acc >= Touge.Drive.Dt; acc -= Touge.Drive.Dt)
                    {
                        race.Tick(Touge.Drive.Dt);
                        foreach (var (id, car) in race.ByPlayer)
                            if (car.Driver is RemoteDriver rd && rd.LastKind != SampleKind.None)
                            {
                                var s = stats.GetValueOrDefault(id);
                                stats[id] = (s.Err + rd.LastError, Math.Max(s.Max, rd.LastError), s.N + 1);
                            }
                    }
                else acc = 0;
                if (race != null && net.RaceTime >= 0 && now >= nextLog && net.Phase is Phase.Race or Phase.Results)
                {
                    nextLog = now + 1;
                    Log(who, net, race);
                }
                if (net.Result != null && logged != net.RaceId && race != null)
                {
                    logged = net.RaceId;
                    races++;
                    resultAt = now;
                    Summary(who, net, race, stats);
                }
                if (resultAt >= 0 && now - resultAt > 4)
                {
                    resultAt = -1;
                    if (races >= o.Races)
                    {
                        net.Leave("DONE");
                        return 0;
                    }
                    if (net.IsHost) net.StartRace(); // rematch
                }
                Thread.Sleep(1);
            }
            Console.WriteLine($"[{who}] Zeitlimit {o.Seconds:F0} s erreicht");
            net.Leave("TIME LIMIT");
            return races > 0 ? 0 : 2;
        }
        finally
        {
            net.Dispose();
        }
    }

    private static void Log(string who, NetSession net, NetRace race)
    {
        var me = race.Local;
        var line = $"[{who}] {net.RaceTime,5:F1} {me.Along,9:F0} {me.Vehicle.SpeedKmh,5:F0} ";
        foreach (var (id, car) in race.ByPlayer)
        {
            if (car == me || car.Driver is not RemoteDriver rd) continue;
            var p = rd.Player;
            var age = p.Snapshots.Latest is { } l ? (net.RaceTime - l.Time) * 1000 : 0;
            line += $" | #{id} {(p.Connected ? $"{car.Along,6:F0} {car.Vehicle.SpeedKmh,4:F0} {rd.LastKind,-12} {rd.LastError * 100,5:F0} {age,5:F0}" : "weg")}";
        }
        var ping = net.IsHost ? string.Join("/", net.Players.Where(p => !p.IsLocal).Select(p => p.PingMs)) : net.Local.PingMs.ToString();
        var lost = net.Players.Where(p => !p.IsLocal).Sum(p => p.Snapshots.Lost);
        var recv = net.Players.Where(p => !p.IsLocal).Sum(p => p.Snapshots.Received);
        Console.WriteLine(line + $"  | {ping,6} {net.Link.Sent,6}/{net.Link.Received,-6} {(recv + lost > 0 ? (float)lost / (recv + lost) : 0),6:P1}");
    }

    private static void Summary(string who, NetSession net, NetRace race, Dictionary<byte, (double Err, double Max, int N)> stats)
    {
        var r = net.Result!;
        Console.WriteLine($"[{who}] Ergebnis Rennen {r.RaceId} ({r.Reason}): " +
                          string.Join(", ", r.Entries.OrderBy(e => e.Place).Select(e => $"{e.Place}. {net.NameOf(e.Id)} {(e.Time >= 0 ? $"{e.Time:F2} s" : $"DNF bei {e.Along:F0} m")}")));
        Console.WriteLine($"[{who}] Kontakte {race.Race.Contacts} (härtester {race.Race.MaxImpact * 3.6f:F1} km/h), eigene Zielzeit {(race.LocalFinish is { } f ? $"{f:F2} s" : "-")}");
        foreach (var (id, car) in race.ByPlayer)
            if (car.Driver is RemoteDriver rd)
            {
                var b = rd.Player.Snapshots;
                var s = stats.GetValueOrDefault(id);
                var ticks = Math.Max(1, rd.Interpolated + rd.Extrapolated + rd.Held);
                Console.WriteLine($"[{who}] #{id} {rd.Player.Name}: Zustände empfangen {b.Received}, verloren {b.Lost} ({(float)b.Lost / Math.Max(1, b.Received + b.Lost):P1}), " +
                                  $"verspätet {b.Late}, doppelt {b.Duplicates}; Abtastung interpoliert {(float)rd.Interpolated / ticks:P0}, extrapoliert {(float)rd.Extrapolated / ticks:P0}, " +
                                  $"gehalten {(float)rd.Held / ticks:P0}; Korrektur Ø {(s.N > 0 ? s.Err / s.N * 100 : 0):F1} cm, max {s.Max * 100:F0} cm; Ping {net.PingTo(rd.Player)} ms");
            }
        Console.WriteLine($"[{who}] Pakete gesendet {net.Link.Sent} ({net.Link.BytesSent / 1024} KiB), empfangen {net.Link.Received} ({net.Link.BytesReceived / 1024} KiB), simuliert verworfen {net.Link.SimDropped}");
    }
}
