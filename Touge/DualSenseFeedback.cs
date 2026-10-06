using System.Numerics;
using Kansei.Audio;
using Kansei.Input;
using Kansei.Physics;
using Touge.Formats;
using static Kansei.Input.DualSense;

namespace Touge;

/// <summary>DualSense features (Options → DUALSENSE): each one can be switched off, the strengths are 0..1.</summary>
public sealed class DualSenseSettings
{
    /// <summary>Lightbar: off, the car's paint, an rpm meter (green → yellow → red, flashing at the shift point), the player colour.</summary>
    public enum Light { Off, CarColour, Rpm, Player }
    /// <summary>Mic LED: off, lit while the music is off, lit while the headlights are on.</summary>
    public enum MicLight { Off, MusicOff, Headlights }

    public Light Lightbar { get; set; } = Light.Rpm;
    public float Brightness { get; set; } = 0.8f;
    public bool Triggers { get; set; } = true;
    public float TriggerStrength { get; set; } = 0.7f;
    public bool Rumble { get; set; } = true;
    public float RumbleStrength { get; set; } = 0.7f;
    public bool Speaker { get; set; } = true;
    public float SpeakerVolume { get; set; } = 0.6f;
    public bool PlayerLeds { get; set; } = true;
    public MicLight Mic { get; set; } = MicLight.MusicOff;
    /// <summary>Swipe left/right: next song; click: course map mode.</summary>
    public bool Touchpad { get; set; } = true;
    /// <summary>Tilt steering (added to the stick), off by default.</summary>
    public bool Gyro { get; set; }
    public float GyroSensitivity { get; set; } = 0.5f;

    public void Sanitize()
    {
        static float Unit(float v, float d) => float.IsFinite(v) ? Math.Clamp(v, 0, 1) : d;
        (Brightness, TriggerStrength, RumbleStrength, SpeakerVolume, GyroSensitivity) =
            (Unit(Brightness, 0.8f), Unit(TriggerStrength, 0.7f), Unit(RumbleStrength, 0.7f), Unit(SpeakerVolume, 0.6f), Unit(GyroSensitivity, 0.5f));
        if (!Enum.IsDefined(Lightbar)) Lightbar = Light.Rpm;
        if (!Enum.IsDefined(Mic)) Mic = MicLight.MusicOff;
    }
}

/// <summary>
///     Output to every connected DualSense, once per frame (<see cref="Update"/>), from the car its player drives (split screen:
///     each pad its own car and player number): lightbar (<see cref="Lightbar"/>, red flash on contacts), adaptive triggers
///     (<see cref="TriggerEffects"/>: R2 light resistance, vibrating with wheel spin and kicking on gear changes; L2 stiffening with brake
///     pressure, ABS-like pulsing when a wheel locks), rumble (engine, kerbs/grass, contacts, landings, drift slip, gear changes),
///     player LEDs, mic LED, speaker one-shots (<see cref="Speak"/>), touchpad gestures and tilt steering (<see cref="Tilt"/>).
///     In menus and paused: triggers free, no rumble, a calm lightbar. Only changes are sent (effects at most 30 per second);
///     <see cref="Reset"/> leaves the pads dark and free when the game ends. Other pads keep the plain rumble (TougeGame.SendForces).
/// </summary>
public sealed class DualSenseFeedback(DualSenseSettings s, PadSpeaker? speaker)
{
    /// <summary>What a pad's player is doing: the car (null = menus/pause), brake pedal 0..1, paint 0xBBGGRR, player 0..3.</summary>
    public readonly record struct Seat(Vehicle? Car, float Brake, uint Paint, int Player);

    /// <summary>What a pad shows this frame.</summary>
    public record struct Output(Vector3 Led, Trigger Right, Trigger Left, float Low, float High, Mic Mic, int Player);

