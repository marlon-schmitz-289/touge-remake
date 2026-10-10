using System.Numerics;
using Kansei.Input;
using Kansei.Physics;

namespace Touge;

/// <summary>
///     Force feedback from the physics, per frame: −1..1, + = pushes the wheel right. Built like the sim racers do it (GT, AC,
///     rF2): the rack force from the tyres is the main signal, the rest gives the wheel the weight of a real car.
///     <list type="bullet">
///         <item>Self-aligning torque of the front tyres: per wheel load × x·e^((1−x²)/2) with x = slip angle / peak slip angle –
///         grows with cornering force and front load (heavier under braking), peaks at the grip limit and goes light past it
///         (understeer, front washing out), and reverses with the slide (the wheel counter-steers by itself in a drift); plus a
///         caster part (tanh x) that keeps centring past the limit, and a caster spring on the road-wheel angle that grows with
///         speed (the centre never goes dead). Faded in over the first 4 m/s, where slip angles mean little.</item>
///         <item>Weight: friction against the wheel's motion (most when parked: tyres scrubbing on the spot) and a damper that
///         grows with speed (gyro), so the wheel does not spin freely or oscillate.</item>
///         <item>Soft-knee curve: linear up to <see cref="Knee"/>, then compressed toward 1 – hard cornering stays below the
///         motor's limit, transients keep their headroom (no clipping that flattens kerbs and slides).</item>
///         <item>Road: bumps under one front wheel tug the rim (left/right load difference, high-passed ~50 ms).</item>
///         <item>Kerbs/gutters/grass: a sine rumble at the stripe frequency (speed / 1.2 m, at most 25 Hz – below the 30 Hz Nyquist
///         limit of a 60 fps update; a square wave's harmonics alias); tyres past their grip a lighter 20 Hz buzz.</item>
///         <item>Wall impacts: a jolt away from the wall, from the closing speed, decaying in ~0.15 s.</item>
///         <item>Soft lock: past the game's steering lock (<see cref="DriverInput.SteerBeyond"/>) a stiff spring back.</item>
///     </list>
///     Sent as one constant force (<see cref="JoystickState.SetForce"/>, SDL haptics), the rumble on Linux as its own sine
///     (<see cref="Vibration"/>), and on pads as rumble.
/// </summary>
public sealed class ForceFeedback
{
    /// <summary>Gain of the tyre forces: the AE86 at ~1 g lands near <see cref="Knee"/>.</summary>
    public const float TyreGain = 1.8f;
    /// <summary>Output above this is compressed toward 1.</summary>
    public const float Knee = 0.7f;

    private float _phase, _jolt, _joltSign, _smooth, _lastSteer = float.NaN, _loadDiff = float.NaN;

    /// <summary>Components of the last update (input debug).</summary>
    public float Aligning { get; private set; }
    public float Damper { get; private set; }
    public float Friction { get; private set; }
    public float Road { get; private set; }
    public float Kerb { get; private set; }
    public float Jolt => _jolt * _joltSign;
    public float Output { get; private set; }
    /// <summary><see cref="Output"/> without the rumble, for a wheel that plays it as its own vibration (<see cref="Vibration"/>).</summary>
    public float Steady { get; private set; }
    /// <summary>Kerb/slip rumble as amplitude 0..1 (strength applied) and frequency in Hz.</summary>
    public (float Amplitude, float Hz) Vibration { get; private set; }

