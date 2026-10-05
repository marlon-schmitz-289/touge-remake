using System.Numerics;

namespace Kansei.Physics;

/// <summary>
///     How an AI rival drives, each 0..1: <paramref name="Skill"/> = planned cornering/braking grip and mistake rate (one
///     scale for every mode, <see cref="RivalPilot.Pace"/>), <paramref name="Aggression"/> = following distance, how readily
///     it attacks and how often it covers the inside, <paramref name="Drift"/> = drift style (share of the eligible corners
///     drifted, slip held), <paramref name="Mistakes"/> = the character's mistake factor (1 = normal, Ryosuke 0.5, Shingo 1.3).
/// </summary>
public readonly record struct RivalStyle(float Skill, float Aggression, float Drift, float Mistakes = 1);

/// <summary>Another car as an AI sees it: metres along and lateral (+ left) on the same driving line, forward speed (m/s).</summary>
public readonly record struct Opponent(float Along, float Lateral, float Speed);

/// <summary>
///     AI driver on a course line. Thin orchestrator over a <see cref="CourseMap"/> (bends, road room, overtaking zones,
///     shared), its own <see cref="RacingLine"/> with the skill's speed plan, a <see cref="DriftController"/> for the corners
///     its style drifts (decided once per corner from the race seed), a racecraft state machine (follow, pressure, set up and
///     commit a pass in a zone, defend the inside, never ram: time-to-contact speed cap, stay on our side of a car alongside,
///     own lane off the start) and small seeded mistakes scaled by skill. <see cref="RubberBand"/> moves only the planned grip.
/// </summary>
public sealed class RivalPilot
{
    private const float G = 9.81f, Deg = 180 / MathF.PI, CarLength = 4.4f;

    /// <summary>Lateral centre distance aimed for when passing alongside (car width + elbow room).</summary>
    public const float PassGap = 2.4f;

    /// <summary>Distance kept between the car's centre line and the end of the drivable ground (half a body plus elbow room).</summary>
    public const float EdgeMargin = RacingLine.EdgeMargin;

    /// <summary>Lateral centre distance from which two cars are side by side (body widths ~1.7 m).</summary>
    public const float Alongside = 1.8f;

    public enum Mode { Line, Follow, Pressure, Setup, Pass, Block, Recover }

    private readonly Vector3[] _line;
    private CourseMap? _map;
    private RacingLine? _racing;
    private CarSpec? _spec;
    private DriftController.Entry?[] _drift = [];
    private float[] _brakeAt = [];
    private CourseMap.Corner[] _window = [];
    private float _time, _band = float.NaN, _startS = float.NaN, _recoverUntil = -1, _lockUntil = -1, _throttleUntil = -1, _overUntil = -1, _settleUntil = -1;
    private bool _fresh = true;
    private int _passing = -1, _failedZone = -1, _defendZone = -1, _defended = -1, _rolled = -1, _driftDone = -1, _decided = -1, _gripCorner = -1;
    private float _defendTarget;
    private (float Shift, float Wide, bool Lock, bool Throttle, bool Over) _err;
    // opponent's progress a few seconds ago (its measured pace against our plan)
    private readonly (float T, float S)[] _hist = new (float, float)[64];
    private int _histN;

    public RivalPilot(Vector3[] line, RivalStyle style)
    {
        _line = line;
        Pilot = new LinePilot(line) { TopSpeed = 250 / 3.6f };
        Style = style;
    }

