using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Touge.Net;

/// <summary>Message kinds; every packet starts with "ID", <see cref="Protocol.Version"/> and one of these.</summary>
public enum MsgType : byte { Discover = 1, Announce, Hello, Lobby, Ping, Pong, State, Result, Bye }

/// <summary>Where a session is (the host's word is law, clients follow its <see cref="Lobby"/> packets).</summary>
public enum Phase : byte { Lobby, Loading, Countdown, Race, Results }

/// <summary>
///     <see cref="Battle"/>: two players, the original's battle (first to the goal, or a breakaway gap of
///     <see cref="Race.Battle.Breakaway"/>); <see cref="Race"/>: 2–4 players, finishing order.
/// </summary>
public enum NetRule : byte { Battle, Race }

/// <summary>What the host chose: course + time of day (AKINA_DAY/_NIT/_RIN), direction, fog, rule.</summary>
public sealed record RaceConfig(string CourseTime = "AKINA_DAY", bool Reverse = false, bool Fog = false, NetRule Rule = NetRule.Battle);

/// <summary>A player as the host lists it: id 0 is the host, ping in ms (the host's measurement), the race it has loaded.</summary>
public sealed record PlayerInfo(byte Id, string Name, string Car, byte Paint, bool Ready, ushort PingMs, int LoadedRace);

/// <summary>One finisher (or not) of a race: place 1.., goal time (race seconds, &lt; 0 = did not finish) and how far it got.</summary>
public readonly record struct ResultEntry(byte Id, byte Place, float Time, float Along);

/// <summary>
///     A car at one moment (sender's race clock, s since GO; negative in the countdown): pose and motion for interpolation and
///     dead reckoning, the driver's input for sound and lamps, per-wheel suspension and slide (smoke/skids), and its own
///     progress and goal time — the sender is the authority over its own car.
/// </summary>
public readonly record struct CarState(
    byte Id, uint Seq, float Time, Vector3 Position, Quaternion Orientation, Vector3 Velocity, Vector3 AngularVelocity,
    float Steer, float Throttle, float Brake, float Rpm, sbyte Gear, byte Flags, uint Wheels, uint Slides, float Along, float FinishedAt)
{
    public const byte Handbrake = 1, LightsLow = 2, LightsHigh = 4, Wall = 8, Finished = 16;
    public bool Has(byte flag) => (Flags & flag) != 0;
}

public interface INetMessage
{
    MsgType Type { get; }
    void Write(ref Protocol.Writer w);
}

public sealed record Discover : INetMessage
{
    public MsgType Type => MsgType.Discover;
    public void Write(ref Protocol.Writer w) { }
}

/// <summary>Host's answer to <see cref="Discover"/> (LAN list).</summary>
public sealed record Announce(string Host, byte Players, byte Max, string CourseTime, Phase Phase) : INetMessage
{
    public MsgType Type => MsgType.Announce;
    public void Write(ref Protocol.Writer w)
    {
        w.Str(Host);
        w.U8(Players);
        w.U8(Max);
        w.Str(CourseTime);
        w.U8((byte)Phase);
    }
}

/// <summary>
///     Client → host, several times a second for the whole session: join request and keep-alive in one, carrying what the client
///     decides itself (name, car, paint, ready, the race it has loaded). <paramref name="Token"/> tells a rejoin from a new player.
/// </summary>
public sealed record Hello(uint Token, string Name, string Car, byte Paint, bool Ready, int LoadedRace) : INetMessage
{
    public MsgType Type => MsgType.Hello;
    public void Write(ref Protocol.Writer w)
    {
        w.U32(Token);
        w.Str(Name);
        w.Str(Car);
        w.U8(Paint);
        w.U8(Ready ? (byte)1 : (byte)0);
        w.I32(LoadedRace);
    }
}

/// <summary>
///     Host → each client, several times a second: the whole session state (idempotent, so a lost one costs nothing):
///     the recipient's id, phase, race number, the host's choice, the players and, in the countdown, the seconds to GO.
/// </summary>
public sealed record Lobby(byte YouId, Phase Phase, int RaceId, RaceConfig Config, PlayerInfo[] Players, float SecondsToGo) : INetMessage
{
    public MsgType Type => MsgType.Lobby;
    public void Write(ref Protocol.Writer w)
    {
        w.U8(YouId);
        w.U8((byte)Phase);
        w.I32(RaceId);
        w.Str(Config.CourseTime);
        w.U8((byte)((Config.Reverse ? 1 : 0) | (Config.Fog ? 2 : 0)));
        w.U8((byte)Config.Rule);
        w.U8((byte)Players.Length);
        foreach (var p in Players)
        {
            w.U8(p.Id);
            w.Str(p.Name);
            w.Str(p.Car);
            w.U8(p.Paint);
            w.U8(p.Ready ? (byte)1 : (byte)0);
            w.U16(p.PingMs);
            w.I32(p.LoadedRace);
        }
        w.F32(SecondsToGo);
    }
}

