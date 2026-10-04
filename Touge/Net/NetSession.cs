using System.Net;

namespace Touge.Net;

/// <summary>A player of a session: what it chose, how the host hears it, and (remote) its car's last states.</summary>
public sealed class NetPlayer(byte id, string name, bool local)
{
    public byte Id { get; internal set; } = id;
    public string Name { get; internal set; } = name;
    public string Car { get; internal set; } = "AE86T";
    public byte Paint { get; internal set; }
    public bool Ready { get; internal set; }
    /// <summary>Race number this player has loaded the course of (−1: none yet).</summary>
    public int LoadedRace { get; internal set; } = -1;
    /// <summary>Round trip in ms: the host's measurement of a client; a client's own entry holds its round trip to the host.</summary>
    public ushort PingMs { get; internal set; }
    public bool IsLocal { get; } = local;
    /// <summary>False once it left or timed out during a race (it stays listed until the race is over: did not finish).</summary>
    public bool Connected { get; internal set; } = true;
    public SnapshotBuffer Snapshots { get; } = new();
    internal IPEndPoint? EndPoint;
    internal uint Token;
    internal double LastHeard;
}

/// <summary>
///     A multiplayer session over UDP, host or client, polled once per frame (<see cref="Update"/>), no threads. Star topology:
///     clients talk to the host only, the host relays car states between clients. Reliability by repetition of whole states
///     instead of acks: clients send <see cref="Hello"/> (join + keep-alive + their choices) and the host sends <see cref="Lobby"/>
///     (everything) several times a second; car states go at 30 Hz, latest wins; results repeat until the next race.
///     The host decides phases (lobby → loading → countdown → race → results), the moment of GO (clients turn "seconds to
///     GO" plus half their round trip into their own clock, so all race clocks agree within the jitter) and the result. Pings
///     both ways (round trip per player in the lobby list), a peer silent for <see cref="TimeoutSeconds"/> is gone: the host
///     drops a client (during a race: kept as disconnected, it does not finish), a client ends the session (<see cref="Ended"/>).
/// </summary>
public sealed class NetSession : IDisposable
{
    public const int DefaultPort = 47860, MaxPlayers = 4;
    public const double TimeoutSeconds = 5, HelloEvery = 0.2, LobbyEvery = 0.2, PingEvery = 0.5;
    /// <summary>Countdown from "everybody has loaded" to GO: the game's telop and 3-2-1 (<see cref="Ui.Menu.GoAt"/>) plus a margin for latency.</summary>
    public const float CountdownSeconds = Ui.Menu.GoAt + 0.6f;
    /// <summary>Players that have not loaded after this long do not hold the others up.</summary>
    public const double LoadTimeout = 30;

    private readonly NetLink _link;
    private readonly Func<double> _clock;
    private readonly IPEndPoint? _host;
    private readonly List<double> _goEstimates = [];
    private double _nextHello, _nextLobby, _nextPing, _phaseSince, _started;
    private uint _seq;
    private float _rttHost = 0.1f;
    /// <summary>The last round trips to the host: their minimum is the path's own delay (a hitch or queue only adds), used to place GO.</summary>
    private readonly Queue<float> _rtts = new();
    private float RttFloor => _rtts.Count > 0 ? _rtts.Min() : _rttHost;
    private bool _welcomed;

    public bool IsHost { get; }
    public NetPlayer Local { get; }
    /// <summary>Everybody in the session, the local player included, ordered by id.</summary>
    public List<NetPlayer> Players { get; } = [];
    public Phase Phase { get; private set; }
    public RaceConfig Config { get; private set; } = new();
    /// <summary>Counts the races of this session; loading and results refer to it.</summary>
    public int RaceId { get; private set; }
    /// <summary>Local clock time of GO (NaN before the countdown); <see cref="RaceTime"/> = seconds since GO (negative before).</summary>
    public double GoAt { get; private set; } = double.NaN;
    public float RaceTime => double.IsNaN(GoAt) ? float.NegativeInfinity : (float)(_clock() - GoAt);
    /// <summary>The host's result of race <see cref="RaceId"/> (null until decided).</summary>
    public Result? Result { get; private set; }
    /// <summary>Why the session is over for us (host left, connection lost, refused), null while it runs.</summary>
    public string? Ended { get; private set; }
    /// <summary>The client has heard from the host (has an id and the lobby).</summary>
    public bool Joined => IsHost || _welcomed;
    public NetLink Link => _link;
    /// <summary>Host: seconds from "all loaded" to GO (<see cref="CountdownSeconds"/>; tests shorten it).</summary>
    public double Countdown { get; set; } = CountdownSeconds;
    public Action<string>? Log { get; set; }
    public double Now => _clock();

