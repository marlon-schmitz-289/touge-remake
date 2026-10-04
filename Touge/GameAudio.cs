using Kansei.Audio;
using Kansei.Physics;
using Touge.Formats;

namespace Touge;

/// <summary>
///     Sound of a drive, all from the ISO except wind/road noise (synthesised, the disc has none):
///     engine = CARSE <c>&lt;BANK&gt;_U</c> (on throttle) + <c>_D</c> (overrun) of the car (<see cref="Engines"/>), 8 looped
///     layers each = 4 rpm zones × (broadband layer k, tonal layer k + 4), every layer resampled by rpm / its native rpm
///     (<see cref="Zones"/>), neighbouring zones crossfaded with equal power (<see cref="ZoneWeights"/>); tyres <c>SRIP_A</c> (road; <c>RAIN_SRIP</c> on _RIN courses) /
///     <c>RAIN_SRIP</c> (grass, stand-in); rain ambience SYSSE <c>rain</c> looped on _RIN courses;
///     walls <c>cr001/cr002</c> (one-shot by impact speed, cr002's middle looped as scrape); <c>zbackfire002a–h</c> on
///     high-rpm upshifts/lift-off. Race music is the course-independent <see cref="Jukebox"/>.
///     <see cref="Update"/> once per physics tick, allocation-free.
/// </summary>
public sealed class GameAudio : IDisposable
{
    private const float EngineVolume = 0.6f; // the game's own ×0.6 on the SECT volume (sub_00178800)

    /// <summary>
    ///     Engine sound per car id (<see cref="CarPaint.Cars"/> order): CARSE bank from the ELF table 0x24E920 (stock;
    ///     sub_00189B00 builds "[TU_]&lt;bank&gt;_U/_D"). AE86T/AE86L: the game plays the MRS bank when stock and switches
    ///     to <c>AE86</c> only at tuning level 5 (sub_00189FA0) — the remake gives them that full-tune sound.
    /// </summary>
    public static readonly string[] Engines =
    [
        "AE86", "AE86", "MRS", "MR2", "MRS", "AL", "MR2", "GTR", "GTR", "GTR", "S13", "S13", "S13", "S13", "S13", "S13",
        "EK9", "EK9", "EK9", "EK9", "EVO", "EVO", "EVO", "FD", "FD", "FD", "NA6", "NA6", "GC8", "GC8", "GC8", "CP",
    ];

    /// <summary>
    ///     Per bank, for <c>_U</c> and <c>_D</c>, in x = rpm / rev limit: centres of zones 1–3 (zone 0 = idle) and the native
    ///     x of zones 0–3 (where layers k and k + 4 play at their recorded rate). Centres: geometric middle of each tonal
    ///     layer's full-volume SECT window, mapped to x through <see cref="Sect.EngineIndex"/> at mid gear. Natives: the
    ///     neighbouring tonal samples' pitch offset measured by cross-correlating their whitened log spectra (pitch class
    ///     taken when the correlation is ≥ 0.5, octave the one nearest the SECT curves' intent, else the SECT intent),
    ///     chained and fitted to the original's rate at the zone centres (+4 semitones key + SECT bend), then limited so a
    ///     zone plays at 0.6–1.8× wherever it is the loudest; zone 0 and GTR_U zone 3 corrected from the measured
    ///     handover offset of <c>--sweep</c> captures. See FORMATS.md.
    /// </summary>
    private static readonly Dictionary<string, ((float[] C, float[] N) U, (float[] C, float[] N) D)> Zones = new()
    {
        ["AE86"] = (([.160f, .377f, .727f], [.094f, .137f, .314f, .671f]), ([.165f, .374f, .691f], [.076f, .138f, .302f, .689f])),
        ["AL"] = (([.169f, .463f, .826f], [.091f, .155f, .392f, .704f]), ([.163f, .450f, .808f], [.093f, .151f, .414f, .576f])),
        ["CP"] = (([.188f, .484f, .801f], [.095f, .168f, .400f, .556f]), ([.163f, .415f, .769f], [.098f, .145f, .347f, .556f])),
        ["EK9"] = (([.152f, .440f, .824f], [.078f, .144f, .334f, .712f]), ([.163f, .431f, .815f], [.081f, .147f, .419f, .556f])),
        ["EVO"] = (([.192f, .485f, .803f], [.092f, .170f, .374f, .662f]), ([.174f, .472f, .801f], [.115f, .159f, .365f, .556f])),
        ["FD"] = (([.188f, .501f, .834f], [.084f, .171f, .469f, .640f]), ([.207f, .551f, .837f], [.089f, .188f, .453f, .659f])),
        ["GC8"] = (([.169f, .462f, .795f], [.120f, .155f, .404f, .556f]), ([.158f, .436f, .819f], [.098f, .146f, .361f, .556f])),
        ["GTR"] = (([.213f, .554f, .862f], [.086f, .191f, .515f, .556f]), ([.216f, .580f, .856f], [.090f, .196f, .463f, .674f])),
        ["MR2"] = (([.196f, .501f, .808f], [.124f, .174f, .354f, .556f]), ([.169f, .426f, .819f], [.083f, .149f, .357f, .849f])),
        ["MRS"] = (([.240f, .574f, .851f], [.098f, .206f, .480f, .756f]), ([.200f, .571f, .856f], [.093f, .188f, .470f, .656f])),
        ["NA6"] = (([.174f, .481f, .815f], [.078f, .161f, .474f, .601f]), ([.140f, .375f, .787f], [.070f, .127f, .302f, .788f])),
        ["S13"] = (([.196f, .551f, .853f], [.087f, .185f, .494f, .600f]), ([.203f, .543f, .855f], [.102f, .193f, .403f, .608f])),
    };

