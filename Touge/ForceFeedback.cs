using System.Numerics;
using Kansei.Input;
using Kansei.Physics;

namespace Touge;

/// <summary>
///     Force feedback from the physics, per frame: −1..1, + = pushes the wheel right.
///     <list type="bullet">
///         <item>Self-aligning torque of the front tyres: per wheel load × x·e^((1−x²)/2) with x = slip angle / peak slip angle –
///         grows with cornering force, peaks at the grip limit and goes light past it (understeer, front washing out), and
///         reverses with the slide (the wheel counter-steers by itself in a drift); plus a caster part (tanh x) that keeps
///         centring past the limit. With a light damper on the wheel's speed, low-passed ~30 ms.</item>
///         <item>Kerbs/gutters/grass: a sine rumble at the stripe frequency (speed / 1.2 m, at most 25 Hz – below the 30 Hz Nyquist limit of a 60 fps
///         update; a square wave's harmonics alias).</item>
///         <item>Wall impacts: a jolt away from the wall, from the closing speed, decaying in ~0.15 s.</item>
///         <item>Soft lock: past the game's steering lock (<see cref="DriverInput.SteerBeyond"/>) a stiff spring back.</item>
///     </list>
///     Sent as one constant force (<see cref="JoystickState.SetForce"/>, SDL haptics) and on pads as rumble.
/// </summary>
public sealed class ForceFeedback
{
    private float _phase, _jolt, _joltSign, _smooth, _lastSteer = float.NaN;

    /// <summary>Components of the last update (input debug).</summary>
    public float Aligning { get; private set; }
    public float Damper { get; private set; }
    public float Kerb { get; private set; }
    public float Jolt => _jolt * _joltSign;
    public float Output { get; private set; }

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
        float sat = 0, rough = 0;
        for (var i = 0; i < 4; i++)
        {
            var w = wheels[i];
            if (!w.Contact) continue;
            rough = MathF.Max(rough, roughness(w.Surface));
            if (i >= 2) continue;
            var x = w.SlipAngle / spec.PeakSlipAngle;
            // pneumatic trail (peaks at the grip limit, light past it) + caster trail on the lateral force (stays past it: the
            // wheel keeps centring and counter-steers by itself in a slide)
            sat += w.Load / nominal * (0.4f * x * MathF.Exp((1 - x * x) / 2) + 0.2f * MathF.Tanh(x));
        }
        Aligning = sat * MathF.Min(speed / 3, 1); // slip angles mean little at walking pace

        _phase = (_phase + dt * MathF.Min(speed / 1.2f, 25)) % 1;
        Kerb = rough * MathF.Min(speed / 10, 1) * 0.25f * MathF.Sin(_phase * MathF.Tau);

        _jolt *= MathF.Exp(-dt / 0.15f);
        if (car.WallImpactSpeed > 1 && car.WallImpactSpeed / 12 > _jolt)
        {
            var local = Vector3.Transform(car.WallNormal, Quaternion.Conjugate(car.Orientation)); // +X = left
            (_jolt, _joltSign) = (MathF.Min(car.WallImpactSpeed / 12, 1), local.X > 0 ? -1 : 1);
        }

        var over = MathF.Abs(steerBeyond) - 1;
        var soft = over > 0 ? -MathF.Sign(steerBeyond) * MathF.Min(over * 10, 1) : 0;
        // light damper on the wheel's speed (lock units/s): steadies the centring and the lock; first call has no rate
        var rate = float.IsNaN(_lastSteer) || dt <= 0 ? 0 : (steerBeyond - _lastSteer) / dt;
        _lastSteer = steerBeyond;
        Damper = Math.Clamp(-0.08f * rate, -0.3f, 0.3f);
        // ~30 ms low-pass on tyre forces + damper (the steering is read per frame, the physics ticks at 120 Hz); kerb, jolt and soft lock unfiltered
        _smooth += (Aligning + Damper - _smooth) * (1 - MathF.Exp(-dt / 0.03f));
        Output = strength <= 0 ? 0 : Math.Clamp(strength * (_smooth + Kerb + Jolt) + soft, -1, 1);
        return Output;
    }

    /// <summary>Lifts a force over the motor's dead band (belt/gear wheels): min + (1 − min)·|f|, faded in over the first 2 % (no square wave around the centre).</summary>
    public static float Lift(float f, float min) => MathF.CopySign(min * MathF.Min(MathF.Abs(f) / 0.02f, 1) + (1 - min) * MathF.Abs(f), f);

    /// <summary>Pad rumble (low, high motor) 0..1: impacts on the heavy motor, kerbs on the light one.</summary>
    public (float Low, float High) PadRumble(float strength) => (strength * _jolt, strength * MathF.Abs(Kerb) * 2.4f);
}
