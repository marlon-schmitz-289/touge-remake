using Kansei.Audio;
using Kansei.Physics;
using Touge.Formats;

namespace Touge;

/// <summary>
///     Sound of a drive, all from the ISO except wind/road noise (synthesised, the disc has none):
///     AE86 engine = CARSE <c>AE86_U</c> (on throttle) + <c>AE86_D</c> (overrun), 8 looped layers each, volume and pitch per
///     layer from the original SECT curves over an rpm index; tyres <c>SRIP_A</c> (road; <c>RAIN_SRIP</c> on _RIN courses) /
///     <c>RAIN_SRIP</c> (grass, stand-in); rain ambience SYSSE <c>rain</c> looped on _RIN courses;
///     walls <c>cr001/cr002</c> (one-shot by impact speed, cr002's middle looped as scrape); <c>zbackfire002a–h</c> on
///     high-rpm upshifts/lift-off; race BGM (RACEBGM.AFS) streamed with its loop points.
///     <see cref="Update"/> once per physics tick, allocation-free.
/// </summary>
public sealed class GameAudio : IDisposable
{
    // ponytail: guesses, not read from the game — the HD split's bend range (0x0100) has unknown units and the game's
    // rpm → index formula (sub_0018A170, soft-float double maths) is not decoded; retune here.
    private const float BendSemitones = 24;  // pitch-curve value 0 / 127 = −24 / +23.6 semitones around 64
    private const float EngineVolume = 0.6f; // the game's own ×0.6 on the SECT volume (sub_00178800)

    private readonly AudioDevice _dev;
    private readonly Sect _loadCurves, _overrunCurves;
    private readonly List<AudioDevice.Clip> _clips = [];
    private readonly AudioDevice.LoopVoice[] _engine = new AudioDevice.LoopVoice[16]; // 0–7 load (U), 8–15 overrun (D)
    private readonly AudioDevice.LoopVoice _squeal, _squealHigh, _dirt, _scrape, _road, _wind;
    private readonly AudioDevice.LoopVoice? _rain;
    private readonly AudioDevice.Clip _crashA, _crashB, _skid;
    private readonly AudioDevice.Clip[] _backfire;

    // state
    private float _load, _limiterPhase, _shiftDip, _crashCooldown, _backfireCooldown, _prevThrottle, _prevRpm, _squealGain, _dirtGain, _scrapeGain;
    private int _prevGear = 1, _backfireNext; // Vehicle.Reset puts the car in 1st
    private bool _prevHandbrake, _crashToggle;

    // music
    private readonly Afs _bgm;
    private readonly Afs.Entry[] _tracks;
    private int _track, _musicRequest;
    private bool _musicOn = true;

    /// <summary>For logs/analysis: gain-weighted playback rate of the engine layers, total engine gain, squeal gain, max normalised wheel slip.</summary>
    public float EnginePitch { get; private set; }
    public float EngineGain { get; private set; }
    public float SquealGain => _squealGain;
    public float ScrapeGain => _scrapeGain;
    public int Crashes { get; private set; }
    public float Slip { get; private set; }
    public string Track => Path.GetFileNameWithoutExtension(_tracks[_track].Name);

    public GameAudio(Iso9660 iso, string courseTime, AudioDevice dev)
    {
        _dev = dev;
        var carse = iso.OpenAfs("CDVD/DATA/SOUND/CARSE.AFS");
        _loadCurves = new Sect(carse.Read(carse.Find("AE86_U.DAT")!.Value));
        _overrunCurves = new Sect(carse.Read(carse.Find("AE86_D.DAT")!.Value));
        var load = Bank(carse, "AE86_U");
        var overrun = Bank(carse, "AE86_D");
        for (var i = 0; i < 8; i++)
        {
            _engine[i] = Loop(load[i]);
            _engine[8 + i] = Loop(overrun[i]);
        }
        var wet = courseTime.EndsWith("_RIN");
        var srip = Bank(carse, "SRIP_A");
        var rainSrip = Bank(carse, "RAIN_SRIP");
        (_squeal, _squealHigh, _skid) = wet ? (Loop(rainSrip[0]), Loop(rainSrip[1]), srip[3]) : (Loop(srip[0]), Loop(srip[1]), srip[3]);
        _dirt = Loop(rainSrip[0]);

        var sysse = iso.ReadFile("CDVD/DATA/SOUND/SYSSE.BIN");
        var bank = Vag.SysSe(sysse);
        var names = Afs.ParseTbl(iso.ReadFile("CDVD/DATA/SOUND/SYSSE.TBL"), bank.Length) ?? [];
        Vag.Sound Sys(string name)
        {
            var i = Array.FindIndex(names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) throw new FileNotFoundException($"SYSSE.BIN: {name}");
            return Vag.Decode(sysse.AsSpan(bank[i].Offset, bank[i].Size), bank[i].Rate);
        }
        _crashA = Clip(Sys("cr001.vag"));
        var cr2 = Sys("cr002.vag");
        _crashB = Clip(cr2);
        // cr002 stays loud from ~0.3 to ~1.9 s: its middle loops as wall scrape (no scrape sample on the disc)
        _scrape = Loop(Clip(cr2 with { Loop = (cr2.Rate * 6 / 10, cr2.Rate * 18 / 10) }));
        _backfire = [.. "abcdefgh".Select(c => Clip(Sys($"zbackfire002{c}.vag")))];
        if (wet)
        {
            _rain = Loop(Clip(Seamless(Sys("rain.vag"), 0.4f)));
            _rain.Gain = 0.5f;
        }

        _road = Loop(Clip(new Vag.Sound(Noise(22050 * 2, 0.06f, 1), 22050, null)));
        _wind = Loop(Clip(new Vag.Sound(Noise(22050 * 2, 0.35f, 2), 22050, null)));

        _bgm = iso.OpenAfs("CDVD/DATA/SOUND/RACEBGM.AFS");
        _tracks = [.. _bgm.Entries.Where(e => e.Name.EndsWith(".ADX", StringComparison.OrdinalIgnoreCase))];
        var course = courseTime[..courseTime.LastIndexOf('_')];
        _track = course.Sum(c => c) % _tracks.Length; // fixed pick per course, M cycles
        (dev.Music, dev.Sfx) = (0.6f, 0.35f);
    }