    private NetSession(bool host, string name, NetLink link, Func<double> clock, IPEndPoint? hostEndPoint)
    {
        (IsHost, _link, _clock, _host) = (host, link, clock, hostEndPoint);
        Local = new NetPlayer(0, Protocol.Clip(name), true) { Token = (uint)Random.Shared.Next() | 1, LastHeard = clock() };
        Players.Add(Local);
        _started = clock();
    }

    /// <summary>Hosts on <paramref name="port"/> (UDP; forward it on the router for players outside the LAN, README).</summary>
    public static NetSession Host(int port, string name, NetSim? sim = null, Func<double>? clock = null, Action<string>? log = null)
    {
        clock ??= StopwatchClock();
        var s = new NetSession(true, name, new NetLink(port, clock, sim, 11), clock, null) { Log = log };
        s.Local.Ready = true;
        s.Say($"Host auf UDP-Port {s._link.Port}");
        return s;
    }

    /// <summary>Joins the host at <paramref name="host"/>; <see cref="Joined"/> once it answered.</summary>
    public static NetSession Join(IPEndPoint host, string name, NetSim? sim = null, Func<double>? clock = null, Action<string>? log = null)
    {
        clock ??= StopwatchClock();
        var s = new NetSession(false, name, new NetLink(0, clock, sim, 23), clock, host) { Log = log };
        s.Local.Id = 255; // until the host gives one
        s.Say($"Verbinde mit {host} (lokaler Port {s._link.Port})");
        return s;
    }