    private sealed class Pad
    {
        public readonly ForceFeedback Road = new();
        public Vector3 Velocity;
        public int Gear = int.MinValue;
        public float Hit, PrevHit, Bump, Kick;
        public byte[]? Effect;
        public (byte, byte, byte)? Led;
        public int PlayerLed = int.MinValue;
        public (ushort, ushort) Rumble;
        public float RumbleAge, EffectAge = 1, LedAge = 1;
        public float? Speaker;
        public bool Motion;
        public float? SwipeFrom;
    }

    private readonly Dictionary<GamepadState, Pad> _pads = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, Vag.Sound> _sounds = new(StringComparer.OrdinalIgnoreCase);
    private float _time, _test;
    private string? _testing;

    /// <summary>Context for the mic LED.</summary>
    public bool MusicOff { get; set; }
    public bool LightsOn { get; set; }
    /// <summary>Touchpad gestures of this frame (any DualSense, <see cref="DualSenseSettings.Touchpad"/>).</summary>
    public bool Swiped { get; private set; }
    public bool Clicked { get; private set; }
    /// <summary>A DualSense is connected (the options page shows then).</summary>
    public bool Connected { get; private set; }
    public bool SpeakerAvailable => speaker?.Available == true;
    /// <summary>Calm menu colour: the menus' lit green, dimmed.</summary>
    public static readonly Vector3 MenuColour = new(0.1f, 0.55f, 0.25f);
    /// <summary>Speaker sounds (SYSSE names): menu cursor/decide/back, countdown, wall hit.</summary>
    public static readonly string[] SpeakerSounds = ["SYS005", "SYS006", "BEEP001", "CAR010", "CAR011", "cr001"];

    /// <summary>The speaker's sounds from the disc (<see cref="SpeakerSounds"/>) plus a synthesised gear-shift click.</summary>
    public void LoadSounds(Iso9660 iso)
    {
        var sys = GameAudio.SysSe(iso);
        foreach (var n in SpeakerSounds) _sounds[n] = n == "cr001" ? Trim(sys(n + ".vag"), 0.35f) : sys(n + ".vag");
        _sounds["shift"] = Click();
    }

    private static Vag.Sound Trim(Vag.Sound v, float seconds) => v with { Pcm = v.Pcm[..Math.Min(v.Pcm.Length, (int)(v.Rate * seconds))] };

    /// <summary>A 25 ms decaying noise burst (the disc has no gear sound).</summary>
    private static Vag.Sound Click()
    {
        var rng = new Random(7);
        var pcm = new short[1200];
        for (var i = 0; i < pcm.Length; i++) pcm[i] = (short)((rng.NextSingle() * 2 - 1) * 20000 * MathF.Exp(-i / 200f));
        return new Vag.Sound(pcm, 48000, null);
    }

    /// <summary>Plays <paramref name="name"/> on the pad speaker (if it is one of its sounds, the speaker is on and present).</summary>
    public void Speak(string name)
    {
        if (!s.Speaker || speaker is not { Available: true } || !_sounds.TryGetValue(name, out var v)) return;
        speaker.Play(v.Pcm, v.Rate, s.SpeakerVolume);
        if (GamepadState.Trace) Console.WriteLine($"[DualSense] Lautsprecher: {name} ({v.Pcm.Length / (float)v.Rate:0.00} s) auf {speaker.Device}");
    }

    /// <summary>Options TEST: shows LIGHTBAR, TRIGGERS, RUMBLE or plays SPEAKER for two seconds on every DualSense.</summary>
    public void Test(string feature)
    {
        (_testing, _test) = (feature, 2);
        if (feature == "SPEAKER") Speak("CAR011");
    }