    /// <summary>Resampling range of a layer; beyond it the layer holds its pitch (it is faded out there anyway).</summary>
    public const float MinRate = 0.5f, MaxRate = 2f;

    private readonly AudioDevice _dev;
    private readonly Afs _carse;
    private readonly List<AudioDevice.Clip> _clips = [], _engineClips = [];
    private readonly AudioDevice.LoopVoice[] _engine = new AudioDevice.LoopVoice[16]; // 0–7 load (U), 8–15 overrun (D)
    internal IReadOnlyList<AudioDevice.LoopVoice> EngineVoices => _engine;
    private readonly float[] _centre = new float[8], _native = new float[8], _level = new float[16], _weight = new float[8]; // [bank·4 + zone], [voice]
    private float _x = -1; // smoothed rpm / rev limit
    private readonly AudioDevice.LoopVoice _squeal, _squealHigh, _dirt, _scrape, _road, _wind;
    private readonly AudioDevice.LoopVoice? _rain;
    private readonly AudioDevice.Clip _crashA, _crashB, _skid;
    private readonly AudioDevice.Clip[] _backfire;

    // state
    private float _load, _limiterPhase, _shiftDip, _dip = 1, _crashCooldown, _backfireCooldown, _prevThrottle, _prevRpm, _squealGain, _dirtGain, _scrapeGain;
    private int _prevGear = 1, _backfireNext; // Vehicle.Reset puts the car in 1st
    private bool _prevHandbrake, _crashToggle;

    /// <summary>For logs/analysis: gain-weighted playback rate of the engine layers, total engine gain, squeal gain, max normalised wheel slip.</summary>
    public float EnginePitch { get; private set; }
    /// <summary>Position between the on-throttle zones (0 = idle … 3 = top; 1.5 = halfway through the 1→2 crossfade).</summary>
    public float EngineZone { get; private set; }
    public float EngineGain { get; private set; }
    public float SquealGain => _squealGain;
    public float ScrapeGain => _scrapeGain;
    public int Crashes { get; private set; }
    public float Slip { get; private set; }

    public GameAudio(Iso9660 iso, string courseTime, AudioDevice dev, string car = "AE86T")
    {
        _dev = dev;
        var carse = _carse = iso.OpenAfs("CDVD/DATA/SOUND/CARSE.AFS");
        SetCar(car);
        var wet = courseTime.EndsWith("_RIN");
        var srip = Bank(carse, "SRIP_A");
        var rainSrip = Bank(carse, "RAIN_SRIP");
        (_squeal, _squealHigh, _skid) = wet ? (Loop(rainSrip[0]), Loop(rainSrip[1]), srip[3]) : (Loop(srip[0]), Loop(srip[1]), srip[3]);
        _dirt = Loop(rainSrip[0]);

        var sys = SysSe(iso);
        _crashA = Clip(sys("cr001.vag"));
        var cr2 = sys("cr002.vag");
        _crashB = Clip(cr2);
        // cr002 stays loud from ~0.3 to ~1.9 s: its middle loops as wall scrape (no scrape sample on the disc)
        _scrape = Loop(Clip(cr2 with { Loop = (cr2.Rate * 6 / 10, cr2.Rate * 18 / 10) }));
        _backfire = [.. "abcdefgh".Select(c => Clip(sys($"zbackfire002{c}.vag")))];
        if (wet)
        {
            _rain = Loop(Clip(Seamless(sys("rain.vag"), 0.4f)));
            _rain.Gain = 0.5f;
        }

        _road = Loop(Clip(new Vag.Sound(Noise(22050 * 2, 0.06f, 1), 22050, null)));
        _wind = Loop(Clip(new Vag.Sound(Noise(22050 * 2, 0.35f, 2), 22050, null)));

        (dev.Music, dev.Sfx) = (0.6f, 0.35f);
    }

