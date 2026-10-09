namespace Touge.Ui;

/// <summary>
///     Drift combo: while the body slips more than <see cref="MinAngle"/>° above 30 km/h, points build with angle × speed,
///     the multiplier grows every 2 s of unbroken drifting (×1…×5). After <see cref="Hold"/> s without drifting the combo is
///     banked into <see cref="Total"/>; a wall hit drops it (a scrape does not, Vehicle.WallHit). Tuned by eye.
/// </summary>
public sealed class DriftMeter
{
    public const float MinAngle = 10, Hold = 0.8f;

    /// <summary>Body slip angle in degrees (signed: + = nose right of travel).</summary>
    public float Angle { get; private set; }
    public float Score { get; private set; }
    public float Total { get; private set; }
    public int Multiplier => 1 + Math.Min(4, (int)(_chain / 2));
    /// <summary>Last combo result: banked points (0 = dropped by a wall hit) and seconds since.</summary>
    public (float Points, float Age) Last { get; private set; } = (0, float.MaxValue);
    public bool Drifting { get; private set; }
    private float _chain, _idle;

    public void Update(float slipRad, float kmh, bool wall, float dt)
    {
        Angle = slipRad * 180 / MathF.PI;
        Last = Last with { Age = Last.Age + dt };
        Drifting = MathF.Abs(Angle) > MinAngle && kmh > 30;
        if (Drifting)
        {
            (_idle, _chain) = (0, _chain + dt);
            Score += (MathF.Abs(Angle) - MinAngle + 5) * kmh * 0.02f * Multiplier * dt * 10;
        }
        else if (Score > 0 && (_idle += dt) > Hold)
        {
            Total += Score;
            (Last, Score, _chain) = ((Score, 0), 0, 0);
        }
        if (wall && Score > 0) (Last, Score, _chain) = ((0, 0), 0, 0);
    }
}