    /// <summary>
    ///     Per frame: every pad's output from <paramref name="seatOf"/> (pad index → seat), road roughness per surface for kerb rumble;
    ///     <paramref name="script"/> (--dualsense-test) replaces the computed output.
    /// </summary>
    public void Update(IReadOnlyList<GamepadState> pads, Func<int, Seat> seatOf, Func<int, float> roughness, float dt, Func<int, Output?>? script = null)
    {
        _time += dt;
        _test = MathF.Max(0, _test - dt);
        speaker?.Scan(_time);
        (Swiped, Clicked, Connected) = (false, false, false);
        foreach (var gone in _pads.Keys.Where(p => !pads.Contains(p)).ToList()) _pads.Remove(gone); // unplugged: fresh state when it returns
        for (var i = 0; i < pads.Count; i++)
        {
            var pad = pads[i];
            if (!pad.IsDualSense) continue;
            Connected = true;
            if (!_pads.TryGetValue(pad, out var st)) _pads[pad] = st = new Pad();
            var seat = seatOf(i);
            Gestures(pad, st);
            var o = script?.Invoke(i) ?? Compute(st, seat, roughness, dt);
            if (_test > 0 && script == null) o = TestOutput(o, _testing, _time);
            Apply(pad, st, o, dt);
        }
    }

    private void Gestures(GamepadState pad, Pad st)
    {
        if (s.Gyro != st.Motion) pad.SetMotion(st.Motion = s.Gyro); // tried once (a pad without sensors just reads 0)
        if (!s.Touchpad) return;
        if (pad.IsButtonPressed(GamepadButton.Touchpad)) Clicked = true;
        if (pad.Touch is { } t) st.SwipeFrom ??= t.X;
        else st.SwipeFrom = null;
        if (pad.Touch is { } now && st.SwipeFrom is { } from && MathF.Abs(now.X - from) > 0.35f)
        {
            Swiped = true;
            st.SwipeFrom = float.NaN; // one swipe per touch (NaN never compares > 0.35 again)
        }
    }

    private Output Compute(Pad st, Seat seat, Func<int, float> roughness, float dt)
    {
        var mic = s.Mic switch
        {
            DualSenseSettings.MicLight.MusicOff when MusicOff => Mic.On,
            DualSenseSettings.MicLight.Headlights when LightsOn => Mic.On,
            _ => Mic.Off,
        };
        var player = s.PlayerLeds ? seat.Player : -1;
        if (seat.Car is not { } car)
        {
            (st.Velocity, st.Gear, st.Hit, st.Bump, st.Kick) = (Vector3.Zero, int.MinValue, 0, 0, 0);
            return new Output(s.Lightbar == DualSenseSettings.Light.Off ? Vector3.Zero : MenuColour * s.Brightness, Trigger.Off, Trigger.Off, 0, 0, mic, player);
        }

        // contacts (walls, cars), landings, gear changes: from the body's velocity jump and the gear
        var dv = car.Velocity - st.Velocity;
        var first = st.Gear == int.MinValue;
        st.Velocity = car.Velocity;
        var decay = MathF.Exp(-dt / 0.25f);
        (st.Hit, st.Bump, st.Kick) = (st.Hit * decay, st.Bump * decay, MathF.Max(0, st.Kick - dt / 0.12f));
        if (!first && dt > 0)
        {
            var side = new Vector2(dv.X, dv.Z).Length() / dt; // m/s²: braking/cornering stays below ~15
            if (side > 30 || car.WallImpactSpeed > 2) st.Hit = MathF.Max(st.Hit, Math.Clamp(MathF.Max((side - 30) / 60, car.WallImpactSpeed / 10), 0.3f, 1));
            if (dv.Y / dt > 25) st.Bump = MathF.Max(st.Bump, Math.Clamp((dv.Y / dt - 25) / 50, 0, 1)); // landing
        }
        if (!first && car.Gear != st.Gear && car.Gear > 0 && st.Gear > 0)
        {
            st.Kick = 1;
            Speak("shift");
        }
        if (!first && st.Hit > 0.6f && st.Hit > st.PrevHit) Speak("cr001");
        st.PrevHit = st.Hit;
        st.Gear = car.Gear;

        var x = car.Rpm / car.Spec.RevLimit;
        var led = s.Lightbar == DualSenseSettings.Light.Off ? Vector3.Zero : Lightbar(s.Lightbar, x, seat.Paint, seat.Player, _time, st.Hit) * s.Brightness;

        float spin = 0, lockup = 0;
        var w = car.Wheels;
        var speed = car.Velocity.Length();
        for (var i = 0; i < 4; i++)
        {
            if (!w[i].Contact) continue;
            var driven = i < 2 ? car.Spec.DriveFront > 0 : car.Spec.DriveFront < 1;
            if (driven) spin = MathF.Max(spin, w[i].SlipRatio);
            if (speed > 3) lockup = MathF.Max(lockup, -w[i].SlipRatio);
        }
        var (r, l) = s.Triggers ? TriggerEffects(s.TriggerStrength, seat.Brake, (spin - 0.15f) / 0.5f, seat.Brake > 0.1f && lockup > 0.3f, st.Kick) : (Trigger.Off, Trigger.Off);

        float low = 0, high = 0;
        if (s.Rumble)
        {
            st.Road.Update(car, roughness, 0, 1, dt);
            var (jolt, kerb) = st.Road.PadRumble(1);
            var drift = speed > 5 ? Math.Clamp((MathF.Abs(car.SlipAngle) - 0.12f) / 0.5f, 0, 1) : 0;
            var engine = car.Rpm > 100 ? 0.04f + 0.1f * x * x + (x > 0.97f ? 0.25f : 0) : 0;
            low = s.RumbleStrength * Math.Clamp(MathF.Max(jolt, st.Hit) + 0.6f * st.Bump + 0.35f * drift + 0.5f * st.Kick, 0, 1);
            high = s.RumbleStrength * Math.Clamp(kerb + engine + 0.3f * st.Bump, 0, 1);
        }
        return new Output(led, r, l, low, high, mic, player);
    }