public sealed record Ping(double Time) : INetMessage
{
    public MsgType Type => MsgType.Ping;
    public void Write(ref Protocol.Writer w) => w.F64(Time);
}

public sealed record Pong(double Time) : INetMessage
{
    public MsgType Type => MsgType.Pong;
    public void Write(ref Protocol.Writer w) => w.F64(Time);
}

/// <summary>A car state of race <paramref name="RaceId"/> (late packets of the race before are dropped by it).</summary>
public sealed record StateMsg(int RaceId, CarState State) : INetMessage
{
    public MsgType Type => MsgType.State;
    public void Write(ref Protocol.Writer w)
    {
        w.I32(RaceId);
        var s = State;
        w.U8(s.Id);
        w.U32(s.Seq);
        w.F32(s.Time);
        w.V3(s.Position);
        w.F32(s.Orientation.X);
        w.F32(s.Orientation.Y);
        w.F32(s.Orientation.Z);
        w.F32(s.Orientation.W);
        w.V3(s.Velocity);
        w.V3(s.AngularVelocity);
        w.F32(s.Steer);
        w.F32(s.Throttle);
        w.F32(s.Brake);
        w.F32(s.Rpm);
        w.U8((byte)s.Gear);
        w.U8(s.Flags);
        w.U32(s.Wheels);
        w.U32(s.Slides);
        w.F32(s.Along);
        w.F32(s.FinishedAt);
    }
}

/// <summary>Host → all, repeated once decided: the race's result (places, goal times) and how it was decided.</summary>
public sealed record Result(int RaceId, string Reason, ResultEntry[] Entries) : INetMessage
{
    public MsgType Type => MsgType.Result;
    public void Write(ref Protocol.Writer w)
    {
        w.I32(RaceId);
        w.Str(Reason);
        w.U8((byte)Entries.Length);
        foreach (var e in Entries)
        {
            w.U8(e.Id);
            w.U8(e.Place);
            w.F32(e.Time);
            w.F32(e.Along);
        }
    }
}

/// <summary>
///     Leaving (sent a few times): a client's makes the host drop it; the host's (id 0) ends the session for the recipient with
///     <paramref name="Reason"/> (host left, session full, race in progress).
/// </summary>
public sealed record Bye(byte Id, string Reason = "") : INetMessage
{
    public MsgType Type => MsgType.Bye;
    public void Write(ref Protocol.Writer w)
    {
        w.U8(Id);
        w.Str(Reason);
    }
}

/// <summary>
///     Binary encoding of the messages: little-endian, header "ID" + version + type, strings as length byte + UTF-8 (at most
///     <see cref="MaxString"/> bytes), at most <see cref="NetSession.MaxPlayers"/> list entries. <see cref="Decode"/> is the
///     trust boundary: anything malformed (wrong header/version, short, oversized, non-finite numbers, ids out of range) is null.
/// </summary>
public static class Protocol
{
    public const byte Version = 1;
    public const int MaxPacket = 1200, MaxString = 32, MaxList = 4;
    private const byte Magic0 = (byte)'I', Magic1 = (byte)'D';

