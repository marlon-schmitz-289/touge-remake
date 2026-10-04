using System.Numerics;
using Kansei.Audio;
using Kansei.Core;
using Kansei.Graphics;
using Kansei.Input;
using Kansei.Physics;
using Touge.Formats;
using Touge.Ui;

namespace Touge;

/// <summary>
///     Course from the ISO with a drivable car (<see cref="Car"/>, AE86 by default) and a free-fly camera (F1).
///     Drive: W/S or ↑/↓ throttle/brake (automatic: hold S at standstill to reverse), A/D or ←/→ steer, Space handbrake, T auto/manual, Shift/Ctrl gear up/down (manual),
///     R (pad Y) reset onto the driving line, B reset in the other direction (downhill/uphill), C chase/bumper camera, F2 graphics quality (MSAA, bloom, shadows) on/off,
///     F4 HUD on/off, N minimap mode (<see cref="Hud"/>), 1/2 previous/next car and 3 next paint at standstill. Pad: left stick, triggers, A handbrake, bumpers shift.
///     Fly: WASD, Q/E down/up, right mouse or arrow keys look, Shift fast, Space jump along the driving line. Esc/Start pause menu (<see cref="Menu"/>; without CLI test arguments the game starts in the title menu).
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
    private TextRenderer _textRenderer = null!;
    private readonly Overlay _overlay = new();
    private Hud _hud = null!;
    private string _courseTime = courseTime;
    /// <summary>Menus at start (no CLI test arguments): settings from/to the app-data JSON, title screen first.</summary>
    public bool UseMenus { get; init; }
    /// <summary>--shot-size WxH: size of the --shot frame (default 1280×720).</summary>
    public (int W, int H) ShotSize { get; init; } = (1280, 720);
    /// <summary>--menu: front-end step (boot, logo, disclaimer, title, mode) or menu screen (course … options, <see cref="Menu.Screen"/>) to open at start, e.g. for --shot.</summary>
    public string? StartMenu { get; init; }
    private Settings _settings = null!;
    private bool _persist, _inRace;
    private Catalog? _catalog;
    private Menu? _menu;
    /// <summary>Boot cards, title and main menu (started plainly or with --menu boot|logo|disclaimer|title|mode).</summary>
    private FrontEnd? _front;
    private MenuAudio? _menuAudio;
    private readonly MenuKeys _frontKeys = new();
    /// <summary>What the music stream plays: null silence, <see cref="Menu.RaceMusic"/>, or a BGM.AFS track of the menus ("" = decide again).</summary>
    private string? _music = "";
    /// <summary>Front end or a menu holds the game (no physics); the intro lets go at GO, the finish banner lets the pilot drive on.</summary>
    private bool Frozen => _front is { Active: true } || _menu is { Freezes: true };
    /// <summary>The run's finish was handed to the menus (once per run).</summary>
    private bool _finished;
    private float _flyS, _menuTime;
    /// <summary>
    ///     --flow &lt;dir&gt;: scripted pass through the whole front end in the window (boot → … → course/car selection → load →
    ///     telop/countdown → race (pilot, 16× time) → pause → finish → result → records → options) with the keys of
    ///     <see cref="FlowScript"/>, a PNG per step into the directory; the settings file is not touched.
    /// </summary>
    public string? Flow { get; init; }
    private int _flowStep, _flowShots;
    private float _flowT;
    private bool _flowShotTaken;
    private string? _flowShot;
    /// <summary>--hud: "north", "overview" (minimap mode at start) or "off"; default rotating map, HUD on.</summary>
    public string? HudMode { get; init; }
    /// <summary>--reverse: start in the reverse (uphill) direction; B switches direction at runtime (<see cref="Drive.Reverse"/>).</summary>
    public bool Reverse { get; init; }
    /// <summary>--car / --paint: HCAR name (<see cref="CarPaint.Cars"/>) and CAR_ENV colour at start; 1/2 cycle the car, 3 the paint (at standstill).</summary>
    public string Car { get; init; } = "AE86T";
    public int Paint { get; init; }
    /// <summary>--livery: stickers/plates of the car (<see cref="Touge.Formats.Livery"/>), default the anime character's car.</summary>
    public Livery Livery { get; init; } = Livery.Rival;
    /// <summary>--cars: PNG path of a contact sheet of all cars (orbit shots), written before quitting.</summary>
    public string? ContactSheet { get; init; }
    private string _carName = "";
    private int _paint, _sheetCar;
    private byte[]? _sheet;
    /// <summary>--sun: free camera at the start point looking towards the sun (glare check).</summary>
    public bool LookAtSun { get; init; }
    private Effects _fx = new();
    private readonly Random _rng = new(3);
    private readonly float[] _smokeDebt = new float[4], _sprayDebt = new float[4];
    private float _simTime, _shake;
    private Vector3 _prevVelocity, _shakeOffset, _lastCamPos, _camVelocity;
    private CarModel _car = null!;
    private Livery _carLivery;
    /// <summary>Best total of this course and direction when the run started (result sheet).</summary>
    private float? _previousBest;
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
        _overlay.Font = new SdfFont(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Rajdhani-Bold.ttf")),
            string.Concat(Enumerable.Range(32, 95).Select(c => (char)c)) + "°");
        _overlayRenderer = new OverlayRenderer(Device);
        _textRenderer = new TextRenderer(Device, _overlay.Font);
        if (UseMenus)
        {
            _settings = Settings.Load();
            _persist = true;
        }
        else
            _settings = new Settings
            {
                HighQuality = highQuality, HudOn = HudMode != "off" && flicker == null, Car = Car, Paint = Paint, Reverse = Reverse, Course = _courseTime,
                MapMode = HudMode switch { "north" => Hud.MapMode.NorthUp, "overview" => Hud.MapMode.Overview, _ => Hud.MapMode.Rotating }, Livery = Livery,
            };
        if (flicker == null && ContactSheet == null)
        {
            _catalog = new Catalog(iso);
            _menu = new Menu(_catalog, _settings);
            if (_persist && !_catalog.Courses.Any(c => _settings.Course == $"{c.Id}_DAY" || _settings.Course == $"{c.Id}_NIT" || _settings.Course == $"{c.Id}_RIN"))
                _settings.Course = "AKINA_DAY";
            _menu.Sound = n => _menuAudio?.Play(n);
            FrontEnd.Step? step = Flow != null ? FrontEnd.Step.Boot : StartMenu switch
            {
                null => UseMenus ? FrontEnd.Step.Boot : null, "boot" => FrontEnd.Step.Boot, "logo" => FrontEnd.Step.Logo,
                "disclaimer" => FrontEnd.Step.Disclaimer, "title" => FrontEnd.Step.Title, "mode" or "modes" or "quit" => FrontEnd.Step.Modes, _ => null,
            };
            if (step is { } first)
            {
                _front = new FrontEnd { SaveFound = File.Exists(Settings.FilePath), Sound = n => _menuAudio?.Play(n) };
                _front.Open(first);
                if (StartMenu == "quit") _front.AskQuit();
                if (shotPath != null) _front.Settle();
            }
        }
        _bumperCam = _settings.BumperCam;
        // the front end shows Akina at night behind the title, like the original's photo; course select loads the choice
        LoadCourse(iso, _front != null && (_persist || Flow != null) ? "AKINA_NIT" : _persist ? _settings.Course : _courseTime, _settings.Reverse, _settings.Car, _settings.Paint, startPoint);
        _drive.ForceDrift = drift;
        if (autodrive is { } seconds)
        {
            _drive.AutoDrive(seconds, () =>
            {
                TickEffects(Drive.Dt);
                _hud.Tick(_drive.Car, Drive.Dt);
            });
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
        if (_front != null) _inRace = false;
        else if (_menu != null && StartMenu != null)
        {
            var screen = StartMenu == "settings" ? Menu.Screen.Options
                : Enum.TryParse<Menu.Screen>(StartMenu, true, out var s) && s is not (Menu.Screen.None or Menu.Screen.Loading or Menu.Screen.Finish or Menu.Screen.Result) ? s
                : throw new ArgumentException($"--menu {StartMenu}: unbekannt");
            OpenMenu(screen);
            _inRace = screen is Menu.Screen.Pause or Menu.Screen.Intro;
            if (shotPath != null) _menu.Settle();
        }
        else _inRace = true;
        if (Flow != null)
        {
            Directory.CreateDirectory(Flow);
            _capture = new FrameCapture(Device, ShotSize.W, ShotSize.H); // and sound as usual (below)
        }
        if (shotPath != null) (_capture, _shotState) = (new FrameCapture(Device, ShotSize.W, ShotSize.H), 1);
        else if (ContactSheet != null) (_capture, _sheet, _fly) = (new FrameCapture(Device, 1280, 720), new byte[SheetW * SheetH * 4], true);
        else if (flicker != null)
        {
            var models = Afs.FromBytes(iso.ReadFile("CDVD/DATA/MODEL/COURSE.AFS"), iso.ReadFile("CDVD/DATA/MODEL/COURSE.TBL"));
            var (corners, batches) = CourseLoader.Flatten(CourseLoader.Meshes(models.Read(models.Find(_courseTime + ".PAC")!.Value), false));
            var spots = FlickerProbe.Spots([.. corners.Select(v => v.Position)], [.. batches.Select(b => b.First)]);
            (_capture, _probe) = (new FrameCapture(Device, 1280, 720), new FlickerProbe(flicker, _courseTime, _course.DrivingLine, startPoint, spots));
        }
        else
        {
            _audioDevice = new AudioDevice { Music = _settings.MusicVolume };
            if (_menu != null) _menuAudio = new MenuAudio(iso, _audioDevice) { Volume = _settings.SoundVolume, Clock = () => _menuTime };
            StartAudio(iso);
        }
    }

    /// <summary>
    ///     (Re)loads everything course-bound: world renderer with course, sky, lights and fog, driving physics with
    ///     the car on line point <paramref name="at"/>, car renderers, HUD, effects and (with sound) the course's music.
    ///     A course change replaces the whole <see cref="WorldRenderer"/>, so its textures go with it.
    /// </summary>
    private void LoadCourse(Iso9660 iso, string courseTime, bool reverse, string car, int paint, int at = 0)
    {
        if (_renderer is not null)
        {
            Device.WaitIdle();
            _audio?.Dispose();
            _course.World.Dispose();
            _course.Sky.Dispose();
            _car.Dispose();
            _carRenderer.Dispose();
            _fxRenderer.Dispose();
            _renderer.Dispose();
        }
        _courseTime = courseTime;
        _renderer = new WorldRenderer(Device) { Atmosphere = AtmosphereFor(courseTime), HighQuality = _settings.HighQuality };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _course = CourseLoader.Load(iso, courseTime, _renderer, reverse);
        SetupFog(_renderer.Atmosphere);
        if (!courseTime.EndsWith("_NIT") && _course.SunDirection is { } sun) _renderer.Atmosphere.SunDirection = sun; // the original's key light
        _drive = new Drive(iso, courseTime, reverse, CarSpecs.All[car]);
        Console.WriteLine($"[Touge] {courseTime} geladen in {sw.ElapsedMilliseconds} ms, {_course.World.Batches.Count} Batches, {_drive.Ground.Walls.Length} Wandsegmente");
        _carRenderer = new CarRenderer(_renderer);
        _fxRenderer = new EffectsRenderer(_renderer);
        _fx = new Effects();
        SetupLights(courseTime.EndsWith("_NIT"), courseTime.EndsWith("_RIN"));
        (_carName, _paint) = (car, paint);
        LoadCarModel(iso);
        _drive.ResetTo(at);
        _hud = NewHud();
        SyncPose();
        if (_audioDevice != null) StartAudio(iso);
    }

    private void StartAudio(Iso9660 iso)
    {
        _audio = new GameAudio(iso, _courseTime, _audioDevice!, _carName);
        if (_persist) _audioDevice!.Music = _settings.MusicVolume; // GameAudio sets its own default
        _music = ""; // SyncMusic starts the race or menu music
    }

    /// <summary>HUD for the loaded line, with the stored best run of this course and direction; a new record is kept (and saved with the menus).</summary>
    private Hud NewHud()
    {
        var key = Settings.BestKey(_courseTime[.._courseTime.LastIndexOf('_')], _drive.Reverse);
        _previousBest = _settings.Best.GetValueOrDefault(key)?[^1];
        var hud = new Hud(_course.Road, _drive.Line, _drive.Pilot, _settings.Best.GetValueOrDefault(key), _drive.Start)
        {
            Visible = _settings.HudOn, Mode = _settings.MapMode, Night = _courseTime.EndsWith("_NIT"),
        };
        hud.Timer.Record += best =>
        {
            _settings.Best[key] = best;
            if (_persist) _settings.Save();
        };
        return hud;
    }

    /// <summary>
    ///     Model of <see cref="_carName"/>/<see cref="_paint"/>, and model space → physics body space (origin CoG): the
    ///     model's wheel centres onto the physics wheel centres at rest.
    /// </summary>
    private void LoadCarModel(Iso9660 iso)
    {
        (_car, _carLivery) = (CarModel.Load(iso, _carName, _paint, _renderer, _settings.Livery), _settings.Livery);
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
        Console.WriteLine($"\n[Touge] Auto {_carName} (Lack {_paint + 1}/{_car.Paints}), {_drive.Car.Spec.Mass:F0} kg, Antrieb vorn {_drive.Car.Spec.DriveFront:P0}, Motor {GameAudio.Engines[Array.IndexOf(CarPaint.Cars, _carName)]}");
    }

    private const int SheetCols = 4, SheetTile = 4, SheetW = SheetCols * 1280 / SheetTile, SheetH = 8 * 720 / SheetTile; // 32 cars, 320×180 each

    /// <summary>--cars: renders each car from the orbit (--orbit, default 35°), pastes the 4× box-filtered frame into its tile, writes the sheet after the last.</summary>
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
        OrbitCar((orbit ?? 35) * MathF.PI / 180);
        _camLook = _pos + Forward();
        _shotState = 1;
    }

    private void SyncPose() => (_prevPos, _prevRot, _camSnap) = (_drive.Car.Position, _drive.Car.Orientation, true);

    public override void Tick(float dt)
    {
        if (_probe != null || Frozen) return; // frozen scene / menus pause the game
        var car = _drive.Car;
        (_prevPos, _prevRot) = (car.Position, car.Orientation);
        // past the finish of a front-end run the game takes the car (auto-run to a stop before the end barrier);
        // the pilot drives for --autodrive/--bench/--flow
        var input = _front != null && _hud.Timer.Phase == LapTimer.State.Finished ? _drive.Coast()
            : autodrive != null || bench != null || Flow != null ? _drive.PilotInput(_simTime)
            : _fly ? new VehicleInput(0, 0, 0, true)
            : new VehicleInput(_throttle, _brake, _steer, _handbrake, _pendingShift);
        _pendingShift = 0;
        _brakeLight = input.Brake;
        car.Step(input, _drive.Ground, dt);
        _simTime += dt;
        TickEffects(dt);
        _hud.Tick(car, dt);
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
            var path = Flow != null ? Path.Combine(Flow, $"om_step2_flow_{++_flowShots:00}_{_flowShot}.png") : shotPath!;
            Png.Write(path, _capture!.Width, _capture.Height, _capture.ReadRgba());
            Console.WriteLine($"\n[Touge] Screenshot -> {path}");
            if (Flow == null)
            {
                Window.ShouldClose = true;
                return;
            }
            _shotState = 0;
        }
        var k = Input.Keyboard;
        var dt = time.DeltaTime;
        _menuTime += dt;
        SyncAudio();
        var keys = Flow != null ? FlowKeys(dt) : _frontKeys.Read(Input, dt);
        if (_front is { Active: true })
        {
            UpdateFrontEnd(keys, dt);
            return;
        }
        if (_menu is { Current: not Menu.Screen.None })
        {
            UpdateMenu(keys, dt);
            if (_menu.Current != Menu.Screen.Intro || _menu.Freezes) return; // after GO the intro only draws
        }
        if (_front != null && !_finished && _hud.Timer.Phase == LapTimer.State.Finished)
        {
            // the run is over: finish banner, then the result sheet (only in the front-end flow)
            _finished = true;
            var t = _hud.Timer;
            _menu!.Finish(new Menu.Run(t.Time, (float[])t.Splits.Clone(), [.. Enumerable.Range(0, LapTimer.Sectors).Select(t.Delta)], _previousBest, t.NewRecord, _hud.Drift.Total));
            return;
        }
        if (Flow != null)
            for (var i = 0; i < 15; i++) Tick(Drive.Dt); // --flow: 16× time, the pilot drives the run to the finish
        if (k.IsKeyPressed(Key.Escape) || Input.Gamepad.IsButtonPressed(GamepadButton.Start) || (Flow != null && keys.Back))
        {
            if (_menu == null) Window.ShouldClose = true;
            else
            {
                _menuAudio?.Play("alarm_02");
                OpenMenu(Menu.Screen.Pause);
            }
            return;
        }
        if (k.IsKeyPressed(Key.F4)) _hud.Visible = _settings.HudOn = !_hud.Visible;
        if (k.IsKeyPressed(Key.N))
        {
            _hud.NextMode();
            _settings.MapMode = _hud.Mode;
        }
        if (k.IsKeyPressed(Key.F2))
        {
            _renderer.HighQuality = _settings.HighQuality = !_renderer.HighQuality;
            Console.WriteLine($"\n[Touge] Grafik: {(_renderer.HighQuality ? "hoch (4× MSAA, Bloom, Schatten)" : "niedrig (ohne MSAA/Bloom/Schatten)")}");
        }
        if (bench is { } benchSeconds && Bench(time, benchSeconds)) return;
        if (_audio != null && k.IsKeyPressed(Key.M))
        {
            _settings.MusicOn = true;
            _audio.NextTrack();
        }
        if (k.IsKeyPressed(Key.F3)) _settings.MusicOn = !_settings.MusicOn; // SyncMusic follows
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

    /// <summary>Opens a menu screen with the selection of the current run; <paramref name="fromFrontEnd"/>: with the saved choice.</summary>
    private void OpenMenu(Menu.Screen screen, bool fromFrontEnd = false)
    {
        if (fromFrontEnd) _menu!.Open(screen, _settings.Course, _settings.Reverse, _carName, _paint, _settings.Manual);
        else _menu!.Open(screen, _courseTime, _drive.Reverse, _carName, _paint, _settings.Manual);
    }

    /// <summary>Front-end input; its results open the game-flow menus behind the main menu or quit.</summary>
    private void UpdateFrontEnd((int X, int Y, bool Ok, bool Back) keys, float dt)
    {
        switch (_front!.Update(keys, dt))
        {
            case FrontEnd.Result.Quit:
                Quit();
                break;
            case FrontEnd.Result.TimeAttack:
                OpenMenu(Menu.Screen.Course, fromFrontEnd: true);
                break;
            case FrontEnd.Result.Records:
                OpenMenu(Menu.Screen.Records, fromFrontEnd: true);
                break;
            case FrontEnd.Result.Options:
                OpenMenu(Menu.Screen.Options, fromFrontEnd: true);
                break;
        }
    }

    /// <summary>
    ///     Sound for the current screen: engine/tyre loops only while the race runs (intro, race, finish); music switched
    ///     only when it changes, front end per <see cref="FrontEnd.Music"/>, menus per <see cref="Menu.Music"/>; none with music off.
    /// </summary>
    private void SyncAudio()
    {
        if (_audio == null || _menuAudio == null) return;
        var front = _front is { Active: true };
        var sfx = !front && _menu!.Current is Menu.Screen.None or Menu.Screen.Intro or Menu.Screen.Finish ? _settings.SoundVolume : 0;
        if (_audioDevice!.Sfx != sfx) _audioDevice.Sfx = sfx; // the setter touches every voice
        var want = !_settings.MusicOn ? null : front ? _front!.Music : _menu!.Music(_music);
        if (want == _music) return;
        if (_music == Menu.RaceMusic) _audio.MusicOn = false;
        _music = want;
        if (want != Menu.RaceMusic) _menuAudio.Music(want);
        else
        {
            _menuAudio.Music(null);
            _audio.MusicOn = true;
        }
    }

    /// <summary>Menu input and its actions; Load loads the chosen course (blocking, the loading screen stays up) or only swaps the car.</summary>
    private void UpdateMenu((int X, int Y, bool Ok, bool Back) keys, float dt)
    {
        var menu = _menu!;
        switch (menu.Update(keys, dt))
        {
            case Menu.Action.Load:
                (_settings.Course, _settings.Reverse, _settings.Car, _settings.Paint, _settings.Manual) = (menu.CourseTime, menu.Reverse, menu.CarId, menu.Paint, menu.Manual);
                if (_persist) _settings.Save();
                if (menu.CourseTime != _courseTime || menu.Reverse != _drive.Reverse)
                {
                    using var iso = new Iso9660(isoPath);
                    LoadCourse(iso, menu.CourseTime, menu.Reverse, menu.CarId, menu.Paint);
                }
                else if (menu.CarId != _carName || menu.Paint != _paint) SwitchCar(Array.IndexOf(CarPaint.Cars, menu.CarId), menu.Paint);
                ResetRun();
                break;
            case Menu.Action.Resume:
                (_inRace, _camSnap, _fly) = (true, true, false);
                break;
            case Menu.Action.Restart:
                ResetRun();
                break;
            case Menu.Action.Exit:
                _inRace = false;
                if (_front == null) Window.ShouldClose = true; // a --menu start has no main menu to go back to
                else _front.Open(FrontEnd.Step.Modes);
                break;
            case Menu.Action.PreviewCar:
                SwitchCar(Array.IndexOf(CarPaint.Cars, menu.CarId), menu.Paint);
                break;
            case Menu.Action.SettingsChanged:
                ApplySettings();
                break;
            case Menu.Action.Quit:
                Quit();
                break;
        }
    }

    /// <summary>QUIT GAME (main or pause menu, confirmed): settings saved, window closed.</summary>
    private void Quit()
    {
        if (_persist) _settings.Save();
        Window.ShouldClose = true;
    }

    /// <summary>Car back at the start of the line (gearbox as chosen in the menus), fresh timing and drift score, race camera behind it.</summary>
    private void ResetRun()
    {
        _drive.ResetTo(0);
        if (_front != null) _drive.Car.AutomaticGearbox = !_settings.Manual;
        _hud = NewHud();
        _fx = new Effects();
        _simTime = 0;
        _finished = false;
        SyncPose();
        (_inRace, _camSnap, _fly) = (true, true, false);
    }

    private void ApplySettings()
    {
        var s = _settings;
        _renderer.HighQuality = s.HighQuality;
        if (_audioDevice != null) _audioDevice.Music = s.MusicVolume;
        if (_menuAudio != null) _menuAudio.Volume = s.SoundVolume;
        (_hud.Visible, _hud.Mode, _bumperCam) = (s.HudOn, s.MapMode, s.BumperCam);
        if (s.Livery != _carLivery) SwitchCar(Array.IndexOf(CarPaint.Cars, _carName), _paint);
        if (_persist) s.Save();
    }

    /// <summary>
    ///     Camera behind the menus: car select and the result sheet turn around the parked car (as the original's turntable
    ///     and result showcase), title and the backdrop screens fly along the road (CRS_ROAD, 9 m up, looking 50 m ahead),
    ///     pause keeps the game's view. Returns false when the game camera should run (race, intro, finish).
    /// </summary>
    private bool UpdateMenuCamera(float dt)
    {
        var front = _front is { Active: true };
        var screen = _menu?.Current ?? Menu.Screen.None;
        if (!front && screen is Menu.Screen.None or Menu.Screen.Intro or Menu.Screen.Finish) return false;
        if (!front && screen is Menu.Screen.Car or Menu.Screen.Gearbox or Menu.Screen.Result)
        {
            OrbitCar(0.6f + _menuTime * 0.35f);
            var fwd = Forward();
            _pos -= fwd * 1.5f; // further out and looking down: the whole car above the info panel
            (_camLook, _fov) = (_pos + fwd - Vector3.UnitY * 0.1f, MathF.PI / 4);
            return true;
        }
        if (!front && screen == Menu.Screen.Pause)
        {
            if (!_fly) UpdateDriveCamera(0); // keeps the game's view (sets it up if the game opened paused)
            return true;
        }
        var road = _course.Road;
        _flyS += dt * (front ? 7 : 16); // slower behind the title
        var i = (int)(_flyS / 2);
        if (i + 26 >= road.Length) (_flyS, i, _camSnap) = (0, 0, true);
        var f = _flyS / 2 - i;
        var eye = Vector3.Lerp(road[i], road[i + 1], f) + Vector3.UnitY * 9;
        var look = Vector3.Lerp(road[i + 25], road[i + 26], f) + Vector3.UnitY * 2;
        var a = _camSnap ? 1 : 1 - MathF.Exp(-2 * dt);
        (_pos, _camLook, _fov, _camSnap) = (Vector3.Lerp(_pos, eye, a), Vector3.Lerp(_camLook, look, a), MathF.PI / 3, false);
        return true;
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

    /// <summary>
    ///     --flow: per step the place it waits for (front-end step, menu screen or "Race"), seconds to settle there, the PNG
    ///     to take (null: none) and the key then pressed. Time Attack on Akina downhill, day, dry, Trueno with the last paint, AT.
    /// </summary>
    private static readonly (string At, float Wait, string? Shot, int X, int Y, bool Ok, bool Back)[] FlowScript =
    [
        ("Boot", 1.2f, "boot", 0, 0, true, false), ("Logo", 1, "logo", 0, 0, true, false), ("Title", 1.5f, "title", 0, 0, true, false),
        ("Modes", 1, "modes", 0, 1, false, false), ("Modes", 0.6f, "modes_time_attack", 0, 0, true, false),
        ("Course", 1, "course", 1, 0, false, false), ("Course", 0.5f, "course_happogahara", -1, 0, false, false), ("Course", 0.5f, null, 0, 0, true, false),
        ("Route", 0.8f, "route", 0, 0, true, false), ("Time", 0.8f, "time", 0, 0, true, false), ("Weather", 0.8f, "weather", 0, 0, true, false),
        ("Maker", 1, "maker", 0, 0, true, false), ("Maker", 0.6f, "model", 0, 0, true, false),
        ("Car", 1.5f, "car", 0, 1, false, false), ("Car", 1, "car_paint", 0, 0, true, false), ("Gearbox", 0.8f, "gearbox", 0, 0, true, false),
        ("Loading", 0.6f, "loading", 0, 0, false, false), ("Intro", 1, "telop", 0, 0, false, false), ("Intro", 2, "countdown", 0, 0, false, false),
        ("Race", 2, "race", 0, 0, false, true), ("Pause", 0.8f, "pause", 0, 0, true, false),
        ("Finish", 1.2f, "finish", 0, 0, false, false), ("Result", 3.6f, "result", 1, 0, false, false), ("Result", 0.3f, null, 1, 0, false, false),
        ("Result", 0.3f, null, 1, 0, false, false), ("Result", 0.5f, "result_exit", 0, 0, true, false),
        ("Modes", 1, null, 0, 1, false, false), ("Modes", 0.5f, null, 0, 1, false, false), ("Modes", 0.5f, null, 0, 0, true, false),
        ("Records", 1, "records", 0, 0, false, true),
        ("Modes", 1, null, 0, 1, false, false), ("Modes", 0.5f, null, 0, 1, false, false), ("Modes", 0.5f, null, 0, 1, false, false), ("Modes", 0.5f, null, 0, 0, true, false),
        ("Options", 1, "options", 0, 1, false, false), ("Options", 0.5f, "options_music", 0, 0, false, true), ("Modes", 1.2f, "modes_end", 0, 0, false, false),
    ];

    /// <summary>--flow: the scripted key of this frame; asks for the step's PNG first (written next frame), quits after the last step.</summary>
    private (int X, int Y, bool Ok, bool Back) FlowKeys(float dt)
    {
        if (_flowStep >= FlowScript.Length)
        {
            Window.ShouldClose = true;
            return default;
        }
        var s = FlowScript[_flowStep];
        var at = _front is { Active: true } ? _front.Current.ToString() : _menu!.Current != Menu.Screen.None ? _menu.Current.ToString() : "Race";
        if (at != s.At)
        {
            _flowT = 0;
            return default;
        }
        if ((_flowT += dt) < s.Wait) return default;
        if (s.Shot != null && !_flowShotTaken)
        {
            (_shotState, _flowShot, _flowShotTaken) = (1, s.Shot, true);
            return default;
        }
        if (_shotState != 0) return default;
        (_flowStep, _flowT, _flowShotTaken) = (_flowStep + 1, 0, false);
        Console.WriteLine($"\n[Flow] {_menuTime:0.00} s {s.At}: {(s.Ok ? "DECIDE" : s.Back ? "BACK" : s.X != 0 || s.Y != 0 ? $"x {s.X} y {s.Y}" : "-")}");
        return (s.X, s.Y, s.Ok, s.Back);
    }

    /// <summary>--bench: frame intervals after a 2 s warm-up; at the end avg/p99/max, frames over 25 ms and the effect peaks.</summary>
    private bool Bench(in GameTime time, float seconds)
    {
        if (time.TotalTime > 2) _frameTimes.Add(time.DeltaTime * 1000);
        _fxPeak = (Math.Max(_fxPeak.Smoke, _fx.SmokeCount), Math.Max(_fxPeak.Skids, _fx.SkidCount), Math.Max(_fxPeak.Sparks, _fx.SparkCount));
        if (time.TotalTime < seconds + 2) return false;
        var sorted = _frameTimes.Order().ToArray();
        Console.WriteLine($"\n[Bench] {_courseTime} {Device.SwapchainWidth}x{Device.SwapchainHeight} Qualität {(_renderer.HighQuality ? "hoch" : "niedrig")}: " +
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
        if (k.IsKeyPressed(Key.R) || pad.IsButtonPressed(GamepadButton.Y))
        {
            _drive.ResetNearest();
            SyncPose();
        }
        if (k.IsKeyPressed(Key.B))
        {
            using (var iso = new Iso9660(isoPath)) _drive.SetDirection(iso, !_drive.Reverse);
            _drive.ResetNearest();
            _hud = NewHud();
            Console.WriteLine($"\n[Touge] Richtung: {(_drive.Reverse ? "rückwärts (CRS_COLI _1, DRV _O)" : "vorwärts (_0, _I)")}");
            SyncPose();
        }
        if (k.IsKeyPressed(Key.C))
            (_bumperCam, _settings.BumperCam, _camSnap) = (!_bumperCam, !_bumperCam, true);
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
        if (!UpdateMenuCamera(ctx.Time.DeltaTime) && !_fly) UpdateDriveCamera(ctx.Time.DeltaTime);
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
        if (_probe == null || carView) _carRenderer.Draw(pass, _car.Body, _car.Decals, _car.Wheel, _carBody, _carWheels, view * proj, _pos);
        _fxRenderer.Draw(pass, _fx, view, view * proj, _pos);
        // camera velocity stretches the rain streaks; a shot has no previous frame, the chase camera moves with the car
        var frameDt = ctx.Time.DeltaTime;
        _camVelocity = shot != null ? (_fly ? Vector3.Zero : _drive.Car.Velocity)
            : frameDt > 0 ? Vector3.Lerp(_camVelocity, (_pos - _lastCamPos) / frameDt, 0.3f) : _camVelocity;
        _lastCamPos = _pos;
        var heightPx = shot?.Height ?? Device.SwapchainHeight;
        _fxRenderer.DrawRain(pass, view * proj, _pos, _camVelocity, _simTime, 2 * MathF.Tan(_fov / 2) / heightPx);
        _renderer.EndScene(ctx.Encoder, pass, shot);
        var (w, h) = shot != null ? (shot.Width, shot.Height) : (Device.SwapchainWidth, Device.SwapchainHeight);
        var menuShown = _menu is { Current: not Menu.Screen.None };
        if (_front is { Active: true }) _front.Build(_overlay, w, h);
        else if (_hud.Visible && (!menuShown || _menu!.OverRace)) // telop/countdown and pause lie over the HUD
            _hud.Build(_overlay, w, h, _carPose.Translation, Vector3.TransformNormal(Vector3.UnitZ, _carPose), _drive.Car, _carName, _menuTime);
        else _overlay.Clear();
        if (_front is not { Active: true } && menuShown) _menu!.Build(_overlay, w, h);
        var target = shot?.View ?? Device.CurrentSwapchainView;
        _overlayRenderer.Draw(ctx.Encoder, _overlay, target, w, h);
        _textRenderer.Draw(ctx.Encoder, _overlay, target, w, h);
        if (shot != null)
        {
            shot.Copy(ctx.Encoder);
            _shotState = 2;
        }
    }

    public override void Dispose()
    {
        _audio?.Dispose();
        _menuAudio?.Dispose();
        _audioDevice?.Dispose();
        _course.World.Dispose();
        _course.Sky.Dispose();
        _car.Dispose();
        _carRenderer.Dispose();
        _fxRenderer.Dispose();
        _overlayRenderer.Dispose();
        _textRenderer.Dispose();
        _capture?.Dispose();
        _renderer.Dispose();
    }
}