    /// <summary>
    ///     Lightbar colour 0..1 before brightness: <paramref name="rpm"/> = rpm / rev limit (green, yellow from 60 %, red from 85 %,
    ///     flashing at 8 Hz from 95 %), <paramref name="paint"/> 0xBBGGRR, player colours blue/red/green/pink; <paramref name="hit"/>
    ///     (0..1, a contact) blends to red.
    /// </summary>
    public static Vector3 Lightbar(DualSenseSettings.Light mode, float rpm, uint paint, int player, float time, float hit)
    {
        Vector3 green = new(0, 1, 0), yellow = new(1, 0.85f, 0), red = new(1, 0, 0);
        var c = mode switch
        {
            DualSenseSettings.Light.CarColour => new Vector3(paint & 0xFF, paint >> 8 & 0xFF, paint >> 16 & 0xFF) / 255,
            DualSenseSettings.Light.Player => PlayerColour(player),
            DualSenseSettings.Light.Rpm => rpm >= 0.95f ? (time * 8 % 1 < 0.5f ? red : Vector3.Zero)
                : rpm >= 0.85f ? Vector3.Lerp(yellow, red, (rpm - 0.85f) / 0.1f)
                : rpm >= 0.6f ? Vector3.Lerp(green, yellow, (rpm - 0.6f) / 0.25f) : green,
            _ => Vector3.Zero,
        };
        return mode == DualSenseSettings.Light.Off ? c : Vector3.Lerp(c, red, Math.Clamp(hit, 0, 1));
    }

    public static Vector3 PlayerColour(int player) => ((player % 4 + 4) % 4) switch
    {
        0 => new(0, 0.35f, 1), 1 => new(1, 0.1f, 0.1f), 2 => new(0.1f, 1, 0.2f), _ => new(1, 0.3f, 0.8f),
    };