    private AudioDevice.Clip Clip(Vag.Sound s)
    {
        var c = _dev.CreateClip(s.Pcm, 1, s.Rate, s.Loop);
        _clips.Add(c);
        return c;
    }

    private AudioDevice.LoopVoice Loop(AudioDevice.Clip clip)
    {
        var v = _dev.CreateLoop(clip);
        v.Gain = 0;
        v.Play();
        return v;
    }

    private AudioDevice.Clip[] Bank(Afs carse, string name)
    {
        var (hd, bd) = Vag.Mrg(carse.Read(carse.Find(name + ".MRG")!.Value));
        // program i plays Vagi i (the HD's Smpl chunk maps them 1:1)
        return [.. Vag.HdSamples(hd, bd.Length).Select(s => Clip(Vag.Decode(bd.AsSpan(s.Offset, s.Size), s.Rate)))];
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
        var spec = car.Spec;
        var speed = car.Velocity.Length();

        // Engine: U/D crossfade by a smoothed load like the game (×0.8 per 60 Hz frame up while the pedal is > 0.2, ×0.9 down)
        _load += ((throttle > 0.2f ? 1 : 0) - _load) * (1 - MathF.Exp((throttle > 0.2f ? -13.4f : -6.3f) * dt));
        float loadGain = _load * (2 - _load), overrunGain = 1 - _load * _load;
        var x = (int)((car.Rpm - spec.IdleRpm) / (spec.RevLimit - spec.IdleRpm) * 255);

        // rev limiter: ~14 Hz fuel cut while on the limiter; gear change: engine dips for the shift
        float cut = 1, pitchMul = 1;
        if (car.Rpm >= spec.RevLimit - 150 && throttle > 0.5f)
        {
            _limiterPhase = (_limiterPhase + dt * 14) % 1;
            if (_limiterPhase < 0.4f) (cut, pitchMul) = (0.3f, 0.96f);
        }
        if (car.Gear != _prevGear)
        {
            if (car.Gear > _prevGear && _prevGear >= 1 && _prevRpm > 6000) Backfire(0.45f);
            _shiftDip = spec.ShiftTime + 0.05f;
            _prevGear = car.Gear;
        }
        _prevRpm = car.Rpm;
        _shiftDip = MathF.Max(_shiftDip - dt, 0);
        var dip = _shiftDip > 0 ? 0.45f : 1;

        float sumGain = 0, sumPitch = 0;
        for (var i = 0; i < 16; i++)
        {
            var curves = i < 8 ? _loadCurves : _overrunCurves;
            var layer = i % 8;
            var gain = curves.Value(layer, Sect.Volume, x) / 127f * EngineVolume * (i < 8 ? loadGain : overrunGain) * cut * dip;
            var pitch = MathF.Pow(2, (curves.Value(layer, Sect.Pitch, x) - 64) / 64f * BendSemitones / 12) * pitchMul;
            _engine[i].Gain = gain;
            _engine[i].Pitch = pitch;
            (sumGain, sumPitch) = (sumGain + gain, sumPitch + gain * pitch);
        }
        (EngineGain, EnginePitch) = (sumGain, sumGain > 0 ? sumPitch / sumGain : 1);

        _backfireCooldown -= dt;
        if (_prevThrottle > 0.8f && throttle < 0.2f && car.Rpm > 6000) Backfire(0.35f); // lift-off pop
        _prevThrottle = throttle;

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

    private static float Smooth(float cur, float target, float up, float down, float dt) =>
        cur + (target - cur) * (1 - MathF.Exp(-(target > cur ? up : down) * dt));

    private void Backfire(float gain)
    {
        if (_backfireCooldown > 0) return;
        _dev.PlaySfx(_backfire[_backfireNext++ % _backfire.Length], gain);
        _backfireCooldown = 0.8f;
    }

    public bool MusicOn
    {
        get => _musicOn;
        set
        {
            _musicOn = value;
            if (value) PlayTrack(background: true);
            else { _musicRequest++; _dev.StopMusic(); }
        }
    }

    public void NextTrack()
    {
        _track = (_track + 1) % _tracks.Length;
        _musicOn = true;
        PlayTrack(background: true);
    }

    /// <summary>Starts the current track (ADX read + decoder set-up off the main thread when <paramref name="background"/>).</summary>
    public void PlayTrack(bool background)
    {
        var request = ++_musicRequest;
        var entry = _tracks[_track];
        void Start()
        {
            var adx = new Adx(_bgm.Read(entry));
            if (request != _musicRequest) return; // superseded (next track / music off) while loading
            _dev.PlayMusic(adx.Open(loop: true).Read, adx.Channels, adx.SampleRate);
            Console.WriteLine($"\n[Audio] Musik: {Track}");
        }
        if (background) Task.Run(Start);
        else Start();
    }

    public void Dispose()
    {
        _musicRequest++;
        _dev.StopMusic();
        foreach (var v in _engine) v.Dispose();
        foreach (var v in new[] { _squeal, _squealHigh, _dirt, _scrape, _road, _wind }) v.Dispose();
        _rain?.Dispose();
        foreach (var c in _clips) c.Dispose();
    }
}