    /// <summary>Decoder for the SYSSE.BIN bank by name ("cr001.vag"; names from SYSSE.TBL, case-insensitive).</summary>
    public static Func<string, Vag.Sound> SysSe(Iso9660 iso)
    {
        var sysse = iso.ReadFile("CDVD/DATA/SOUND/SYSSE.BIN");
        var bank = Vag.SysSe(sysse);
        var names = Afs.ParseTbl(iso.ReadFile("CDVD/DATA/SOUND/SYSSE.TBL"), bank.Length) ?? [];
        return name =>
        {
            var i = Array.FindIndex(names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) throw new FileNotFoundException($"SYSSE.BIN: {name}");
            return Vag.Decode(sysse.AsSpan(bank[i].Offset, bank[i].Size), bank[i].Rate);
        };
    }

    /// <summary>Engine banks of <paramref name="car"/> (HCAR name); replaces the current ones (car change at standstill).</summary>
    public void SetCar(string car)
    {
        var bank = Engines[Math.Max(Array.IndexOf(CarPaint.Cars, car), 0)];
        foreach (var v in _engine) v?.Dispose();
        foreach (var c in _engineClips) c.Dispose();
        _engineClips.Clear();
        var (u, d) = Zones[bank];
        foreach (var (side, zones, b) in new[] { ("_U", u, 0), ("_D", d, 1) })
        {
            zones.C.CopyTo(_centre, b * 4 + 1);
            zones.N.CopyTo(_native, b * 4);
            // level of a layer = its SECT volume plateau × the game's ×0.6
            var curves = new Sect(_carse.Read(_carse.Find(bank + side + ".DAT")!.Value));
            var clips = Bank(_carse, bank + side, _engineClips);
            for (var i = 0; i < 8; i++)
            {
                _engine[b * 8 + i] = Loop(clips[i]);
                _level[b * 8 + i] = Enumerable.Range(0, 256).Max(x => curves.Value(i, Sect.Volume, x)) / 127f * EngineVolume;
            }
        }
        _x = -1;
    }

    /// <summary>
    ///     Equal-power weights of the zones at <paramref name="x"/>: the two zones whose centres enclose x share it by
    ///     cos/sin of the log-x position between them (w₀² + w₁² = 1), below the first / above the last centre only that
    ///     zone sounds. Returns the fractional zone position.
    /// </summary>
    public static float ZoneWeights(ReadOnlySpan<float> centres, float x, Span<float> w)
    {
        w.Clear();
        var last = centres.Length - 1;
        if (x <= centres[0]) { w[0] = 1; return 0; }
        if (x >= centres[last]) { w[last] = 1; return last; }
        var k = 0;
        while (x > centres[k + 1]) k++;
        var u = MathF.Log(x / centres[k]) / MathF.Log(centres[k + 1] / centres[k]);
        (w[k], w[k + 1]) = (MathF.Cos(u * MathF.PI / 2), MathF.Sin(u * MathF.PI / 2));
        return k + u;
    }

    /// <summary>Playback rate of a layer recorded at <paramref name="native"/> x when the engine runs at x: exactly proportional, clamped.</summary>
    public static float LayerRate(float x, float native) => Math.Clamp(x / native, MinRate, MaxRate);

    private AudioDevice.Clip Clip(Vag.Sound s, List<AudioDevice.Clip>? owner = null)
    {
        var c = _dev.CreateClip(s.Pcm, 1, s.Rate, s.Loop);
        (owner ?? _clips).Add(c);
        return c;
    }

    private AudioDevice.LoopVoice Loop(AudioDevice.Clip clip)
    {
        var v = _dev.CreateLoop(clip);
        v.Gain = 0;
        v.Play();
        return v;
    }

    private AudioDevice.Clip[] Bank(Afs carse, string name, List<AudioDevice.Clip>? owner = null)
    {
        var (hd, bd) = Vag.Mrg(carse.Read(carse.Find(name + ".MRG")!.Value));
        // program i plays Vagi i (the HD's Smpl chunk maps them 1:1)
        return [.. Vag.HdSamples(hd, bd.Length).Select(s => Clip(Vag.Decode(bd.AsSpan(s.Offset, s.Size), s.Rate), owner))];
    }

