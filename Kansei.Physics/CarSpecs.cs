namespace Kansei.Physics;

/// <summary>
///     A <see cref="CarSpec"/> per car of the game (ids/names as in the ELF table 0x2C4978, HCAR/CAR_ENV order).
///     The game itself has no real physics data (its gearbox is a list of speed fractions, 0x2CBF30): from the game
///     only track and wheelbase (wheel nodes fr_l/fr_r/re_l of the HCAR body00, so physics and model wheels agree), wheel
///     radius (per-car table 0x24EB00, 11 floats: half track F/R, front/rear axle z, …, [8] radius — its track/axle values
///     equal the model's for most cars but are copies for some, e.g. ALTEZ/GT-4 = MRS) and the number of gears
///     (engine table 0x2CBBD0, 24 B per car: gear-table index, gear count, rev limit 7500–11500, …).
///     Everything else is real-world data of the stock Japanese-market model (published figures from memory, ±5 %:
///     mass, front weight share, peak power/torque, rev limit, gear and final ratios, drivetrain, length/width).
///     The torque curve is built from the two peaks (<see cref="Curve"/>). Suspension and brakes scale with mass from
///     the AE86 so ride and stopping feel alike; the arcade layer is shared, only scaled per drivetrain (<see cref="Make"/>).
///     AE86T/AE86L are <see cref="CarSpec.AE86"/> unchanged (hand-tuned).
/// </summary>
public static class CarSpecs
{
    const float Fr = 0, Ff = 1;