    public static Func<double> StopwatchClock()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        return () => sw.Elapsed.TotalSeconds;
    }

    private void Say(string text) => Log?.Invoke($"[Netz] {_clock():F2} {text}");

    // ------------------------------------------------------------ what the game sets

    /// <summary>The local player's car, paint and ready flag (sent with the next <see cref="Hello"/> / lobby).</summary>
    public void SetLocal(string car, byte paint, bool ready)
    {
        (Local.Car, Local.Paint) = (car, paint);
        Local.Ready = IsHost || ready;
    }

    /// <summary>The course of the current race is loaded here.</summary>
    public void MarkLoaded() => Local.LoadedRace = RaceId;

    /// <summary>Host, in the lobby: course, direction, weather, rule.</summary>
    public void SetConfig(RaceConfig config)
    {
        if (IsHost && Phase == Phase.Lobby) Config = config;
    }

    /// <summary>Host: every connected client is ready (the host is by starting), at least two players.</summary>
    public bool CanStart => IsHost && Phase is Phase.Lobby or Phase.Results && Players.Count(p => p.Connected) >= 2
                            && Players.All(p => p.IsLocal || !p.Connected || p.Ready);

    /// <summary>Host: everybody loads race <see cref="RaceId"/> + 1 (from the lobby, or a rematch from the results).</summary>
    public void StartRace()
    {
        if (!IsHost) return;
        Players.RemoveAll(p => !p.Connected);
        RaceId++;
        Enter(Phase.Loading);
        Say($"Rennen {RaceId}: {Config.CourseTime}{(Config.Reverse ? " rückwärts" : "")}{(Config.Fog ? " Nebel" : "")}, {Config.Rule}, {Players.Count} Spieler");
    }

    /// <summary>Host: back to the lobby (after the results, or to abort a race).</summary>
    public void BackToLobby()
    {
        if (!IsHost) return;
        Players.RemoveAll(p => !p.Connected);
        Enter(Phase.Lobby);
    }

    /// <summary>Host: the race is decided (<see cref="Referee"/>); repeated to everybody until the next race.</summary>
    public void Publish(Result result)
    {
        if (!IsHost || Phase != Phase.Race) return;
        Result = result;
        Enter(Phase.Results);
        Say($"Ergebnis ({result.Reason}): {string.Join(", ", result.Entries.OrderBy(e => e.Place).Select(e => $"{e.Place}. {NameOf(e.Id)} {(e.Time >= 0 ? $"{e.Time:F2} s" : "DNF")}"))}");
    }

    /// <summary>
    ///     Round trip (ms) between us and <paramref name="p"/>: the host measures each client; a client's own round trip is to
    ///     the host, another client's goes through the host (theirs + ours).
    /// </summary>
    public int PingTo(NetPlayer p) => IsHost ? p.PingMs : p.Id == 0 ? Local.PingMs : p.PingMs + Local.PingMs;

    /// <summary>A player's name, also of one that has left since (results).</summary>
    public string NameOf(byte id) => Players.FirstOrDefault(p => p.Id == id)?.Name ?? _names.GetValueOrDefault(id) ?? $"#{id}";

    private readonly Dictionary<byte, string> _names = [];

    private void Enter(Phase phase)
    {
        Phase = phase;
        _phaseSince = _clock();
        if (phase is Phase.Lobby or Phase.Loading)
        {
            (GoAt, Result) = (double.NaN, null);
            _goEstimates.Clear();
            foreach (var p in Players) p.Snapshots.Clear();
        }
        if (IsHost && phase == Phase.Countdown) GoAt = _clock() + Countdown;
        _nextLobby = 0; // tell everybody now
    }

    /// <summary>Our car's state (race clock time inside), to the host or, from the host, to everybody.</summary>
    public void SendState(CarState s)
    {
        if (!Joined || Ended != null) return;
        var packet = Protocol.Encode(new StateMsg(RaceId, s with { Id = Local.Id, Seq = ++_seq }));
        if (IsHost)
            foreach (var p in Players)
            {
                if (!p.IsLocal && p.Connected) _link.Send(packet, p.EndPoint!);
            }
        else _link.Send(packet, _host!);
    }

    /// <summary>Leaves (the host ends the session for everybody).</summary>
    public void Leave(string reason = "LEFT")
    {
        if (Ended != null) return;
        var bye = Protocol.Encode(new Bye(IsHost ? (byte)0 : Local.Id < MaxPlayers ? Local.Id : (byte)0, IsHost ? "HOST LEFT" : reason));
        for (var i = 0; i < 3; i++)
            if (IsHost)
                foreach (var p in Players.Where(p => !p.IsLocal)) _link.Send(bye, p.EndPoint!);
            else if (Local.Id < MaxPlayers) _link.Send(bye, _host!);
        Ended = reason;
        Say($"Sitzung verlassen ({reason})");
    }

    // ------------------------------------------------------------ the loop

    /// <summary>Receives and handles everything that arrived, sends what is due, notices peers that went quiet.</summary>
    public void Update()
    {
        if (Ended != null)
        {
            while (_link.Receive(out _, out _)) { }
            return;
        }
        while (_link.Receive(out var packet, out var from))
            if (Protocol.Decode(packet) is { } m) Handle(m, packet, from);
        var now = _clock();
        if (IsHost) HostTick(now);
        else ClientTick(now);
    }

    private void HostTick(double now)
    {
        foreach (var p in Players.ToArray())
        {
            if (p.IsLocal || !p.Connected || now - p.LastHeard < TimeoutSeconds) continue;
            Say($"{p.Name} (#{p.Id}) antwortet nicht mehr");
            Drop(p);
        }
        switch (Phase)
        {
            case Phase.Loading when Players.All(p => !p.Connected || p.LoadedRace == RaceId) || now - _phaseSince > LoadTimeout:
                Enter(Phase.Countdown);
                Say($"Alle geladen, GO in {Countdown:F1} s");
                break;
            case Phase.Countdown when now >= GoAt:
                Enter(Phase.Race);
                break;
        }
        if (now >= _nextLobby)
        {
            _nextLobby = now + LobbyEvery;
            var players = Players.Where(p => p.Connected).Select(Info).ToArray();
            var toGo = Phase == Phase.Countdown ? (float)(GoAt - now) : 0;
            foreach (var p in Players.Where(p => !p.IsLocal && p.Connected))
            {
                _link.Send(Protocol.Encode(new Lobby(p.Id, Phase, RaceId, Config, players, toGo)), p.EndPoint!);
                if (Result != null) _link.Send(Protocol.Encode(Result), p.EndPoint!);
            }
        }
        if (now >= _nextPing)
        {
            _nextPing = now + PingEvery;
            var ping = Protocol.Encode(new Ping(now));
            foreach (var p in Players.Where(p => !p.IsLocal && p.Connected)) _link.Send(ping, p.EndPoint!);
        }
    }

    private void ClientTick(double now)
    {
        var silent = now - (_welcomed ? Players.FirstOrDefault(p => p.Id == 0)?.LastHeard ?? _started : _started);
        if (silent > TimeoutSeconds)
        {
            Ended = _welcomed ? "CONNECTION LOST" : "NO ANSWER";
            Say(_welcomed ? "Verbindung zum Host verloren" : $"Keine Antwort von {_host}");
            return;
        }
        if (now >= _nextHello)
        {
            _nextHello = now + HelloEvery;
            _link.Send(Protocol.Encode(new Hello(Local.Token, Local.Name, Local.Car, Local.Paint, Local.Ready, Local.LoadedRace)), _host!);
        }
        if (_welcomed && now >= _nextPing)
        {
            _nextPing = now + PingEvery;
            _link.Send(Protocol.Encode(new Ping(now)), _host!);
        }
    }

    private static PlayerInfo Info(NetPlayer p) => new(p.Id, p.Name, p.Car, p.Paint, p.Ready, p.PingMs, p.LoadedRace);

    private void Drop(NetPlayer p)
    {
        if (Phase is Phase.Lobby or Phase.Results) Players.Remove(p);
        else p.Connected = false;
        _nextLobby = 0;
    }

    private void Handle(INetMessage m, byte[] packet, IPEndPoint from)
    {
        var now = _clock();
        if (IsHost) HostHandle(m, packet, from, now);
        else if (from.Equals(_host)) ClientHandle(m, now);
    }

    private void HostHandle(INetMessage m, byte[] packet, IPEndPoint from, double now)
    {
        var sender = Players.FirstOrDefault(p => !p.IsLocal && p.EndPoint != null && p.EndPoint.Equals(from));
        switch (m)
        {
            case Discover:
                _link.Send(Protocol.Encode(new Announce(Local.Name, (byte)Players.Count(p => p.Connected), MaxPlayers, Config.CourseTime, Phase)), from);
                return;
            case Hello h:
                sender ??= Players.FirstOrDefault(p => !p.IsLocal && p.Token == h.Token);
                if (sender == null)
                {
                    var refuse = Players.Count(p => p.Connected) >= MaxPlayers ? "SESSION FULL" : Phase != Phase.Lobby ? "RACE IN PROGRESS" : null;
                    if (refuse != null)
                    {
                        _link.Send(Protocol.Encode(new Bye(0, refuse)), from);
                        return;
                    }
                    var id = (byte)Enumerable.Range(1, MaxPlayers - 1).First(i => Players.All(p => p.Id != i));
                    sender = new NetPlayer(id, h.Name, false) { Token = h.Token };
                    Players.Add(sender);
                    Players.Sort((a, b) => a.Id.CompareTo(b.Id));
                    Say($"{h.Name} (#{id}) von {from} beigetreten");
                    _nextLobby = 0;
                }
                sender.EndPoint = from; // a NAT may change the client's port: the token keeps it the same player
                (sender.Name, sender.Car, sender.Paint, sender.Ready, sender.LoadedRace) = (h.Name, h.Car, h.Paint, h.Ready, h.LoadedRace);
                _names[sender.Id] = h.Name;
                break;
            case Ping p:
                _link.Send(Protocol.Encode(new Pong(p.Time)), from);
                break;
            case Pong p when sender != null:
                sender.PingMs = Smooth(sender.PingMs, now - p.Time);
                break;
            case StateMsg s when sender != null && s.State.Id == sender.Id && sender.Connected && s.RaceId == RaceId:
                sender.Snapshots.Add(s.State);
                foreach (var other in Players) // relay as it came
                    if (!other.IsLocal && other != sender && other.Connected) _link.Send(packet, other.EndPoint!);
                break;
            case Bye b when sender != null && b.Id == sender.Id && sender.Connected:
                Say($"{sender.Name} (#{sender.Id}) hat verlassen");
                Drop(sender);
                return;
        }
        if (sender != null) sender.LastHeard = now;
    }

    private static ushort Smooth(ushort ms, double rtt) => (ushort)Math.Clamp(ms == 0 ? rtt * 1000 : ms * 0.8 + rtt * 1000 * 0.2, 1, 9999);

    private void ClientHandle(INetMessage m, double now)
    {
        var host = Players.FirstOrDefault(p => p.Id == 0 && !p.IsLocal);
        if (host != null) host.LastHeard = now;
        switch (m)
        {
            case Lobby l:
                OnLobby(l, now);
                break;
            case Ping p:
                _link.Send(Protocol.Encode(new Pong(p.Time)), _host!);
                break;
            case Pong p:
                var rtt = (float)(now - p.Time);
                _rttHost = _rtts.Count == 0 ? rtt : rtt * 0.2f + _rttHost * 0.8f;
                Local.PingMs = (ushort)Math.Clamp(_rttHost * 1000, 1, 9999);
                _rtts.Enqueue(rtt);
                if (_rtts.Count > 16) _rtts.Dequeue();
                break;
            case StateMsg s when s.State.Id != Local.Id && s.RaceId == RaceId:
                Players.FirstOrDefault(p => p.Id == s.State.Id && !p.IsLocal)?.Snapshots.Add(s.State);
                break;
            case Result r when r.RaceId == RaceId && Result == null:
                Result = r;
                Say($"Ergebnis vom Host ({r.Reason})");
                break;
            case Bye { Id: 0 } b when Ended == null:
                Ended = string.IsNullOrEmpty(b.Reason) ? "HOST LEFT" : b.Reason;
                Say($"Host beendet: {Ended}");
                break;
        }
    }

    private void OnLobby(Lobby l, double now)
    {
        if (!_welcomed) Say($"Aufgenommen als #{l.YouId}");
        _welcomed = true;
        Local.Id = l.YouId;
        var phaseChanged = l.Phase != Phase || l.RaceId != RaceId;
        if (l.RaceId != RaceId) RaceId = l.RaceId;
        if (phaseChanged) Enter(l.Phase);
        Config = l.Config;
        // players: keep the objects (their snapshot buffers), drop the ones the host no longer lists
        foreach (var info in l.Players)
        {
            if (info.Id == l.YouId) continue;
            var p = Players.FirstOrDefault(x => x.Id == info.Id && !x.IsLocal);
            if (p == null)
            {
                Players.Add(p = new NetPlayer(info.Id, info.Name, false) { LastHeard = now });
                Say($"Spieler {info.Name} (#{info.Id})");
            }
            (p.Name, p.Car, p.Paint, p.Ready, p.PingMs, p.LoadedRace, p.Connected) = (info.Name, info.Car, info.Paint, info.Ready, info.PingMs, info.LoadedRace, true);
            _names[info.Id] = info.Name;
            if (info.Id == 0) p.LastHeard = now;
        }
        foreach (var p in Players.ToArray())
            if (!p.IsLocal && l.Players.All(i => i.Id != p.Id))
            {
                if (Phase is Phase.Lobby or Phase.Results) Players.Remove(p);
                else p.Connected = false;
            }
        Players.Sort((a, b) => a.Id.CompareTo(b.Id));
        // GO on our clock: the host's "seconds to go" when it sent, minus half the round trip it took; the median of all estimates
        if (l.Phase == Phase.Countdown)
        {
            _goEstimates.Add(now + l.SecondsToGo - RttFloor / 2);
            var sorted = _goEstimates.Order().ToArray();
            GoAt = sorted[sorted.Length / 2];
        }
        if (l.Phase == Phase.Race && double.IsNaN(GoAt)) GoAt = now - RttFloor / 2; // missed the whole countdown (packet loss): GO was just now
    }

    public void Dispose()
    {
        if (Ended == null) Leave();
        _link.Dispose();
    }
}

