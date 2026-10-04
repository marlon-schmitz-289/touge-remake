using System.Numerics;

namespace Kansei.Physics;

/// <summary>Driver input. Steer −1 = full left, +1 = full right. <see cref="Shift"/> is one-shot: +1 up, −1 down.</summary>
public readonly record struct VehicleInput(float Throttle, float Brake, float Steer, bool Handbrake = false, int Shift = 0);

/// <summary>Per-wheel state for rendering/audio. Wheel order: FL, FR, RL, RR.</summary>
public struct WheelState
{
    public Vector3 LocalCenter;   // body space (origin = CoG)
    public bool Contact;
    public int Surface;
    public float Compression;     // 0 = full droop, Travel = full bump
    public float SteerAngle;      // rad, + = right
    public float SpinAngle;       // rad
    public float AngularVelocity; // rad/s, + = rolling forward
    public float Load;            // N
    public float SlipRatio;
    public float SlipAngle;       // rad
}

/// <summary>
///     Rigid body on four raycast wheels: spring/damper/anti-roll suspension, combined-slip Magic Formula tyres
///     (normalised slip vector → friction circle), engine curve, gearbox, clutch-type LSD, brakes, handbrake.
///     World: metres, right-handed, +Y up. Body: +Z forward, origin at CoG.
/// </summary>
public sealed class Vehicle
{
    public const float ProbeRadius = 0.3f;
    const float G = 9.81f, VMin = 3f, AirDensity = 1.225f, WallRestitution = 0.2f, WallFriction = 0.3f;

    readonly WheelState[] _wheels = new WheelState[4];
    readonly GroundHit[] _hits = new GroundHit[4];
    readonly Vector3[] _mounts = new Vector3[4]; // body space, wheel centre at full droop + Travel
    readonly Vector3 _inertia;                   // body-space principal moments
    float _steer, _shiftTimer, _rearGrip = 1, _prevBeta;
    bool _drifting;

    public Vehicle(CarSpec spec)
    {
        Spec = spec;
        float m = spec.Mass, l = spec.Length, w = spec.Width, h = spec.Height;
        _inertia = new Vector3(m / 12 * (h * h + l * l), m / 12 * (w * w + l * l), m / 12 * (w * w + h * h));
        var zFront = spec.Wheelbase * (1 - spec.FrontWeight);
        var zRear = -spec.Wheelbase * spec.FrontWeight;
        for (var i = 0; i < 4; i++)
        {
            var front = i < 2;
            var sag = m * G * (front ? spec.FrontWeight : 1 - spec.FrontWeight) / 2 / (front ? spec.SpringFront : spec.SpringRear);
            var y = -(spec.CogHeight - spec.WheelRadius) + spec.Travel - sag;
            _mounts[i] = new Vector3(i % 2 == 0 ? spec.Track / 2 : -spec.Track / 2, y, front ? zFront : zRear); // left = +X
        }
        Reset(Vector3.Zero, 0);
    }

    public CarSpec Spec { get; }
    /// <summary>Grip multiplier per <see cref="GroundHit.Surface"/> id; null = 1 everywhere.</summary>
    public Func<int, float>? SurfaceGrip { get; set; }
    public bool AutomaticGearbox { get; set; } = true;

    public Vector3 Position { get; private set; } // CoG
    public Quaternion Orientation { get; private set; }
    public Vector3 Velocity { get; private set; }
    public Vector3 AngularVelocity { get; private set; }
    public float Rpm { get; private set; }
    public int Gear { get; private set; } // −1 R, 0 N, 1..n
    /// <summary>Body slip angle β in rad; + = travelling to the right of the nose. 0 when not moving forward.</summary>
    public float SlipAngle { get; private set; }
    public float SpeedKmh => Velocity.Length() * 3.6f;
    /// <summary>Wall contacts summed over the substeps of the last <see cref="Step" />.</summary>
    public int WallContacts { get; private set; }
    /// <summary>Point and normal of the last wall contact (valid while <see cref="WallContacts" /> &gt; 0).</summary>
    public Vector3 WallPoint { get; private set; }
    public Vector3 WallNormal { get; private set; }
    /// <summary>Highest closing speed (m/s) into a wall over the substeps of the last <see cref="Step" />; 0 without contact.</summary>
    public float WallImpactSpeed { get; private set; }
    public Matrix4x4 Pose => Matrix4x4.CreateFromQuaternion(Orientation) * Matrix4x4.CreateTranslation(Position);
    public ReadOnlySpan<WheelState> Wheels => _wheels;

