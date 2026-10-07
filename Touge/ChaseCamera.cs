using System.Numerics;

namespace Touge;

/// <summary>
///     The chase views' camera, fed the interpolated pose once per drawn frame: X/Z sit on the car at once, only the view's
///     heading (<see cref="Lag"/>) and height (<see cref="HeightLag"/>) are eased, frame-rate independent (1 − e^(−dt/τ)). The aim
///     turns <see cref="LookIntoTurn"/> of the way from the car's axis to its direction of travel (into the drift), slopes count
///     half, up is the world's (no roll). Default = not set: the next <see cref="Update"/> snaps.
/// </summary>
public struct ChaseCamera
{
    /// <summary>Metres behind and over the car, the aim's height over it and distance ahead of it.</summary>
    public const float Distance = 5.4f, Height = 1.7f, LookHeight = 0.8f, LookAhead = 4;

    /// <summary>Time constants (s) of the view's heading and height; share of the turn into the direction of travel, from 3 m/s.</summary>
    public const float Lag = 0.22f, HeightLag = 0.12f, LookIntoTurn = 0.35f, TurnSpeed = 3;

    /// <summary>Field of view added (rad) at <see cref="FovFullSpeed"/> (m/s), linear up to it.</summary>
    public const float FovSpeedGain = 10 * MathF.PI / 180, FovFullSpeed = 50;

    private Vector3 _dir, _look, _pos;
    private float _fovGain;
    private bool _init;

    /// <summary>Where the car was last frame (a jump of it: <see cref="Reset"/>).</summary>
    public readonly Vector3 Car => _pos;

    /// <summary>Spawn, respawn, race start, any teleport: the next frame snaps instead of swinging across the map.</summary>
    public void Reset() => _init = false;

    /// <summary>Once per drawn frame with the interpolated <paramref name="carPos"/> and forward axis <paramref name="fwd"/> (unit), world <paramref name="velocity"/> and frame <paramref name="dt"/>.</summary>
    public void Update(Vector3 carPos, Vector3 fwd, Vector3 velocity, float dt)
    {
        var speed = velocity.Length();
        var look = speed > TurnSpeed ? Vector3.Normalize(Vector3.Lerp(fwd, velocity / speed, LookIntoTurn)) : fwd;
        if (!_init) (_dir, _look, _pos, _init) = (fwd, look, carPos, true);

        var k = 1 - MathF.Exp(-dt / Lag);
        _dir = Vector3.Normalize(Vector3.Lerp(_dir, fwd, k));
        _look = Vector3.Normalize(Vector3.Lerp(_look, look, k));
        // X/Z follow at once, only Y is eased
        _pos = carPos with { Y = _pos.Y + (carPos.Y - _pos.Y) * (1 - MathF.Exp(-dt / HeightLag)) };
        _fovGain = FovSpeedGain * Math.Clamp(speed / FovFullSpeed, 0, 1);
    }

    /// <summary>Eye and aim (up = world Y) and the field of view: <paramref name="fov"/> (rad) widened with speed. <paramref name="scale"/> moves the eye further back and up (far chase).</summary>
    public readonly (Vector3 Position, Vector3 Target, float Fov) Pose(float fov, float scale = 1)
    {
        var flat = Vector3.Normalize(_dir with { Y = _dir.Y * 0.5f }); // half the slope
        var position = _pos - flat * (Distance * scale) + Vector3.UnitY * (Height * scale);
        var target = _pos + Vector3.UnitY * LookHeight + _look * LookAhead;
        return (position, target, fov + _fovGain);
    }
}