    /// <summary>
    ///     Adaptive triggers at <paramref name="strength"/>: R2 vibrates with wheel spin (<paramref name="spin"/> 0..1) or kicks on a gear
    ///     change (<paramref name="kick"/> 0..1), else a light resistance; L2 pulses at 20 Hz while a wheel locks (ABS feel), else its
    ///     resistance grows with the brake pedal.
    /// </summary>
    public static (Trigger Right, Trigger Left) TriggerEffects(float strength, float brake, float spin, bool locked, float kick)
    {
        if (strength <= 0) return (Trigger.Off, Trigger.Off);
        spin = Math.Clamp(spin, 0, 1);
        var right = kick > 0 ? Trigger.Vibration(60, strength * kick)
            : spin > 0 ? Trigger.Vibration(30, Q(strength * (0.3f + 0.7f * spin)), 0.2f)
            : Trigger.Resistance(0.2f, strength * 0.2f);
        var left = locked ? Trigger.Vibration(20, strength, 0.1f) : Trigger.Resistance(0.1f, Q(strength * (0.25f + 0.6f * Math.Clamp(brake, 0, 1))));
        return (right, left);
    }

    /// <summary>Quantised to 1/16: fewer packets for a slowly changing value.</summary>
    private static float Q(float v) => MathF.Round(v * 16) / 16;

    private static Output TestOutput(Output o, string? feature, float t) => feature switch
    {
        "LIGHTBAR" => o with { Led = Hue(t) },
        "TRIGGERS" => o with { Right = Trigger.Vibration(30, 0.8f, 0.2f), Left = Trigger.Resistance(0.1f, 0.9f) },
        "RUMBLE" => o with { Low = t % 1 < 0.5f ? 0.8f : 0, High = t % 1 < 0.5f ? 0 : 0.8f },
        _ => o,
    };

    /// <summary>Length of the --dualsense-test cycle in seconds.</summary>
    public const float ScriptSeconds = 20;

    /// <summary>
    ///     --dualsense-test at <paramref name="t"/> s: 0–4 lightbar (rainbow, car colour, rpm sweep into the shift flash, the player
    ///     colours with their player LEDs), 4–9 triggers (resistance, spin vibration, ABS pulse, gear kicks, free), 9–13 rumble (heavy
    ///     motor, light motor, engine sweep, impacts), 13–17 mic LED on/pulsing (speaker: countdown, TougeGame), 17–20 tilt shown green/red.
    /// </summary>
    public static Output Script(float t, Vector3 accel)
    {
        var o = new Output(new Vector3(0.1f, 0.1f, 0.1f), Trigger.Off, Trigger.Off, 0, 0, Mic.Off, 0);
        var f = t % 1;
        switch (t)
        {
            case < 1: return o with { Led = Hue(f) };
            case < 2: return o with { Led = Lightbar(DualSenseSettings.Light.CarColour, 0, 0x2020E0, 0, t, 0) };
            case < 3: return o with { Led = Lightbar(DualSenseSettings.Light.Rpm, 0.5f + 0.55f * f, 0, 0, t, 0) };
            case < 4: return o with { Led = PlayerColour((int)(f * 4)), Player = (int)(f * 4) };
            case < 5: return o with { Right = Trigger.Resistance(0.1f, 1), Left = Trigger.Resistance(0.1f, f) };
            case < 6: return o with { Right = TriggerEffects(1, 0, f, false, 0).Right };
            case < 7: return o with { Left = TriggerEffects(1, 1, 0, true, 0).Left };
            case < 8: return o with { Right = f % 0.33f < 0.12f ? Trigger.Vibration(60, 1) : Trigger.Resistance(0.2f, 0.2f) };
            case < 9: return o;
            case < 10: return o with { Low = 0.8f };
            case < 11: return o with { High = 0.8f };
            case < 12: return o with { Low = 0.1f, High = 0.04f + 0.1f * (0.3f + 0.7f * f) * (0.3f + 0.7f * f) + (f > 0.9f ? 0.25f : 0) };
            case < 13: return o with { Low = f % 0.5f < 0.15f ? 1 : 0, Led = f % 0.5f < 0.15f ? new Vector3(1, 0, 0) : o.Led };
            case < 15: return o with { Mic = Mic.On };
            case < 17: return o with { Mic = Mic.Pulse };
            default:
                var tilt = Tilt(accel, 0.5f);
                return o with { Led = tilt > 0 ? new Vector3(0, tilt, 0) : new Vector3(-tilt, 0, 0.05f) };
        }
    }