    public static readonly IReadOnlyDictionary<string, CarSpec> All = new Dictionary<string, CarSpec>
    {
        ["AE86T"] = CarSpec.AE86,
        ["AE86L"] = CarSpec.AE86,
        ["AE85"] = Make(1.370f, 2.440f, 0.290f, 925, 0.53f, 83, 5600, 118, 3600, 6500, [3.789f, 2.220f, 1.435f, 1.000f, 0.865f], 4.10f, Fr, 4.20f, 1.63f),
        ["MR2"] = Make(1.465f, 2.405f, 0.300f, 1270, 0.43f, 225, 6000, 304, 3200, 7000, [3.230f, 1.913f, 1.258f, 0.918f, 0.731f], 4.285f, Fr, 4.17f, 1.70f),
        ["MRS"] = Make(1.460f, 2.437f, 0.300f, 970, 0.42f, 140, 6400, 171, 4400, 7000, [3.166f, 1.904f, 1.392f, 1.031f, 0.815f], 4.312f, Fr, 3.89f, 1.70f),
        ["ALTEZ"] = Make(1.495f, 2.670f, 0.313f, 1340, 0.53f, 210, 7600, 216, 6400, 8000, [3.538f, 2.060f, 1.404f, 1.000f, 0.713f, 0.582f], 4.30f, Fr, 4.40f, 1.72f),
        ["GT-4"] = Make(1.550f, 2.535f, 0.320f, 1440, 0.60f, 255, 6000, 304, 4000, 7000, [3.230f, 1.913f, 1.258f, 0.918f, 0.731f], 4.285f, 0.5f, 4.42f, 1.75f),
        ["R32"] = Make(1.500f, 2.606f, 0.330f, 1430, 0.59f, 280, 6800, 353, 4400, 8000, [3.214f, 1.925f, 1.302f, 1.000f, 0.752f], 4.111f, 0.3f, 4.55f, 1.76f),
        ["R34"] = Make(1.480f, 2.638f, 0.330f, 1560, 0.55f, 280, 6800, 392, 4400, 8000, [3.827f, 2.360f, 1.685f, 1.312f, 1.000f, 0.793f], 3.545f, 0.3f, 4.60f, 1.79f),
        ["ER34"] = Make(1.480f, 2.665f, 0.320f, 1430, 0.55f, 280, 6400, 343, 3200, 7000, [3.827f, 2.360f, 1.685f, 1.312f, 1.000f, 0.793f], 3.692f, Fr, 4.58f, 1.73f),
        ["S13"] = Make(1.454f, 2.426f, 0.310f, 1170, 0.56f, 205, 6000, 275, 4000, 7500, [3.321f, 1.902f, 1.308f, 1.000f, 0.759f], 4.111f, Fr, 4.47f, 1.69f),
        ["S14Q"] = Make(1.480f, 2.525f, 0.320f, 1180, 0.56f, 160, 6400, 188, 4800, 7500, [3.321f, 1.902f, 1.308f, 1.000f, 0.759f], 4.111f, Fr, 4.50f, 1.73f),
        ["S14"] = Make(1.480f, 2.482f, 0.310f, 1240, 0.56f, 220, 6000, 275, 4800, 7500, [3.321f, 1.902f, 1.308f, 1.000f, 0.759f], 4.083f, Fr, 4.50f, 1.73f),
        ["S15"] = Make(1.420f, 2.529f, 0.310f, 1250, 0.56f, 250, 6400, 275, 4800, 7500, [3.626f, 2.200f, 1.541f, 1.213f, 1.000f, 0.767f], 4.083f, Fr, 4.45f, 1.70f),
        ["ONE80"] = Make(1.436f, 2.472f, 0.310f, 1230, 0.56f, 205, 6000, 275, 4000, 7500, [3.321f, 1.902f, 1.308f, 1.000f, 0.759f], 4.111f, Fr, 4.54f, 1.69f),
        ["SIL80"] = Make(1.436f, 2.472f, 0.310f, 1200, 0.56f, 205, 6000, 275, 4000, 7500, [3.321f, 1.902f, 1.308f, 1.000f, 0.759f], 4.111f, Fr, 4.47f, 1.69f),
        ["EK9"] = Make(1.398f, 2.638f, 0.290f, 1070, 0.62f, 185, 8200, 160, 7500, 9000, [3.230f, 2.105f, 1.458f, 1.107f, 0.848f], 4.40f, Ff, 4.18f, 1.70f),
        ["EG6"] = Make(1.506f, 2.570f, 0.290f, 1050, 0.62f, 170, 7800, 157, 7300, 8200, [3.230f, 2.105f, 1.458f, 1.107f, 0.848f], 4.40f, Ff, 4.07f, 1.70f),
        ["INTGR"] = Make(1.409f, 2.510f, 0.300f, 1080, 0.62f, 200, 8000, 186, 7500, 8500, [3.230f, 2.105f, 1.458f, 1.107f, 0.848f], 4.40f, Ff, 4.38f, 1.70f),
        ["S2000"] = Make(1.499f, 2.447f, 0.310f, 1240, 0.50f, 250, 8300, 218, 7500, 9000, [3.133f, 2.045f, 1.481f, 1.161f, 0.970f, 0.810f], 4.10f, Fr, 4.14f, 1.75f),
        ["EVO3"] = Make(1.460f, 2.489f, 0.300f, 1260, 0.60f, 270, 6250, 309, 3000, 7000, [2.785f, 1.950f, 1.407f, 1.031f, 0.761f], 4.529f, 0.5f, 4.31f, 1.70f),
        ["EVO4"] = Make(1.380f, 2.489f, 0.300f, 1350, 0.60f, 280, 6500, 352, 3000, 7500, [2.785f, 1.950f, 1.444f, 1.096f, 0.825f], 4.529f, 0.5f, 4.33f, 1.69f),
        ["EVO7"] = Make(1.500f, 2.622f, 0.320f, 1400, 0.60f, 280, 6500, 383, 3500, 7500, [2.785f, 1.950f, 1.407f, 1.031f, 0.761f], 4.529f, 0.5f, 4.46f, 1.77f),
        ["FD3S"] = Make(1.480f, 2.584f, 0.320f, 1260, 0.50f, 255, 6500, 294, 5000, 8000, [3.483f, 2.015f, 1.391f, 1.000f, 0.719f], 4.10f, Fr, 4.28f, 1.76f, rotary: true),
        ["FD3SA"] = Make(1.480f, 2.584f, 0.320f, 1270, 0.50f, 280, 6500, 314, 5000, 8000, [3.483f, 2.015f, 1.391f, 1.000f, 0.719f], 4.10f, Fr, 4.28f, 1.76f, rotary: true),
        ["FC3S"] = Make(1.420f, 2.510f, 0.310f, 1250, 0.51f, 215, 6500, 275, 4000, 8000, [3.475f, 2.002f, 1.366f, 1.000f, 0.758f], 4.10f, Fr, 4.34f, 1.69f, rotary: true),
        ["NA6C"] = Make(1.434f, 2.300f, 0.300f, 950, 0.52f, 120, 6500, 140, 5500, 7200, [3.136f, 1.888f, 1.330f, 1.000f, 0.814f], 4.30f, Fr, 3.97f, 1.68f),
        ["NB8C"] = Make(1.416f, 2.257f, 0.300f, 1030, 0.52f, 160, 7000, 177, 5500, 7500, [3.760f, 2.269f, 1.645f, 1.257f, 1.000f, 0.843f], 3.909f, Fr, 3.96f, 1.68f),
        ["IMP"] = Make(1.410f, 2.520f, 0.300f, 1250, 0.60f, 280, 6500, 353, 4000, 8000, [3.083f, 2.062f, 1.545f, 1.151f, 0.825f], 4.444f, 0.35f, 4.34f, 1.69f),
        ["IMP2"] = Make(1.469f, 2.525f, 0.310f, 1430, 0.60f, 280, 6400, 373, 4400, 8000, [3.636f, 2.375f, 1.761f, 1.346f, 0.971f, 0.756f], 3.90f, 0.35f, 4.42f, 1.74f),
        ["IMP3"] = Make(1.470f, 2.550f, 0.300f, 1270, 0.60f, 280, 6000, 363, 3200, 8000, [3.083f, 2.062f, 1.545f, 1.151f, 0.825f], 4.444f, 0.35f, 4.37f, 1.77f),
        ["CAPPU"] = Make(1.180f, 2.050f, 0.270f, 700, 0.51f, 64, 6500, 85, 4000, 9000, [3.483f, 2.015f, 1.391f, 1.000f, 0.806f], 5.125f, Fr, 3.30f, 1.40f),
    };

