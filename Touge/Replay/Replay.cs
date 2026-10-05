using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Kansei.Physics;

namespace Touge.Replays;

/// <summary>A car of a replay: who drove it, model/paint, and the assists its physics ran with (<see cref="Ui.Settings.Assisted"/>).</summary>
public sealed record ReplayCar(string Name, string Car, int Paint, int SteerAssist = 2, int DriftAssist = 1);

/// <summary>What a replay shows (the file header, readable without the tick data for the lists).</summary>
public sealed class ReplayInfo
{
    public string Course { get; set; } = "AKINA_DAY";
    public bool Reverse { get; set; }
    public bool Fog { get; set; }
    public DateTime Date { get; set; }
    /// <summary>"TIME ATTACK", "BATTLE" or "FREE BATTLE vs &lt;rival&gt;".</summary>
    public string Mode { get; set; } = "TIME ATTACK";
    /// <summary>Run time (time attack: the timer at the goal; battle: the player's goal time), null if unfinished.</summary>
    public float? Time { get; set; }
    /// <summary>Battle outcome ("WIN", "LOSE", "DRAW") or null.</summary>
    public string? Result { get; set; }
    public List<ReplayCar> Cars { get; set; } = [];
    public int Ticks { get; set; }
}

/// <summary>
///     A recorded run: every car's input per physics tick (<see cref="Drive.Dt"/>) and keyframes of every car's full state
///     (<see cref="Vehicle.Save"/>) every <see cref="ReplayRecorder.KeyEvery"/> ticks and after any teleport (reset, respawn).
///     The physics is deterministic, so the inputs alone replay the run (<see cref="ReplayPlayer"/>); the keyframes make
///     seeking cheap and absorb what the inputs do not carry (the auto-run's speed cap after the goal, respawns).
///     File: gzip of "IDRP", u16 version, u16 state size, header JSON (<see cref="ReplayInfo"/>), i32 ticks, i32 cars,
///     ticks × cars × 18-byte inputs, i32 keyframes × (i32 tick, cars × state).
/// </summary>
public sealed class Replay
{
    public const ushort Version = 1;
    private const int InputBytes = 18;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public ReplayInfo Info { get; init; } = new();
    /// <summary>Inputs, tick-major: tick t of car c at [t × cars + c].</summary>
    public List<VehicleInput> Inputs { get; } = [];
    /// <summary>Full states of all cars before the tick.</summary>
    public SortedList<int, byte[][]> Keys { get; } = [];

    public int CarCount => Info.Cars.Count;
    public int Ticks => CarCount == 0 ? 0 : Inputs.Count / CarCount;
    public float Seconds => Ticks * Drive.Dt;
    public VehicleInput Input(int tick, int car) => Inputs[tick * CarCount + car];

