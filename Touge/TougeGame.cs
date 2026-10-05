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
///     R (pad Y) reset onto the driving line, B reset in the other direction (downhill/uphill), C chase/bumper camera, L lights, H high beam (pad D-pad up/down, <see cref="Headlights"/>), F2 graphics quality (MSAA, bloom, shadows) on/off,
///     F4 HUD on/off, N minimap mode (<see cref="Hud"/>), 1/2 previous/next car and 3 next paint at standstill. Pad: left stick, triggers, A handbrake, bumpers shift.
///     Fly: WASD, Q/E down/up, right mouse or arrow keys look, Shift fast, Space jump along the driving line. Esc/Start pause menu (<see cref="Menu"/>; without CLI test arguments the game starts in the title menu).
///     <paramref name="orbit"/> (degrees, 0 = front, 90 = left, 180 = rear) puts the fly camera around the car;
///     <paramref name="autodrive"/> lets the line pilot drive that many seconds before the first frame (for --shot);
///     <paramref name="bench"/> lets it drive in real time with the chase camera for that many seconds, then logs frame times and quits;
///     <paramref name="drift"/> makes the pilot throw in a scripted handbrake drift every 7 s (<see cref="Drive.ForceDrift"/>).
///     Tyre smoke, skid marks and sparks come from the car's wheel/wall state every tick (<see cref="TickEffects"/>), in rain
///     also tyre spray; falling rain is drawn around the camera.
///     Fog: per time of day (<see cref="AtmosphereFor"/>) with the original's fog colour (CRS_INFO) and height fog over the course's altitude range.
///     Sound: engine, tyres, walls, wind, race music (<see cref="Jukebox"/>: M / pad D-pad right next song, F3 music on/off); none for --shot.
///     <paramref name="flicker"/>: no game loop, renders the <see cref="FlickerProbe"/> views and quits.
/// </summary>
public sealed partial class TougeGame(string isoPath, string courseTime, string? shotPath = null, int startPoint = 0, float? orbit = null, float? autodrive = null,
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
    /// <summary>The course argument (--flow --bench drives it after the menus; the front end starts on its backdrop).</summary>
    private readonly string _benchCourse = courseTime;
    /// <summary>Menus at start (no CLI test arguments): settings from/to the app-data JSON, title screen first.</summary>
    public bool UseMenus { get; init; }
    /// <summary>--shot-size WxH: size of the --shot frame (default 1280×720).</summary>
    public (int W, int H) ShotSize { get; init; } = (1280, 720);
    /// <summary>--hud-scale <percent>: HUD size for test runs (menus use the stored Options value).</summary>
    public float HudScale { get; init; } = 1;
    /// <summary>--menu: front-end step (boot, logo, disclaimer, title, mode) or menu screen (course … options, <see cref="Menu.Screen"/>) to open at start, e.g. for --shot.</summary>
    public string? StartMenu { get; init; }
    private Settings _settings = null!;
    private bool _persist, _inRace;
    private Catalog? _catalog;
    private Menu? _menu;
    /// <summary>Boot cards, title and main menu (started plainly or with --menu boot|logo|disclaimer|title|mode).</summary>
    private FrontEnd? _front;
    private MenuAudio? _menuAudio;
    /// <summary>Race music, shuffled over all enabled songs, independent of the course (<see cref="Jukebox"/>).</summary>
    private Jukebox? _jukebox;
    /// <summary>IKETANI'S CAR GUIDE (main menu or --menu guide|guide-list|guide-talk) and Iketani's voice; <see cref="_voice"/> = car talking.</summary>
    private CarGuide? _guide;
    private GuideVoice? _guideVoice;
    private string? _voice;
    private readonly MenuKeys _frontKeys = new();
    /// <summary>What the music stream plays: null silence, <see cref="Menu.RaceMusic"/>, or a BGM.AFS track of the menus ("" = decide again).</summary>
    private string? _music = "";
    /// <summary>Front end or a menu holds the game (no physics); the intro lets go at GO, the finish banner lets the pilot drive on.</summary>
    private bool Frozen => _front is { Active: true } || _guide is { Active: true } || _legend is { Active: true } || _versusUi is { Active: true } || _story is { Freezes: true } || ReplayFreezes
                           || _menu is { Freezes: true } && !(_netRace != null && _menu.Current == Menu.Screen.Pause);
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
    /// <summary>--render-scale: 3D resolution in percent of the window (test runs; the menus use the saved option).</summary>
    public int RenderScale { get; init; } = 100;
    /// <summary>--reverse: start in the reverse (uphill) direction; B switches direction at runtime (<see cref="Drive.Reverse"/>).</summary>
    public bool Reverse { get; init; }
    /// <summary>--fog: dense fog over the day or night course (<see cref="FogAtmosphere"/>); chosen in the menus as weather FOG.</summary>
    public bool Fog { get; init; }
    /// <summary>The loaded course has fog.</summary>
    private bool _fog;
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
    /// <summary>--orbit …:m: distance of the orbit camera from the car.</summary>
    public float OrbitDistance { get; init; } = 5.5f;
    /// <summary>--lights: light switch at the start instead of the course default (<see cref="Headlights.For"/>).</summary>
    public Headlights.Mode? Lights { get; init; }
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
    private Headlights _lights = new(Headlights.Mode.Off);

    // physics → render interpolation
    private Vector3 _prevPos;
    private Quaternion _prevRot;

    // driver input (bindings: Options → Controls), sampled per frame, consumed per tick; force feedback per tick
    private DriverInput _driver = null!;
    private readonly ForceFeedback _ffb = new();
    private float _brakeLight;
    private int _pendingShift;
    /// <summary>--input-debug: devices, raw axes/buttons, mapped input and force feedback over the picture (<see cref="InputDebug"/>).</summary>
    public bool InputDebug { get; init; }
    /// <summary>--sim-wheel: a virtual wheel (steering swings, pedals pump) for screenshots and tests without hardware.</summary>
    public bool SimWheel { get; init; }
    private JoystickState? _simWheel;

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
        {
            // a test run: its own options over a fresh profile, or over the --data-dir profile (records, assists, progress kept and saved as in a real one)
            _settings = SaveRuns ? Settings.Load() : new Settings();
            (_settings.HighQuality, _settings.HudOn, _settings.HudScale, _settings.Car, _settings.Paint, _settings.Reverse, _settings.Course, _settings.Fog) =
                (highQuality, HudMode != "off" && flicker == null, HudScale, Car, Paint, Reverse, _courseTime, Fog);
            (_settings.MapMode, _settings.Livery, _settings.RenderScale) =
                (HudMode switch { "north" => Hud.MapMode.NorthUp, "overview" => Hud.MapMode.Overview, _ => Hud.MapMode.Rotating }, Livery, RenderScale);
        }
        _driver = new DriverInput(_settings.Controls);
        _frontKeys.Wheel = _settings.Controls;
        if (SimWheel) Input.AddVirtual(_simWheel = new JoystickState("Simulated wheel", 4, 20, 1, wheel: true));
        if (flicker == null && ContactSheet == null)
        {
            _catalog = new Catalog(iso);
            _menu = new Menu(_catalog, _settings) { Controls = new ControlsScreen(_settings.Controls, Input, _driver) };
            if (_persist && !_catalog.Courses.Any(c => _settings.Course == $"{c.Id}_DAY" || _settings.Course == $"{c.Id}_NIT" || _settings.Course == $"{c.Id}_RIN"))
                _settings.Course = "AKINA_DAY";
            _menu.Sound = n => _menuAudio?.Play(n);
            _guide = new CarGuide(_catalog) { Sound = n => _menuAudio?.Play(n) };
            LoadLegend();
            LoadStory(iso);
            SetupReplay();
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
        if (_persist) ApplyDisplay();
        if (_menu != null)
        {
            _menu.Options.Resolutions = Resolutions;
            Window.FullscreenModeChanged += m => _settings.Display = m switch // F11 keeps the setting in step
            {
                Kansei.Windowing.FullscreenMode.Windowed => Settings.DisplayMode.Window,
                Kansei.Windowing.FullscreenMode.Exclusive => Settings.DisplayMode.Fullscreen,
                _ => Settings.DisplayMode.Borderless,
            };
            Window.FullscreenModeChanged += _ => { _applied = DisplayState; if (SavesRuns) _settings.Save(); };
        }
        // the front end shows Akina at night behind the title, like the original's photo; course select loads the choice
        var title = _front != null && (_persist || Flow != null);
        LoadCourse(iso, title ? "AKINA_NIT" : _persist ? _settings.Course : _courseTime, _settings.Reverse, _settings.Car, _settings.Paint, startPoint, !title && _settings.Fog);
        _drive.ForceDrift = drift;
        if (GhostFile != null) LoadGhost();
        // --versus: StartVersusCli below, once sound and menus are up
        if (VersusStart == null && autodrive is { } battleSeconds && _race != null) BattleAutoDrive(battleSeconds);
        else if (VersusStart == null && autodrive is { } seconds)
        {
            _drive.AutoDrive(seconds, () =>
            {
                TickEffects(Drive.Dt);
                _hud.Tick(_drive.Car, Drive.Dt);
                _ffb.Update(_drive.Car, _drive.Roughness, 0, _settings.Controls.FfbStrength, Drive.Dt); // --input-debug shows it
                GhostTick();
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
        else if (OpenReplayStart()) { } // --menu replay|saveload|photo, --replay
        else if (_guide != null && StartMenu?.StartsWith("guide") == true)
        {
            OpenGuide(StartMenu == "guide" ? CarGuide.Step.Intro : CarGuide.Step.List);
            if (StartMenu == "guide-talk") _guide.Talk(4);
            if (shotPath != null) _guide.Settle();
            _inRace = false;
        }
        else if (_legend != null && StartMenu?.StartsWith("legend") == true)
        {
            OpenLegendAtStart(StartMenu);
            _inRace = false;
        }
        else if (_story != null && StartMenu?.StartsWith("story") == true) StartStoryMenu(StartMenu);
        else if (_menu != null && StartMenu != null)
        {
            // options:<page> opens an options page directly (screenshots), controls:<device> the controls screen
            var start = StartMenu;
            var page = start.StartsWith("options:", StringComparison.OrdinalIgnoreCase) ? start[8..] : null;
            if (page?.ToLowerInvariant() == "controller" && _menu.Controls != null) (start, page) = ("controls", null); // the plate opens the controls screen
            var screen = start == "settings" || page != null ? Menu.Screen.Options
                : Enum.TryParse<Menu.Screen>(start.Split(':')[0], true, out var s) && s is not (Menu.Screen.None or Menu.Screen.Loading or Menu.Screen.Finish or Menu.Screen.Result) ? s
                : throw new ArgumentException($"--menu {start}: unbekannt");
            OpenMenu(screen);
            if (page != null && !_menu.Options.OpenPage(page))
                throw new ArgumentException($"--menu options:{page}: unbekannt, möglich: {string.Join(' ', _menu.Options.Pages.Select(p => p.Title.Replace(" ", "").ToLowerInvariant()))}");
            if (screen == Menu.Screen.Controls)
            {
                var device = start.Split(':') is [_, var d] ? d : "";
                _menu.Controls!.Open(device.ToLowerInvariant() == "gamepad" ? DeviceKind.Pad : Enum.TryParse<DeviceKind>(device, true, out var dk) ? dk : DeviceKind.Keyboard); // --menu controls:wheel
            }
            if (screen == Menu.Screen.Maker && start.Split(':') is [_, var row]) _menu.ShowModel(int.Parse(row)); // --menu maker:2: the model list, row 2
            _inRace = screen is Menu.Screen.Pause or Menu.Screen.Intro;
            if (shotPath != null) _menu.Settle();
        }
        else _inRace = true;
        if (_race != null && _front == null && _menu != null && StartMenu == null && shotPath == null && autodrive == null && bench == null && Flow == null)
            OpenMenu(Menu.Screen.Intro); // --battle: telop with the rival, countdown
        if (Flow != null)
        {
            Directory.CreateDirectory(Flow);
            _capture = new FrameCapture(Device, ShotSize.W, ShotSize.H); // and sound as usual (below)
        }
        if (Offscreen)
        {
            Device.Offscreen = true;
            _offscreen = new FrameCapture(Device, Device.SwapchainWidth, Device.SwapchainHeight);
        }
        if (shotPath != null) (_capture, _shotState) = (new FrameCapture(Device, ShotSize.W, ShotSize.H), ShotAfter > 0 ? 0 : 1);
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
            _audioDevice = new AudioDevice { Music = _settings.MusicVolume, Master = _settings.MasterVolume };
            if (_menu != null) _menuAudio = new MenuAudio(iso, _audioDevice) { Volume = _settings.MenuVolume, Clock = () => _menuTime };
            _jukebox = new Jukebox(iso, _audioDevice, _settings) { Clock = () => _menuTime };
            if (_guide != null) _guideVoice = new GuideVoice(iso, _audioDevice);
            StartAudio(iso);
        }
        StartVersusCli();
    }

    /// <summary>
    ///     (Re)loads everything course-bound: world renderer with course, sky, lights and fog, driving physics with
    ///     the car on line point <paramref name="at"/>, car renderers, HUD, effects and (with sound) the course's music.
    ///     A course change replaces the whole <see cref="WorldRenderer"/>, so its textures go with it.
    /// </summary>
    private void LoadCourse(Iso9660 iso, string courseTime, bool reverse, string car, int paint, int at = 0, bool fog = false)
    {
        if (_renderer is not null)
        {
            Device.WaitIdle();
            _audio?.Dispose();
            _course.World.Dispose();
            _course.Sky.Dispose();
            DisposeVersusCars();
            DisposeRival(); // its textures came after the player's car's (ReleaseTextures frees from an index on)
            _car.Dispose();
            _carRenderer.Dispose();
            _fxRenderer.Dispose();
            _renderer.Dispose();
        }
        (_courseTime, _fog) = (courseTime, fog && !courseTime.EndsWith("_RIN"));
        _renderer = new WorldRenderer(Device) { Atmosphere = AtmosphereFor(courseTime) };
        ApplyGraphics();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _course = CourseLoader.Load(iso, courseTime, _renderer, reverse);
        SetupFog(_renderer.Atmosphere);
        if (_fog) _renderer.Atmosphere = FogAtmosphere(courseTime.EndsWith("_NIT"));
        if (!courseTime.EndsWith("_NIT") && _course.SunDirection is { } sun) _renderer.Atmosphere.SunDirection = sun; // the original's key light
        _drive = new Drive(iso, courseTime, reverse, _settings.Assisted(CarSpecs.All[car]));
        Console.WriteLine($"[Touge] {courseTime} geladen in {sw.ElapsedMilliseconds} ms, {_course.World.Batches.Count} Batches, {_drive.Ground.Walls.Length} Wandsegmente");
        _carRenderer = new CarRenderer(_renderer);
        _fxRenderer = new EffectsRenderer(_renderer);
        _fx = new Effects();
        SetupLights(courseTime.EndsWith("_NIT"));
        (_carName, _paint) = (car, paint);
        LoadCarModel(iso);
        _drive.ResetTo(at);
        _hud = NewHud();
        SyncPose();
        if (_audioDevice != null) StartAudio(iso);
        LoadBattle(iso);
        LoadVersusCars(iso);
    }

    private void StartAudio(Iso9660 iso)
    {
        _audio = new GameAudio(iso, _courseTime, _audioDevice!, _carName, rain: _storyRain) { EngineLevel = _settings.EngineVolume };
        StartRivalAudio(iso);
        if (_persist) _audioDevice!.Music = _settings.MusicVolume; // GameAudio sets its own default
        _music = ""; // SyncMusic starts the race or menu music
    }

    /// <summary>HUD for the loaded line, with the stored best run of this course and direction; a new record is kept (and saved with the menus).</summary>
    private Hud NewHud()
    {
        var key = _settings.RunKey(_courseTime[.._courseTime.LastIndexOf('_')], _drive.Reverse); // other assists race their own records
        _previousBest = _settings.Best.GetValueOrDefault(key)?[^1];
        var hud = new Hud(_course.Road, _drive.Line, _drive.Pilot, _settings.Best.GetValueOrDefault(key), _drive.Start)
        {
            Visible = _settings.HudOn, Mode = _settings.MapMode, Scale = _settings.HudScale, Night = _courseTime.EndsWith("_NIT"), Mph = _settings.Mph,
        };
        hud.Timer.Record += best =>
        {
            if (_race != null || _vsRace != null || _story is { InRun: true }) return; // a battle (rival, contacts), versus or story run is no time attack record
            _settings.Best[key] = best;
            if (SavesRuns) _settings.Save();
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
        _modelToBody = ModelToBody(_car, _drive.Car.Spec);
    }

    /// <summary>Model space → physics body space (origin CoG): the model's wheel centres onto the physics wheel centres at rest.</summary>
    private static Matrix4x4 ModelToBody(CarModel model, CarSpec spec)
    {
        var modelWheels = Vector3.Zero;
        foreach (var w in model.Wheels) modelWheels += w.Translation / 4;
        return Matrix4x4.CreateTranslation(new Vector3(0, spec.WheelRadius - spec.CogHeight, spec.Wheelbase * (0.5f - spec.FrontWeight)) - modelWheels);
    }

    /// <summary>
    ///     Car <paramref name="car"/> (index in <see cref="CarPaint.Cars"/>) with <paramref name="paint"/>: new model, and for a
    ///     different car its physics spec (back on the line where the old one stood) and engine sound.
    /// </summary>
    private void SwitchCar(int car, int paint)
    {
        var name = CarPaint.Cars[(car % CarPaint.Cars.Length + CarPaint.Cars.Length) % CarPaint.Cars.Length];
        using var iso = new Iso9660(isoPath);
        Device.WaitIdle(); // the last frame may still read the old meshes
        DisposeVersusCars();
        DisposeRival(); // loaded after the player's car: its textures go with it, reloaded below
        _car.Dispose();
        if (name != _carName)
        {
            _drive.ChangeCar(_settings.Assisted(CarSpecs.All[name]));
            _audio?.SetCar(name);
            SyncPose();
        }
        (_carName, _paint) = (name, paint);
        LoadCarModel(iso);
        LoadRivalModel(iso);
        LoadVersusCars(iso);
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
        if (ReplayTick(dt)) return; // the replay viewer drives the cars
        if (_probe != null || Frozen) return; // frozen scene / menus pause the game
        RecordBefore();
        var car = _drive.Car;
        (_prevPos, _prevRot) = (car.Position, car.Orientation);
        // past the finish of a front-end run the game takes the car (auto-run to a stop before the end barrier);
        // the pilot drives for --autodrive/--bench/--flow
        var input = _race == null && _vsRace == null && _front != null && _hud.Timer.Phase == LapTimer.State.Finished ? _drive.Coast()
            : autodrive != null || bench != null || Flow != null ? _drive.PilotInput(_simTime)
            : _fly ? new VehicleInput(0, 0, 0, true)
            : _driver.Vehicle(_pendingShift);
        _pendingShift = 0;
        if (_vsRace != null) input = VersusStep(input, dt); // versus: split screen or online (Touge/Net)
        else if (_race != null) input = BattleStep(input, dt); // the session steps every car, the player's included
        else car.Step(input, _drive.Ground, dt);
        _brakeLight = input.Brake;
        _ffb.Update(car, _drive.Roughness, _driver.SteerBeyond, _settings.Controls.FfbStrength, dt);
        _simTime += dt;
        _lights.Tick(dt);
        TickEffects(dt);
        _hud.Tick(car, dt);
        StoryTick(dt);
        _audio?.Update(car, input.Throttle, input.Handbrake, dt);
        RecordAfter(input);
        GhostTick();
    }

    /// <summary>
    ///     Per physics tick: smoke and skid marks from each wheel's slide speed (slip ratio/angle × speed, scaled by
    ///     load), sparks and camera shake from wall contacts, then the particles move. Thresholds tuned by eye.
    /// </summary>
    private void TickEffects(float dt)
    {
        var car = _drive.Car;
        WheelEffects(car, _smokeDebt, _sprayDebt, 0, dt);
        var impact = (car.Velocity - _prevVelocity).Length();
        _prevVelocity = car.Velocity;
        if (car.WallContacts > 0)
        {
            _shake = MathF.Max(_shake, Math.Clamp((impact - 0.5f) / 4, 0, 1));
            WallSparks(car);
        }
        BattleEffects(dt);
        VersusEffects(dt);
        _fx.Update(dt);
    }

    /// <summary>Smoke, skid marks (strips <paramref name="track0"/>…+3) and rain spray of one car's wheels.</summary>
    private void WheelEffects(Vehicle car, float[] smokeDebt, float[] sprayDebt, int track0, float dt)
    {
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
            _fx.Skid(track0 + i, contact, side, up, Math.Clamp((slide - 2f) / 3, 0, 1) * Math.Clamp(load * 4, 0, 1) * (1 - 0.7f * wet));
            var smoke = Math.Clamp((slide - 4.5f) / 7, 0, 1) * load * (1 - 0.8f * wet);
            smokeDebt[i] += smoke * 45 * dt; // puffs per second per wheel at full slide
            for (; smokeDebt[i] >= 1; smokeDebt[i]--)
            {
                var jitter = new Vector3(_rng.NextSingle() - 0.5f, _rng.NextSingle() * 0.5f, _rng.NextSingle() - 0.5f);
                _fx.EmitSmoke(contact + up * 0.2f + jitter * 0.3f, car.Velocity * 0.12f + jitter * 1.5f + up * 0.5f, 0.3f, 0.12f + 0.25f * smoke);
            }
            if (!w.Contact || i < 2) continue; // rear wheels: the fronts spray into the rears
            sprayDebt[i] += spray * 30 * dt;
            for (; sprayDebt[i] >= 1; sprayDebt[i]--)
            {
                // thrown up and back off the tread (the car's velocity carried partly), then arcs down (Effects.EmitSpray)
                var jitter = new Vector3(_rng.NextSingle() - 0.5f, _rng.NextSingle(), _rng.NextSingle() - 0.5f);
                _fx.EmitSpray(contact + up * 0.15f + jitter * 0.2f, car.Velocity * 0.45f + up * (1.2f + 2f * jitter.Y) + jitter * 1.5f, 0.22f, 0.2f * spray);
            }
        }
    }

    /// <summary>Sparks where <paramref name="car"/> scrapes a wall.</summary>
    private void WallSparks(Vehicle car)
    {
        var up = Vector3.TransformNormal(Vector3.UnitY, car.Pose);
        var scrape = car.Velocity - car.WallNormal * Vector3.Dot(car.Velocity, car.WallNormal);
        var sparks = (int)MathF.Min(scrape.Length() / 3, 6);
        for (var i = 0; i < sparks; i++)
        {
            var r = new Vector3(_rng.NextSingle() - 0.5f, _rng.NextSingle(), _rng.NextSingle() - 0.5f);
            _fx.EmitSpark(car.WallPoint + car.WallNormal * 0.05f - up * 0.2f,
                scrape * (0.3f + 0.5f * _rng.NextSingle()) + car.WallNormal * (1 + 2 * r.Y) + r * 4);
        }
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
        _pos = target + dir * OrbitDistance + new Vector3(0, 1.3f * OrbitDistance / 5.5f, 0);
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
            var photo = PhotoShotDone();
            var path = photo ?? (Flow != null ? Path.Combine(Flow, $"om_step2_flow_{++_flowShots:00}_{_flowShot}.png") : shotPath!);
            Png.Write(path, _capture!.Width, _capture.Height, _capture.ReadRgba());
            Console.WriteLine($"\n[Touge] Screenshot -> {path}");
            if (Flow == null && photo == null)
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
        _jukebox?.Update(dt, hold: _menu?.Current == Menu.Screen.Pause);
        if (_simWheel != null) InputDebugView.Simulate(_simWheel, time.TotalTime);
        _driver.Update(Input, dt);
        SendForces();
        var keys = Flow != null ? FlowKeys(dt) : _frontKeys.Read(Input, dt);
        if (UpdateReplay(keys, dt)) return; // viewer, photo mode, REPLAY & RECORD, SAVE & LOAD
        if (VersusUpdate(keys, dt)) return;
        if (_front is { Active: true })
        {
            UpdateFrontEnd(keys, dt);
            return;
        }
        if (_guide is { Active: true })
        {
            UpdateGuide(keys, dt);
            return;
        }
        if (_legend is { Active: true })
        {
            UpdateLegend(keys, dt);
            return;
        }
        if (_story is { Active: true })
        {
            UpdateStory(keys, dt);
            return;
        }
        if (_menu is { Current: not Menu.Screen.None })
        {
            UpdateMenu(keys, dt);
            if (_menu.Current != Menu.Screen.Intro || _menu.Freezes) return; // after GO the intro only draws
        }
        if (bench is { } benchSeconds && Bench(time, benchSeconds)) return;
        if (StoryFinished() || BattleFinished() || VersusFinished()) return;
        if (_race == null && _vsRace == null && _front != null && !_finished && _hud.Timer.Phase == LapTimer.State.Finished)
        {
            // the run is over: finish banner, then the result sheet (only in the front-end flow)
            _finished = true;
            var t = _hud.Timer;
            _menu!.Finish(new Menu.Run(t.Time, (float[])t.Splits.Clone(), [.. Enumerable.Range(0, LapTimer.Sectors).Select(t.Delta)], _previousBest, t.NewRecord, _hud.Drift.Total));
            return;
        }
        if (Flow != null && bench == null)
            for (var i = 0; i < 15; i++) Tick(Drive.Dt); // --flow: 16× time, the pilot drives the run to the finish
        if (k.IsKeyPressed(Key.Escape) || _driver.Pressed(Control.Pause) || (Flow != null && keys.Back) || P2Pause())
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
            _settings.HighQuality = !_settings.HighQuality;
            ApplyGraphics();
            Console.WriteLine($"\n[Touge] Grafik: {(_renderer.HighQuality ? "hoch (4× MSAA, Bloom, Schatten)" : "niedrig (ohne MSAA/Bloom/Schatten)")}");
        }
        if (_jukebox != null && (k.IsKeyPressed(Key.M) || Input.Gamepad.IsButtonPressed(GamepadButton.DpadRight) && !PadBound(GamepadButton.DpadRight)))
        {
            _settings.MusicOn = true;
            _jukebox.Next();
        }
        if (k.IsKeyPressed(Key.F3)) _settings.MusicOn = !_settings.MusicOn; // SyncMusic follows
        if (_drive.Car.SpeedKmh < 3 && _vsRace == null) // car/paint change only at standstill
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
        else UpdateDriver();

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
        if (fromFrontEnd) _menu!.Open(screen, _settings.Course, _settings.Reverse, _carName, _paint, _settings.Manual, _settings.Fog);
        else _menu!.Open(screen, _courseTime, _drive.Reverse, _carName, _paint, _settings.Manual, _fog);
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
            case FrontEnd.Result.Versus:
                OpenVersus();
                break;
            case FrontEnd.Result.Guide:
                OpenGuide(CarGuide.Step.Intro);
                break;
            case FrontEnd.Result.Legend:
                _legend!.Open(LegendScreen.Step.Course);
                break;
            case FrontEnd.Result.Story:
                OpenStory();
                break;
            case FrontEnd.Result.Replay:
                _replayMenu?.Open();
                break;
            case FrontEnd.Result.SaveLoad:
                _saveMenu?.Open();
                break;
        }
    }

    /// <summary>Opens the car guide; its turntable car stands on open road away from the arches (<see cref="CarGuide.Spot"/>).</summary>
    private void OpenGuide(CarGuide.Step step)
    {
        _guide!.Open(_carName, _paint, step);
        _drive.ResetTo(CarGuide.Spot(_course.DrivingLine));
        SyncPose();
    }

    /// <summary>Car guide input: shows the chosen car/paint; on exit the saved car comes back and the main menu opens.</summary>
    private void UpdateGuide((int X, int Y, bool Ok, bool Back) keys, float dt)
    {
        var g = _guide!;
        switch (g.Update(keys, dt))
        {
            case CarGuide.Action.PreviewCar:
                SwitchCar(Array.IndexOf(CarPaint.Cars, g.CarId), g.Paint);
                break;
            case CarGuide.Action.Exit:
                if (_front == null)
                {
                    Window.ShouldClose = true; // a --menu start has no main menu to go back to
                    break;
                }
                if (_carName != _settings.Car || _paint != _settings.Paint) SwitchCar(Array.IndexOf(CarPaint.Cars, _settings.Car), _settings.Paint);
                _drive.ResetTo(0); // back at the start for car select
                SyncPose();
                _front.Open(FrontEnd.Step.Modes);
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
        var guide = _guide is { Active: true } || _legend is { Active: true };
        var versus = _versusUi is { Active: true };
        var sfx = !front && !guide && !versus && !ReplayMutes && _story is not { Mutes: true } && _menu!.Current is Menu.Screen.None or Menu.Screen.Intro or Menu.Screen.Finish ? _settings.SoundVolume : 0;
        if (_audioDevice!.Sfx != sfx) _audioDevice.Sfx = sfx; // the setter touches every voice
        if (_guide?.Voice != _voice && _guideVoice != null)
        {
            // the music steps back while Iketani talks
            _voice = _guide!.Voice;
            _audioDevice.Music = MusicLevel;
            _guide.VoiceSeconds = _guideVoice.Play(_voice, _settings.MenuVolume);
        }
        var want = !_settings.MusicOn ? null : front ? _front!.Music : _legend is { Active: true } ? _legend.Music : guide ? _guide!.Music : versus ? _versusUi!.Music : _story is { Active: true } ? _story.Music : ReplayMusic ?? VersusMusic(_menu!.Music(_music));
        if (want == _music) return;
        if (_music == Menu.RaceMusic) _jukebox!.Stop();
        _music = want;
        if (want != Menu.RaceMusic) _menuAudio.Music(want);
        else
        {
            _menuAudio.Music(null);
            _jukebox!.Play();
        }
    }

    /// <summary>Menu input and its actions; Load loads the chosen course (blocking, the loading screen stays up) or only swaps the car.</summary>
    private void UpdateMenu((int X, int Y, bool Ok, bool Back) keys, float dt)
    {
        var menu = _menu!;
        switch (menu.Update(keys, dt))
        {
            case Menu.Action.Load:
                (_settings.Course, _settings.Reverse, _settings.Car, _settings.Paint, _settings.Manual, _settings.Fog) = (menu.CourseTime, menu.Reverse, menu.CarId, menu.Paint, menu.Manual, menu.Fog);
                if (SavesRuns) _settings.Save();
                if (menu.CourseTime != _courseTime || menu.Reverse != _drive.Reverse || menu.Fog != _fog)
                {
                    using var iso = new Iso9660(isoPath);
                    LoadCourse(iso, menu.CourseTime, menu.Reverse, menu.CarId, menu.Paint, fog: menu.Fog);
                }
                else if (menu.CarId != _carName || menu.Paint != _paint) SwitchCar(Array.IndexOf(CarPaint.Cars, menu.CarId), menu.Paint);
                ResetRun();
                break;
            case Menu.Action.Resume:
                (_inRace, _camSnap, _fly) = (true, true, false);
                break;
            case Menu.Action.Restart when _netRace != null:
                menu.Close(); // an online race cannot restart for one player
                break;
            case Menu.Action.Restart:
                ResetRun();
                break;
            case Menu.Action.Rivals:
                ReturnToLadder();
                break;
            case Menu.Action.Exit when _story is { InRun: true }:
                EndBattle(); // pause → Exit in a story chapter: back to the chapter select
                OpenStory(_story.Chapter);
                break;
            case Menu.Action.Exit when _vsRace != null:
                VersusExitRace();
                break;
            case Menu.Action.Exit:
                EndRecording(); // the finished run is saved now, not at the next run
                EndLegendBattle();
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
            case Menu.Action.Replay when _rec is { Valid: true, Replay.Ticks: > 0 }:
                OpenReplay(_rec.Replay, menu.Current == Menu.Screen.Pause ? Back.Pause : Back.Result);
                break;
            case Menu.Action.Replay:
                _menuAudio?.Play("BEEP001");
                break;
            case Menu.Action.Photo:
                OpenPhoto();
                break;
        }
    }

    /// <summary>QUIT GAME (main or pause menu, confirmed): settings saved, window closed.</summary>
    private void Quit()
    {
        EndRecording();
        Autosave();
        if (SavesRuns) _settings.Save();
        Window.ShouldClose = true;
    }

    /// <summary>Car back at the start of the line (gearbox as chosen in the menus), fresh timing and drift score, race camera behind it.</summary>
    private void ResetRun()
    {
        var spec = _settings.Assisted(CarSpecs.All[_carName]); // assists changed in the options since the car was loaded
        if (spec != _drive.Car.Spec) _drive.ChangeCar(spec);
        _drive.ResetTo(0);
        NewBattle();
        if (_story is { InRun: true }) _storyJudge = _story.NewJudge();
        if (_vsRace != null) NewVersusRace(); // RETRY of a split-screen race
        if (_front != null || _story != null) _drive.Car.AutomaticGearbox = !_settings.Manual;
        _hud = NewHud();
        _fx = new Effects();
        _simTime = 0;
        _finished = false;
        SyncPose();
        (_inRace, _camSnap, _fly) = (true, true, false);
        RestartRecording();
    }

    /// <summary>Music volume from the settings; it steps back while Iketani talks.</summary>
    private float MusicLevel => _settings.MusicVolume * (_voice != null ? 0.35f : 1);

    /// <summary>Options changed: everything live except the assists (next run, <see cref="ResetRun"/>); saved with the menus.</summary>
    private void ApplySettings()
    {
        var s = _settings;
        ApplyGraphics();
        if (_persist) ApplyDisplay();
        if (_audioDevice != null) (_audioDevice.Music, _audioDevice.Master) = (MusicLevel, s.MasterVolume);
        if (_menuAudio != null) _menuAudio.Volume = s.MenuVolume;
        if (_audio != null) _audio.EngineLevel = s.EngineVolume;
        (_hud.Visible, _hud.Mode, _hud.Scale, _hud.Mph, _bumperCam) = (s.HudOn, s.MapMode, s.HudScale, s.Mph, s.BumperCam);
        if (s.Livery != _carLivery) SwitchCar(Array.IndexOf(CarPaint.Cars, _carName), _paint);
        if (SavesRuns) s.Save();
    }

    private void ApplyGraphics()
    {
        var s = _settings;
        (_renderer.Msaa, _renderer.Shadows, _renderer.Ao, _renderer.Bloom, _renderer.Reflections, _renderer.RenderScale) =
            (s.Msaa, s.Shadows, s.Ao, s.Bloom, s.Ssr, s.RenderScale / 100f);
    }

    private bool? _vsync;
    private object? _applied;
    private object DisplayState => (_settings.Display, _settings.Width, _settings.Height, _settings.VSync, _settings.FrameCap);

    /// <summary>Window mode, size, vsync and frame cap from the settings (only with the menus: test runs keep their fixed window).</summary>
    private void ApplyDisplay()
    {
        var s = _settings;
        if (Equals(_applied, DisplayState)) return; // other options must not snap a dragged window back to the saved size
        _applied = DisplayState;
        Window.SetFullscreenMode(s.Display switch
        {
            Settings.DisplayMode.Borderless => Kansei.Windowing.FullscreenMode.Borderless,
            Settings.DisplayMode.Fullscreen => Kansei.Windowing.FullscreenMode.Exclusive,
            _ => Kansei.Windowing.FullscreenMode.Windowed,
        });
        if (s.Display != Settings.DisplayMode.Borderless) Window.SetResolution(s.Width, s.Height);
        if (_vsync != s.VSync) Device.SetVSync((_vsync = s.VSync).Value);
        FrameCap = s.FrameCap;
    }

    /// <summary>Sizes for RESOLUTION: the display's modes, in a window only those that fit its usable area (from 1024 wide).</summary>
    private IReadOnlyList<(int W, int H)> Resolutions()
    {
        var display = Math.Max(0, Window.DisplayIndex);
        var (w, h) = _settings.Display == Settings.DisplayMode.Fullscreen ? (int.MaxValue, int.MaxValue) : Window.GetUsableBounds(display);
        return [.. Window.GetResolutions(display).Where(r => r.W >= 1024 && r.W <= w && r.H <= h)];
    }

    /// <summary>
    ///     Camera behind the menus: car select and the result sheet turn around the parked car (as the original's turntable
    ///     and result showcase), title and the backdrop screens fly along the road (CRS_ROAD, 9 m up, looking 50 m ahead),
    ///     pause keeps the game's view. Returns false when the game camera should run (race, intro, finish).
    /// </summary>
    private bool UpdateMenuCamera(float dt)
    {
        if (ReplayCamera(dt)) return true;
        var front = _front is { Active: true } || _guide is { Active: true, ShowsCar: false } || _legend is { Active: true, ShowsCar: false } || _story is { Flyover: true } || _versusUi is { Active: true } || ReplayBackdrop;
        var screen = _menu?.Current ?? Menu.Screen.None;
        if (_legend is { ShowsCar: true } && _rivalModel != null)
        {
            LegendCamera();
            return true;
        }
        if (_guide is { ShowsCar: true })
        {
            // the guide's turntable: the car turns in place (UpdateCarMatrices), the camera stands on the road behind it; the
            // car sits right of the list (look turned left by ~0.23 screen heights) and comes to the middle while Iketani talks
            const float back = 8f;
            var target = _drive.Car.Position + Vector3.UnitY * 0.15f;
            var fwd = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, _drive.Car.Orientation) with { Y = 0 });
            var right = Vector3.Cross(fwd, Vector3.UnitY);
            _pos = target - fwd * back + Vector3.UnitY * 1.5f;
            (_camLook, _fov) = (target - right * (0.19f * back * _guide.Shift), MathF.PI / 4);
            return true;
        }
        var showcase = _story is { Showcase: true };
        if (!front && !showcase && screen is Menu.Screen.None or Menu.Screen.Intro or Menu.Screen.Finish) return false;
        if (!front && (showcase || screen is Menu.Screen.Car or Menu.Screen.Gearbox or Menu.Screen.Result))
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
        if (_probeView.Group == 1 || _probeView is { Group: 3, LinePoint: < 0 })
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
        _pos += Forward() * _probeView.Push;
        _renderer.Reflections = !_probeView.NoSsr;
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
        ("Race", 2, "race", 0, 0, false, true),
        // pause → REPLAY (the run so far, TV camera) → back; → PHOTO → back; CONTINUE
        ("Pause", 0.8f, "pause", 1, 0, false, false), ("Pause", 0.3f, null, 1, 0, false, false), ("Pause", 0.3f, null, 0, 0, true, false),
        ("Replay", 2.5f, "replay_pause", 0, 0, false, true),
        ("Pause", 0.5f, "pause_back_replay", 1, 0, false, false), ("Pause", 0.3f, null, 0, 0, true, false), // the cursor stays on REPLAY
        ("Photo", 1, "photo", 0, 0, false, true),
        ("Pause", 0.5f, "pause_back_photo", -1, 0, false, false), ("Pause", 0.3f, null, -1, 0, false, false), ("Pause", 0.3f, null, -1, 0, false, false), ("Pause", 0.3f, null, 0, 0, true, false),
        ("Finish", 1.2f, "finish", 0, 0, false, false), ("Result", 3.6f, "result", 1, 0, false, false), ("Result", 0.3f, null, 0, 0, true, false),
        ("Replay", 3, "replay_result", 0, 0, false, true), ("Result", 0.8f, "result_back", 1, 0, false, false),
        ("Result", 0.3f, null, 1, 0, false, false), ("Result", 0.3f, null, 1, 0, false, false),
        ("Result", 0.5f, "result_exit", 0, 0, true, false),
        ("Modes", 1, null, 0, 1, false, false), ("Modes", 0.5f, null, 0, 1, false, false), ("Modes", 0.5f, null, 0, 1, false, false), ("Modes", 0.5f, null, 0, 0, true, false),
        ("ReplayMenu", 1, "replay_menu", 0, 0, false, true),
        ("Modes", 1, null, 0, 1, false, false), ("Modes", 0.5f, null, 0, 1, false, false), ("Modes", 0.5f, null, 0, 0, true, false),
        ("SaveLoad", 1, "saveload", 0, 0, false, true),
        ("Modes", 1, null, 0, 1, false, false), ("Modes", 0.5f, null, 0, 0, true, false),
        ("Options", 1, "options", 0, 4, false, false), ("Options", 0.5f, "options_sound_section", 0, 0, true, false), ("Options", 0.5f, "options_sound", 0, 0, false, true),
        ("Options", 0.5f, null, 0, 0, false, true), ("Modes", 1.2f, "modes_end", 0, -1, false, false),
        // IKETANI'S CAR GUIDE: intro lines, down the list to the R32, next paint, Iketani talks, skip, back to the main menu
        ("Modes", 0.5f, null, 0, -1, false, false), ("Modes", 0.6f, "modes_guide", 0, 0, true, false),
        ("GuideIntro", 2, "guide_intro", 0, 0, true, false), ("GuideIntro", 1.5f, null, 0, 0, true, false), ("GuideIntro", 2.6f, "guide_intro_iketani", 0, 0, true, false),
        ("GuideList", 1.5f, "guide_list", 0, 1, false, false), .. Enumerable.Repeat(("GuideList", 0.4f, (string?)null, 0, 1, false, false), 6),
        ("GuideList", 1, "guide_r32", 1, 0, false, false), ("GuideList", 1, "guide_r32_paint", 0, 0, true, false),
        ("GuideList", 3.5f, "guide_talk_r32", 0, 0, true, false), ("GuideList", 1, "guide_skipped", 0, 0, false, true), ("Modes", 1.2f, "guide_back", 0, 0, false, false),
    ];

    /// <summary>
    ///     --flow with --bench: the player's way into a run as in a real session (title backdrop, Time Attack, Akina downhill at
    ///     the time of day/weather of the course argument (_DAY, _NIT, _RIN: the menus open on it), 24 car previews and 6 paint changes in the car select),
    ///     then the race in real time with the --bench log.
    /// </summary>
    private (string At, float Wait, string? Shot, int X, int Y, bool Ok, bool Back)[] FlowBenchScript =>
    [
        ("Boot", 1.2f, null, 0, 0, true, false), ("Logo", 1, null, 0, 0, true, false), ("Title", 3, null, 0, 0, true, false),
        ("Modes", 1, null, 0, 1, false, false), ("Modes", 0.6f, null, 0, 0, true, false), ("Course", 1, null, 0, 0, true, false),
        ("Route", 0.5f, null, 0, 0, true, false), ("Time", 0.5f, null, 0, 0, true, false),
        .. _benchCourse.EndsWith("_NIT") ? [] : new[] { ("Weather", 0.5f, (string?)null, 0, 0, true, false) },
        ("Maker", 0.5f, null, 0, 0, true, false), ("Maker", 0.5f, null, 0, 0, true, false),
        .. Enumerable.Repeat(("Car", 0.3f, (string?)null, 1, 0, false, false), 12), .. Enumerable.Repeat(("Car", 0.3f, (string?)null, -1, 0, false, false), 12),
        .. Enumerable.Repeat(("Car", 0.3f, (string?)null, 0, 1, false, false), 3), .. Enumerable.Repeat(("Car", 0.3f, (string?)null, 0, -1, false, false), 3),
        ("Car", 1, "bench_car", 0, 0, true, false), ("Gearbox", 0.5f, null, 0, 0, true, false),
    ];

    /// <summary>--offscreen: frames go into this target instead of the window (no display pacing, for GPU-bound --bench timing).</summary>
    public bool Offscreen { get; init; }
    private FrameCapture? _offscreen;

    /// <summary>--flow: the scripted key of this frame; asks for the step's PNG first (written next frame), quits after the last step (with --bench: races on).</summary>
    private (int X, int Y, bool Ok, bool Back) FlowKeys(float dt)
    {
        var script = bench != null ? FlowBenchScript : LegendFlow ? LegendFlowScript : StoryFlow ? StoryFlowScript : VersusStart == "flow" ? VersusFlowScript : FlowScript;
        if (_flowStep >= script.Length)
        {
            if (bench == null) Window.ShouldClose = true;
            return default;
        }
        var s = script[_flowStep];
        var at = ReplayFlowAt ?? (_front is { Active: true } ? _front.Current.ToString() : _guide is { Active: true } ? "Guide" + _guide.Current : _legend is { Active: true } ? "Legend" + _legend.Current
            : _story is { Active: true } ? "Story" + _story.Current : _versusUi is { Active: true } ? "Vs" + _versusUi.Current
            : _menu!.Current != Menu.Screen.None ? _menu.Current.ToString() : "Race");
        if (at != s.At)
        {
            _flowT = 0;
            return default;
        }
        if ((_flowT += MathF.Min(dt, 1 / 20f)) < s.Wait) return default; // clamped like the menus: a loading frame must not eat the wait
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

    /// <summary>
    ///     --bench: frame intervals after a 2 s warm-up, until <paramref name="seconds"/> have passed or the run has finished.
    ///     Per second a line: metres along the line, km/h, fps, frame avg/max, CPU ms (worst frame, without waiting for GPU/display),
    ///     draws per frame, effects, GCs and MB allocated, working set, macOS thermal state. At the end avg/p99/max, frames over
    ///     18/25 ms and each 500 m section. GPU cost: with --offscreen the frame time of a GPU-bound run is the GPU time.
    /// </summary>
    private bool Bench(in GameTime time, float seconds)
    {
        var along = _drive.Pilot.Track(_drive.Car.Position).Along;
        var draws = _renderer.DrawCalls;
        _renderer.DrawCalls = 0;
        if (_benchT0 < 0) _benchT0 = time.TotalTime;
        var t = time.TotalTime - _benchT0;
        if (t <= 2) return false;
        var ms = time.DeltaTime * 1000;
        _frameTimes.Add(ms);
        _benchAlong.Add(along);
        var b = _benchSecond;
        (b.Frames, b.Sum, b.Max, b.Cpu, b.Draws) = (b.Frames + 1, b.Sum + ms, MathF.Max(b.Max, ms), Math.Max(b.Cpu, CpuMs), Math.Max(b.Draws, draws));
        _fxPeak = (Math.Max(_fxPeak.Smoke, _fx.SmokeCount), Math.Max(_fxPeak.Skids, _fx.SkidCount), Math.Max(_fxPeak.Sparks, _fx.SparkCount));
        if (t - b.Start >= 1)
        {
            long gc = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2), alloc = GC.GetTotalAllocatedBytes();
            if (b.Start == 0) Console.WriteLine("\n[Bench]    t   m_linie  km/h  fps  avg_ms  max_ms  cpu_ms  draws  rauch/spur/funken  gc  alloc_mb  ws_mb  wärme");
            else
                Console.WriteLine($"\n[Bench] {t - 2,4:F0} {along,9:F0} {_drive.Car.SpeedKmh,5:F0} {b.Frames,4} {b.Sum / b.Frames,7:F2} {b.Max,7:F2} {b.Cpu,7:F2} {b.Draws,6} " +
                                  $"{_fx.SmokeCount,7}/{_fx.SkidCount}/{_fx.SparkCount,-5} {gc - b.Gc,4} {(alloc - b.Alloc) / 1e6,9:F2} {Environment.WorkingSet / 1e6,6:F0}  {Thermal.State}");
            _benchSecond = new BenchSecond { Start = t, Gc = gc, Alloc = alloc };
        }
        if (t < seconds + 2 && _hud.Timer.Phase != LapTimer.State.Finished) return false;
        var sorted = _frameTimes.Order().ToArray();
        Console.WriteLine($"\n[Bench] {_courseTime} {_carName} {Device.SwapchainWidth}x{Device.SwapchainHeight} Qualität {(_renderer.HighQuality ? "hoch" : "niedrig")}, {_renderer.TextureCount} Texturen: " +
                          $"{sorted.Length} Frames in {t - 2:F0} s, {along:F0} m, Frametime avg {sorted.Average():F2} ms, p99 {sorted[(int)(sorted.Length * 0.99)]:F2} ms, " +
                          $"max {sorted[^1]:F2} ms, > 18 ms: {sorted.Count(x => x > 18)}, > 25 ms: {sorted.Count(x => x > 25)}; Effekte max {_fxPeak.Smoke} Rauch, {_fxPeak.Skids} Spursegmente, {_fxPeak.Sparks} Funken");
        foreach (var g in _frameTimes.Select((x, i) => (t: x, s: (int)(_benchAlong[i] / 500))).GroupBy(x => x.s).OrderBy(g => g.Key))
            Console.WriteLine($"[Bench] Abschnitt {g.Key * 500,5}–{g.Key * 500 + 500,5} m: avg {g.Average(x => x.t):F2} ms, max {g.Max(x => x.t):F2} ms, > 18 ms: {g.Count(x => x.t > 18)}");
        Window.ShouldClose = true;
        return true;
    }

    private sealed class BenchSecond
    {
        public double Start, Cpu;
        public long Gc, Alloc;
        public int Frames, Draws;
        public float Sum, Max;
    }

    private BenchSecond _benchSecond = new();
    private double _benchT0 = -1;
    private readonly List<float> _benchAlong = [];

    /// <summary>
    ///     Car lights on at night, in rain and fog (<see cref="Headlights.For"/>), CRS_LIGHT points as sodium street lights at night
    ///     (on Akina they sit 6–7 m above and 5–10 m beside the road: lamp heads; the game itself only brightens the car near
    ///     them). Intensities tuned by eye.
    /// </summary>
    private void SetupLights(bool night)
    {
        var l = _renderer.Lights;
        _lights = new Headlights(Lights ?? Headlights.For(_courseTime, _fog));
        l.StreetLights = _course.Lights;
        l.StreetLightColor = night ? new Vector3(1f, 0.62f, 0.3f) * 50 : Vector3.Zero;
    }

    /// <summary>Per view: the view's car's lamps from its pose (<see cref="Headlights.Apply"/>), env maps of the road point nearest to it.</summary>
    private void UpdateLights(in ViewCar own)
    {
        var l = _renderer.Lights;
        own.Lights.Apply(l, own.Model.Lamp, own.Body, _renderer.Atmosphere.LocalLightShare, own.Brake, own.Reverse);
        l.Car = own.Body;
        if (_course.Env != null)
        {
            var (a, b, mix) = _course.EnvAt(own.Body.Translation);
            _renderer.SetEnvironment(a, b, mix);
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

    /// <summary>Extinction of the fog weather (1/m) where the car drives: visibility (5 % contrast) ≈ 3 / σ ≈ 60 m.</summary>
    public const float FogDensity = 0.05f;

    /// <summary>
    ///     Weather FOG over the day or night course: dense exponential fog around the car's altitude (<see cref="FogDensity"/>,
    ///     thinning ×1/e every 50 m upwards, thicker in the valleys below; the base follows the car), slowly drifting
    ///     banks, the sky hidden. Day: bright grey-white, overcast and flat (no sun, no shadows, soft ambient), the sun only
    ///     a faint brighter patch in the fog. Night: the night course's light in near-black fog, lamps glowing in it and
    ///     their light swallowed with distance (lighting.glsl). Tuned by eye.
    /// </summary>
    private static Atmosphere FogAtmosphere(bool night)
    {
        var a = AtmosphereFor(night ? "_NIT" : "_DAY");
        (a.Shadows, a.SunDisk, a.Specular, a.FogSun) = (false, Vector3.Zero, 0, Vector3.Zero);
        (a.FogStart, a.FogEnd, a.HeightFogDensity, a.HeightFogScale, a.FogDrift) = (0, 1e6f, FogDensity, 50, 0.6f);
        if (night)
        {
            (a.FogColor, a.LightGlow) = (new(0.006f, 0.007f, 0.010f), 0.0012f);
            (a.Zenith, a.Horizon) = (a.FogColor, a.FogColor);
            return a;
        }
        (a.FogColor, a.FogSun, a.LightGlow) = (new(0.50f, 0.52f, 0.54f), new(0.08f, 0.07f, 0.05f), 0.0006f);
        (a.Zenith, a.Horizon) = (a.FogColor * 1.1f, a.FogColor);
        (a.SunColor, a.ShadeSky, a.ShadeGround) = (Vector3.One, Vector3.One, Vector3.One);
        (a.SunIntensity, a.BakedKeep, a.BakedSun, a.Ambient) = (0.15f, 0.92f, 0.08f, new(0.36f, 0.37f, 0.39f));
        (a.ContactShadow, a.EnvStrength, a.Exposure, a.Contrast, a.Saturation, a.Vignette) = (0.7f, 3.5f, 1.3f, 1f, 0.82f, 0.3f);
        (a.BloomThreshold, a.BloomStrength) = (1.6f, 0.35f);
        return a;
    }

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

    /// <summary>
    ///     Force feedback to the wheel (<see cref="ForceFeedback"/>, from the last tick) and rumble to the pad while it is
    ///     the device in use; held by the menus: no force, except the test on the controls screen.
    /// </summary>
    private void SendForces()
    {
        var cfg = _settings.Controls;
        // only the wheel in use gets the physics: forces on an unattended wheel (autocentre off) would turn it and steal the steering
        var force = Frozen || _fly ? _menu?.Controls?.TestForce ?? 0 : _driver.Active == DeviceKind.Wheel ? _ffb.Output : 0;
        _driver.Wheel?.SetForce(cfg.FfbInvert ? -force : force);
        if (Frozen || _fly || _driver.Active != DeviceKind.Pad) return;
        var (low, high) = _ffb.PadRumble(cfg.Rumble);
        if (low + high > 0.02f) Input.RumblePad(low, high, 80);
    }

    private void UpdateDriver()
    {
        var k = Input.Keyboard;
        var d = _driver;
        var car = _drive.Car;
        if (d.Pressed(Control.ResetCar))
        {
            _drive.ResetNearest();
            SyncPose();
        }
        if (k.IsKeyPressed(Key.B) && _race == null && _vsRace == null)
        {
            using (var iso = new Iso9660(isoPath)) _drive.SetDirection(iso, !_drive.Reverse);
            _drive.ResetNearest();
            _hud = NewHud();
            Console.WriteLine($"\n[Touge] Richtung: {(_drive.Reverse ? "rückwärts (CRS_COLI _1, DRV _O)" : "vorwärts (_0, _I)")}");
            SyncPose();
        }
        if (d.Pressed(Control.Camera))
            (_bumperCam, _settings.BumperCam, _camSnap) = (!_bumperCam, !_bumperCam, true);
        if (k.IsKeyPressed(Key.T)) car.AutomaticGearbox = !car.AutomaticGearbox;
        if (d.Pressed(Control.Lights)) _lights.Toggle();
        if (d.Pressed(Control.HighBeam)) _lights.ToggleHigh();
        if (d.Shift(car) is var shift and not 0) _pendingShift = shift; // steering, pedals: DriverInput
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
        // the guide's turntable turns the parked car in place
        var spin = _guide is { ShowsCar: true } ? Matrix4x4.CreateRotationY(2.5f + _menuTime * 0.3f) : Matrix4x4.Identity;
        (_carPose, _carBody) = PoseCar(_drive.Car, _car, _modelToBody, _prevPos, _prevRot, alpha, _carWheels, spin);
        UpdateRivalMatrices(alpha);
        UpdateVersusMatrices(alpha);
        UpdateShowMatrices(alpha);
    }

    /// <summary>Pose (physics body → world) and model matrix of <paramref name="car"/> interpolated by <paramref name="alpha"/>, its wheel matrices into <paramref name="wheels"/>.</summary>
    private static (Matrix4x4 Pose, Matrix4x4 Body) PoseCar(Vehicle car, CarModel model, in Matrix4x4 modelToBody, Vector3 prevPos, Quaternion prevRot, float alpha,
        Span<Matrix4x4> wheels, in Matrix4x4 spin)
    {
        var pose = spin * Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(prevRot, car.Orientation, alpha))
                   * Matrix4x4.CreateTranslation(Vector3.Lerp(prevPos, car.Position, alpha));
        var body = modelToBody * pose;
        var restY = car.Spec.WheelRadius - car.Spec.CogHeight;
        for (var i = 0; i < 4; i++)
        {
            // CarParts node order fr_l, fr_r, re_l, re_r = physics FL, FR, RL, RR; model node X/Z, physics suspension travel
            var w = car.Wheels[i];
            var node = model.Wheels[i];
            var spinSteer = Matrix4x4.CreateRotationX(w.SpinAngle) * Matrix4x4.CreateRotationY(-w.SteerAngle);
            var flip = node with { M41 = 0, M42 = 0, M43 = 0 };
            wheels[i] = flip * spinSteer * Matrix4x4.CreateTranslation(node.Translation + new Vector3(0, w.LocalCenter.Y - restY, 0)) * body;
        }
        return (pose, body);
    }

    /// <summary>Chase camera (spring towards a point behind/above the car, looks a bit ahead) or bumper camera.</summary>
    private void UpdateDriveCamera(float dt) => UpdateDriveCamera(dt, _drive.Car, _carPose);

    /// <summary>The chase/bumper camera of <paramref name="car"/> at its interpolated <paramref name="carPose"/> (split screen: player 2's too).</summary>
    private void UpdateDriveCamera(float dt, Vehicle car, Matrix4x4 carPose)
    {
        var pos = carPose.Translation; // interpolated CoG
        var fwd = Vector3.TransformNormal(Vector3.UnitZ, carPose);
        if (_bumperCam)
        {
            var up = Vector3.TransformNormal(Vector3.UnitY, carPose);
            _pos = pos + up * 0.15f + fwd * (car.Spec.Length / 2 + 0.05f);
            _camLook = _pos + fwd * 10;
            _fov = _settings.Fov * MathF.PI / 180;
            _camSnap = false;
            return;
        }
        var flat = Vector3.Normalize(fwd with { Y = 0 });
        var desired = pos - flat * 5.8f + Vector3.UnitY * 1.9f;
        var look = pos + flat * 4f + Vector3.UnitY * 0.6f;
        float a = _camSnap ? 1 : 1 - MathF.Exp(-6 * dt), b = _camSnap ? 1 : 1 - MathF.Exp(-12 * dt);
        _pos = Vector3.Lerp(_pos, desired, a);
        _camLook = Vector3.Lerp(_camLook, look, b);
        _fov = _settings.Fov * MathF.PI / 180 + MathF.Min(car.SpeedKmh / 180, 1) * 0.2f;
        _camSnap = false;
        // wall hits shake the camera briefly (up to 12 cm, decays in ~0.3 s)
        _shake *= MathF.Exp(-10 * dt);
        _shakeOffset = new Vector3(MathF.Sin(_simTime * 53), MathF.Sin(_simTime * 47 + 1), MathF.Sin(_simTime * 61 + 2)) * (0.12f * _shake * _settings.CameraShake);
    }

    /// <summary>The car a view belongs to: its lamps light the scene (the other cars only glow), the fog layer and env maps follow it.</summary>
    private readonly record struct ViewCar(CarModel Model, Matrix4x4 Body, Matrix4x4[] Wheels, Headlights Lights, float Brake, bool Reverse, Vehicle Vehicle);

    private readonly (StaticMesh, Matrix4x4)[] _casters = new (StaticMesh, Matrix4x4)[5 + 5 + 5 * MaxVersusCars];

    private ViewCar PlayerView => new(_car, _carBody, _carWheels, _lights, _fly ? 0 : _brakeLight, !_fly && _drive.Car.Gear < 0, _drive.Car);

    public override void Render(in FrameContext ctx)
    {
        var shot = _shotState == 1 ? _capture : null;
        var frame = shot ?? _offscreen; // where the frame goes (null: the window)
        var (w, h) = frame != null ? (frame.Width, frame.Height) : (Device.SwapchainWidth, Device.SwapchainHeight);
        UpdateCarMatrices(shot != null ? 1 : ReplayAlpha(ctx.TickAlpha));
        if (!UpdateMenuCamera(ctx.Time.DeltaTime) && !_fly) UpdateDriveCamera(ctx.Time.DeltaTime);
        var split = SplitViews(w, h);
        if (split is var (first, second))
        {
            // split screen: player 1's view, then player 2's with its camera, car and lamps
            RenderView(ctx, shot, frame, first, PlayerView, 0);
            RenderP2View(ctx, shot, frame, second);
        }
        else
        {
            var aspect = shot != null ? (float)shot.Width / shot.Height
                : ctx.Viewport.Height > 0 ? (float)ctx.Viewport.Width / ctx.Viewport.Height : 16f / 9f;
            RenderView(ctx, shot, frame, null, PlayerView, 0, aspect);
        }
        var menuShown = _menu is { Current: not Menu.Screen.None };
        var hudShown = _hud.Visible && (!menuShown || _menu!.OverRace); // telop/countdown and pause lie over the HUD
        if (ReplayOverlay(w, h)) hudShown = false; // viewer, photo mode, REPLAY & RECORD, SAVE & LOAD
        else if (_front is { Active: true }) _front.Build(_overlay, w, h);
        else if (_guide is { Active: true }) _guide.Build(_overlay, w, h);
        else if (_legend is { Active: true }) _legend.Build(_overlay, w, h);
        else if (_story is { Active: true, OverRace: false })
        {
            _overlay.Clear();
            _story.Build(_overlay, w, h);
        }
        else if (_versusUi is { Active: true }) _versusUi.Build(_overlay, w, h);
        else if (hudShown)
        {
            var (hw, hh) = split is var (v0, _) ? (v0.Width, v0.Height) : (w, h);
            _hud.Lights = _lights.State;
            _hud.Rival = _race is { } race ? (_rivalPose.Translation, race.Cars[1].Along) : VersusRival(0);
            _hud.Build(_overlay, hw, hh, _carPose.Translation, Vector3.TransformNormal(Vector3.UnitZ, _carPose), _drive.Car, _carName, _menuTime);
            BuildBattleHud(hw, hh);
            _story?.BuildHud(_overlay, hw, hh, _race?.Battle);
            BuildVersusHud(_overlay, hw, hh, 0);
        }
        else _overlay.Clear();
        if (_versusUi is not { Active: true } && split is var (s1, s2)) SplitSeam(s1, s2);
        var target = frame?.View ?? Device.CurrentSwapchainView;
        if (_versusUi is not { Active: true } && hudShown && split is var (_, p2))
        {
            // player 2's HUD in its own overlay, moved into its half
            BuildP2Hud(p2);
            DrawOverlay(ctx.Encoder, _p2Overlay, target, w, h);
        }
        if (_front is not { Active: true } && _guide is not { Active: true } && _legend is not { Active: true } && _versusUi is not { Active: true } && menuShown)
        {
            // HUD as its own layer first: one overlay draws all shapes before all text, so HUD text would land on the menu panels
            DrawOverlay(ctx.Encoder, target, w, h);
            _overlay.Clear();
            _menu!.Build(_overlay, w, h);
        }
        if (_story is { Active: true, OverRace: true }) // the story's banner over the running-out race
        {
            DrawOverlay(ctx.Encoder, target, w, h);
            _overlay.Clear();
            _story.Build(_overlay, w, h);
        }
        if (_jukebox is { Playing: true, Current: { } song } && _music == Menu.RaceMusic && _front is not { Active: true } && _guide is not { Active: true } && _legend is not { Active: true }
            && _story is not { Active: true } && _versusUi is not { Active: true } && !OverlayQuiet && ShowsNowPlaying(_menu?.Current == Menu.Screen.Pause))
            NowPlaying.Draw(_overlay, w, h, song, _jukebox.Since, hold: _menu?.Current == Menu.Screen.Pause, below: NowPlayingBelow(split), clear: NowPlayingClear(w, h, split));
        if (InputDebug) InputDebugView.Build(_overlay, w, h, Input, _driver, _ffb);
        DrawOverlay(ctx.Encoder, target, w, h);
        if (shot != null)
        {
            shot.Copy(ctx.Encoder);
            _shotState = 2;
        }
    }

    /// <summary>
    ///     One 3D view: shadows, sky, course, every car, effects and rain from the current camera into <paramref name="frame"/>
    ///     (or the window), into <paramref name="viewport"/> of it (split screen) or the whole. <paramref name="own"/> is the car
    ///     whose lamps light the road; the others draw with their own lamps glowing (<paramref name="view"/>: whose view, 0/1).
    /// </summary>
    private void RenderView(in FrameContext ctx, FrameCapture? shot, FrameCapture? frame, Kansei.Core.Viewport? viewport, ViewCar own, int view, float aspect = 0)
    {
        if (viewport is { } vp) aspect = (float)vp.Width / Math.Max(1, vp.Height);
        // Game data is right-handed (y up). Vulkan clip space is Y-down, Metal/GL Y-up.
        var proj = WorldRenderer.Perspective(_fov, aspect, 0.3f, Device.Backend == Penelope.BackendKind.Vulkan);
        var shake = _fly ? Vector3.Zero : _shakeOffset; // moves the view only, not the camera spring
        var view3 = Matrix4x4.CreateLookAt(_pos + shake, _camLook + shake * 0.5f, Vector3.UnitY);
        var skyView = view3; // analytic sky + sun
        if (_probeView.Spin != 0)
        {
            // --flicker: camera and world turned together (same image, different depth rounding)
            var spin = Matrix4x4.CreateRotationY(_probeView.Spin);
            view3 = spin * Matrix4x4.CreateLookAt(Vector3.Transform(_pos, spin), Vector3.Transform(_camLook, spin), Vector3.UnitY);
        }

        UpdateLights(own);
        var shell = own.Lights.State != Headlights.Mode.Off ? own.Model.Lit : own.Model.Day;
        if (_fog) _renderer.Atmosphere.HeightFogBase = own.Body.Translation.Y; // the fog layer lies where the car drives
        var p1Shell = _lights.State != Headlights.Mode.Off ? _car.Lit : _car.Day;
        Span<(StaticMesh, Matrix4x4)> casters = _casters;
        (casters[0], casters[1], casters[2], casters[3], casters[4]) =
            ((p1Shell.Body, _carBody), (_car.Wheel, _carWheels[0]), (_car.Wheel, _carWheels[1]), (_car.Wheel, _carWheels[2]), (_car.Wheel, _carWheels[3]));
        var n = 5 + RivalCasters(casters[5..]);
        n += VersusCasters(casters[n..]);
        _renderer.RenderShadows(ctx.Encoder, _pos, Vector3.Normalize(_camLook - _pos), _fov, aspect, _course.World, casters[..n]);
        var pass = _renderer.BeginScene(ctx.Encoder, skyView, proj, frame, viewport);
        _renderer.Time = _simTime;
        _renderer.DrawSky(pass, _course.Sky, Matrix4x4.CreateTranslation(_pos with { Y = 0 }) * view3 * proj, _pos); // follows the camera
        var carView = _probe != null && _probeView.Group == 1;
        if (_probe == null || !carView) _renderer.Draw(pass, _course.World, view3 * proj, _pos);
        if (_probe == null || carView || _probeView.Group == 3)
        {
            _carRenderer.Draw(pass, shell.Body, shell.Decals, own.Model.Wheel, own.Body, own.Wheels, view3 * proj, _pos);
            if (shell.PopUp is { } popUp) _carRenderer.DrawPart(pass, popUp, own.Model.Lamp.PopUpAt(own.Lights.Open) * own.Body, view3 * proj, _pos);
            DrawRival(pass, view3 * proj);
            DrawVersusCars(pass, view3 * proj, view);
            DrawShowCars(pass, view3 * proj);
        }
        _fxRenderer.Draw(pass, _fx, view3, view3 * proj, _pos);
        // camera velocity stretches the rain streaks; a shot has no previous frame, the chase camera moves with the car
        var frameDt = ctx.Time.DeltaTime;
        _camVelocity = shot != null ? (_fly ? Vector3.Zero : own.Vehicle.Velocity)
            : frameDt > 0 ? Vector3.Lerp(_camVelocity, (_pos - _lastCamPos) / frameDt, 0.3f) : _camVelocity;
        _lastCamPos = _pos;
        var heightPx = viewport?.Height ?? shot?.Height ?? Device.SwapchainHeight;
        _fxRenderer.DrawRain(pass, view3 * proj, _pos, _camVelocity, _simTime, 2 * MathF.Tan(_fov / 2) / heightPx);
        _renderer.EndScene(ctx.Encoder, pass, frame, viewport);
    }

    /// <summary>A pad button the player bound to a driving control (then its fixed extra, e.g. D-pad right = next song, stays off).</summary>
    private bool PadBound(GamepadButton b) => _settings.Controls.Pad.Values.Any(binds => binds.Contains(Bind.Pad(b)));

    private void DrawOverlay(Penelope.ICommandEncoder encoder, Penelope.TextureViewHandle target, int w, int h) => DrawOverlay(encoder, _overlay, target, w, h);

    private void DrawOverlay(Penelope.ICommandEncoder encoder, Overlay overlay, Penelope.TextureViewHandle target, int w, int h)
    {
        _overlayRenderer.Draw(encoder, overlay, target, w, h);
        _textRenderer.Draw(encoder, overlay, target, w, h);
    }

    public override void Dispose()
    {
        EndRecording();
        _audio?.Dispose();
        _rivalAudio?.Dispose();
        DisposeVersus();
        _menuAudio?.Dispose();
        _guideVoice?.Dispose();
        _audioDevice?.Dispose();
        _course.World.Dispose();
        _course.Sky.Dispose();
        DisposeVersusCars();
        DisposeRival();
        _car.Dispose();
        _carRenderer.Dispose();
        _fxRenderer.Dispose();
        _overlayRenderer.Dispose();
        _textRenderer.Dispose();
        _capture?.Dispose();
        _offscreen?.Dispose();
        _renderer.Dispose();
    }
}