    /// <summary>
    ///     Spec from game geometry + real figures. Suspension, brakes and wheel inertia scale with mass/radius from the
    ///     AE86 (same ride frequency and deceleration). Arcade layer by drivetrain: FF keeps more rear grip when the drift
    ///     layer kicks in (0.85 instead of 0.75), 4WD rotates less through its front drive share alone and carves 20 % less.
    ///     Engine inertia grows with √torque (bigger/turbo engines carry heavier cranks and flywheels), rotaries are 40 % lighter.
    ///     Steer-in at 110 km/h (CarSpecsTests): FR/MR 7–19° body slip (Cappuccino 30°), 4WD 5–10°, FF 3°.
    /// </summary>
    static CarSpec Make(float track, float wheelbase, float radius, float mass, float front, float ps, float psRpm, float nm, float nmRpm,
        float limit, float[] gears, float final, float driveFront, float length, float width, bool rotary = false)
    {
        var a = CarSpec.AE86;
        float m = mass / a.Mass, r = radius / a.WheelRadius;
        var (tRpm, tNm) = Curve(ps, psRpm, nm, nmRpm, limit);
        return a with
        {
            Mass = mass, FrontWeight = front, Wheelbase = wheelbase, Track = track, WheelRadius = radius, Length = length, Width = width,
            SpringFront = a.SpringFront * m, SpringRear = a.SpringRear * m, Damper = a.Damper * m,
            AntiRollFront = a.AntiRollFront * m, AntiRollRear = a.AntiRollRear * m,
            WheelInertia = a.WheelInertia * r * r, BrakeTorque = a.BrakeTorque * m * r, HandbrakeTorque = a.HandbrakeTorque * m * r,
            TorqueRpm = tRpm, TorqueNm = tNm, RevLimit = limit, AutoUpRpm = limit - 400, AutoDownRpm = 0.4f * limit,
            Gears = gears, FinalDrive = final, DriveFront = driveFront,
            EngineInertia = a.EngineInertia * MathF.Sqrt(nm / a.TorqueNm.Max()) * (rotary ? 0.6f : 1),
            DriftRearGrip = driveFront < 1 ? a.DriftRearGrip : 0.85f,
            DriftCarve = driveFront is > 0 and < 1 ? 0.8f * a.DriftCarve : a.DriftCarve,
        };
    }

    /// <summary>
    ///     Torque curve through 1000 rpm (60 % of peak), the torque peak, the power peak (P/ω) and the rev limit (90 % of
    ///     peak power) — piecewise linear like <see cref="CarSpec.TorqueNm"/>.
    /// </summary>
    public static (float[] Rpm, float[] Nm) Curve(float ps, float psRpm, float nm, float nmRpm, float limit)
    {
        var atPower = ps * 735.5f / (psRpm * MathF.PI / 30);
        return ([1000, nmRpm, psRpm, limit], [0.6f * nm, nm, atPower, 0.9f * atPower * psRpm / limit]);
    }
}
