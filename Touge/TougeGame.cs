using System.Numerics;
using Kansei.Audio;
using Kansei.Core;
using Kansei.Graphics;
using Kansei.Input;
using Kansei.Physics;
using Touge.Formats;

namespace Touge;

/// <summary>
///     Course from the ISO with a drivable AE86 (default) and a free-fly camera (F1).
///     Drive: W/S or ↑/↓ throttle/brake (automatic: hold S at standstill to reverse), A/D or ←/→ steer, Space handbrake, T auto/manual, Shift/Ctrl gear up/down (manual),
///     R reset onto the driving line, C chase/bumper camera, F2 graphics quality (MSAA, bloom, shadows) on/off. Pad: left stick, triggers, A handbrake, bumpers shift.
///     Fly: WASD, Q/E down/up, right mouse or arrow keys look, Shift fast, Space jump along the driving line. Esc quit.
///     <paramref name="orbit"/> (degrees, 0 = front, 90 = left, 180 = rear) puts the fly camera around the car;
///     <paramref name="autodrive"/> lets the line pilot drive that many seconds before the first frame (for --shot);
///     <paramref name="bench"/> lets it drive in real time with the chase camera for that many seconds, then logs frame times and quits;
///     <paramref name="drift"/> makes the pilot throw in a scripted handbrake drift every 7 s (<see cref="Drive.ForceDrift"/>).
///     Tyre smoke, skid marks and sparks come from the car's wheel/wall state every tick (<see cref="TickEffects"/>).
///     Sound: engine, tyres, walls, wind, race BGM (M next track, F3 music on/off); none for --shot.
///     <paramref name="flicker"/>: no game loop, renders the <see cref="FlickerProbe"/> views and quits.
/// </summary>
public sealed class TougeGame(string isoPath, string courseTime, string? shotPath = null, int startPoint = 0, float? orbit = null, float? autodrive = null,
    float? bench = null, bool highQuality = true, bool drift = false, string? flicker = null)
    : KanseiGame
{
    private AudioDevice? _audioDevice;
    private GameAudio? _audio;

    private CarRenderer _carRenderer = null!;
    private EffectsRenderer _fxRenderer = null!;
    private readonly Effects _fx = new();
    private readonly Random _rng = new(3);
    private readonly float[] _smokeDebt = new float[4];
    private float _simTime, _shake;
    private Vector3 _prevVelocity, _shakeOffset;
    private CarModel _car = null!;
    private Matrix4x4 _carBody, _carPose, _modelToBody;
    private readonly Matrix4x4[] _carWheels = new Matrix4x4[4];
    private FrameCapture? _capture;
    private int _shotState; // 0 none, 1 render next frame into capture, 2 read back
    private FlickerProbe? _probe;
    private FlickerProbe.View _probeView;
    private WorldRenderer _renderer = null!;
    private CourseLoader.Course _course = null!;
    private Drive _drive = null!;

    // physics → render interpolation
    private Vector3 _prevPos;
    private Quaternion _prevRot;

    // driver input, sampled per frame, consumed per tick
    private float _throttle, _brake, _steer, _brakeLight;
    private bool _handbrake;
    private int _pendingShift;

    // cameras
    private bool _fly, _bumperCam, _camSnap = true;
    private Vector3 _pos, _camLook;
    private float _yaw, _pitch, _fov = MathF.PI / 3;
    private int _lastMouseX, _lastMouseY, _linePoint;
    private double _statusTime;
    private int _frames;
    private readonly List<float> _frameTimes = [];
    private (int Smoke, int Skids, int Sparks) _fxPeak;

    public override void Load()
    {
        using var iso = new Iso9660(isoPath);
        _renderer = new WorldRenderer(Device) { Atmosphere = AtmosphereFor(courseTime), HighQuality = highQuality };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _course = CourseLoader.Load(iso, courseTime, _renderer);
        _drive = new Drive(iso, courseTime);
        Console.WriteLine($"[Touge] {courseTime} geladen in {sw.ElapsedMilliseconds} ms, {_course.World.Batches.Count} Batches, {_drive.Ground.Walls.Length} Wandsegmente");
        _carRenderer = new CarRenderer(_renderer);
        _fxRenderer = new EffectsRenderer(_renderer);
        SetupLights(courseTime.EndsWith("_NIT"));
        _car = CarModel.Load(iso, "AE86T", 0, _renderer);

        // model space → physics body space (origin CoG): model wheel centres onto the physics wheel centres at rest
        var spec = _drive.Car.Spec;
        var modelWheels = Vector3.Zero;
        foreach (var w in _car.Wheels) modelWheels += w.Translation / 4;
        _modelToBody = Matrix4x4.CreateTranslation(new Vector3(0, spec.WheelRadius - spec.CogHeight, spec.Wheelbase * (0.5f - spec.FrontWeight)) - modelWheels);

        _drive.ResetTo(startPoint);
        _drive.ForceDrift = drift;
        if (autodrive is { } seconds)
        {
            _drive.AutoDrive(seconds, () => TickEffects(Drive.Dt));
            _simTime = seconds;
            _brakeLight = _drive.Pilot.Drive(_drive.Car).Brake; // the shot frame may come before the first tick
        }
        SyncPose();
        JumpToLine(startPoint);
        if (orbit is { } deg)
        {
            _fly = true;
            UpdateCarMatrices(1);
            OrbitCar(deg * MathF.PI / 180);
        }
        if (shotPath != null) (_capture, _shotState) = (new FrameCapture(Device, 1280, 720), 1);
        else if (flicker != null)
        {
            var models = Afs.FromBytes(iso.ReadFile("CDVD/DATA/MODEL/COURSE.AFS"), iso.ReadFile("CDVD/DATA/MODEL/COURSE.TBL"));
            var (corners, batches) = CourseLoader.Flatten(CourseLoader.Meshes(models.Read(models.Find(courseTime + ".PAC")!.Value), false));
            var spots = FlickerProbe.Spots([.. corners.Select(v => v.Position)], [.. batches.Select(b => b.First)]);
            (_capture, _probe) = (new FrameCapture(Device, 1280, 720), new FlickerProbe(flicker, courseTime, _course.DrivingLine, startPoint, spots));
        }
        else
        {
            _audioDevice = new AudioDevice();
            _audio = new GameAudio(iso, courseTime, _audioDevice);
            _audio.PlayTrack(background: true);
        }
    }

    private void SyncPose() => (_prevPos, _prevRot, _camSnap) = (_drive.Car.Position, _drive.Car.Orientation, true);

    public override void Tick(float dt)
    {
        if (_probe != null) return; // frozen scene
        var car = _drive.Car;
        (_prevPos, _prevRot) = (car.Position, car.Orientation);
        var input = autodrive != null || bench != null ? _drive.PilotInput(_simTime)
            : _fly ? new VehicleInput(0, 0, 0, true)
            : new VehicleInput(_throttle, _brake, _steer, _handbrake, _pendingShift);
        _pendingShift = 0;
        _brakeLight = input.Brake;
        car.Step(input, _drive.Ground, dt);
        _simTime += dt;
        TickEffects(dt);
        _audio?.Update(car, input.Throttle, input.Handbrake, dt);
    }

    /// <summary>
    ///     Per physics tick: smoke and skid marks from each wheel's slide speed (slip ratio/angle × speed, scaled by
    ///     load), sparks and camera shake from wall contacts, then the particles move. Thresholds tuned by eye.
    /// </summary>
    private void TickEffects(float dt)
    {
        var car = _drive.Car;
        var spec = car.Spec;
        var pose = car.Pose;
        var up = Vector3.TransformNormal(Vector3.UnitY, pose);
        var side = Vector3.TransformNormal(Vector3.UnitX, pose) * 0.1f; // half tread width
        var speed = MathF.Max(car.Velocity.Length(), 3); // the tyre model's slip denominator (VMin)
        for (var i = 0; i < 4; i++)
        {
            var w = car.Wheels[i];
            var contact = Vector3.Transform(w.LocalCenter, pose) - up * spec.WheelRadius;
            var tan = MathF.Tan(w.SlipAngle);
            var slide = w.Contact ? speed * MathF.Sqrt(w.SlipRatio * w.SlipRatio + tan * tan) : 0; // m/s
            var load = Math.Clamp(w.Load / spec.NominalLoad, 0, 1.5f);
            _fx.Skid(i, contact, side, up, Math.Clamp((slide - 2f) / 3, 0, 1) * Math.Clamp(load * 4, 0, 1));
            var smoke = Math.Clamp((slide - 4.5f) / 7, 0, 1) * load;
            _smokeDebt[i] += smoke * 45 * dt; // puffs per second per wheel at full slide
            for (; _smokeDebt[i] >= 1; _smokeDebt[i]--)
            {
                var jitter = new Vector3(_rng.NextSingle() - 0.5f, _rng.NextSingle() * 0.5f, _rng.NextSingle() - 0.5f);
                _fx.EmitSmoke(contact + up * 0.2f + jitter * 0.3f, car.Velocity * 0.12f + jitter * 1.5f + up * 0.5f, 0.3f, 0.12f + 0.25f * smoke);
            }
        }

        var impact = (car.Velocity - _prevVelocity).Length();
        _prevVelocity = car.Velocity;
        if (car.WallContacts > 0)
        {
            _shake = MathF.Max(_shake, Math.Clamp((impact - 0.5f) / 4, 0, 1));
            var scrape = car.Velocity - car.WallNormal * Vector3.Dot(car.Velocity, car.WallNormal);
            var sparks = (int)MathF.Min(scrape.Length() / 3, 6);
            for (var i = 0; i < sparks; i++)
            {
                var r = new Vector3(_rng.NextSingle() - 0.5f, _rng.NextSingle(), _rng.NextSingle() - 0.5f);
                _fx.EmitSpark(car.WallPoint + car.WallNormal * 0.05f - up * 0.2f,
                    scrape * (0.3f + 0.5f * _rng.NextSingle()) + car.WallNormal * (1 + 2 * r.Y) + r * 4);
            }
        }
        _fx.Update(dt);
    }

    private void JumpToLine(int i)
    {
        var line = _course.DrivingLine;
        _linePoint = i % line.Length;
        var a = line[_linePoint];
        var b = line[Math.Min(_linePoint + 2, line.Length - 1)];
        _pos = a + new Vector3(0, 1.2f, 0);
        _yaw = MathF.Atan2(b.X - a.X, b.Z - a.Z);
        _pitch = 0;
    }

    private void OrbitCar(float angle)
    {
        var target = Vector3.Transform(new Vector3(0, 0.4f, 0), _carBody);
        var dir = Vector3.TransformNormal(new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)), _carBody);
        _pos = target + dir * 5.5f + new Vector3(0, 1.3f, 0);
        var look = Vector3.Normalize(target - _pos);
        (_yaw, _pitch) = (MathF.Atan2(look.X, look.Z), MathF.Asin(look.Y));
    }

    public override void Update(in GameTime time)
    {
        if (_probe != null)
        {
            ProbeStep();
            return;
        }
        if (_shotState == 2)
        {
            Png.Write(shotPath!, _capture!.Width, _capture.Height, _capture.ReadRgba());
            Console.WriteLine($"[Touge] Screenshot -> {shotPath}");
            Window.ShouldClose = true;
            return;
        }
        var k = Input.Keyboard;
        var dt = time.DeltaTime;
        if (k.IsKeyPressed(Key.Escape)) Window.ShouldClose = true;
        if (k.IsKeyPressed(Key.F2))
        {
            _renderer.HighQuality = !_renderer.HighQuality;
            Console.WriteLine($"\n[Touge] Grafik: {(_renderer.HighQuality ? "hoch (4× MSAA, Bloom, Schatten)" : "niedrig (ohne MSAA/Bloom/Schatten)")}");
        }
        if (bench is { } benchSeconds && Bench(time, benchSeconds)) return;
        if (_audio != null && k.IsKeyPressed(Key.M)) _audio.NextTrack();
        if (_audio != null && k.IsKeyPressed(Key.F3)) _audio.MusicOn = !_audio.MusicOn;
        if (k.IsKeyPressed(Key.F1))
        {
            _fly = !_fly;
            if (_fly) (_yaw, _pitch) = (MathF.Atan2(_camLook.X - _pos.X, _camLook.Z - _pos.Z), 0);
            else _camSnap = true;
        }
        if (_fly) UpdateFly(time);
        else UpdateDriver(dt);

        _frames++;
        if (time.TotalTime - _statusTime >= 0.1)
        {
            var c = _drive.Car;
            Console.Write($"\r[Touge] {c.SpeedKmh,4:F0} km/h  Gang {Drive.Gear(c),-2}  {c.Rpm,5:F0} rpm  Schräglauf {c.SlipAngle * 180 / MathF.PI,6:F1}°  {_frames / (time.TotalTime - _statusTime),4:F0} fps   ");
            (_statusTime, _frames) = (time.TotalTime, 0);
        }
    }

    /// <summary>--flicker: reads back the last view, sets up the next one (or writes the result and quits).</summary>
    private void ProbeStep()
    {
        if (_shotState == 1) return;
        if (_shotState == 2) _probe!.Add(_capture!.Width, _capture.Height, _capture.ReadRgba());
        if (!_probe!.Next(out _probeView))
        {
            _probe.Finish(_capture!.Width);
            Window.ShouldClose = true;
            return;
        }
        _fly = true;
        if (_probeView.Group == 1)
        {
            UpdateCarMatrices(1);
            OrbitCar(_probeView.Orbit * MathF.PI / 180);
        }
        else if (_probeView.Group == 2)
        {
            var look = Vector3.Normalize(_probeView.Target - _probeView.Eye);
            (_pos, _yaw, _pitch) = (_probeView.Eye, MathF.Atan2(look.X, look.Z), MathF.Asin(look.Y));
        }
        else
        {
            JumpToLine(_probeView.LinePoint);
            _pitch = -0.12f;
        }
        _camLook = _pos + Forward() * 100; // far target: rounding of the turned target barely tilts the view
        _shotState = 1;
    }

    /// <summary>--bench: frame intervals after a 2 s warm-up; at the end avg/p99/max, frames over 25 ms and the effect peaks.</summary>
    private bool Bench(in GameTime time, float seconds)
    {
        if (time.TotalTime > 2) _frameTimes.Add(time.DeltaTime * 1000);
        _fxPeak = (Math.Max(_fxPeak.Smoke, _fx.SmokeCount), Math.Max(_fxPeak.Skids, _fx.SkidCount), Math.Max(_fxPeak.Sparks, _fx.SparkCount));
        if (time.TotalTime < seconds + 2) return false;
        var sorted = _frameTimes.Order().ToArray();
        Console.WriteLine($"\n[Bench] {courseTime} {Device.SwapchainWidth}x{Device.SwapchainHeight} Qualität {(_renderer.HighQuality ? "hoch" : "niedrig")}: " +
                          $"{sorted.Length} Frames in {seconds:F0} s, Frametime avg {sorted.Average():F2} ms, p99 {sorted[(int)(sorted.Length * 0.99)]:F2} ms, " +
                          $"max {sorted[^1]:F2} ms, > 25 ms: {sorted.Count(t => t > 25)}; Effekte max {_fxPeak.Smoke} Rauch, {_fxPeak.Skids} Spursegmente, {_fxPeak.Sparks} Funken");
        Window.ShouldClose = true;
        return true;
    }

    /// <summary>
    ///     Night: headlights on, CRS_LIGHT points as sodium street lights (on Akina they sit 6–7 m above and 5–10 m beside
    ///     the road: lamp heads; the game itself only brightens the car near them). Intensities tuned by eye.
    /// </summary>
    private void SetupLights(bool night)
    {
        var l = _renderer.Lights;
        l.HeadlightColor = night ? new Vector3(1f, 0.92f, 0.8f) * 700 : Vector3.Zero;
        l.StreetLights = _course.Lights;
        l.StreetLightColor = night ? new Vector3(1f, 0.62f, 0.3f) * 50 : Vector3.Zero;
    }

    /// <summary>Per frame: headlights from the car pose, brake lamps, env maps of the road point nearest to the car.</summary>
    private void UpdateLights()
    {
        var l = _renderer.Lights;
        var dir = Vector3.Normalize(Vector3.TransformNormal(new Vector3(0, -0.03f, 1), _carBody));
        for (var i = 0; i < 2; i++)
        {
            // AE86 pop-up lamps, model space (x left, z front), placed by eye on the mesh
            l.HeadlightPosition[i] = Vector3.Transform(new Vector3(i == 0 ? 0.55f : -0.55f, 0.62f, 2.0f), _carBody);
            l.HeadlightDirection[i] = dir;
        }
        l.Brake = _fly ? 0 : _brakeLight;
        if (_course.Env is { } env)
        {
            var e = env[_course.NearestRoadPoint(_carBody.Translation)];
            _renderer.SetEnvironment(e[0], e[1], e[2], e[3]);
        }
    }

    /// <summary>Sky, fog, light and grading per time of day (_DAY, _NIT, _RIN). Tuned by eye, not from game data.</summary>
    private static Atmosphere AtmosphereFor(string courseTime) => courseTime[(courseTime.LastIndexOf('_') + 1)..] switch
    {
        "NIT" => new Atmosphere
        {
            Zenith = new(0.004f, 0.006f, 0.016f), Horizon = new(0.018f, 0.022f, 0.035f),
            SunDirection = Vector3.Normalize(new Vector3(-0.5f, 0.45f, 0.6f)), SunDisk = new(1.2f, 1.3f, 1.5f), // moon
            SunIntensity = 0.12f, Ambient = new(0.025f, 0.03f, 0.045f), FogDistance = 900, BakedKeep = 0.88f, BakedSun = 0.15f, EnvStrength = 2f,
            Exposure = 3.2f, BloomThreshold = 0.5f, BloomStrength = 1.0f, Tint = new(0.92f, 0.97f, 1.1f), Saturation = 0.9f, Vignette = 0.35f,
        },
        "RIN" => new Atmosphere
        {
            Zenith = new(0.22f, 0.24f, 0.27f), Horizon = new(0.40f, 0.42f, 0.45f), SunDisk = Vector3.Zero,
            SunIntensity = 0.35f, Ambient = new(0.32f, 0.34f, 0.37f), FogDistance = 700, BakedKeep = 0.85f, BakedSun = 0.2f, Wetness = 0.85f,
            SunDirection = Vector3.Normalize(new Vector3(0.2f, 1f, 0.15f)),
            Exposure = 1.45f, Tint = new(0.96f, 0.99f, 1.03f), Saturation = 0.8f, Vignette = 0.3f,
        },
        _ => new Atmosphere(),
    };

    private void UpdateDriver(float dt)
    {
        var k = Input.Keyboard;
        var pad = Input.Gamepad;
        var car = _drive.Car;
        if (k.IsKeyPressed(Key.R))
        {
            _drive.ResetNearest();
            SyncPose();
        }
        if (k.IsKeyPressed(Key.C))
            (_bumperCam, _camSnap) = (!_bumperCam, true);
        if (k.IsKeyPressed(Key.T)) car.AutomaticGearbox = !car.AutomaticGearbox;
        if (!car.AutomaticGearbox)
        {
            if (k.IsKeyPressed(Key.LeftShift) || k.IsKeyPressed(Key.RightShift) || pad.IsButtonPressed(GamepadButton.RightShoulder)) _pendingShift = 1;
            if (k.IsKeyPressed(Key.LeftCtrl) || k.IsKeyPressed(Key.RightCtrl) || pad.IsButtonPressed(GamepadButton.LeftShoulder)) _pendingShift = -1;
        }

        float steerKey = (k.IsKeyDown(Key.D) || k.IsKeyDown(Key.Right) ? 1 : 0) - (k.IsKeyDown(Key.A) || k.IsKeyDown(Key.Left) ? 1 : 0);
        // keyboard steering ramps in (3/s) and returns faster (6/s), the pad stick is used as is
        var rate = (steerKey == 0 || MathF.Sign(steerKey) != MathF.Sign(_steer) ? 6 : 3) * dt;
        var stick = pad.IsConnected ? pad.GetAxis(GamepadAxis.LeftX) : 0;
        _steer = stick != 0 ? stick : _steer + Math.Clamp(steerKey - _steer, -rate, rate);
        var keyGas = k.IsKeyDown(Key.W) || k.IsKeyDown(Key.Up) ? 1f : 0;
        var keyBrake = k.IsKeyDown(Key.S) || k.IsKeyDown(Key.Down) ? 1f : 0;
        _throttle = MathF.Max(keyGas, pad.IsConnected ? pad.GetAxis(GamepadAxis.TriggerRight) : 0);
        _brake = MathF.Max(keyBrake, pad.IsConnected ? pad.GetAxis(GamepadAxis.TriggerLeft) : 0);
        _handbrake = k.IsKeyDown(Key.Space) || pad.IsButtonDown(GamepadButton.A);
    }

    private void UpdateFly(in GameTime time)
    {
        var k = Input.Keyboard;
        var dt = time.DeltaTime;
        if (k.IsKeyPressed(Key.Space)) JumpToLine(_linePoint + 40);

        var m = Input.Mouse;
        if (m.IsButtonDown(3))
        {
            _yaw -= (m.X - _lastMouseX) * 0.004f;
            _pitch = Math.Clamp(_pitch - (m.Y - _lastMouseY) * 0.004f, -1.5f, 1.5f);
        }
        (_lastMouseX, _lastMouseY) = (m.X, m.Y);
        if (k.IsKeyDown(Key.Left)) _yaw += 1.8f * dt;
        if (k.IsKeyDown(Key.Right)) _yaw -= 1.8f * dt;
        if (k.IsKeyDown(Key.Up)) _pitch = MathF.Min(_pitch + 1.2f * dt, 1.5f);
        if (k.IsKeyDown(Key.Down)) _pitch = MathF.Max(_pitch - 1.2f * dt, -1.5f);

        var fwd = Forward();
        var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
        var move = Vector3.Zero;
        if (k.IsKeyDown(Key.W)) move += fwd;
        if (k.IsKeyDown(Key.S)) move -= fwd;
        if (k.IsKeyDown(Key.D)) move += right;
        if (k.IsKeyDown(Key.A)) move -= right;
        if (k.IsKeyDown(Key.E)) move += Vector3.UnitY;
        if (k.IsKeyDown(Key.Q)) move -= Vector3.UnitY;
        var speed = k.IsKeyDown(Key.LeftShift) ? 120f : 25f;
        if (move != Vector3.Zero) _pos += Vector3.Normalize(move) * speed * dt;
        _camLook = _pos + Forward();
        _fov = MathF.PI / 3;
    }

    private Vector3 Forward() => new(MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), MathF.Cos(_yaw) * MathF.Cos(_pitch));

    /// <summary>Body and wheel world matrices from the physics state, body pose interpolated between the last two ticks.</summary>
    private void UpdateCarMatrices(float alpha)
    {
        var car = _drive.Car;
        _carPose = Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(_prevRot, car.Orientation, alpha))
                   * Matrix4x4.CreateTranslation(Vector3.Lerp(_prevPos, car.Position, alpha));
        _carBody = _modelToBody * _carPose;
        var restY = car.Spec.WheelRadius - car.Spec.CogHeight;
        for (var i = 0; i < 4; i++)
        {
            // CarParts node order fr_l, fr_r, re_l, re_r = physics FL, FR, RL, RR; model node X/Z, physics suspension travel
            var w = car.Wheels[i];
            var node = _car.Wheels[i];
            var spinSteer = Matrix4x4.CreateRotationX(w.SpinAngle) * Matrix4x4.CreateRotationY(-w.SteerAngle);
            var flip = node with { M41 = 0, M42 = 0, M43 = 0 };
            _carWheels[i] = flip * spinSteer * Matrix4x4.CreateTranslation(node.Translation + new Vector3(0, w.LocalCenter.Y - restY, 0)) * _carBody;
        }
    }

    /// <summary>Chase camera (spring towards a point behind/above the car, looks a bit ahead) or bumper camera.</summary>
    private void UpdateDriveCamera(float dt)
    {
        var car = _drive.Car;
        var pos = _carPose.Translation; // interpolated CoG
        var fwd = Vector3.TransformNormal(Vector3.UnitZ, _carPose);
        if (_bumperCam)
        {
            var up = Vector3.TransformNormal(Vector3.UnitY, _carPose);
            _pos = pos + up * 0.15f + fwd * (car.Spec.Length / 2 + 0.05f);
            _camLook = _pos + fwd * 10;
            _fov = MathF.PI / 3;
            _camSnap = false;
            return;
        }
        var flat = Vector3.Normalize(fwd with { Y = 0 });
        var desired = pos - flat * 5.8f + Vector3.UnitY * 1.9f;
        var look = pos + flat * 4f + Vector3.UnitY * 0.6f;
        float a = _camSnap ? 1 : 1 - MathF.Exp(-6 * dt), b = _camSnap ? 1 : 1 - MathF.Exp(-12 * dt);
        _pos = Vector3.Lerp(_pos, desired, a);
        _camLook = Vector3.Lerp(_camLook, look, b);
        _fov = MathF.PI / 3 + MathF.Min(car.SpeedKmh / 180, 1) * 0.2f;
        _camSnap = false;
        // wall hits shake the camera briefly (up to 12 cm, decays in ~0.3 s)
        _shake *= MathF.Exp(-10 * dt);
        _shakeOffset = new Vector3(MathF.Sin(_simTime * 53), MathF.Sin(_simTime * 47 + 1), MathF.Sin(_simTime * 61 + 2)) * (0.12f * _shake);
    }

    public override void Render(in FrameContext ctx)
    {
        var shot = _shotState == 1 ? _capture : null;
        var aspect = shot != null ? (float)shot.Width / shot.Height
            : ctx.Viewport.Height > 0 ? (float)ctx.Viewport.Width / ctx.Viewport.Height : 16f / 9f;
        UpdateCarMatrices(shot != null ? 1 : ctx.TickAlpha);
        if (!_fly) UpdateDriveCamera(ctx.Time.DeltaTime);
        // Game data is right-handed (y up). Vulkan clip space is Y-down, Metal/GL Y-up.
        var proj = WorldRenderer.Perspective(_fov, aspect, 0.3f, Device.Backend == Penelope.BackendKind.Vulkan);
        var shake = _fly ? Vector3.Zero : _shakeOffset; // moves the view only, not the camera spring
        // --flicker: camera and world turned together by Spin (same image, different depth rounding)
        var spin = Matrix4x4.CreateRotationY(_probeView.Spin);
        var view = spin * Matrix4x4.CreateLookAt(Vector3.Transform(_pos + shake, spin), Vector3.Transform(_camLook + shake * 0.5f, spin), Vector3.UnitY);

        UpdateLights();
        Span<(StaticMesh, Matrix4x4)> casters =
        [
            (_car.Body, _carBody), (_car.Wheel, _carWheels[0]), (_car.Wheel, _carWheels[1]), (_car.Wheel, _carWheels[2]), (_car.Wheel, _carWheels[3]),
        ];
        _renderer.RenderShadows(ctx.Encoder, _pos, Vector3.Normalize(_camLook - _pos), _fov, aspect, _course.World, casters);
        var pass = _renderer.BeginScene(ctx.Encoder, view, proj, shot);
        _renderer.DrawSky(pass, _course.Sky, Matrix4x4.CreateTranslation(_pos with { Y = 0 }) * view * proj); // follows the camera
        var carView = _probe != null && _probeView.Group == 1;
        if (_probe == null || !carView) _renderer.Draw(pass, _course.World, view * proj, _pos);
        if (_probe == null || carView) _carRenderer.Draw(pass, _car.Body, _car.Wheel, _carBody, _carWheels, view * proj, _pos);
        _fxRenderer.Draw(pass, _fx, view, view * proj, _pos);
        _renderer.EndScene(ctx.Encoder, pass, shot);
        if (shot != null)
        {
            shot.Copy(ctx.Encoder);
            _shotState = 2;
        }
    }

    public override void Dispose()
    {
        _audio?.Dispose();
        _audioDevice?.Dispose();
        _course.World.Dispose();
        _course.Sky.Dispose();
        _car.Dispose();
        _carRenderer.Dispose();
        _fxRenderer.Dispose();
        _capture?.Dispose();
        _renderer.Dispose();
    }
}