    /// <summary>
    ///     Loops a sample without a loop point (rain.vag): the last <paramref name="seconds"/> crossfade into the first ones,
    ///     the loop runs from there to the end, so the jump back lands on matching material.
    /// </summary>
    private static Vag.Sound Seamless(Vag.Sound s, float seconds)
    {
        var n = Math.Min((int)(s.Rate * seconds), s.Pcm.Length / 2);
        var pcm = (short[])s.Pcm.Clone();
        var start = pcm.Length - n;
        for (var k = 0; k < n; k++)
            pcm[start + k] = (short)(s.Pcm[start + k] * (1 - (float)k / n) + s.Pcm[k] * ((float)k / n));
        return s with { Pcm = pcm, Loop = (n, pcm.Length) };
    }

    /// <summary>Low-passed white noise (one-pole, <paramref name="a"/> = filter coefficient), normalised to −6 dBFS peak.</summary>
    private static short[] Noise(int n, float a, uint seed)
    {
        var f = new float[n];
        float y = 0, peak = 1e-6f;
        for (var i = 0; i < n; i++)
        {
            seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; // xorshift32
            y += a * ((seed / (float)uint.MaxValue * 2 - 1) - y);
            f[i] = y;
            peak = MathF.Max(peak, MathF.Abs(y));
        }
        return Array.ConvertAll(f, v => (short)(v / peak * 16000));
    }

    /// <summary>One physics tick: <paramref name="throttle"/>/<paramref name="handbrake"/> as given to <see cref="Vehicle.Step"/>.</summary>
    public void Update(Vehicle car, float throttle, bool handbrake, float dt)
    {
        UpdateEngine(car.Spec, car.Rpm, car.Gear, throttle, dt);
        var spec = car.Spec;
        var speed = car.Velocity.Length();

        // Tyres: normalised combined slip (1 = peak grip); grass wheels go to the dirt voice
        float road = 0, dirt = 0, maxSlip = 0;
        foreach (ref readonly var w in car.Wheels)
        {
            if (!w.Contact) continue;
            var n = MathF.Sqrt(w.SlipRatio * w.SlipRatio / (spec.PeakSlipRatio * spec.PeakSlipRatio) + w.SlipAngle * w.SlipAngle / (spec.PeakSlipAngle * spec.PeakSlipAngle));
            maxSlip = MathF.Max(maxSlip, n);
            var s = Math.Clamp((n - 0.4f) / 2, 0, 1); // arcade: audible from well below the grip peak
            if ((car.SurfaceGrip?.Invoke(w.Surface) ?? 1) < 0.7f) dirt = MathF.Max(dirt, MathF.Max(s, 0.3f));
            else road = MathF.Max(road, s);
        }
        Slip = maxSlip;
        var rolling = Math.Clamp(speed / 6, 0, 1);
        _squealGain = Smooth(_squealGain, MathF.Pow(road, 0.6f) * rolling * 0.7f, 30, 10, dt);
        _squeal.Gain = _squealGain;
        _squeal.Pitch = 0.92f + 0.2f * road;
        _squealHigh.Gain = _squealGain * road * 0.6f;
        _squealHigh.Pitch = 1 + 0.15f * road;
        _dirtGain = Smooth(_dirtGain, MathF.Sqrt(dirt) * rolling * 0.5f, 20, 8, dt);
        _dirt.Gain = _dirtGain;
        _dirt.Pitch = 0.8f + 0.3f * Math.Clamp(speed / 30, 0, 1);
        if (handbrake && !_prevHandbrake && speed > 8) _dev.PlaySfx(_skid, 0.5f);
        _prevHandbrake = handbrake;

        // Walls
        _crashCooldown -= dt;
        if (car.WallImpactSpeed > 1.5f && _crashCooldown <= 0)
        {
            _dev.PlaySfx((_crashToggle = !_crashToggle) ? _crashA : _crashB, Math.Clamp(car.WallImpactSpeed / 10, 0.15f, 1));
            _crashCooldown = 0.3f;
            Crashes++;
        }
        _scrapeGain = Smooth(_scrapeGain, car.WallContacts > 0 ? Math.Clamp(speed / 25, 0.1f, 1) * 0.5f : 0, 20, 8, dt);
        _scrape.Gain = _scrapeGain;

        // Road rumble and wind by speed
        var anyContact = car.Wheels[0].Contact || car.Wheels[1].Contact || car.Wheels[2].Contact || car.Wheels[3].Contact;
        _road.Gain = anyContact ? Math.Clamp(speed / 35, 0, 1) * 0.25f : 0;
        _road.Pitch = 0.7f + 0.6f * Math.Clamp(speed / 40, 0, 1);
        var v = Math.Clamp(speed / 45, 0, 1);
        _wind.Gain = v * v * 0.3f;
        _wind.Pitch = 0.8f + 0.4f * v;
    }

