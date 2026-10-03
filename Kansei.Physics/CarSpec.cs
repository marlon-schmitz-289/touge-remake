namespace Kansei.Physics;

/// <summary>
///     Vehicle parameters (SI units: kg, m, N, Nm, rad). Defaults = Toyota AE86 Trueno (4A-GE, T50 5-speed, FR, LSD).
///     Body space: +Z forward, +Y up, origin at the centre of gravity.
/// </summary>
public sealed record CarSpec
{
    public static readonly CarSpec AE86 = new();

    // Body
    public float Mass { get; init; } = 940f;
    public float CogHeight { get; init; } = 0.36f;           // above ground at rest; lower than real (0.48) so arcade grip does not roll the car
    public float FrontWeight { get; init; } = 0.53f;          // static share on the front axle
    public float Wheelbase { get; init; } = 2.40f;
    public float Track { get; init; } = 1.35f;
    public float Length { get; init; } = 4.20f;               // body box for inertia + wall probes
    public float Width { get; init; } = 1.63f;
    public float Height { get; init; } = 1.00f;               // effective mass height (not roof height)
    public float DragArea { get; init; } = 0.60f;             // Cd·A, m²
    public float RollingResistance { get; init; } = 0.015f;

    // Suspension (per corner)
    public float SpringFront { get; init; } = 30000f;         // N/m
    public float SpringRear { get; init; } = 26000f;
    public float Damper { get; init; } = 2300f;               // N/(m/s)
    public float AntiRollFront { get; init; } = 12000f;       // N/m of left-right compression difference
    public float AntiRollRear { get; init; } = 6000f;
    public float Travel { get; init; } = 0.18f;               // full droop → full bump

    // Wheels / tyres (185/70R13)
    public float WheelRadius { get; init; } = 0.29f;
    public float WheelInertia { get; init; } = 1.0f;          // kg·m², wheel + tyre + brake
    public float Grip { get; init; } = 1.7f;                  // peak μ (Magic Formula D / Fz); arcade level (~1.45 g cornering), a road tyre is ~1.05
    public float RearGripFactor { get; init; } = 1.12f;         // rear μ multiplier: >1 = stable/understeer bias, drift comes from the drift layer
    public float PeakSlipRatio { get; init; } = 0.10f;
    public float PeakSlipAngle { get; init; } = 0.14f;        // ~8°
    public float TyreB { get; init; } = 2.35f;                // with C=1.35, E=0 the peak sits at normalised slip 1
    public float TyreC { get; init; } = 1.35f;                // → sliding μ ≈ 0.85·peak (drift-friendly)
    public float TyreE { get; init; } = 0f;
    public float LoadSensitivity { get; init; } = 0.10f;      // μ drop per +100 % load over NominalLoad
    public float NominalLoad { get; init; } = 2500f;          // N

    // Engine (4A-GE: ~130 PS @ 6600, 149 Nm @ 5800)
    public float[] TorqueRpm { get; init; } = [1000, 2000, 3000, 4000, 5000, 5800, 6600, 7600];
    public float[] TorqueNm { get; init; } = [100, 115, 125, 135, 143, 149, 138, 115];
    public float IdleRpm { get; init; } = 900f;
    public float RevLimit { get; init; } = 7600f;
    public float LaunchRpm { get; init; } = 3000f;            // clutch-slip rpm at full throttle below idle-coupled speed
    public float EngineInertia { get; init; } = 0.12f;
    public float EngineBrake { get; init; } = 30f;            // Nm at the rev limit, throttle closed

    // Drivetrain (T50, FR)
    public float[] Gears { get; init; } = [3.587f, 2.022f, 1.384f, 1.000f, 0.861f];
    public float ReverseGear { get; init; } = 3.484f;
    public float FinalDrive { get; init; } = 4.30f;
    public float DrivetrainEfficiency { get; init; } = 0.85f;
    public float ShiftTime { get; init; } = 0.15f;            // clutch open
    public float AutoUpRpm { get; init; } = 7200f;
    public float AutoDownRpm { get; init; } = 3000f;
    public float LsdPreload { get; init; } = 40f;             // Nm
    public float LsdLock { get; init; } = 0.5f;               // extra lock torque per Nm of axle torque

    // Brakes
    public float BrakeTorque { get; init; } = 3200f;          // Nm, all four wheels together
    public float BrakeBias { get; init; } = 0.65f;            // front share
    public float HandbrakeTorque { get; init; } = 800f;       // Nm per rear wheel (enough to slide, not an instant lock)

    // Steering
    public float MaxSteer { get; init; } = 0.61f;             // ~35° full lock
    public float SteerSpeedFactor { get; init; } = 0.05f;     // input lock = MaxSteer / (1 + v·factor)
    public float SteerRate { get; init; } = 3f;               // rad/s at the wheels
    public float CounterSteerAssist { get; init; } = 0.8f;    // steer added per rad of body slip, 0 = off
    public float SteerSlipLimit { get; init; } = 1.15f;       // front wheels steer at most this × PeakSlipAngle past their travel direction

    // Arcade drift layer (Initial D feel: steer-in drifts, slides that hold and settle instead of snapping)
    public float DriftDamping { get; init; } = 5f;            // yaw torque per rad/s of body-slip-angle change, × yaw inertia
    public float HandbrakeDamping { get; init; } = 0.5f;      // DriftDamping factor while the handbrake is pulled
    public float HandbrakeRearGrip { get; init; } = 0.4f;     // rear grip factor while the handbrake is pulled (arcade: rotates the car)
    public float MaxDriftAngle { get; init; } = 0.75f;        // rad (~43°); beyond it a spring pushes the slip back
    public float DriftRearGrip { get; init; } = 0.75f;        // rear grip factor while drifting/entering on throttle
    public float DriftMomentum { get; init; } = 0.75f;        // share of the tyre drag against the travel direction cancelled while drifting on throttle
    public float DriftEntrySpeed { get; init; } = 20f;        // m/s; full lock + full throttle above this starts a drift

    // Integration
    public int Substeps { get; init; } = 2;
}