    public void Write(Stream stream)
    {
        Info.Ticks = Ticks;
        using var gz = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true);
        using var w = new BinaryWriter(gz);
        w.Write("IDRP"u8);
        w.Write(Version);
        w.Write((ushort)Vehicle.StateBytes);
        var header = JsonSerializer.SerializeToUtf8Bytes(Info, Json);
        w.Write(header.Length);
        w.Write(header);
        w.Write(Ticks);
        w.Write(CarCount);
        foreach (var i in Inputs)
        {
            w.Write(i.Throttle);
            w.Write(i.Brake);
            w.Write(i.Steer);
            w.Write(i.Clutch);
            w.Write((sbyte)Math.Clamp(i.Shift, -128, 127));
            w.Write((byte)((i.Handbrake ? 1 : 0) | (i.DirectSteer ? 2 : 0)));
        }
        w.Write(Keys.Count);
        foreach (var (tick, states) in Keys)
        {
            w.Write(tick);
            foreach (var s in states) w.Write(s);
        }
    }

    public static Replay Read(Stream stream)
    {
        using var gz = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        using var r = new BinaryReader(gz);
        var info = Header(r);
        var replay = new Replay { Info = info };
        int ticks = r.ReadInt32(), cars = r.ReadInt32();
        if (cars != info.Cars.Count || ticks < 0 || cars <= 0) throw new InvalidDataException("Replay: Kopf und Daten passen nicht zusammen");
        replay.Inputs.Capacity = ticks * cars;
        for (var i = 0; i < ticks * cars; i++)
        {
            float t = r.ReadSingle(), b = r.ReadSingle(), s = r.ReadSingle(), c = r.ReadSingle();
            var shift = r.ReadSByte();
            var flags = r.ReadByte();
            replay.Inputs.Add(new VehicleInput(t, b, s, (flags & 1) != 0, shift, c, (flags & 2) != 0));
        }
        var keys = r.ReadInt32();
        for (var k = 0; k < keys; k++)
        {
            var tick = r.ReadInt32();
            var states = new byte[cars][];
            for (var c = 0; c < cars; c++) states[c] = r.ReadBytes(Vehicle.StateBytes);
            replay.Keys[tick] = states;
        }
        return replay;
    }

    private static ReplayInfo Header(BinaryReader r)
    {
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "IDRP") throw new InvalidDataException("keine Replay-Datei");
        var version = r.ReadUInt16();
        var stateBytes = r.ReadUInt16();
        if (version != Version || stateBytes != Vehicle.StateBytes)
            throw new InvalidDataException($"Replay aus einer anderen Version (v{version}, Zustand {stateBytes} B)");
        var len = r.ReadInt32();
        return JsonSerializer.Deserialize<ReplayInfo>(r.ReadBytes(len), Json) ?? throw new InvalidDataException("Replay: Kopf leer");
    }

    /// <summary>Only the header (lists show many replays without reading their ticks).</summary>
    public static ReplayInfo ReadInfo(Stream stream)
    {
        using var gz = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        using var r = new BinaryReader(gz);
        return Header(r);
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        using (var f = File.Create(tmp)) Write(f);
        File.Move(tmp, path, true);
    }

    public static Replay Load(string path)
    {
        using var f = File.OpenRead(path);
        return Read(f);
    }

    /// <summary>Position stored in a keyframe state (the first field of <see cref="Vehicle.Save"/>).</summary>
    public static Vector3 PositionOf(byte[] state) =>
        new(BitConverter.ToSingle(state, 0), BitConverter.ToSingle(state, 4), BitConverter.ToSingle(state, 8));
}

/// <summary>Records a run into a <see cref="Replay"/>: <see cref="Before"/> each physics tick, <see cref="After"/> with the inputs the cars got.</summary>
public sealed class ReplayRecorder
{
    /// <summary>Keyframe interval in ticks (0.5 s).</summary>
    public const int KeyEvery = 60;
    /// <summary>Recording stops after 30 minutes (free roaming would grow without end).</summary>
    public const int MaxTicks = 30 * 60 * 120;

    private readonly CarSpec[] _specs;
    private bool _mark = true;

    public ReplayRecorder(Replay replay, IReadOnlyList<Vehicle> cars)
    {
        Replay = replay;
        _specs = [.. cars.Select(c => c.Spec)];
    }

    public Replay Replay { get; }
    /// <summary>False once a car changed under it (car swapped at standstill): the recording is dropped.</summary>
    public bool Valid { get; private set; } = true;

    /// <summary>The next tick gets a keyframe (a car was put somewhere: reset, respawn).</summary>
    public void Mark() => _mark = true;

    public void Before(IReadOnlyList<Vehicle> cars)
    {
        if (!Valid || Replay.Ticks >= MaxTicks) return;
        var same = cars.Count == _specs.Length;
        for (var i = 0; same && i < cars.Count; i++) same = cars[i].Spec == _specs[i];
        if (!same)
        {
            Valid = false;
            return;
        }
        if (!_mark && Replay.Ticks % KeyEvery != 0) return;
        Replay.Keys[Replay.Ticks] = [.. cars.Select(c => c.SaveState())];
        _mark = false;
    }

    public void After(ReadOnlySpan<VehicleInput> inputs)
    {
        if (!Valid || Replay.Ticks >= MaxTicks) return;
        foreach (var i in inputs) Replay.Inputs.Add(i);
    }
}