    /// <summary>Rainbow over one second.</summary>
    public static Vector3 Hue(float t)
    {
        var h = t % 1 * 6;
        return new Vector3(Math.Clamp(MathF.Abs(h - 3) - 1, 0, 1), Math.Clamp(2 - MathF.Abs(h - 2), 0, 1), Math.Clamp(2 - MathF.Abs(h - 4), 0, 1));
    }

    private void Apply(GamepadState pad, Pad st, Output o, float dt)
    {
        var led = (DualSense.Byte(o.Led.X), DualSense.Byte(o.Led.Y), DualSense.Byte(o.Led.Z));
        // each change is sent once: a failure (pad without the feature, rc −1, logged) is not retried every frame
        st.LedAge += dt;
        if (st.Led != led && st.LedAge >= 1 / 30f) // ≤ 30 per second: the rpm meter changes every frame
        {
            pad.SetLed(led.Item1, led.Item2, led.Item3);
            (st.Led, st.LedAge) = (led, 0);
        }
        if (st.PlayerLed != o.Player) pad.SetPlayerIndex(st.PlayerLed = o.Player);

        // rumble lasts 120 ms per call: renewed every 60 ms while it runs, a change at once, silence once
        (ushort, ushort) rumble = ((ushort)(Q(o.Low) * 65535), (ushort)(Q(o.High) * 65535));
        st.RumbleAge += dt;
        if (rumble != st.Rumble || rumble != (0, 0) && st.RumbleAge > 0.06f)
        {
            pad.Rumble(rumble.Item1 / 65535f, rumble.Item2 / 65535f, 120);
            (st.Rumble, st.RumbleAge) = (rumble, 0);
        }

        var vol = s.Speaker && SpeakerAvailable ? s.SpeakerVolume : 0;
        var effect = DualSense.Effect(o.Right, o.Left, o.Mic, st.Speaker == vol ? null : vol);
        st.EffectAge += dt;
        if (st.EffectAge < 1 / 30f || st.Effect != null && effect.AsSpan(8, 24).SequenceEqual(st.Effect.AsSpan(8, 24)) && st.Speaker == vol) return; // mic LED + triggers
        pad.SendEffect(effect);
        (st.Effect, st.EffectAge, st.Speaker) = (effect, 0, vol);
    }

    /// <summary>Game over: every DualSense dark and free (lightbar, player LEDs, triggers, mic LED, speaker, rumble, motion).</summary>
    public void Reset(IReadOnlyList<GamepadState> pads)
    {
        foreach (var pad in pads.Where(p => p.IsDualSense))
        {
            pad.SetLed(0, 0, 0);
            pad.SetPlayerIndex(-1);
            pad.SendEffect(DualSense.Reset());
            pad.Rumble(0, 0, 0);
            pad.SetMotion(false);
        }
        _pads.Clear();
    }

    /// <summary>
    ///     Tilt steering −1..1 from the accelerometer: the pad rolled about the axis pointing at the player (pitch ignored), full lock
    ///     at 60° (sensitivity 0) … 20° (1), 2° dead zone. 0 without sensor data.
    /// </summary>
    public static float Tilt(Vector3 accel, float sensitivity)
    {
        if (accel.LengthSquared() < 1) return 0;
        var roll = MathF.Atan2(-accel.X, MathF.Sqrt(accel.Y * accel.Y + accel.Z * accel.Z)) * 180 / MathF.PI; // + = right side down
        const float dz = 2;
        var lockDeg = 60 - 40 * Math.Clamp(sensitivity, 0, 1);
        var a = MathF.Abs(roll);
        return a <= dz ? 0 : MathF.CopySign(Math.Clamp((a - dz) / (lockDeg - dz), 0, 1), roll);
    }
}