    /// <summary>Own line follower (tracking state per car).</summary>
    public LinePilot Pilot { get; }
    public RivalStyle Style { get; set; }
    public DriftController Drift { get; } = new();
    /// <summary>The course map and own racing line (after the first <see cref="Drive"/>).</summary>
    public CourseMap? Map => _map;
    public RacingLine? Racing => _racing;
    /// <summary>−1 … +1 from the race: + = behind, push (≤ <see cref="BandUp"/> planned grip); − = ahead, ease off (≤ <see cref="BandDown"/>, mistakes ×1.3). 0 = off.</summary>
    public float RubberBand { get; set; }
    public float BandUp { get; set; } = 0.03f;
    public float BandDown { get; set; } = 0.04f;
    /// <summary>Mistake rate factor of the level (LEGEND 0.5).</summary>
    public float MistakeScale { get; set; } = 1;
    /// <summary>Race seed: drift decisions and mistakes (deterministic per battle setup).</summary>
    public int Seed { get; set; }
    /// <summary>Current lateral override (m, + left of the course line) and its weight (0 = the racing line).</summary>
    public float Offset => Pilot.Blend > 0 ? Pilot.Lateral : float.NaN;
    public Mode State { get; private set; }
    /// <summary>No passing (follow only), e.g. the chaser in the first seconds of a lead/chase.</summary>
    public bool NoPass { get; set; }
    /// <summary>Mistakes so far (statistics).</summary>
    public int Mistakes { get; private set; }

    /// <summary>
    ///     Planned cornering and braking deceleration (m/s²) for <paramref name="skill"/> 0..1: (0.95 + 0.50 k) g and
    ///     (0.60 + 0.35 k) g, cornering scaled by the car's skid-pad limit against the AE86's (<see cref="GripLimit"/>, <paramref name="spec"/>).
    /// </summary>
    public static (float Corner, float Brake) Pace(float skill, CarSpec? spec = null)
    {
        var k = Math.Clamp(skill, 0, 1);
        return (G * (PaceTune[0] - PaceTune[1] * MathF.Pow(1 - k, PaceTune[2])) * (spec == null ? 1 : CarFactor(spec, 30)), G * (PaceTune[3] + (0.95f - PaceTune[3]) * k));
    }

    /// <summary>
    ///     How much of the AE86's planned cornering <paramref name="spec"/> gets in a bend of <paramref name="radius"/> m: its
    ///     skid-pad limit against the AE86's on the 30 m and 100 m pads (<see cref="GripLimit"/>), squared (heavier cars also
    ///     lose more in the transients), between them by radius.
    /// </summary>
    public static float CarFactor(CarSpec spec, float radius)
    {
        if (spec == CarSpec.AE86) return 1;
        float r30 = GripLimit.At(spec, 30) / GripLimit.At(CarSpec.AE86, 30), r100 = GripLimit.At(spec, 100) / GripLimit.At(CarSpec.AE86, 100);
        return MathF.Pow(float.Lerp(r30, r100, Math.Clamp((radius - 30) / 70, 0, 1)), PaceTune[4]);
    }

