using System.Numerics;
using Kansei.Audio;
using Kansei.Core;
using Kansei.Graphics;
using Kansei.Input;
using Kansei.Physics;
using Touge.Formats;

namespace Touge;

/// <summary>
///     Course from the ISO with a drivable car (<see cref="Car"/>, AE86 by default) and a free-fly camera (F1).
///     Drive: W/S or ↑/↓ throttle/brake (automatic: hold S at standstill to reverse), A/D or ←/→ steer, Space handbrake, T auto/manual, Shift/Ctrl gear up/down (manual),
///     R reset onto the driving line, B reset in the other direction (downhill/uphill), C chase/bumper camera, F2 graphics quality (MSAA, bloom, shadows) on/off,
///     F4 HUD on/off, N minimap mode (<see cref="Hud"/>), 1/2 previous/next car and 3 next paint at standstill. Pad: left stick, triggers, A handbrake, bumpers shift.
///     Fly: WASD, Q/E down/up, right mouse or arrow keys look, Shift fast, Space jump along the driving line. Esc quit.
///     <paramref name="orbit"/> (degrees, 0 = front, 90 = left, 180 = rear) puts the fly camera around the car;
///     <paramref name="autodrive"/> lets the line pilot drive that many seconds before the first frame (for --shot);
///     <paramref name="bench"/> lets it drive in real time with the chase camera for that many seconds, then logs frame times and quits;
///     <paramref name="drift"/> makes the pilot throw in a scripted handbrake drift every 7 s (<see cref="Drive.ForceDrift"/>).
///     Tyre smoke, skid marks and sparks come from the car's wheel/wall state every tick (<see cref="TickEffects"/>), in rain
///     also tyre spray; falling rain is drawn around the camera.
///     Fog: per time of day (<see cref="AtmosphereFor"/>) with the original's fog colour (CRS_INFO) and height fog over the course's altitude range.
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
    private OverlayRenderer _overlayRenderer = null!;
    private Hud _hud = null!;
    /// <summary>--hud: "north", "overview" (minimap mode at start) or "off"; default rotating map, HUD on.</summary>
    public string? HudMode { get; init; }
    /// <summary>--reverse: start in the reverse (uphill) direction; B switches direction at runtime (<see cref="Drive.Reverse"/>).</summary>
    public bool Reverse { get; init; }
    /// <summary>--car / --paint: HCAR name (<see cref="CarPaint.Cars"/>) and CAR_ENV colour at start; 1/2 cycle the car, 3 the paint (at standstill).</summary>
    public string Car { get; init; } = "AE86T";
    public int Paint { get; init; }
    /// <summary>--cars: PNG path of a contact sheet of all cars (orbit shots), written before quitting.</summary>
    public string? ContactSheet { get; init; }
    private string _carName = "";
    private int _paint, _sheetCar;
    private byte[]? _sheet;
    /// <summary>--sun: free camera at the start point looking towards the sun (glare check).</summary>
    public bool LookAtSun { get; init; }
    private readonly Effects _fx = new();
    private readonly Random _rng = new(3);
    private readonly float[] _smokeDebt = new float[4], _sprayDebt = new float[4];
    private float _simTime, _shake;
    private Vector3 _prevVelocity, _shakeOffset, _lastCamPos, _camVelocity;
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
        SetupFog(_renderer.Atmosphere);
        if (!courseTime.EndsWith("_NIT") && _course.SunDirection is { } sun) _renderer.Atmosphere.SunDirection = sun; // the original's key light
        _drive = new Drive(iso, courseTime, Reverse, CarSpecs.All[Car]);
        Console.WriteLine($"[Touge] {courseTime} geladen in {sw.ElapsedMilliseconds} ms, {_course.World.Batches.Count} Batches, {_drive.Ground.Walls.Length} Wandsegmente");
        _carRenderer = new CarRenderer(_renderer);
        _fxRenderer = new EffectsRenderer(_renderer);
        _overlayRenderer = new OverlayRenderer(Device);
        _hud = new Hud(_course.Road, _drive.Line)
        {
            Visible = HudMode != "off" && flicker == null,
            Mode = HudMode switch { "north" => Hud.MapMode.NorthUp, "overview" => Hud.MapMode.Overview, _ => Hud.MapMode.Rotating },
        };
        SetupLights(courseTime.EndsWith("_NIT"), courseTime.EndsWith("_RIN"));
        (_carName, _paint) = (Car, Paint);
        LoadCarModel(iso);

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
        if (LookAtSun)
        {
            var s = Vector3.Normalize(_renderer.Atmosphere.SunDirection);
            (_fly, _yaw, _pitch) = (true, MathF.Atan2(s.X, s.Z), MathF.Asin(s.Y) - 0.3f);
            _pos += Vector3.Normalize(s with { Y = 0 }) * -7 + Vector3.UnitY * 0.3f; // the parked car in front, against the light
        }
        if (shotPath != null) (_capture, _shotState) = (new FrameCapture(Device, 1280, 720), 1);
        else if (ContactSheet != null) (_capture, _sheet, _fly) = (new FrameCapture(Device, 1280, 720), new byte[SheetW * SheetH * 4], true);
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
            _audio = new GameAudio(iso, courseTime, _audioDevice, _carName);
            _audio.PlayTrack(background: true);
        }
    }

    /// <summary>
    ///     Model of <see cref="_carName"/>/<see cref="_paint"/>, and model space → physics body space (origin CoG): the
    ///     model's wheel centres onto the physics wheel centres at rest.
    /// </summary>
    private void LoadCarModel(Iso9660 iso)
    {
        _car = CarModel.Load(iso, _carName, _paint, _renderer);
        var spec = _drive.Car.Spec;
        var modelWheels = Vector3.Zero;
        foreach (var w in _car.Wheels) modelWheels += w.Translation / 4;
        _modelToBody = Matrix4x4.CreateTranslation(new Vector3(0, spec.WheelRadius - spec.CogHeight, spec.Wheelbase * (0.5f - spec.FrontWeight)) - modelWheels);
    }

    /// <summary>
    ///     Car <paramref name="car"/> (index in <see cref="CarPaint.Cars"/>) with <paramref name="paint"/>: new model, and for a
    ///     different car its physics spec (back on the line where the old one stood) and engine sound.
    ///     ponytail: the old car's textures stay in the renderer (a few MB per change); free them if cars are swapped a lot.
    /// </summary>
    private void SwitchCar(int car, int paint)
    {
        var name = CarPaint.Cars[(car % CarPaint.Cars.Length + CarPaint.Cars.Length) % CarPaint.Cars.Length];
        using var iso = new Iso9660(isoPath);
        Device.WaitIdle(); // the last frame may still read the old meshes
        _car.Dispose();
        if (name != _carName)
        {
            _drive.ChangeCar(CarSpecs.All[name]);
            _audio?.SetCar(name);
            SyncPose();
        }
        (_carName, _paint) = (name, paint);
        LoadCarModel(iso);
        Console.WriteLine($"\n[Touge] Auto {_carName} (Lack {_paint + 1}/{_car.Paints}), {_drive.Car.Spec.Mass:F0} kg, Antrieb vorn {_drive.Car.Spec.DriveFront:P0}, Motor {GameAudio.Engines[Array.IndexOf(CarPaint.Cars, _carName)].Bank}");
    }

    private const int SheetCols = 4, SheetTile = 4, SheetW = SheetCols * 1280 / SheetTile, SheetH = 8 * 720 / SheetTile; // 32 cars, 320×180 each

    /// <summary>--cars: renders each car from the orbit (35°), pastes the 4× box-filtered frame into its tile, writes the sheet after the last.</summary>
    private void ContactSheetStep()
    {
        if (_shotState == 1) return;
        if (_shotState == 2)
        {
            var px = _capture!.ReadRgba();
            int x0 = _sheetCar % SheetCols * 1280 / SheetTile, y0 = _sheetCar / SheetCols * 720 / SheetTile;
            for (var y = 0; y < 720 / SheetTile; y++)
            for (var x = 0; x < 1280 / SheetTile; x++)
            for (var c = 0; c < 4; c++)
            {
                var sum = 0;
                for (var dy = 0; dy < SheetTile; dy++)
                for (var dx = 0; dx < SheetTile; dx++) sum += px[((y * SheetTile + dy) * 1280 + x * SheetTile + dx) * 4 + c];
                _sheet![((y0 + y) * SheetW + x0 + x) * 4 + c] = (byte)(sum / (SheetTile * SheetTile));
            }
            if (++_sheetCar == CarPaint.Cars.Length)
            {
                Png.Write(ContactSheet!, SheetW, SheetH, _sheet!);
                Console.WriteLine($"[Touge] Kontaktbogen -> {ContactSheet}");
                Window.ShouldClose = true;
                return;
            }
        }
        SwitchCar(_sheetCar, 0);
        UpdateCarMatrices(1);
        OrbitCar(35 * MathF.PI / 180);
        _camLook = _pos + Forward();
        _shotState = 1;
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
        var wet = _renderer.Atmosphere.Wetness;
        var spray = wet * Math.Clamp((speed - 4) / 22, 0, 1); // water thrown up by the tyres, 0..1 by speed
        for (var i = 0; i < 4; i++)
        {
            var w = car.Wheels[i];
            var contact = Vector3.Transform(w.LocalCenter, pose) - up * spec.WheelRadius;
            var tan = MathF.Tan(w.SlipAngle);
            var slide = w.Contact ? speed * MathF.Sqrt(w.SlipRatio * w.SlipRatio + tan * tan) : 0; // m/s
            var load = Math.Clamp(w.Load / spec.NominalLoad, 0, 1.5f);
            // wet tyres barely mark the road or smoke
            _fx.Skid(i, contact, side, up, Math.Clamp((slide - 2f) / 3, 0, 1) * Math.Clamp(load * 4, 0, 1) * (1 - 0.7f * wet));
            var smoke = Math.Clamp((slide - 4.5f) / 7, 0, 1) * load * (1 - 0.8f * wet);
            _smokeDebt[i] += smoke * 45 * dt; // puffs per second per wheel at full slide
            for (; _smokeDebt[i] >= 1; _smokeDebt[i]--)
            {
                var jitter = new Vector3(_rng.NextSingle() - 0.5f, _rng.NextSingle() * 0.5f, _rng.NextSingle() - 0.5f);
                _fx.EmitSmoke(contact + up * 0.2f + jitter * 0.3f, car.Velocity * 0.12f + jitter * 1.5f + up * 0.5f, 0.3f, 0.12f + 0.25f * smoke);
            }
            if (!w.Contact || i < 2) continue; // rear wheels: the fronts spray into the rears
            _sprayDebt[i] += spray * 30 * dt;
            for (; _sprayDebt[i] >= 1; _sprayDebt[i]--)
            {
                // thrown up and back off the tread (the car's velocity carried partly), then arcs down (Effects.EmitSpray)
                var jitter = new Vector3(_rng.NextSingle() - 0.5f, _rng.NextSingle(), _rng.NextSingle() - 0.5f);
                _fx.EmitSpray(contact + up * 0.15f + jitter * 0.2f, car.Velocity * 0.45f + up * (1.2f + 2f * jitter.Y) + jitter * 1.5f, 0.22f, 0.2f * spray);
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
        if (_sheet != null)
        {
            ContactSheetStep();
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
        if (k.IsKeyPressed(Key.F4)) _hud.Visible = !_hud.Visible;
        if (k.IsKeyPressed(Key.N)) _hud.NextMode();
        if (k.IsKeyPressed(Key.F2))
        {
            _renderer.HighQuality = !_renderer.HighQuality;
            Console.WriteLine($"\n[Touge] Grafik: {(_renderer.HighQuality ? "hoch (4× MSAA, Bloom, Schatten)" : "niedrig (ohne MSAA/Bloom/Schatten)")}");
        }
        if (bench is { } benchSeconds && Bench(time, benchSeconds)) return;
        if (_audio != null && k.IsKeyPressed(Key.M)) _audio.NextTrack();
        if (_audio != null && k.IsKeyPressed(Key.F3)) _audio.MusicOn = !_audio.MusicOn;
        if (_drive.Car.SpeedKmh < 3) // car/paint change only at standstill
        {
            var id = Array.IndexOf(CarPaint.Cars, _carName);
            if (k.IsKeyPressed(Key.D1)) SwitchCar(id - 1, 0);
            if (k.IsKeyPressed(Key.D2)) SwitchCar(id + 1, 0);
            if (k.IsKeyPressed(Key.D3)) SwitchCar(id, (_paint + 1) % _car.Paints);
        }
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
            Console.Write($"\r[Touge] {_carName,-5} {c.SpeedKmh,4:F0} km/h  Gang {Drive.Gear(c),-2}  {c.Rpm,5:F0} rpm  Schräglauf {c.SlipAngle * 180 / MathF.PI,6:F1}°  {_frames / (time.TotalTime - _statusTime),4:F0} fps   ");
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
    ///     the road: lamp heads; the game itself only brightens the car near them). Rain: headlights on (dimmer, it is day). Intensities tuned by eye.
    /// </summary>
    private void SetupLights(bool night, bool rain)
    {
        var l = _renderer.Lights;
        l.HeadlightColor = night ? new Vector3(1f, 0.92f, 0.8f) * 700 : rain ? new Vector3(1f, 0.92f, 0.8f) * 250 : Vector3.Zero;
        l.StreetLights = _course.Lights;
        l.StreetLightColor = night ? new Vector3(1f, 0.62f, 0.3f) * 50 : Vector3.Zero;
    }

    /// <summary>Per frame: headlights and rear lamps from the car pose, brake lamps, env maps of the road point nearest to the car.</summary>
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
        // rear lamps (placed by eye on the AE86 mesh): dim red with the headlights on, bright when braking
        for (var i = 0; i < 2; i++) l.TailLightPosition[i] = Vector3.Transform(new Vector3(i == 0 ? 0.5f : -0.5f, 0.7f, -2.15f), _carBody);
        l.TailLightColor = new Vector3(1f, 0.08f, 0.03f) * ((l.HeadlightColor != Vector3.Zero ? 0.08f : 0) + 0.8f * l.Brake);
        l.Car = _carBody;
        if (_course.Env is { } env)
        {
            var e = env[_course.NearestRoadPoint(_carBody.Translation)];
            _renderer.SetEnvironment(e[0], e[1], e[2], e[3]);
        }
    }

    /// <summary>
    ///     Sky, fog, light and grading per time of day (_DAY, _NIT, _RIN), tuned by eye. Fog start/end and tint are
    ///     replaced by the course's own (CRS_INFO, <see cref="SetupFog"/>), the day sun direction by its key light.
    ///     Rain is overcast: no sun shadows, soft ambient, flat contrast.
    /// </summary>
    private static Atmosphere AtmosphereFor(string courseTime) => courseTime[(courseTime.LastIndexOf('_') + 1)..] switch
    {
        "NIT" => new Atmosphere
        {
            Zenith = new(0.004f, 0.006f, 0.016f), Horizon = new(0.018f, 0.022f, 0.035f),
            SunDirection = Vector3.Normalize(new Vector3(-0.5f, 0.45f, 0.6f)), SunDisk = new(1.2f, 1.3f, 1.5f), // moon
            SunIntensity = 0.12f, Ambient = new(0.025f, 0.03f, 0.045f), BakedKeep = 0.88f, BakedSun = 0.15f, EnvStrength = 2f, ContactShadow = 0.6f,
            FogColor = new(0.010f, 0.014f, 0.026f), FogSun = Vector3.Zero, FogStart = 10, FogEnd = 4000, HeightFogDensity = 0.0012f, LightGlow = 0.00012f,
            Exposure = 3.2f, BloomThreshold = 0.5f, BloomStrength = 1.0f, Tint = new(0.92f, 0.97f, 1.1f), Saturation = 0.9f, Vignette = 0.35f,
        },
        "RIN" => new Atmosphere
        {
            Zenith = new(0.17f, 0.18f, 0.20f), Horizon = new(0.27f, 0.28f, 0.30f), SunDisk = Vector3.Zero, Shadows = false,
            SunIntensity = 0.15f, Ambient = new(0.30f, 0.32f, 0.35f), BakedKeep = 0.9f, BakedSun = 0.1f, Wetness = 1f, ContactShadow = 0.7f,
            SunDirection = Vector3.Normalize(new Vector3(0.2f, 1f, 0.15f)), EnvStrength = 4f,
            FogColor = new(0.22f, 0.23f, 0.25f), FogSun = Vector3.Zero, FogStart = 15, FogEnd = 1300, HeightFogDensity = 0.0025f, LightGlow = 0.0004f,
            Exposure = 1.5f, BloomThreshold = 1.6f, BloomStrength = 0.4f, Tint = new(0.96f, 0.99f, 1.03f), Saturation = 0.9f, Vignette = 0.3f,
        },
        // clear day: a warm sun (direction from the course, this one is the fallback) against cool sky shade and a warm
        // ground bounce; less of the baked light kept
        // in shade, more re-lit by the sun (flat road in sun ≈ original brightness, walls facing the sun brighter, the
        // others and the car's shadow side darker), sun glints, contact shadow under the car, blue aerial haze, filmic contrast
        _ => new Atmosphere
        {
            SunDirection = Vector3.Normalize(new Vector3(0.48f, 0.53f, 0.36f)), SunColor = new(1.1f, 0.97f, 0.8f),
            ShadeSky = new(0.86f, 0.98f, 1.2f), ShadeGround = new(1.05f, 1f, 0.82f),
            BakedKeep = 0.38f, BakedSun = 1.15f, SunIntensity = 1.5f, Ambient = new(0.22f, 0.25f, 0.30f),
            Specular = 0.6f, ContactShadow = 0.6f,
            FogEnd = 6000, Exposure = 1.35f, Contrast = 1.12f, Saturation = 1.1f,
        },
    };

    /// <summary>
    ///     Course-dependent fog: the original's linear start/end (CRS_INFO; negative starts = haze at the camera clamped
    ///     to 0), its tint (day/rain; its night fog is black, ours stays dark blue) at our brightness — its values are for unlit PS2 output (USUI0 rain is as light as day, which turns puddles white) — and the
    ///     height fog over the driving line's altitude range (densest at its lowest point, ×1/e every third of the range).
    /// </summary>
    private void SetupFog(Atmosphere a)
    {
        if (_course.FogColour is { } c && c != Vector3.Zero)
        {
            var tint = new Vector3(MathF.Pow(c.X, 2.2f), MathF.Pow(c.Y, 2.2f), MathF.Pow(c.Z, 2.2f));
            a.FogColor = tint * (Luminance(a.FogColor) / Luminance(tint));
        }
        // the PS2 blends fog into gamma-space output; in linear light its haze at the camera (negative start) is a milky veil
        if (_course.Fog is { } f) (a.FogStart, a.FogEnd) = (MathF.Max(f.Start, 0), f.End);
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var p in _course.DrivingLine) (lo, hi) = (MathF.Min(lo, p.Y), MathF.Max(hi, p.Y));
        a.HeightFogBase = lo;
        a.HeightFogScale = MathF.Max((hi - lo) / 3, 30);
    }

    private static float Luminance(Vector3 c) => Vector3.Dot(c, new Vector3(0.2126f, 0.7152f, 0.0722f));

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
        if (k.IsKeyPressed(Key.B))
        {
            using (var iso = new Iso9660(isoPath)) _drive.SetDirection(iso, !_drive.Reverse);
            _drive.ResetNearest();
            _hud = new Hud(_course.Road, _drive.Line) { Visible = _hud.Visible, Mode = _hud.Mode };
            Console.WriteLine($"\n[Touge] Richtung: {(_drive.Reverse ? "rückwärts (CRS_COLI _1, DRV _O)" : "vorwärts (_0, _I)")}");
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
        var view = Matrix4x4.CreateLookAt(_pos + shake, _camLook + shake * 0.5f, Vector3.UnitY);
        var skyView = view; // analytic sky + sun
        if (_probeView.Spin != 0)
        {
            // --flicker: camera and world turned together (same image, different depth rounding)
            var spin = Matrix4x4.CreateRotationY(_probeView.Spin);
            view = spin * Matrix4x4.CreateLookAt(Vector3.Transform(_pos, spin), Vector3.Transform(_camLook, spin), Vector3.UnitY);
        }

        UpdateLights();
        Span<(StaticMesh, Matrix4x4)> casters =
        [
            (_car.Body, _carBody), (_car.Wheel, _carWheels[0]), (_car.Wheel, _carWheels[1]), (_car.Wheel, _carWheels[2]), (_car.Wheel, _carWheels[3]),
        ];
        _renderer.RenderShadows(ctx.Encoder, _pos, Vector3.Normalize(_camLook - _pos), _fov, aspect, _course.World, casters);
        var pass = _renderer.BeginScene(ctx.Encoder, skyView, proj, shot);
        _renderer.Time = _simTime;
        _renderer.DrawSky(pass, _course.Sky, Matrix4x4.CreateTranslation(_pos with { Y = 0 }) * view * proj, _pos); // follows the camera
        var carView = _probe != null && _probeView.Group == 1;
        if (_probe == null || !carView) _renderer.Draw(pass, _course.World, view * proj, _pos);
        if (_probe == null || carView) _carRenderer.Draw(pass, _car.Body, _car.Wheel, _carBody, _carWheels, view * proj, _pos);
        _fxRenderer.Draw(pass, _fx, view, view * proj, _pos);
        // camera velocity stretches the rain streaks; a shot has no previous frame, the chase camera moves with the car
        var frameDt = ctx.Time.DeltaTime;
        _camVelocity = shot != null ? (_fly ? Vector3.Zero : _drive.Car.Velocity)
            : frameDt > 0 ? Vector3.Lerp(_camVelocity, (_pos - _lastCamPos) / frameDt, 0.3f) : _camVelocity;
        _lastCamPos = _pos;
        var heightPx = shot?.Height ?? Device.SwapchainHeight;
        _fxRenderer.DrawRain(pass, view * proj, _pos, _camVelocity, _simTime, 2 * MathF.Tan(_fov / 2) / heightPx);
        _renderer.EndScene(ctx.Encoder, pass, shot);
        if (_hud.Visible)
        {
            var car = _drive.Car;
            var (w, h) = shot != null ? (shot.Width, shot.Height) : (Device.SwapchainWidth, Device.SwapchainHeight);
            var overlay = _hud.Build(w, h, _carPose.Translation, Vector3.TransformNormal(Vector3.UnitZ, _carPose), car.SpeedKmh, car.Gear,
                car.AutomaticGearbox, _drive.Pilot.Track(car.Position).Along / _drive.Pilot.Length);
            _overlayRenderer.Draw(ctx.Encoder, overlay, shot?.View ?? Device.CurrentSwapchainView, w, h);
        }
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
        _overlayRenderer.Dispose();
        _capture?.Dispose();
        _renderer.Dispose();
    }
}