    public ref struct Writer(Span<byte> buffer)
    {
        private readonly Span<byte> _b = buffer;
        public int Length { get; private set; }
        private Span<byte> Take(int n)
        {
            var s = _b.Slice(Length, n);
            Length += n;
            return s;
        }
        public void U8(byte v) => Take(1)[0] = v;
        public void U16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v);
        public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);
        public void I32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), v);
        public void F32(float v) => BinaryPrimitives.WriteSingleLittleEndian(Take(4), v);
        public void F64(double v) => BinaryPrimitives.WriteDoubleLittleEndian(Take(8), v);
        public void V3(Vector3 v)
        {
            F32(v.X);
            F32(v.Y);
            F32(v.Z);
        }
        public void Str(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(Clip(s));
            U8((byte)bytes.Length);
            bytes.CopyTo(Take(bytes.Length));
        }
    }

    /// <summary>Reads, throwing <see cref="FormatException"/> on anything short or out of range (caught in <see cref="Decode"/>).</summary>
    public ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _d = data;
        private int _p;
        public readonly bool End => _p == _d.Length;
        private ReadOnlySpan<byte> Take(int n)
        {
            if (_p + n > _d.Length) throw new FormatException("short packet");
            var s = _d.Slice(_p, n);
            _p += n;
            return s;
        }
        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public float F32()
        {
            var v = BinaryPrimitives.ReadSingleLittleEndian(Take(4));
            return float.IsFinite(v) ? v : throw new FormatException("non-finite");
        }
        public double F64()
        {
            var v = BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
            return double.IsFinite(v) ? v : throw new FormatException("non-finite");
        }
        public Vector3 V3() => new(F32(), F32(), F32());
        public string Str()
        {
            var n = U8();
            if (n > MaxString * 4) throw new FormatException("long string");
            return Clip(Encoding.UTF8.GetString(Take(n)));
        }
        public byte Id()
        {
            var id = U8();
            return id < MaxList ? id : throw new FormatException("id");
        }
        public int Count()
        {
            var n = U8();
            return n <= MaxList ? n : throw new FormatException("list");
        }
    }

    /// <summary>Printable text of at most <see cref="MaxString"/> characters (names come from other players).</summary>
    public static string Clip(string s)
    {
        var clean = new string([.. s.Where(c => c >= ' ' && c != '\u007f')]);
        return clean.Length > MaxString ? clean[..MaxString] : clean;
    }

    /// <summary>The packet for <paramref name="m"/>.</summary>
    public static byte[] Encode(INetMessage m)
    {
        Span<byte> buf = stackalloc byte[MaxPacket];
        var w = new Writer(buf);
        w.U8(Magic0);
        w.U8(Magic1);
        w.U8(Version);
        w.U8((byte)m.Type);
        m.Write(ref w);
        return buf[..w.Length].ToArray();
    }

    /// <summary>The message in <paramref name="packet"/>, or null when it is not a valid packet of this version.</summary>
    public static INetMessage? Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 4 || packet.Length > MaxPacket || packet[0] != Magic0 || packet[1] != Magic1 || packet[2] != Version) return null;
        try
        {
            var r = new Reader(packet[4..]);
            INetMessage m = (MsgType)packet[3] switch
            {
                MsgType.Discover => new Discover(),
                MsgType.Announce => new Announce(r.Str(), r.U8(), r.U8(), r.Str(), ReadPhase(ref r)),
                MsgType.Hello => new Hello(r.U32(), r.Str(), r.Str(), r.U8(), r.U8() != 0, r.I32()),
                MsgType.Lobby => ReadLobby(ref r),
                MsgType.Ping => new Ping(r.F64()),
                MsgType.Pong => new Pong(r.F64()),
                MsgType.State => new StateMsg(r.I32(), ReadState(ref r)),
                MsgType.Result => ReadResult(ref r),
                MsgType.Bye => new Bye(r.Id(), r.Str()),
                _ => throw new FormatException("type"),
            };
            return r.End ? m : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Phase ReadPhase(ref Reader r)
    {
        var p = r.U8();
        return p <= (byte)Phase.Results ? (Phase)p : throw new FormatException("phase");
    }

    private static Lobby ReadLobby(ref Reader r)
    {
        var you = r.Id();
        var phase = ReadPhase(ref r);
        var raceId = r.I32();
        var course = r.Str();
        var flags = r.U8();
        var rule = r.U8() == 1 ? NetRule.Race : NetRule.Battle;
        var n = r.Count();
        var players = new PlayerInfo[n];
        for (var i = 0; i < n; i++) players[i] = new PlayerInfo(r.Id(), r.Str(), r.Str(), r.U8(), r.U8() != 0, r.U16(), r.I32());
        return new Lobby(you, phase, raceId, new RaceConfig(course, (flags & 1) != 0, (flags & 2) != 0, rule), players, r.F32());
    }

    private static CarState ReadState(ref Reader r)
    {
        var id = r.Id();
        var seq = r.U32();
        var time = r.F32();
        var pos = r.V3();
        var q = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
        var len = q.Length();
        if (len < 0.5f || len > 1.5f) throw new FormatException("orientation");
        return new CarState(id, seq, time, pos, Quaternion.Normalize(q), r.V3(), r.V3(), r.F32(), r.F32(), r.F32(), r.F32(), (sbyte)r.U8(), r.U8(), r.U32(), r.U32(), r.F32(), r.F32());
    }

    private static Result ReadResult(ref Reader r)
    {
        var raceId = r.I32();
        var reason = r.Str();
        var n = r.Count();
        var e = new ResultEntry[n];
        for (var i = 0; i < n; i++) e[i] = new ResultEntry(r.Id(), r.U8(), r.F32(), r.F32());
        return new Result(raceId, reason, e);
    }
}