    /// <summary>Places the car at rest with its wheels on <paramref name="ground"/> level; heading = yaw about +Y (0 = +Z).</summary>
    public void Reset(Vector3 ground, float heading)
    {
        Position = ground + Vector3.UnitY * Spec.CogHeight;
        Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, heading);
        Velocity = AngularVelocity = Vector3.Zero;
        Gear = 1;
        Rpm = Spec.IdleRpm;
        _steer = _shiftTimer = _prevBeta = 0;
        _rearGrip = 1;
        SlipAngle = 0;
        for (var i = 0; i < 4; i++)
            _wheels[i] = new WheelState { LocalCenter = _mounts[i] - Vector3.UnitY * Spec.Travel };
    }

    public void Step(in VehicleInput input, IGround ground, float dt)
    {
        var throttle = Math.Clamp(input.Throttle, 0, 1);
        var brake = Math.Clamp(input.Brake, 0, 1);
        if (AutomaticGearbox)
        {
            // Arcade automatic: brake held at standstill engages reverse, throttle at standstill goes back to 1st.
            // In reverse the pedals swap: brake drives backwards, throttle brakes.
            var fwdSpeed = Vector3.Dot(Velocity, Vector3.Transform(Vector3.UnitZ, Orientation));
            if (MathF.Abs(fwdSpeed) < 0.5f)
            {
                if (Gear >= 1 && brake > 0.5f && throttle < 0.1f) Gear = -1;
                else if (Gear == -1 && throttle > 0.5f && brake < 0.1f) Gear = 1;
            }
            if (Gear == -1) (throttle, brake) = (brake, throttle);
        }
        UpdateGear(input.Shift, dt, input.Handbrake);
        UpdateSteer(Math.Clamp(input.Steer, -1, 1), dt);
        UpdateDriftGrip(Math.Abs(input.Steer), throttle, input.Handbrake);
        WallContacts = 0;
        WallImpactSpeed = 0;
        var h = dt / Spec.Substeps;
        for (var s = 0; s < Spec.Substeps; s++)
        {
            Substep(throttle, brake, input.Handbrake, ground, h);
            CollideWalls(ground);
        }

        var vb = Vector3.Transform(Velocity, Quaternion.Conjugate(Orientation));
        SlipAngle = vb.Z < 1 ? 0 : MathF.Atan2(-vb.X, vb.Z); // forward travel only: reversing is not a 180° drift
        DriftStabilise(dt, input.Handbrake);
    }

    /// <summary>
    ///     Arcade: rear tyres lose some grip at full lock + full throttle at speed, and keep it reduced while the car
    ///     slides on throttle — drifts start from steering and hold. Part throttle = grip cornering; lift off and grip returns.
    /// </summary>
    void UpdateDriftGrip(float steerInput, float throttle, bool handbrake)
    {
        var fwd = Vector3.Dot(Velocity, Vector3.Transform(Vector3.UnitZ, Orientation));
        var sliding = MathF.Abs(SlipAngle) > 0.09f;
        var entering = steerInput > 0.8f && throttle > 0.8f && fwd > Spec.DriftEntrySpeed;
        _drifting = sliding && throttle > 0.3f && steerInput > 0.3f && !handbrake;
        var target = handbrake ? Spec.HandbrakeRearGrip
            : entering || sliding && throttle > 0.3f && steerInput > 0.3f ? Spec.DriftRearGrip : 1f; // let go of the wheel = grip back
        _rearGrip += (target - _rearGrip) * 0.15f;
    }

    /// <summary>
    ///     Arcade: damps the rate of change of the body slip angle (pendulum snap-back, spins) without touching a
    ///     steady drift, and springs the slip back beyond <see cref="CarSpec.MaxDriftAngle"/>.
    /// </summary>
    void DriftStabilise(float dt, bool handbrake)
    {
        var betaRate = (SlipAngle - _prevBeta) / dt;
        _prevBeta = SlipAngle;
        if (Velocity.LengthSquared() < 25) return;
        var over = MathF.Max(MathF.Abs(SlipAngle) - Spec.MaxDriftAngle, 0) * MathF.Sign(SlipAngle);
        // +yaw (nose left) raises β, so the corrective yaw acceleration has the opposite sign of β's change/excess
        var yawAcc = -Spec.DriftDamping * (handbrake ? Spec.HandbrakeDamping : 1) * betaRate - 20f * over; // handbrake = deliberate rotation
        var up = Vector3.Transform(Vector3.UnitY, Orientation);
        AngularVelocity += up * (yawAcc * dt);
    }

    float Ratio => Gear switch
    {
        > 0 => Spec.Gears[Gear - 1] * Spec.FinalDrive,
        < 0 => -Spec.ReverseGear * Spec.FinalDrive,
        _ => 0,
    };

    void UpdateGear(int shift, float dt, bool clutchOpen)
    {
        _shiftTimer = MathF.Max(_shiftTimer - dt, -1); // < 0: time since the shift finished
        var target = Gear;
        if (shift != 0)
            target = Math.Clamp(Gear + Math.Sign(shift), -1, Spec.Gears.Length);
        else if (AutomaticGearbox && Gear >= 1 && _shiftTimer <= 0 && !clutchOpen) // handbrake opens the clutch: free revs are no reason to shift
        {
            if (Rpm > Spec.AutoUpRpm && Gear < Spec.Gears.Length) target++;
            else if (Gear > 1 && _shiftTimer < -0.5f && Rpm < Spec.AutoDownRpm && Rpm * Spec.Gears[Gear - 2] / Spec.Gears[Gear - 1] < Spec.AutoUpRpm) target--;
        }

        if (target == Gear) return;
        Gear = target;
        _shiftTimer = Spec.ShiftTime;
    }

    void UpdateSteer(float steer, float dt)
    {
        var fwd = Vector3.Dot(Velocity, Vector3.Transform(Vector3.UnitZ, Orientation));
        var target = steer * Spec.MaxSteer / (1 + MathF.Abs(fwd) * Spec.SteerSpeedFactor);
        if (fwd > 2)
        {
            target += Spec.CounterSteerAssist * SlipAngle; // steer into the slide
            // never ask the front tyres for much more than their peak slip angle (full key = max cornering, not plough)
            var vb = Vector3.Transform(Velocity + Vector3.Cross(AngularVelocity, Vector3.Transform(_mounts[0] with { X = 0 }, Orientation)),
                Quaternion.Conjugate(Orientation));
            var travel = MathF.Atan2(-vb.X, vb.Z); // front axle travel direction, + = right
            var lim = Spec.SteerSlipLimit * Spec.PeakSlipAngle;
            target = Math.Clamp(target, travel - lim, travel + lim);
        }
        target = Math.Clamp(target, -Spec.MaxSteer, Spec.MaxSteer);
        var rate = Spec.SteerRate * dt;
        _steer += Math.Clamp(target - _steer, -rate, rate);
    }

    void Substep(float throttle, float brake, bool handbrake, IGround ground, float h)
    {
        var s = Spec;
        var q = Orientation;
        var up = Vector3.Transform(Vector3.UnitY, q);
        var fwd = Vector3.Transform(Vector3.UnitZ, q);
        var steerRot = Quaternion.CreateFromAxisAngle(up, -_steer);
        var r = s.WheelRadius;

        // Engine + clutch + LSD → drive torque on the driven axles (front share DriveFront, locked centre for 4WD).
        var ratio = Ratio;
        var df = s.DriveFront;
        float frontInertia = s.WheelInertia, rearInertia = s.WheelInertia;
        float axle = 0;
        if (ratio != 0 && !handbrake)
        {
            // Clutch slips (launch) only in 1st/reverse; during a shift the engine is rev-matched, no torque.
            var driven = (1 - df) * (_wheels[2].AngularVelocity + _wheels[3].AngularVelocity) * 0.5f + df * (_wheels[0].AngularVelocity + _wheels[1].AngularVelocity) * 0.5f;
            var wheelRpm = MathF.Abs(driven * ratio) * (30 / MathF.PI);
            var slipRpm = Gear is 1 or -1 ? s.IdleRpm + throttle * (s.LaunchRpm - s.IdleRpm) : s.IdleRpm;
            float te;
            if (wheelRpm >= slipRpm)
            {
                Rpm = wheelRpm;
                frontInertia += df * s.EngineInertia * ratio * ratio / 2;
                rearInertia += (1 - df) * s.EngineInertia * ratio * ratio / 2;
                te = (Rpm >= s.RevLimit ? 0 : throttle * EngineTorque(Rpm)) - (1 - throttle) * s.EngineBrake * Rpm / s.RevLimit;
            }
            else
            {
                Rpm = slipRpm; // clutch slipping: no engine braking
                te = throttle * EngineTorque(Rpm);
            }

            if (_shiftTimer <= 0) axle = te * ratio * s.DrivetrainEfficiency;
        }
        else
            Rpm += (s.IdleRpm + throttle * (s.RevLimit - s.IdleRpm) - Rpm) * MathF.Min(1, 10 * h);

        // same LSD on every driven axle, locking with that axle's torque; an undriven axle stays open
        var lsdLock = df < 1 ? s.LsdPreload + s.LsdLock * MathF.Abs(axle * (1 - df)) : 0;
        var lsd = Math.Clamp((_wheels[2].AngularVelocity - _wheels[3].AngularVelocity) * rearInertia / (2 * h), -lsdLock, lsdLock);
        var lsdLockFront = df > 0 ? s.LsdPreload + s.LsdLock * MathF.Abs(axle * df) : 0;
        var lsdFront = Math.Clamp((_wheels[0].AngularVelocity - _wheels[1].AngularVelocity) * frontInertia / (2 * h), -lsdLockFront, lsdLockFront);

        // Pass 1: suspension raycasts (ray starts one radius above the mount so bump-stop hits still register).
        var rayLength = s.Travel + 2 * r;
        for (var i = 0; i < 4; i++)
        {
            ref var w = ref _wheels[i];
            var origin = Position + Vector3.Transform(_mounts[i], q) + up * r;
            w.Contact = ground.Raycast(origin, -up, rayLength, out _hits[i]);
            w.Compression = w.Contact ? rayLength - _hits[i].Distance : 0;
        }

        // Pass 2: forces.
        var force = new Vector3(0, -s.Mass * G, 0);
        var tyreForce = Vector3.Zero;
        var torque = Vector3.Zero;
        var speed = Velocity.Length();
        force -= Velocity * (0.5f * AirDensity * s.DragArea * speed);
        for (var i = 0; i < 4; i++)
        {
            ref var w = ref _wheels[i];
            var front = i < 2;
            var inertia = front ? frontInertia : rearInertia;
            var drive = front ? axle * df / 2 + (i == 0 ? -lsdFront : lsdFront) : axle * (1 - df) / 2 + (i == 2 ? -lsd : lsd);
            var brakeTorque = brake * s.BrakeTorque * (front ? s.BrakeBias : 1 - s.BrakeBias) / 2 + (!front && handbrake ? s.HandbrakeTorque : 0);

            var omega = w.AngularVelocity + drive * h / inertia;
            var brakeDelta = brakeTorque * h / inertia;
            omega = MathF.Abs(omega) <= brakeDelta ? 0 : omega - MathF.CopySign(brakeDelta, omega);

            var comp = w.Compression;
            w.SteerAngle = front ? _steer : 0;
            w.LocalCenter = _mounts[i] - Vector3.UnitY * (s.Travel - MathF.Min(comp, s.Travel));
            w.Load = w.SlipRatio = w.SlipAngle = 0;
            if (w.Contact)
            {
                var hit = _hits[i];
                var arm = hit.Point - Position;
                var vp = Velocity + Vector3.Cross(AngularVelocity, arm);
                var k = front ? s.SpringFront : s.SpringRear;
                var fs = k * MathF.Min(comp, s.Travel) + 10 * k * MathF.Max(comp - s.Travel, 0)
                         - s.Damper * Vector3.Dot(vp, up)
                         + (front ? s.AntiRollFront : s.AntiRollRear) * (comp - _wheels[i ^ 1].Compression);
                fs = MathF.Max(fs, 0);

                var n = hit.Normal;
                var heading = front ? Vector3.Transform(fwd, steerRot) : fwd;
                var tf = Vector3.Normalize(heading - n * Vector3.Dot(heading, n));
                var tr = Vector3.Cross(tf, n);
                var vx = Vector3.Dot(vp, tf);
                var vy = Vector3.Dot(vp, tr);
                var denom = MathF.Max(MathF.Abs(vx), VMin);
                var kappa = (omega * r - vx) / denom;
                var alpha = MathF.Atan(vy / denom);

                var mu = s.Grip * (SurfaceGrip?.Invoke(hit.Surface) ?? 1) * MathF.Max(0.3f, 1 - s.LoadSensitivity * (fs / s.NominalLoad - 1))
                         * (front ? 1 : s.RearGripFactor * _rearGrip);
                var sx = kappa / s.PeakSlipRatio;
                var sy = alpha / s.PeakSlipAngle;
                var sigma = MathF.Sqrt(sx * sx + sy * sy);
                float fx = 0, fy = 0;
                if (sigma > 1e-6f)
                {
                    var f = mu * fs * MagicFormula(sigma) / sigma;
                    fx = f * sx;
                    fy = -f * sy;
                }

                // ponytail: explicit-integration stabiliser — a force may at most cancel the slip velocity within one
                // substep. Side effect: a braked car on a slope creeps at a few cm/s; a static-friction constraint fixes that.
                var fxMax = MathF.Abs(omega * r - vx) * inertia / (r * r * h);
                var fyMax = MathF.Abs(vy) * fs / G / h;
                fx = Math.Clamp(fx, -fxMax, fxMax);
                fy = Math.Clamp(fy, -fyMax, fyMax);
                omega -= fx * r * h / inertia;

                var f3 = up * fs + tf * fx + tr * fy;
                force += f3;
                tyreForce += tf * fx + tr * fy;
                torque += Vector3.Cross(arm, f3);
                force -= Velocity * (s.RollingResistance * fs / MathF.Max(speed, 1));
                w.Load = fs;
                w.SlipRatio = kappa;
                w.SlipAngle = alpha;
                w.Surface = hit.Surface;
            }

            w.AngularVelocity = omega;
            w.SpinAngle = (w.SpinAngle + omega * h) % MathF.Tau;
        }

        // Arcade: a held drift keeps its momentum — cancel part of the tyre force that brakes along the travel direction.
        if (_drifting && speed > 1)
        {
            var dir = Velocity / speed;
            var along = Vector3.Dot(tyreForce, dir);
            if (along < 0) force -= dir * (along * s.DriftMomentum);
        }

        Velocity += force * (h / s.Mass);

        // Arcade: while drifting the path carves toward the nose (keeps speed, tightens the arc instead of skating).
        if (_drifting && speed > 5)
        {
            var vh = Velocity with { Y = 0 };
            var nose = Vector3.Normalize(fwd with { Y = 0 });
            var vLen = vh.Length();
            var dirV = vh / vLen;
            var slip = MathF.Acos(Math.Clamp(Vector3.Dot(dirV, nose), -1, 1));
            if (slip is > 1e-4f and < MathF.PI / 2)
            {
                var t = MathF.Min(s.DriftCarve * h / slip, 1);
                var dir = Vector3.Normalize(Vector3.Lerp(dirV, nose, t));
                Velocity = dir * vLen + Vector3.UnitY * Velocity.Y;
            }
        }
        AngularVelocity += InvInertia(torque) * h;
        Position += Velocity * h;
        var angle = AngularVelocity.Length() * h;
        if (angle > 1e-9f)
            Orientation = Quaternion.Normalize(Quaternion.Concatenate(q, Quaternion.CreateFromAxisAngle(Vector3.Normalize(AngularVelocity), angle)));
    }

    /// <summary>
    ///     Wall probes in world space: body corners inset by <see cref="ProbeRadius" />, as a loop FL, FR, RR, RL
    ///     (the ground also tests the capsules between them, so the whole rounded body outline collides).
    /// </summary>
    public void WallProbes(Span<Vector3> probes)
    {
        float px = Spec.Width / 2 - ProbeRadius, pz = Spec.Length / 2 - ProbeRadius;
        for (var i = 0; i < 4; i++)
            probes[i] = Position + Vector3.Transform(new Vector3(i is 0 or 3 ? px : -px, 0, i < 2 ? pz : -pz), Orientation);
    }

    void CollideWalls(IGround ground)
    {
        Span<Vector3> probes = stackalloc Vector3[4];
        WallProbes(probes);
        Span<WallContact> contacts = stackalloc WallContact[8];
        var count = ground.CollideWalls(probes, ProbeRadius, contacts);
        WallContacts += count;
        if (count > 0) (WallPoint, WallNormal) = (contacts[0].Point, contacts[0].Normal);
        var moved = Vector3.Zero;
        Span<float> target = stackalloc float[count];
        Span<float> total = stackalloc float[count];
        for (var i = 0; i < count; i++)
        {
            var depth = contacts[i].Depth - Vector3.Dot(moved, contacts[i].Normal);
            if (depth > 0) moved += contacts[i].Normal * depth;
            var vn = Vector3.Dot(PointVelocity(contacts[i].Point), contacts[i].Normal);
            target[i] = vn < 0 ? -WallRestitution * vn : 0;
            WallImpactSpeed = MathF.Max(WallImpactSpeed, -vn);
            total[i] = 0;
        }

        // Sequential impulses (a few passes so simultaneous corner hits stay symmetric), friction once at the end.
        for (var pass = 0; pass < 4; pass++)
        for (var i = 0; i < count; i++)
        {
            var c = contacts[i];
            var arm = c.Point - Position;
            var j = (target[i] - Vector3.Dot(PointVelocity(c.Point), c.Normal)) / InvMass(arm, c.Normal);
            j = MathF.Max(total[i] + j, 0) - total[i];
            total[i] += j;
            ApplyImpulse(arm, c.Normal * j);
        }

        for (var i = 0; i < count; i++)
        {
            var c = contacts[i];
            var arm = c.Point - Position;
            var vp = PointVelocity(c.Point);
            var vt = vp - c.Normal * Vector3.Dot(vp, c.Normal);
            var vtLen = vt.Length();
            if (vtLen < 1e-4f) continue;
            var t = vt / vtLen;
            ApplyImpulse(arm, -t * MathF.Min(WallFriction * total[i], vtLen / InvMass(arm, t)));
        }

        Position += moved;
    }

    Vector3 PointVelocity(Vector3 point) => Velocity + Vector3.Cross(AngularVelocity, point - Position);

    void ApplyImpulse(Vector3 arm, Vector3 impulse)
    {
        Velocity += impulse / Spec.Mass;
        AngularVelocity += InvInertia(Vector3.Cross(arm, impulse));
    }

    float InvMass(Vector3 arm, Vector3 dir) => 1 / Spec.Mass + Vector3.Dot(dir, Vector3.Cross(InvInertia(Vector3.Cross(arm, dir)), arm));

    Vector3 InvInertia(Vector3 worldTorque)
    {
        var b = Vector3.Transform(worldTorque, Quaternion.Conjugate(Orientation)) / _inertia;
        return Vector3.Transform(b, Orientation);
    }

    float MagicFormula(float x)
    {
        var bx = Spec.TyreB * x;
        return MathF.Sin(Spec.TyreC * MathF.Atan(bx - Spec.TyreE * (bx - MathF.Atan(bx))));
    }

    float EngineTorque(float rpm)
    {
        var x = Spec.TorqueRpm;
        var y = Spec.TorqueNm;
        if (rpm <= x[0]) return y[0];
        for (var i = 1; i < x.Length; i++)
            if (rpm <= x[i])
                return y[i - 1] + (y[i] - y[i - 1]) * (rpm - x[i - 1]) / (x[i] - x[i - 1]);
        return y[^1];
    }
}