    /// <summary>
    ///     Advances by <paramref name="dt"/>: the force for <paramref name="car"/> with ground roughness 0..1 per surface id,
    ///     the wheel's unclamped steering and <see cref="ControlSettings.FfbStrength"/>.
    /// </summary>
    public float Update(Vehicle car, Func<int, float> roughness, float steerBeyond, float strength, float dt)
    {
        var spec = car.Spec;
        var nominal = spec.Mass * 9.81f / 4;
        var speed = car.Velocity.Length();
        var wheels = car.Wheels;
        float sat = 0, rough = 0, slide = 0;
        for (var i = 0; i < 4; i++)
        {
            var w = wheels[i];
            if (!w.Contact) continue;
            rough = MathF.Max(rough, roughness(w.Surface));
            if (i >= 2) continue;
            var x = w.SlipAngle / spec.PeakSlipAngle;
            slide = MathF.Max(slide, MathF.Abs(x) - 1.2f); // front scrubbing past its grip
            // pneumatic trail (peaks at the grip limit, light past it) + caster trail on the lateral force (stays past it: the
            // wheel keeps centring and counter-steers by itself in a slide)
            sat += w.Load / nominal * (0.4f * x * MathF.Exp((1 - x * x) / 2) + 0.2f * MathF.Tanh(x));
        }
        var moving = MathF.Min(speed / 4, 1);
        // caster spring on the road-wheel angle (wheels[0].SteerAngle, + = right): pulls back toward the centre, more with speed
        var caster = -0.25f * wheels[0].SteerAngle / spec.MaxSteer * MathF.Min(speed / 20, 1);
        Aligning = TyreGain * sat * moving + caster;
        slide = MathF.Max(slide, (MathF.Abs(car.SlipAngle) - 0.15f) * 2); // rear stepping out
        slide = Math.Clamp(slide, 0, 1) * MathF.Min(speed / 10, 1);

        // road: a bump under one front wheel tugs the rim toward it; high-passed so a steady lean (cornering) adds nothing
        var front = wheels[0].Contact && wheels[1].Contact ? (wheels[1].Load - wheels[0].Load) / nominal : 0; // + = more on the right
        if (float.IsNaN(_loadDiff)) _loadDiff = front;
        _loadDiff += (front - _loadDiff) * (1 - MathF.Exp(-dt / 0.05f));
        Road = Math.Clamp(0.25f * (front - _loadDiff), -0.2f, 0.2f) * MathF.Min(speed / 5, 1);

        var hz = MathF.Min(speed / 1.2f, 25);
        _phase = (_phase + dt * hz) % 1;
        var kerb = rough * MathF.Min(speed / 10, 1) * 0.25f;
        Kerb = kerb * MathF.Sin(_phase * MathF.Tau);
        var buzz = 0.06f * slide;

        _jolt *= MathF.Exp(-dt / 0.15f);
        if (car.WallImpactSpeed > 1 && car.WallImpactSpeed / 12 > _jolt)
        {
            var local = Vector3.Transform(car.WallNormal, Quaternion.Conjugate(car.Orientation)); // +X = left
            (_jolt, _joltSign) = (MathF.Min(car.WallImpactSpeed / 12, 1), local.X > 0 ? -1 : 1);
        }

        var over = MathF.Abs(steerBeyond) - 1;
        var soft = over > 0 ? -MathF.Sign(steerBeyond) * MathF.Min(over * 10, 1) : 0;
        // the wheel's speed (lock units/s); first call has no rate
        var rate = float.IsNaN(_lastSteer) || dt <= 0 ? 0 : (steerBeyond - _lastSteer) / dt;
        _lastSteer = steerBeyond;
        // damper growing with speed (gyro: steadies the centring and a slide's snap-back), friction most when parked
        Damper = Math.Clamp(-(0.05f + 0.07f * MathF.Min(speed / 30, 1)) * rate, -0.3f, 0.3f);
        Friction = -(0.08f + 0.1f * (1 - moving)) * MathF.Tanh(rate / 0.15f);
        // ~15 ms low-pass on tyre forces + damper + friction (the steering is read per frame, the physics ticks at 120 Hz);
        // road, kerb, jolt and soft lock unfiltered
        _smooth += (Compress(Aligning) + Damper + Friction - _smooth) * (1 - MathF.Exp(-dt / 0.015f));

        // the buzz takes the kerb's place where it is stronger (never both at once: one sine, one wave on the rim)
        var (amp, freq) = buzz > kerb ? (buzz, 20f) : (kerb, hz);
        var wave = buzz > kerb ? buzz * MathF.Sin(_phase * MathF.Tau) : Kerb;
        var gain = MathF.Max(strength, 0);
        Steady = strength <= 0 ? 0 : Math.Clamp(gain * (_smooth + Road + Jolt) + soft, -1, 1);
        Output = strength <= 0 ? 0 : Math.Clamp(gain * (_smooth + Road + wave + Jolt) + soft, -1, 1);
        Vibration = (gain * amp, freq);
        return Output;
    }

    /// <summary>Soft knee: linear up to <see cref="Knee"/>, above it compressed toward 1 (tanh), so it never clips hard.</summary>
    public static float Compress(float f)
    {
        var a = MathF.Abs(f);
        return a <= Knee ? f : MathF.CopySign(Knee + (1 - Knee) * MathF.Tanh((a - Knee) / (1 - Knee)), f);
    }

    /// <summary>Lifts a force over the motor's dead band (belt/gear wheels): min + (1 − min)·|f|, faded in over the first 2 % (no square wave around the centre).</summary>
    public static float Lift(float f, float min) => MathF.CopySign(min * MathF.Min(MathF.Abs(f) / 0.02f, 1) + (1 - min) * MathF.Abs(f), f);

    /// <summary>Pad rumble (low, high motor) 0..1: impacts on the heavy motor, kerbs on the light one.</summary>
    public (float Low, float High) PadRumble(float strength) => (strength * _jolt, strength * MathF.Abs(Kerb) * 2.4f);
}
