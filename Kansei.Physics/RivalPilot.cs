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

    /// <summary>
    ///     Overlap (m, our nose past the leader's tail) that commits a pass from the inside lane under braking: alongside by
    ///     its turn-in, the leader then has to leave room; less drops in behind again (no dive-bombs from further back).
    /// </summary>
    public const float CommitOverlap = 0.5f;

    /// <summary>Distance from a car's centre to the end of the full-grip road when side by side (half a body, a hand's width).</summary>
    public const float PassMargin = 1.2f;

    /// <summary>Lateral centre distance from which two cars are side by side (body widths ~1.7 m).</summary>
    public const float Alongside = 1.8f;

    public enum Mode { Line, Follow, Pressure, Setup, Pass, Block, Recover }

    private readonly Vector3[] _line;
    private CourseMap? _map;
    private RacingLine? _racing;
    private CarSpec? _spec;
    private DriftController.Entry?[] _drift = [];
    private DriftController.Entry _entry; // of the next drift, chosen at its decision
    private float[] _brakeAt = [];
    private CourseMap.Corner[] _window = [];
    private float _time, _driftEnd = float.MinValue, _band = float.NaN, _startS = float.NaN, _recoverUntil = -1, _lockUntil = -1, _throttleUntil = -1, _overUntil = -1;
    private bool _fresh = true;
    private int _passing = -1, _failedZone = -1, _defendZone = -1, _defended = -1, _rolled = -1, _driftDone = -1, _decided = -1, _gripCorner = -1;
    private float _defendTarget, _oSpeed = float.NaN, _oDecel;
    private int _setupZone = -1, _setupSide;
    private float _attemptNose, _attemptMax;
    /// <summary>Racecraft events (attempts, commits, failures) for traces.</summary>
    public Action<string>? Log { get; set; }
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
    /// <summary>Passing attempts set up in braking zones, and how many got alongside and committed (statistics).</summary>
    public int Attempts { get; private set; }
    public int Commits { get; private set; }
    /// <summary>What the racecraft saw this tick (traces: only while <see cref="Log"/> is set).</summary>
    public string Note { get; private set; } = "";
    /// <summary>The mistakes rolled for the current corner (traces).</summary>
    public string MistakeNote => _rolled >= 0 ? $"err c{_rolled} shift {_err.Shift:+0.0;-0.0} wide {_err.Wide:+0.0;-0.0}{(_err.Lock ? " lock" : "")}{(_err.Throttle ? " throttle" : "")}{(_err.Over ? " over" : "")}" : "";
    /// <summary>Mistakes so far (statistics).</summary>
    public int Mistakes { get; private set; }

    /// <summary>
    ///     Planned cornering of the skill scale (g): <see cref="CornerTop"/> at skill 1 (the AE86 at its skid-pad limit, a good
    ///     player's pace), falling by <see cref="CornerSpan"/> × (1 − k)^1.5 below (0.67 g at 0: a beginner); braking
    ///     <see cref="BrakeLow"/> … <see cref="BrakeTop"/> g. Calibrated with --ai-bench solo against H (README).
    /// </summary>
    public const float CornerTop = 1.45f, CornerSpan = 0.78f, BrakeLow = 0.6f, BrakeTop = 0.95f;

    /// <summary>
    ///     Planned cornering and braking deceleration (m/s²) for <paramref name="skill"/> 0..1, cornering scaled by the car's
    ///     skid-pad limit against the AE86's (<see cref="CarFactor"/>, <paramref name="spec"/>).
    /// </summary>
    public static (float Corner, float Brake) Pace(float skill, CarSpec? spec = null)
    {
        var k = Math.Clamp(skill, 0, 1);
        return (G * (CornerTop - CornerSpan * MathF.Pow(1 - k, 1.5f)) * (spec == null ? 1 : CarFactor(spec, 30)), G * float.Lerp(BrakeLow, BrakeTop, k));
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
        return MathF.Pow(float.Lerp(r30, r100, Math.Clamp((radius - 30) / 70, 0, 1)), 2);
    }

    /// <summary>
    ///     Extra planned lateral grip in drift corners for the AE86 (m/s²): the drift layer keeps momentum and carves (1.5–1.6 g
    ///     measured on the flat, more with the drift line's room); heavier cars carry less of it (× AE86 mass / mass, so the
    ///     R34 gets ~0.25 g). Tuned so a drift corner is about as fast as the grip one (--ai-bench drift).
    /// </summary>
    public const float DriftBonus = 0.40f * G;

    /// <summary>The drift bonus of <paramref name="spec"/> (m/s²).</summary>
    public static float DriftBonusOf(CarSpec spec) => DriftBonus * MathF.Min(CarSpec.AE86.Mass / spec.Mass, 1);

    /// <summary>A drift starts where the own line's curvature first reaches this share of the bend's sharpest, and this long before (s at the planned speed).</summary>
    public const float DriftWindow = 0.6f, TurnInLead = 0.35f;

    /// <summary>Extra edge margin of the drift line per unit sin β (m): the swung-out tail and a drift that carves tighter than planned.</summary>
    public const float DriftTail = 0.9f;

    /// <summary>Feint entries from this speed at the turn-in (m/s).</summary>
    public const float FeintSpeed = 70 / 3.6f;

    /// <summary>Road (m) beyond the line on the outside at the turn-in that a feint needs.</summary>
    public const float FeintRoom = 3.5f;

    /// <summary>Slower corners (m/s at the apex) are not drifted: the tail cannot be held under ~40 km/h (IROHA's tightest hairpins).</summary>
    public const float MinDriftSpeed = 40 / 3.6f;

    /// <summary>
    ///     Nor faster ones than the car holds a drift at (<see cref="GripLimit.DriftSpeed"/>, +15 %), at most this (m/s): a
    ///     heavy, powerful car sliding a fast bend runs wide (FD3S: 2–3 wall hits per run more).
    /// </summary>
    public const float MaxDriftSpeed = 65 / 3.6f;

    /// <summary>Following distance (m, centre to centre) behind a car at <paramref name="speed"/>: a metre plus 0.8 … 0.4 s by aggression, 0.12 … 0.02 s when pressing (on its bumper).</summary>
    public static float FollowGap(float speed, float aggression, bool pressure = false) =>
        CarLength + 1 + speed * (pressure ? 0.12f - 0.1f * aggression : 0.8f - 0.4f * aggression);

    /// <summary>
    ///     Probability that a drift-eligible corner is drifted: 1.25 × drift − 0.15 (Takumi/Keisuke ~1, Ryosuke 0.6), less
    ///     for beginners (from nothing at skill 0.1 to all of it at 0.4: a drift is an advanced move).
    /// </summary>
    public static float DriftChance(float drift, float skill = 1) => Math.Clamp(1.25f * drift - 0.15f, 0, 1) * Math.Clamp((skill - 0.1f) / 0.3f, 0, 1);

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
        var p = DriftChance(d, style.Skill);
        if (!eligible || roll >= p) return null;
        if (ff) return DriftController.Entry.Tuck;
        // Takumi-style: half of the drifts braked deep into the turn-in
        return fr && d >= 0.85f && roll < p / 2 ? DriftController.Entry.BrakingDrift : DriftController.Entry.Handbrake;
    }

    /// <summary>
    ///     The entry of a planned handbrake drift at the turn-in, at <paramref name="v"/> m/s: a feint (drift style ≥ 0.8, fast
    ///     corners with <paramref name="outside"/> ≥ <see cref="FeintRoom"/> m of road beyond the line to swing out into, half of
    ///     them), else the flick. <paramref name="roll"/> 0..1. (A power-over entry without the handbrake ran wide twice as
    ///     often in the FD3S: not used.)
    /// </summary>
    public static DriftController.Entry EntryAt(DriftController.Entry planned, RivalStyle style, float v, float outside, float roll) =>
        planned == DriftController.Entry.Handbrake && style.Drift >= 0.8f && v >= FeintSpeed && outside >= FeintRoom && roll < 0.5f ? DriftController.Entry.Feint : planned;

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
        var holds = MathF.Min(GripLimit.DriftSpeed(car.Spec) * 1.15f, MaxDriftSpeed);
        for (var pass = 0; pass < 2; pass++)
        {
            // drift corners: room for the swung-out tail
            var margin = new float[_map.Count];
            Array.Fill(margin, EdgeMargin);
            for (var i = 0; i < cs.Count; i++)
                if (_drift[i] is not (null or DriftController.Entry.Tuck))
                    for (var j = _map.Index(cs[i].From - 15); j <= _map.Index(cs[i].To + 10); j++) margin[j] = MathF.Max(margin[j], EdgeMargin + DriftTail * MathF.Sin(beta));
            _racing = new RacingLine(_map, margin);
            Pilot.Plan = _racing;
            _band = float.NaN;
            Replan(0);
            // drift window per corner: where the own line really bends (entry from DriftWindow of its sharpest, exit 35 %)
            _window = new CourseMap.Corner[cs.Count];
            for (var i = 0; i < cs.Count; i++)
            {
                var c = cs[i];
                int a = _map.Index(c.From - 30), b = Math.Min(_map.Index(c.To + 30), _map.Count - 1), top = a;
                for (var j = a; j <= b; j++)
                    if (_racing.Curvature[j] * c.Dir > _racing.Curvature[top] * c.Dir) top = j;
                float max = _racing.Curvature[top] * c.Dir, entry = DriftWindow * max, exit = 0.35f * max;
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
        var bonus = DriftBonusOf(spec);
        _racing.Plan(i => aLat * CarFactor(spec, 1 / MathF.Max(MathF.Abs(racing.Curvature[i]), 1e-3f)) + (drift[i] ? bonus : 0), aBrake, _spec, Pilot.TopSpeed);
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
        float cap = float.PositiveInfinity, ram = float.PositiveInfinity; // speed caps: traffic, and no contact
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
        // side by side a car may use the road out to a body half plus a hand's width from its end (the racing line keeps 1.3 m)
        var (roomL, roomR) = map.Room(s);
        float passLo = MathF.Min(lo, PassMargin - roomR), passHi = MathF.Max(hi, roomL - PassMargin);
        // side by side in a hairpin the bodies swing out: more room between the two
        var sideGap = PassGap + (MathF.Abs(map.At(map.Bend, s + 10)) > 1 / 25f ? 0.6f : 0);

        if (ahead >= 0)
        {
            var o = others[ahead];
            Remember(o.Along, o.Speed, dt);
            var adv = PaceAdvantage(o.Along);
            var zone = map.ZoneAt(s) ?? map.ZoneAt(s + 25);
            var zi = zone is { } zz ? IndexOf(map, zz) : -1;
            var nose = s - o.Along + CarLength; // our nose past their tail (m)
            var mode = Mode.Follow;
            // an attack set up in a zone runs on to its corner's apex (the zone itself ends at the turn-in)
            if (_setupZone >= 0 && (map.Zones[_setupZone] is var sz && (sz.Corner >= 0 ? s > cs[sz.Corner].Apex : zi != _setupZone) || _setupZone == _failedZone))
                _setupZone = -1;
            var az = _setupZone >= 0 ? _setupZone : zi; // the zone being attacked
            var why = "";
            if (!NoPass && dsAhead < 40 && (adv >= 0.02f || zone is { Corner: < 0 } && v > o.Speed * 1.03f))
            {
                mode = Mode.Pressure;
                if (_passing >= 0 && s < cs[_passing].To && nose > -3)
                {
                    // committed: hold the inside through the corner
                    target = Math.Clamp(o.Lateral + cs[_passing].Dir * sideGap, passLo, passHi);
                    mode = Mode.Pass;
                    Pilot.BrakeDecel += 0.05f * G;
                }
                else if (az >= 0 && az != _failedZone)
                {
                    var z = map.Zones[az];
                    // the inside of the zone's corner, else (no room there, or a straight) the side the leader leaves open
                    var open = passHi - o.Lateral > o.Lateral - passLo ? 1 : -1;
                    var side = _setupZone == az ? _setupSide
                        : z.Corner >= 0 && (cs[z.Corner].Dir > 0 ? passHi - o.Lateral : o.Lateral - passLo) >= Alongside ? cs[z.Corner].Dir : open;
                    var want = Math.Clamp(o.Lateral + side * sideGap, passLo, passHi);
                    var close = dsAhead <= FollowGap(v, Style.Aggression, true) + 2; // pressing, right behind
                    if (MathF.Abs(want - o.Lateral) < Alongside) why = "room"; // no room for two here
                    else if (z.Corner >= 0)
                    {
                        // a braking zone: pull out from close behind before the leader brakes, stay out and brake later
                        // than it (own plan, 3–6 m later, ≤ +0.05 g); an overlap by the turn-in commits the pass,
                        // still behind at the apex (or well back when it brakes) drops in behind again: no dive-bombs
                        var c = cs[z.Corner];
                        var braking = o.Along >= BrakePoint(z.Corner, o.Speed) - 1 || _oDecel > 3;
                        // worth it: a real stop ahead (≥ 8 m of braking left for the leader), and our nose within 6–9 m of
                        // its tail before it brakes, or on its bumper (2–4 m) while it brakes, 10 m before its turn-in
                        var vc = racing.SpeedAt(c.Apex);
                        var stop = (o.Speed * o.Speed - vc * vc) / (2 * Pilot.BrakeDecel) >= 8;
                        var near = braking ? nose >= -2 - 2 * Style.Aggression : nose >= -6 - 3 * Style.Aggression;
                        var soon = o.Along >= BrakePoint(z.Corner, o.Speed) - 25; // no long run side by side before it
                        if (_setupZone != az && stop && near && soon && o.Along < c.From - 10)
                        {
                            (_setupZone, _setupSide, Attempts, _attemptNose, _attemptMax) = (az, side, Attempts + 1, nose, nose);
                            Log?.Invoke($"attempt z{az} c{z.Corner} {cs[z.Corner].Kind} nose {nose:F1} braking {braking} v {v * 3.6f:F0}/{o.Speed * 3.6f:F0} to turn-in {c.From - o.Along:F0} m");
                        }
                        else if (_setupZone != az) why = !stop ? "nostop" : !near ? "far" : "late";
                        if (_setupZone == az)
                        {
                            _attemptMax = MathF.Max(_attemptMax, nose);
                            if (nose >= CommitOverlap && (braking || o.Along >= c.From - 2))
                            {
                                if (_passing != z.Corner) Log?.Invoke($"commit z{az} nose {nose:F1} (from {_attemptNose:F1})");
                                (mode, _passing, Commits) = (Mode.Pass, z.Corner, Commits + (_passing == z.Corner ? 0 : 1));
                            }
                            else if (o.Along >= c.Apex || braking && nose < -8)
                            {
                                _failedZone = az;
                                Log?.Invoke($"fail z{az} nose {nose:F1} max {_attemptMax:F1} (from {_attemptNose:F1}) {(o.Along >= c.Apex ? "apex" : "dropped back")}");
                            }
                            else
                            {
                                target = want;
                                mode = Mode.Setup;
                                if (braking)
                                {
                                    Pilot.PlanShift = -(3 + 3 * Style.Aggression);
                                    Pilot.BrakeDecel += 0.05f * G;
                                }
                            }
                        }
                    }
                    else if (close || _setupZone == az)
                    {
                        // a straight: out to the open side from close behind; alongside with the speed on it: past
                        if (_setupZone != az) (_setupZone, _setupSide) = (az, side);
                        target = want;
                        mode = MathF.Abs(lat - o.Lateral) > Alongside - 0.2f ? Mode.Pass : Mode.Setup;
                        if (v < o.Speed - 1.5f && nose < 0) _failedZone = az; // it pulls away: give up
                    }
                    if (mode == Mode.Pass && target == null) target = want;
                }
                if (mode == Mode.Pressure && zone is { Corner: >= 0 } z2)
                    target = Math.Clamp(o.Lateral + cs[z2.Corner].Dir * 0.8f, lo, hi); // show the nose on the inside
            }
            if (mode is Mode.Setup or Mode.Pass) rate = 2.5f;
            if (Log != null) // traces only: no garbage per tick in the game
                Note = $"{why} adv {adv * 100:+0.0;-0.0}% zone {zi}{(zone is { } zn ? zn.Corner >= 0 ? $"→c{zn.Corner}" : "=str" : "")} setup {_setupZone} fail {_failedZone} nose {nose:+0.0;-0.0} tgt {target:+0.0;-0.0}";
            State = mode;
            if (_passing >= 0 && (s >= cs[_passing].To || mode != Mode.Pass)) _passing = -1;
            // keep out of its boot: in line behind it the following gap, against any overlap a time to contact ≥ 1 s
            // with the leader's braking taken into account (then braking at once, below)
            var overlap = MathF.Abs(lat - o.Lateral) < Alongside + 0.1f;
            var inLine = MathF.Abs(lat - o.Lateral) < Alongside && MathF.Abs((target ?? lat) - o.Lateral) < Alongside + 0.2f;
            var oSpeed = MathF.Max(o.Speed - _oDecel * 0.3f, 0); // where its speed is going
            if (overlap) ram = MathF.Min(ram, oSpeed + MathF.Max(dsAhead - CarLength - 0.8f, 0) / 1.0f);
            if (inLine || NoPass) // in line behind it, or no passing yet (a lead/chase off the start): its following gap
            {
                var gap = State is Mode.Setup or Mode.Pass ? CarLength + 0.8f : FollowGap(o.Speed, Style.Aggression, State == Mode.Pressure);
                cap = MathF.Min(cap, MathF.Max(oSpeed + 1.0f * (dsAhead - gap), 0));
            }
            cap = MathF.Min(cap, ram);
            if (State == Mode.Follow && cap >= racing.SpeedAt(s)) State = Mode.Line;
        }
        else (_histN, _oDecel, _oSpeed) = (0, 0, float.NaN);

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
            // a car that gets 30 % alongside meanwhile has its lane: the cover ends (no squeezing it)
            if (_defended >= 0 && -dsBehind < CarLength * 0.7f) _defended = -1;
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
            if (!(MathF.Abs(ds) < (startLane ? 12 : 6) && MathF.Abs(o.Lateral - lat) < sideGap + 1)) continue;
            var side = lat >= o.Lateral ? 1 : -1;
            var keep = o.Lateral + side * sideGap;
            var t = target ?? racing.OffsetAt(s + 10);
            if ((t - keep) * side >= 0) continue;
            // where the road's room ends before our side does, hold where we are (never towards it) and drop in behind
            var squeezed = (Math.Clamp(keep, passLo, passHi) - keep) * side < 0;
            var aimNow = Pilot.Blend > 0 ? Pilot.Lateral : lat; // the aim, not the car: no ratchet on the tracking error
            var limit = squeezed ? side > 0 ? MathF.Min(keep, aimNow) : MathF.Max(keep, aimNow) : keep;
            target = squeezed ? side > 0 ? MathF.Max(t, limit) : MathF.Min(t, limit) : keep;
            hold = hold is { } h0 ? side > 0 ? MathF.Max(h0, limit) : MathF.Min(h0, limit) : limit;
            holdSide = side;
            if (squeezed && ds > -1 && State != Mode.Pass && o.Speed > 3)
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
            // never towards a wall: at most to half a metre inside the line's bounds (the line itself may run along them)
            var off = racing.OffsetAt(s + 5);
            wide = Math.Clamp(wide, MathF.Min(lo + 0.5f - off, 0), MathF.Max(hi - 0.5f - off, 0));
        }

        // --- lateral: ease the override (absolute) and its weight
        if (target is { } tg)
        {
            var tgt = State is Mode.Setup or Mode.Pass ? Math.Clamp(tg, passLo, passHi) : Math.Clamp(tg, lo, hi);
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
        // the traction aid as for everybody in grip corners (drifts are the drift controller's), looser for an early-throttle slide
        Pilot.SlipTolerance = 0.05f + (_throttleUntil > _time ? 0.15f : 0);

        // --- drift corner ahead: decided 40 m before the turn-in (traffic alongside, a manoeuvre, a wall → grip; the
        // pilot then brakes for the grip speed: the plan there assumed the faster drift)
        var dc = -1;
        for (var i = 0; i < _window.Length && dc < 0; i++)
            if (_drift[i] != null && s >= _window[i].From - 70 && s <= _window[i].To) dc = i;
        var turnIn = dc >= 0 ? _window[dc].From - MathF.Max(TurnInLead * racing.SpeedAt(_window[dc].From), 3) : 0;
        if (dc >= 0 && _decided != dc && s >= turnIn - 40)
        {
            _decided = dc;
            var blocked = State is Mode.Pass or Mode.Setup or Mode.Block or Mode.Recover || car.WallContacts > 0;
            foreach (var o in others) blocked |= MathF.Abs(o.Along - s) < 15 && MathF.Abs(o.Lateral - lat) < 3.5f;
            _gripCorner = blocked ? dc : -1;
            if (_drift[dc] is { } planned)
            {
                var (l, r) = map.Room(turnIn);
                var outside = cs[dc].Dir > 0 ? racing.OffsetAt(turnIn) + r : l - racing.OffsetAt(turnIn);
                _entry = EntryAt(planned, Style, racing.SpeedAt(turnIn), outside, Hash(Seed, 9, dc));
            }
        }
        if (dc >= 0 && _entry == DriftController.Entry.Feint) turnIn -= DriftController.FeintTime * racing.SpeedAt(turnIn); // the feint comes first
        Pilot.CheckCurvature = _gripCorner >= 0 && s <= _window[_gripCorner].To + 10;
        var input = Pilot.Drive(car);
        if (dc >= 0 && _drift[dc] != null && _decided == dc && _gripCorner != dc && Drift.State == DriftController.Phase.Idle && _driftDone != dc)
        {
            var c = _window[dc];
            var alongside = false;
            foreach (var o in others) alongside |= MathF.Abs(o.Along - s) < 8 && MathF.Abs(o.Lateral - lat) < 3.5f;
            if (s >= turnIn && s < c.Apex)
            {
                _driftDone = dc;
                // not too slow (the tail would not come back), not straight out of the last one (an S-bend)
                if (!alongside && v > MinDriftSpeed && _time > _driftEnd + 0.5f) Drift.Start(car, c, dc, _entry);
                else _gripCorner = dc;
            }
            else if (_entry == DriftController.Entry.BrakingDrift && s >= turnIn - 15 && v > Pilot.TargetSpeed - 0.5f)
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
                if (Drift.Step(car, racing, c, s, lat, beta, noise, input, dt) is { } d) input = d;
                if (Drift.EndedEarly) _gripCorner = Drift.Corner; // caught or faded: grip speed for the rest of it
                if (Drift.State == DriftController.Phase.Idle) _driftEnd = _time;
            }
            else Drift.Stop();
        }

        // about to touch the car ahead: brake now (the speed control alone reacts too gently for this)
        if (v > ram + 0.3f) input = input with { Throttle = 0, Brake = MathF.Max(input.Brake, Math.Clamp((v - ram) * 0.6f, 0.3f, 1)) };
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

    private void Remember(float along, float speed, float dt)
    {
        // the leader's deceleration (m/s², smoothed over ~0.1 s; + = braking)
        if (!float.IsNaN(_oSpeed)) _oDecel += ((_oSpeed - speed) / dt - _oDecel) * MathF.Min(10 * dt, 1);
        _oSpeed = speed;
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
        (_fresh, _passing, _defended, _driftDone, _decided, _gripCorner, _lockUntil, _throttleUntil, _overUntil) = (true, -1, -1, -1, -1, -1, -1, -1, -1);
        (_setupZone, _failedZone, _defendZone, _oSpeed, _oDecel) = (-1, -1, -1, float.NaN, 0);
        (Pilot.Offset, Pilot.SpeedCap, Pilot.Blend) = (0, float.PositiveInfinity, 0);
        Drift.Stop();
    }
}