    public static float[] PaceTune = Environment.GetEnvironmentVariable("PACE_P") is { } pp ? [.. pp.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture))] : [1.45f, 0.78f, 1.5f, 0.6f, 2f];

    /// <summary>Extra planned lateral grip in drift corners (the drift layer keeps momentum: 1.5–1.6 g measured).</summary>
    public const float DriftBonus = 0.10f * G;

    /// <summary>Slower corners (m/s at the apex) are not drifted: the tail cannot be held under ~40 km/h (IROHA's tightest hairpins).</summary>
    public const float MinDriftSpeed = 40 / 3.6f;

    /// <summary>Following distance (m, centre to centre) behind a car at <paramref name="speed"/>: 0.8 … 0.4 s by aggression, 0.3 s under pressure.</summary>
    public static float FollowGap(float speed, float aggression, bool pressure = false) =>
        CarLength + 1 + speed * (pressure ? 0.3f : 0.8f - 0.4f * aggression);

    /// <summary>Probability that a drift-eligible corner is drifted: 1.25 × drift − 0.15.</summary>
    public static float DriftChance(float drift) => Math.Clamp(1.25f * drift - 0.15f, 0, 1);

    /// <summary>
    ///     How corner <paramref name="c"/> is drifted with <paramref name="style"/> in <paramref name="spec"/>, or null (grip):
    ///     FR/MR hairpins and tight bends of ≥ 70°, medium ones (r ≤ 60 m, ≥ 100°) for drift ≥ 0.7; 4WD hairpins for drift ≥ 0.6;
    ///     FF only a handbrake tuck in hairpins for drift ≥ 0.5. <paramref name="roll"/> 0..1 decides against <see cref="DriftChance"/>.
    /// </summary>
    public static DriftController.Entry? DriftPlan(CourseMap.Corner c, RivalStyle style, CarSpec spec, float roll)
    {
        float d = style.Drift, df = spec.DriveFront;
        bool fr = df <= 0, ff = df >= 1;
        var eligible = ff ? c.Hairpin && d >= 0.5f
            : !fr ? c.Hairpin && d >= 0.6f
            : c.Slow && c.Angle >= 70 / Deg || d >= 0.7f && c.Radius <= 60 && c.Angle >= 100 / Deg;
        if (!eligible || roll >= DriftChance(d)) return null;
        if (ff) return DriftController.Entry.Tuck;
        if (Environment.GetEnvironmentVariable("DRIFT_ENTRY") is { } fe) return Enum.Parse<DriftController.Entry>(fe);
        // Takumi-style: half of the drifts braked deep into the turn-in
        return fr && d >= 0.85f && roll < DriftChance(d) / 2 ? DriftController.Entry.BrakingDrift : DriftController.Entry.Handbrake;
    }

    /// <summary>Deterministic 0..1 from the seed and two keys.</summary>
    public static float Hash(int seed, int a, int b)
    {
        var h = (uint)seed * 0x9E3779B1u ^ (uint)a * 0x85EBCA77u ^ (uint)b * 0xC2B2AE3Du;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return (h >> 8) / 16777216f;
    }

    private float Gauss(int a, int b)
    {
        var u1 = MathF.Max(Hash(Seed, a, b), 1e-6f);
        var u2 = Hash(Seed, a, b + 7919);
        return MathF.Sqrt(-2 * MathF.Log(u1)) * MathF.Cos(MathF.Tau * u2);
    }

    /// <summary>
    ///     Builds the map, the drift decisions, the racing line (wider margin in drift corners) and the plan for
    ///     <paramref name="car"/> on <paramref name="ground"/>; done on the first <see cref="Drive"/>, or earlier for tools.
    /// </summary>
    public void Prepare(Vehicle car, IGround ground)
    {
        _spec = car.Spec;
        _map = CourseMap.Of(_line, ground, car.SurfaceGrip);
        var cs = _map.Corners;
        _drift = new DriftController.Entry?[cs.Count];
        for (var i = 0; i < cs.Count; i++) _drift[i] = DriftPlan(cs[i], Style, car.Spec, Hash(Seed, 1, i));
        var beta = DriftController.StyleBeta(Style.Drift, car.Spec);
        var holds = GripLimit.DriftSpeed(car.Spec) * 1.15f;
        for (var pass = 0; pass < 2; pass++)
        {
            var margin = new float[_map.Count];
            Array.Fill(margin, EdgeMargin);
            for (var i = 0; i < cs.Count; i++)
                if (_drift[i] is not (null or DriftController.Entry.Tuck))
                    for (var j = _map.Index(cs[i].From - 15); j <= _map.Index(cs[i].To + 10); j++) margin[j] = MathF.Max(margin[j], EdgeMargin + 0.9f * MathF.Sin(beta));
            _racing = new RacingLine(_map, margin);
            Pilot.Plan = _racing;
            _band = float.NaN;
            Replan(0);
            // drift window per corner: where the own line really bends (entry from Tune[3] of its sharpest, exit 35 %)
            _window = new CourseMap.Corner[cs.Count];
            for (var i = 0; i < cs.Count; i++)
            {
                var c = cs[i];
                int a = _map.Index(c.From - 30), b = Math.Min(_map.Index(c.To + 30), _map.Count - 1), top = a;
                for (var j = a; j <= b; j++)
                    if (_racing.Curvature[j] * c.Dir > _racing.Curvature[top] * c.Dir) top = j;
                float max = _racing.Curvature[top] * c.Dir, entry = DriftController.Tune[3] * max, exit = 0.35f * max;
                int from = top, to = top;
                while (from > a && _racing.Curvature[from - 1] * c.Dir >= entry) from--;
                while (to < b && _racing.Curvature[to + 1] * c.Dir >= exit) to++;
                _window[i] = c with { From = from * CourseMap.Step, To = to * CourseMap.Step, Apex = top * CourseMap.Step };
            }
            // corners too fast to hold a drift in this car: grip
            var dropped = false;
            for (var i = 0; i < cs.Count; i++)
                if (_drift[i] is { } e && _racing.SpeedAt(_window[i].Apex) is var va && (va > holds && e != DriftController.Entry.Tuck || va < MinDriftSpeed))
                    (_drift[i], dropped) = (null, true);
            if (!dropped) break;
        }
        var racing = _racing!;
        // braking point per corner: where the plan starts to slow for it
        _brakeAt = new float[cs.Count];
        for (var i = 0; i < cs.Count; i++)
        {
            var j = _map.Index(cs[i].Apex);
            while (j > 0 && racing.Speed[j - 1] > racing.Speed[j] + 0.01f) j--;
            _brakeAt[i] = j * CourseMap.Step;
        }
    }

    private void Replan(float band)
    {
        if (_racing == null || _map == null || _spec == null || band == _band) return;
        _band = band;
        var (corner, brake) = Pace(Style.Skill);
        var cap = Pace(1).Corner; // never above the LEGEND plan for the car
        var aLat = MathF.Min(corner * (1 + band), MathF.Max(cap, corner));
        var aBrake = brake * (1 + band);
        var drift = new bool[_map.Count];
        for (var i = 0; i < _drift.Length; i++)
            if (_drift[i] is not (null or DriftController.Entry.Tuck))
                for (var j = _map.Index(_map.Corners[i].From - 5); j <= _map.Index(_map.Corners[i].To + 5); j++) drift[j] = true;
        var racing = _racing;
        var spec = _spec;
        _racing.Plan(i => aLat * CarFactor(spec, 1 / MathF.Max(MathF.Abs(racing.Curvature[i]), 1e-3f)) + (drift[i] ? DriftBonus * DriftController.Tune[6] : 0), aBrake, _spec, Pilot.TopSpeed);
        (Pilot.CornerAccel, Pilot.BrakeDecel) = (aLat * CarFactor(spec, 30), aBrake);
    }

    /// <summary>Is corner <paramref name="i"/> planned as a drift (and how).</summary>
    public DriftController.Entry? DriftAt(int i) => (uint)i < (uint)_drift.Length ? _drift[i] : null;

    /// <summary>Input for this tick; <paramref name="others"/> = every other car on the course.</summary>
    public VehicleInput Drive(Vehicle car, IGround ground, ReadOnlySpan<Opponent> others, float dt)
    {
        _time += dt;
        if (_racing == null || _spec != car.Spec) Prepare(car, ground);
        var map = _map!;
        var racing = _racing!;
        var (s, lat) = Pilot.Track(car.Position);
        var v = Vector3.Dot(car.Velocity, Vector3.Transform(Vector3.UnitZ, car.Orientation));
        if (_fresh)
        {
            // start/reset: the override starts where the car stands (side-by-side grid) and eases onto the line
            (Pilot.Lateral, Pilot.Blend, _fresh, _histN) = (lat, 1, false, 0);
            if (float.IsNaN(_startS)) _startS = s;
        }

        // rubber band: only the planned grip/braking, in 0.5 % steps
        var rb = Math.Clamp(RubberBand, -1, 1);
        Replan(MathF.Round((rb > 0 ? BandUp * rb : BandDown * rb) * 200) / 200);

        var cs = map.Corners;
        var ci = map.CornerAt(s, 80, 0);
        float? target = null;
        var cap = float.PositiveInfinity;
        var rate = 1.2f;
        State = Mode.Line;
        Pilot.PlanShift = 0;
        Pilot.BrakeDecel = Pace(Style.Skill, _spec).Brake * (1 + MathF.Max(rb, 0) * BandUp);

        // --- traffic: the nearest car ahead and behind
        int ahead = -1, behind = -1;
        float dsAhead = float.MaxValue, dsBehind = float.MinValue;
        for (var i = 0; i < others.Length; i++)
        {
            var ds = others[i].Along - s;
            if (ds > 0 && ds < 60 && ds < dsAhead) (ahead, dsAhead) = (i, ds);
            else if (ds <= 0 && ds > -40 && ds > dsBehind) (behind, dsBehind) = (i, ds);
        }
        var underPressure = behind >= 0 && -dsBehind < 0.5f * MathF.Max(v, 10);
        var (lo, hi) = racing.BoundsAt(s);

        if (ahead >= 0)
        {
            var o = others[ahead];
            Remember(o.Along);
            var faster = PaceAdvantage(o.Along) >= 0.02f;
            var zone = map.ZoneAt(s) ?? map.ZoneAt(s + 25);
            var zi = zone is { } zz ? IndexOf(map, zz) : -1;
            var mode = Mode.Follow;
            if (!NoPass && dsAhead < 30 && (faster || v > o.Speed * 1.03f && zone is { Corner: < 0 }))
            {
                mode = Mode.Pressure;
                if (zone is { } z && zi != _failedZone)
                {
                    // the inside of the zone's corner; on a straight the side the leader leaves open
                    var side = z.Corner >= 0 ? cs[z.Corner].Dir : (hi - o.Lateral > o.Lateral - lo ? 1 : -1);
                    var want = Math.Clamp(o.Lateral + side * PassGap, lo, hi);
                    if (MathF.Abs(want - o.Lateral) >= Alongside)
                    {
                        target = want;
                        rate = 2.5f;
                        var nose = s - o.Along + CarLength; // our nose past their tail
                        if (z.Corner >= 0)
                        {
                            var brakeAt = BrakePoint(z.Corner, o.Speed); // the leader's
                            if (s >= brakeAt - 2 && nose < 1.7f && _passing != z.Corner)
                            {
                                // not half a car alongside at its braking point: no dive-bomb, back in behind
                                _failedZone = zi;
                                target = null;
                            }
                            else if (s >= brakeAt - 2)
                            {
                                mode = Mode.Pass;
                                _passing = z.Corner;
                            }
                            else mode = Mode.Setup;
                        }
                        else mode = MathF.Abs(lat - o.Lateral) > Alongside - 0.2f ? Mode.Pass : Mode.Setup;
                        if (mode == Mode.Pass)
                        {
                            // brake 3–6 m later on the inside, never above the planned braking + 0.05 g
                            Pilot.PlanShift = -(3 + 3 * Style.Aggression);
                            Pilot.BrakeDecel += 0.05f * G;
                        }
                    }
                }
                else if (_passing >= 0 && s < cs[_passing].To && dsAhead < 12)
                {
                    // committed: hold the inside through the corner
                    target = Math.Clamp(o.Lateral + cs[_passing].Dir * PassGap, lo, hi);
                    mode = Mode.Pass;
                    Pilot.BrakeDecel += 0.05f * G;
                }
                else if (zone is { Corner: >= 0 } z2)
                {
                    // pressure: show the nose on the inside in the braking zone (and keep 0.3 s)
                    target = Math.Clamp(o.Lateral + cs[z2.Corner].Dir * 0.8f, lo, hi);
                }
            }
            State = mode;
            if (_passing >= 0 && (s >= cs[_passing].To || mode != Mode.Pass)) _passing = -1;
            // keep out of its boot: time-to-contact ≥ 1 s against any overlap, the following gap when in line behind it
            var inLine = MathF.Abs(lat - o.Lateral) < Alongside && MathF.Abs((target ?? lat) - o.Lateral) < Alongside + 0.2f;
            if (MathF.Abs(lat - o.Lateral) < 2.0f)
                cap = MathF.Min(cap, o.Speed + MathF.Max(dsAhead - CarLength, 0) / 1.0f);
            if (inLine)
            {
                var gap = State is Mode.Setup or Mode.Pass ? CarLength + 0.8f : FollowGap(o.Speed, Style.Aggression, State == Mode.Pressure);
                cap = MathF.Min(cap, MathF.Max(o.Speed + 1.0f * (dsAhead - gap), 0));
            }
            if (State == Mode.Follow && cap >= racing.SpeedAt(s)) State = Mode.Line;
        }
        else _histN = 0;

        // --- defend: once per zone, before its braking point, when the car behind is < 1 s back and on the inside
        if (behind >= 0 && target == null && State == Mode.Line)
        {
            var att = others[behind];
            var next = map.NextCorner(s);
            var z = map.ZoneAt(s) ?? map.ZoneAt(s + 30);
            if (next >= 0 && z is { Corner: >= 0 } zd && zd.Corner == next && -dsBehind < 1.0f * MathF.Max(v, 10) && -dsBehind > CarLength * 0.7f)
            {
                var zi = IndexOf(map, zd);
                var dir = cs[next].Dir;
                if (_defendZone != zi && s < BrakePoint(next, v) - 5 && (att.Lateral - lat) * dir > 0.5f)
                {
                    _defendZone = zi;
                    if (Hash(Seed, 2, zi) < 0.8f * Style.Aggression)
                    {
                        // 1.0–1.5 m to the inside, leaving a car's width to the edge
                        var edge = dir > 0 ? hi : lo;
                        _defendTarget = dir > 0 ? MathF.Min(lat + 1 + 0.5f * Style.Aggression, edge - PassGap) : MathF.Max(lat - 1 - 0.5f * Style.Aggression, edge + PassGap);
                        _defended = (_defendTarget - lat) * dir > 0.2f ? next : -1;
                    }
                }
            }
            if (_defended >= 0 && s < cs[_defended].Apex)
            {
                target = _defendTarget;
                State = Mode.Block;
            }
        }
        if (_defended >= 0 && s >= cs[_defended].Apex) _defended = -1;

        // --- a car alongside (overlapping lengthwise), and off the start in a race: stay on our side of it
        float? hold = null; // our side of a car alongside: the override never crosses this
        var holdSide = 0;
        var startLane = s < MathF.Max(_startS + 150, cs.Count > 0 && cs[0].From < _startS + 400 ? cs[0].To : 0);
        foreach (var o in others)
        {
            var ds = o.Along - s;
            if (!(MathF.Abs(ds) < (startLane ? 12 : 6) && MathF.Abs(o.Lateral - lat) < PassGap + 1)) continue;
            var side = lat >= o.Lateral ? 1 : -1;
            var keep = o.Lateral + side * PassGap;
            var t = target ?? racing.OffsetAt(s + 10);
            if ((t - keep) * side >= 0) continue;
            // where the road's room ends before our side does, hold where we are (never towards it) and drop in behind
            var squeezed = (Math.Clamp(keep, lo, hi) - keep) * side < 0;
            var aimNow = Pilot.Blend > 0 ? Pilot.Lateral : lat; // the aim, not the car: no ratchet on the tracking error
            var limit = squeezed ? side > 0 ? MathF.Min(keep, aimNow) : MathF.Max(keep, aimNow) : keep;
            target = squeezed ? side > 0 ? MathF.Max(t, limit) : MathF.Min(t, limit) : keep;
            hold = hold is { } h0 ? side > 0 ? MathF.Max(h0, limit) : MathF.Min(h0, limit) : limit;
            holdSide = side;
            if (squeezed && ds > -3 && o.Speed > 3)
            {
                cap = MathF.Min(cap, o.Speed - 3);
                State = Mode.Follow;
            }
        }

        // --- recover after a wall hit or a spin: back to the line slowly, not across a car close behind
        if (car.WallImpactSpeed > 1 || MathF.Abs(car.SlipAngle) > 60 / Deg) _recoverUntil = _time + 1.5f;
        if (_time < _recoverUntil && !Drifting)
        {
            rate = 0.8f;
            if (target == null && behind >= 0 && -dsBehind < 30)
            {
                target = lat;
                State = Mode.Recover;
            }
        }

        // --- mistakes (seeded per corner, scaled by skill; never into a wall: the line's bounds hold)
        var u = MathF.Pow(1 - Math.Clamp(Style.Skill, 0, 1), 0.85f) * Style.Mistakes * MistakeScale * (underPressure && Style.Skill < 0.9f ? 1.5f : 1) * (rb < 0 ? 1.3f : 1);
        float wide = 0;
        if (ci >= 0)
        {
            var c = cs[ci];
            if (_rolled != ci)
            {
                (_rolled, _lockUntil, _throttleUntil, _overUntil) = (ci, -1, -1, -1);
                // braking point off by a Gaussian (only where there is braking to do), late errors halved: the corner is still made
                var braking = racing.SpeedAt(_brakeAt[ci]) > 1.1f * racing.SpeedAt(c.Apex);
                var heavy = racing.SpeedAt(_brakeAt[ci]) > 1.3f * racing.SpeedAt(c.Apex);
                var raw = braking ? Gauss(3, ci) * (0.5f + 3 * u) : 0;
                _err = (raw < 0 ? raw * 0.5f : raw, Hash(Seed, 4, ci) < 0.12f * u ? -c.Dir * (0.5f + Hash(Seed, 5, ci)) : 0,
                    heavy && Hash(Seed, 6, ci) < 0.02f * u, _spec!.DriveFront <= 0 && Hash(Seed, 7, ci) < 0.05f * u,
                    _drift[ci] != null && Hash(Seed, 8, ci) < 0.04f * u * Style.Drift);
                // a visible mistake: a braking point 4 m out, a wide exit, a lock-up, early throttle, an over-rotated drift
                Mistakes += (MathF.Abs(raw) > 4 ? 1 : 0) + (_err.Wide != 0 ? 1 : 0) + (_err.Lock ? 1 : 0) + (_err.Throttle ? 1 : 0) + (_err.Over ? 1 : 0);
            }
            if (s < c.Apex) Pilot.PlanShift += _err.Shift;
            if (_err.Lock && s >= _brakeAt[ci] && _lockUntil < 0) _lockUntil = _time + 0.3f;
            if (s > c.Apex && s < c.Apex + 30) wide = _err.Wide * MathF.Sin(MathF.PI * (s - c.Apex) / 30);
            if (_err.Lock && MathF.Abs(s - c.Apex) < 15) wide += -c.Dir * 1;
            if (_err.Throttle && s > c.Apex && _throttleUntil < 0) _throttleUntil = _time + 0.6f;
        }

        // --- lateral: ease the override (absolute) and its weight
        if (target is { } tg)
        {
            var tgt = Math.Clamp(tg, lo, hi);
            if (hold is { } hl) tgt = holdSide > 0 ? MathF.Max(tgt, hl) : MathF.Min(tgt, hl);
            if (Pilot.Blend <= 0) Pilot.Lateral = lat;
            Pilot.Lateral += Math.Clamp(tgt - Pilot.Lateral, -rate * dt, rate * dt);
            Pilot.Blend = MathF.Min(Pilot.Blend + 2 * dt, 1);
        }
        else
        {
            Pilot.Blend = MathF.Max(Pilot.Blend - 0.8f * dt, 0);
            if (Pilot.Blend <= 0) Pilot.Lateral = lat;
        }
        Pilot.Offset = wide;
        Pilot.SpeedCap = cap;
        // drift style lets the tail move more before the traction aid steps in (FR fully, 4WD half, FF not: it only tucks)
        var styleSlip = 0.1f * Style.Drift * (car.Spec.DriveFront <= 0 ? 1 : car.Spec.DriveFront < 1 ? 0.5f : 0);
        Pilot.SlipTolerance = _settleUntil > _time ? 0.6f : 0.05f + styleSlip + (_throttleUntil > _time ? 0.15f : 0);

        // --- drift corner ahead: decided 40 m before the turn-in (traffic alongside, a manoeuvre, a wall → grip; the
        // pilot then brakes for the grip speed: the plan there assumed the faster drift)
        var dc = -1;
        for (var i = 0; i < _window.Length && dc < 0; i++)
            if (_drift[i] != null && s >= _window[i].From - 70 && s <= _window[i].To) dc = i;
        var turnIn = dc >= 0 ? _window[dc].From - MathF.Max(DriftController.Tune[4] * racing.SpeedAt(_window[dc].From), 3) : 0;
        if (dc >= 0 && _decided != dc && s >= turnIn - 40)
        {
            _decided = dc;
            var blocked = State is Mode.Pass or Mode.Setup or Mode.Block or Mode.Recover || car.WallContacts > 0;
            foreach (var o in others) blocked |= MathF.Abs(o.Along - s) < 15 && MathF.Abs(o.Lateral - lat) < 3.5f;
            _gripCorner = blocked ? dc : -1;
        }
        Pilot.CheckCurvature = _gripCorner >= 0 && s <= _window[_gripCorner].To + 10;
        var input = Pilot.Drive(car);
        if (dc >= 0 && _drift[dc] is { } entry && _gripCorner != dc && Drift.State == DriftController.Phase.Idle && _driftDone != dc)
        {
            var c = _window[dc];
            var alongside = false;
            foreach (var o in others) alongside |= MathF.Abs(o.Along - s) < 8 && MathF.Abs(o.Lateral - lat) < 3.5f;
            if (s >= turnIn && s < c.Apex)
            {
                _driftDone = dc;
                if (!alongside && v > 8) Drift.Start(car, c, dc, entry);
                else _gripCorner = dc;
            }
            else if (entry == DriftController.Entry.BrakingDrift && s >= turnIn - 15 && v > Pilot.TargetSpeed - 0.5f)
                input = input with { Brake = MathF.Max(input.Brake, 0.3f), Throttle = 0 }; // trail-brake into the turn-in
        }
        if (Drift.State != DriftController.Phase.Idle)
        {
            if (Drift.Corner >= 0 && Drift.Corner < cs.Count)
            {
                var c = _window[Drift.Corner];
                var noise = 4 / Deg * u * MathF.Sin(MathF.Tau * 1.5f * _time + Seed);
                if (_err.Over && _rolled == Drift.Corner && Drift.State == DriftController.Phase.Hold && _overUntil < 0) _overUntil = _time + 0.3f;
                if (_overUntil > _time) noise += 15 / Deg;
                var beta = DriftController.StyleBeta(Style.Drift, car.Spec);
                var aborted = Drift.Aborted;
                if (Drift.Step(car, racing, c, s, lat, beta, noise, input, dt) is { } d) input = d;
                if (Drift.Aborted != aborted) _gripCorner = Drift.Corner; // caught: grip speed for the rest of it
                else if (Drift.State == DriftController.Phase.Idle) _settleUntil = _time + DriftController.Tune[11]; // let the tail settle on throttle
            }
            else Drift.Stop();
        }

        if (_lockUntil > _time) input = input with { Brake = 1, Throttle = 0 };
        if (_throttleUntil > _time && !Drifting) input = input with { Throttle = 1, Brake = 0 };
        return input;
    }

    /// <summary>Where a car at <paramref name="v"/> m/s has to start braking for corner <paramref name="i"/> (the plan's apex speed by its turn-in).</summary>
    private float BrakePoint(int i, float v)
    {
        var c = _map!.Corners[i];
        var vc = _racing!.SpeedAt(c.Apex);
        return c.From - MathF.Max(v * v - vc * vc, 0) / (2 * Pilot.BrakeDecel);
    }

    /// <summary>The drift controller is in charge.</summary>
    public bool Drifting => Drift.State != DriftController.Phase.Idle;

    private static int IndexOf(CourseMap map, CourseMap.Zone z)
    {
        for (var i = 0; i < map.Zones.Count; i++)
            if (map.Zones[i] == z) return i;
        return -1;
    }

    private void Remember(float along)
    {
        if (_histN > 0 && _time - _hist[(_histN - 1) % _hist.Length].T < 0.25f) return;
        _hist[_histN++ % _hist.Length] = (_time, along);
    }

    /// <summary>The car ahead's time over its last ~8 s of road against our plan's over the same stretch, −1: + = we are faster.</summary>
    private float PaceAdvantage(float along)
    {
        if (_histN < 8 || _racing == null) return 0;
        var oldest = _hist[Math.Max(0, _histN - 32) % _hist.Length];
        var planned = _racing.TimeBetween(oldest.S, along);
        return planned > 1 ? (_time - oldest.T) / planned - 1 : 0;
    }

    /// <summary>Forgets traffic and drift state (car reset onto the line).</summary>
    public void Reset()
    {
        (_fresh, _passing, _defended, _driftDone, _decided, _gripCorner, _lockUntil, _throttleUntil) = (true, -1, -1, -1, -1, -1, -1, -1);
        (Pilot.Offset, Pilot.SpeedCap, Pilot.Blend) = (0, float.PositiveInfinity, 0);
        Drift.Stop();
    }
}