/// <summary>
///     LAN discovery: broadcasts <see cref="Discover"/> on the game port once a second, lists the hosts that answer
///     (<see cref="Announce"/>), forgets them after 3 s without an answer.
/// </summary>
public sealed class NetDiscovery : IDisposable
{
    public sealed record Game(IPEndPoint EndPoint, string Host, int Players, int Max, string CourseTime, Phase Phase, double Seen);

    private readonly NetLink _link;
    private readonly Func<double> _clock;
    private readonly int _port;
    private readonly List<Game> _games = [];
    private double _next;

    public NetDiscovery(int port, Func<double>? clock = null)
    {
        (_port, _clock) = (port, clock ?? NetSession.StopwatchClock());
        _link = new NetLink(0, _clock);
    }

    public IReadOnlyList<Game> Games => _games;

    public void Update()
    {
        var now = _clock();
        if (now >= _next)
        {
            _next = now + 1;
            _link.Broadcast(Protocol.Encode(new Discover()), _port);
        }
        while (_link.Receive(out var packet, out var from))
        {
            if (Protocol.Decode(packet) is not Announce a) continue;
            _games.RemoveAll(g => g.EndPoint.Equals(from));
            _games.Add(new Game(from, a.Host, a.Players, a.Max, a.CourseTime, a.Phase, now));
        }
        _games.RemoveAll(g => now - g.Seen > 3);
        _games.Sort((x, y) => string.CompareOrdinal(x.Host, y.Host));
    }

    public void Dispose() => _link.Dispose();
}