/// <summary>
///     Plays a <see cref="Replay"/> on live <see cref="Vehicle"/>s (the game's own cars, so drawing, sound and effects work
///     as in the race): each tick every car gets its recorded input, then car-to-car contacts as in <see cref="Race.RaceSession"/>.
///     <see cref="Restore"/> loads each keyframe it passes (exact even where inputs alone drift); <see cref="Seek"/> jumps
///     to the keyframe before the tick and simulates up to it.
/// </summary>
public sealed class ReplayPlayer(Replay replay, Vehicle[] cars, IGround ground)
{
    public Replay Replay => replay;
    public Vehicle[] Cars => cars;
    /// <summary>The next tick to simulate (= ticks shown so far).</summary>
    public int Tick { get; private set; }
    public bool Restore { get; set; } = true;
    public bool Done => Tick >= replay.Ticks;
    /// <summary>Poses before the last tick (render interpolation).</summary>
    public Vector3[] PrevPosition { get; } = new Vector3[cars.Length];
    public Quaternion[] PrevOrientation { get; } = new Quaternion[cars.Length];
    public CarContact? LastContact { get; private set; }

    /// <summary>Input car <paramref name="car"/> got in the last tick (brake lights, engine sound).</summary>
    public VehicleInput LastInput(int car) => Tick > 0 ? replay.Input(Math.Min(Tick, replay.Ticks) - 1, car) : default;

    public void Seek(int tick)
    {
        tick = Math.Clamp(tick, 0, replay.Ticks);
        var keys = replay.Keys.Keys;
        int lo = 0, hi = keys.Count - 1, at = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (keys[mid] <= tick) (at, lo) = (mid, mid + 1);
            else hi = mid - 1;
        }
        if (at < 0) throw new InvalidDataException("Replay ohne Keyframe am Anfang");
        Load(keys[at]);
        while (Tick < tick) Step();
        Sync();
    }

    private void Load(int tick)
    {
        var states = replay.Keys[tick];
        for (var c = 0; c < cars.Length; c++) cars[c].LoadState(states[c]);
        Tick = tick;
    }

    /// <summary>The previous poses = the current ones (after a jump: no interpolation across it).</summary>
    public void Sync()
    {
        for (var c = 0; c < cars.Length; c++) (PrevPosition[c], PrevOrientation[c]) = (cars[c].Position, cars[c].Orientation);
    }

    /// <summary>One tick; false at the end.</summary>
    public bool Step()
    {
        if (Done) return false;
        if (Restore && replay.Keys.ContainsKey(Tick)) Load(Tick);
        for (var c = 0; c < cars.Length; c++)
        {
            var v = cars[c];
            (PrevPosition[c], PrevOrientation[c]) = (v.Position, v.Orientation);
            v.Step(replay.Input(Tick, c), ground, Drive.Dt);
        }
        LastContact = null;
        for (var i = 0; i < cars.Length; i++)
        for (var j = i + 1; j < cars.Length; j++)
            if (CarCollision.Resolve(cars[i], cars[j], PrevPosition[i], PrevOrientation[i], PrevPosition[j], PrevOrientation[j]) is { } hit
                && (LastContact is not { } hard || hit.ImpactSpeed > hard.ImpactSpeed)) LastContact = hit;
        Tick++;
        return true;
    }

    /// <summary>
    ///     Plays the whole replay from its first keyframe on inputs alone (<see cref="Restore"/> off) and compares every car's
    ///     position with each later keyframe: how far pure input playback drifts from the recording (0 = deterministic),
    ///     and when it first differed.
    /// </summary>
    public (float Max, float Mean, int Samples, float MaxAt, float? FirstAt) MeasureDrift()
    {
        var restore = Restore;
        Restore = false;
        Seek(0);
        float max = 0, sum = 0, maxAt = 0;
        float? first = null;
        var n = 0;
        foreach (var (tick, states) in replay.Keys)
        {
            if (tick == 0) continue;
            while (Tick < tick) Step();
            for (var c = 0; c < cars.Length; c++)
            {
                var e = Vector3.Distance(cars[c].Position, Replay.PositionOf(states[c]));
                if (e > max) (max, maxAt) = (e, tick * Drive.Dt);
                if (e > 0 && first == null) first = tick * Drive.Dt;
                sum += e;
                n++;
            }
        }
        Restore = restore;
        return (max, n > 0 ? sum / n : 0, n, maxAt, first);
    }
}