    /// <summary>
    ///     Engine part of <see cref="Update"/> (also driven directly by the <c>--sweep</c> capture). x = rpm / rev limit,
    ///     smoothed over ~20 ms, picks the two neighbouring zones of each bank (<see cref="ZoneWeights"/>); every layer
    ///     plays at x / its native x, so the pitch follows the rpm exactly in every gear and through shifts. U/D crossfade
    ///     t(2 − t) / 1 − t² over the smoothed pedal like the game (×0.8 per 60 Hz frame up, ×0.9 down).
    /// </summary>
    internal void UpdateEngine(CarSpec spec, float rpm, int gear, float throttle, float dt)
    {
        _load = Smooth(_load, throttle, 13.4f, 6.3f, dt);
        float loadGain = _load * (2 - _load), overrunGain = 1 - _load * _load;
        var target = MathF.Max(rpm, 0) / spec.RevLimit;
        _x = _x < 0 ? target : _x + (target - _x) * (1 - MathF.Exp(-dt / 0.02f));

        // rev limiter: ~14 Hz fuel cut while on the limiter; gear change: engine dips for the shift (ramped, no click)
        float cut = 1, pitchMul = 1;
        if (rpm >= spec.RevLimit - 150 && throttle > 0.5f)
        {
            _limiterPhase = (_limiterPhase + dt * 14) % 1;
            if (_limiterPhase < 0.4f) (cut, pitchMul) = (0.3f, 0.96f);
        }
        if (gear != _prevGear)
        {
            if (gear > _prevGear && _prevGear >= 1 && _prevRpm > 6000) Backfire(0.45f);
            _shiftDip = spec.ShiftTime + 0.05f;
            _prevGear = gear;
        }
        _prevRpm = rpm;
        _shiftDip = MathF.Max(_shiftDip - dt, 0);
        _dip = Smooth(_dip, _shiftDip > 0 ? 0.45f : 1, 40, 40, dt);

        _centre[0] = _centre[4] = MathF.Min(spec.IdleRpm / spec.RevLimit, 0.9f * MathF.Min(_centre[1], _centre[5]));
        EngineZone = ZoneWeights(_centre.AsSpan(0, 4), _x, _weight.AsSpan(0, 4));
        ZoneWeights(_centre.AsSpan(4, 4), _x, _weight.AsSpan(4, 4));
        float sumGain = 0, sumPitch = 0;
        for (var i = 0; i < 16; i++)
        {
            var zone = i / 8 * 4 + i % 4; // voices k and k + 4 of a bank = zone k
            var gain = _level[i] * _weight[zone] * (i < 8 ? loadGain : overrunGain) * cut * _dip;
            var pitch = LayerRate(_x, _native[zone]) * pitchMul;
            _engine[i].Gain = gain;
            _engine[i].Pitch = pitch;
            (sumGain, sumPitch) = (sumGain + gain, sumPitch + gain * pitch);
        }
        (EngineGain, EnginePitch) = (sumGain, sumGain > 0 ? sumPitch / sumGain : 1);

        _backfireCooldown -= dt;
        if (_prevThrottle > 0.8f && throttle < 0.2f && rpm > 6000) Backfire(0.35f); // lift-off pop
        _prevThrottle = throttle;
    }

    private static float Smooth(float cur, float target, float up, float down, float dt) =>
        cur + (target - cur) * (1 - MathF.Exp(-(target > cur ? up : down) * dt));

    private void Backfire(float gain)
    {
        if (_backfireCooldown > 0) return;
        _dev.PlaySfx(_backfire[_backfireNext++ % _backfire.Length], gain);
        _backfireCooldown = 0.8f;
    }

    public void Dispose()
    {
        foreach (var v in _engine) v.Dispose();
        foreach (var v in new[] { _squeal, _squealHigh, _dirt, _scrape, _road, _wind }) v.Dispose();
        _rain?.Dispose();
        foreach (var c in _clips.Concat(_engineClips)) c.Dispose();
    }
}
