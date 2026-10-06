using System.Numerics;
using System.Text.Json;
using Kansei.Core;
using Kansei.Graphics;
using Kansei.Input;
using Kansei.Physics;
using Touge.Formats;
using Touge.Net;
using Touge.Race;
using Touge.Ui;

namespace Touge;

/// <summary>
///     VERSUS (<see cref="Versus"/> screens, Touge/Net): split screen — two local players, each with a view (top/bottom or
///     left/right), camera, HUD, lamps that light their road and their own controller (pads by index, or the keyboard split
///     into WASD and arrows) — and online — up to four players over UDP (<see cref="NetSession"/>, <see cref="NetRace"/>),
///     the others as <see cref="RemoteDriver"/> puppets. Both race in a <see cref="RaceSession"/> with car-to-car contacts and
///     are decided by a <see cref="Referee"/> (split: here; online: by the host). Every other car is a <see cref="VsCar"/>:
///     model, lamps, engine/tyre sound heard from player 1's camera, smoke and skids. The session runs on through every screen
///     (<see cref="VersusUpdate"/> each frame); the host decides when everybody loads, counts down (the intro synced to the
///     shared race clock) and races.
/// </summary>
public sealed partial class TougeGame
{
    /// <summary>Other cars at most (online: four players).</summary>
    public const int MaxVersusCars = NetSession.MaxPlayers - 1;

    /// <summary>--versus split|host|join[:address]|menu: start in that versus mode (tests, pictures); with <see cref="VersusBot"/> the autopilot drives and the lobby goes by itself.</summary>
    public string? VersusStart { get; init; }
    public bool VersusBot { get; init; }
    /// <summary>--split vertical: left/right instead of top/bottom.</summary>
    public bool VersusVertical { get; init; }
    /// <summary>--car2: player 2's car in a --versus split run.</summary>
    public string? Car2 { get; init; }
    /// <summary>--players: a bot host starts once this many are in and ready.</summary>
    public int VersusPlayers { get; init; } = 2;
    /// <summary>--net-sim / --port / --name / --net-rule for --versus runs (the menus use the saved name and port).</summary>
    public NetSim? NetSim { get; init; }
    public int? NetPort { get; init; }
    public string? PlayerName { get; init; }
    public NetRule VersusRule { get; init; }
    /// <summary>--shot-after: the --shot frame is taken this many seconds after the start instead of at once.</summary>
    public float ShotAfter { get; init; }

    /// <summary>Another car of a versus race: player 2 or a remote player.</summary>
    private sealed class VsCar(byte id, string name, string car, int paint)
    {
        public readonly byte Id = id;
        public readonly string Name = name, Car = car;
        public readonly int Paint = paint;
        public CarModel? Model;
        public Matrix4x4 ModelToBody, Pose, Body;
        public readonly Matrix4x4[] Wheels = new Matrix4x4[4];
        public Headlights Lights = new(Headlights.Mode.Off);
        public readonly SceneLights Lamps = new();
        public GameAudio? Audio;
        public readonly float[] Smoke = new float[4], Spray = new float[4];
        public RaceCar? Race;
        public bool Gone => Race?.Driver is RemoteDriver { Player.Connected: false };
    }

    /// <summary>The camera of player 2's view (swapped in while it renders).</summary>
    private struct CamState
    {
        public Vector3 Pos, Look, ShakeOffset, Velocity, Last;
        public float Fov, Shake;
        public bool Snap;
        public CameraView View;
        public CameraView? OnBoard;
        public CameraRig.Follow Follow;
    }

    private Versus? _versusUi;
    private NetSession? _net;
    private NetDiscovery? _lan;
    private RaceSession? _vsRace;
    private NetRace? _netRace;
    private Referee? _vsReferee;
    private Result? _vsResult;
    private float _vsDecidedAt = -1;
    private int _vsLoaded = -1;
    private bool _vsSplit, _vsLoadPending, _vsShotTaken, _vsPauseShown;
    private RaceConfig _vsConfig = new();
    /// <summary>What is loaded for versus (course, cars): a rematch of the same skips the load.</summary>
    private string? _vsLoadedKey;
    private readonly List<VsCar> _vsCars = [];
    private readonly ManualDriver _p1 = new(), _p2 = new();
    private DriverInput? _p2Input, _savedDriver;
    private string _p2ResetKey = "BACKSPACE";
    private int _p2Shift;
    private Hud? _hud2;
    private CamState _cam2;
    private readonly Overlay _p2Overlay = new();
    private readonly SeatKeys _k1 = new(), _k2 = new();
    private readonly SceneLights _p1Lamps = new();
    private float _vsLog;

    private string MyName => PlayerName ?? _settings.PlayerName;
    private int MyPort => _versusUi?.Port ?? NetPort ?? _settings.NetPort; // the ONLINE screen's UDP PORT row

    // ------------------------------------------------------------ opening and the screens

    /// <summary>VERSUS from the main menu.</summary>
    private void OpenVersus()
    {
        _versusUi ??= new Versus(_catalog!) { Sound = n => _menuAudio?.Play(n), CarLocked = id => Race.Legend.CarLocked(id, _progress) };
        _versusUi.Open();
        (_versusUi.Name, _versusUi.Address, _versusUi.Vertical, _versusUi.Port) = (_settings.PlayerName, _settings.JoinAddress, _settings.SplitVertical, NetPort ?? _settings.NetPort);
        _inRace = false;
    }

    /// <summary>--versus …: straight into split screen, hosting or joining (and with --bot through the lobby on its own).</summary>
    private void StartVersusCli()
    {
        if (VersusStart is null or "flow" || _catalog == null) return; // flow: through the main menu like a player
        OpenVersus();
        var ui = _versusUi!;
        ui.Name = MyName;
        ui.Pads = Input.Pads.Count;
        var cfg = new RaceConfig(_courseTime, Reverse, Fog && !_courseTime.EndsWith("_RIN"), VersusRule);
        var mode = VersusStart.Split(':', 2);
        switch (mode[0])
        {
            case "split":
                ui.OpenSplit(Car, Paint);
                (ui.SplitConfig, ui.Vertical) = (cfg, VersusVertical);
                if (Car2 != null) ui.Seats[1].Car = Math.Max(0, _catalog.Cars.ToList().FindIndex(c => c.Id.Equals(Car2, StringComparison.OrdinalIgnoreCase)));
                if (!VersusBot) break;
                ui.Seats[1].Ready = true;
                if (shotPath != null && autodrive is { } seconds && ShotAfter <= 0)
                {
                    // a picture of the race after that long: load and race now, before the first frame
                    ui.ShowLoading();
                    LoadVersusRace();
                    VersusAutoDrive(seconds);
                }
                else StartSplit();
                break;
            case "host":
                HostSession();
                _net?.SetConfig(cfg);
                break;
            case "join":
                ui.Address = mode.Length > 1 ? mode[1] : "127.0.0.1";
                JoinSession();
                break;
            case "online":
                ui.ShowOnline();
                break;
        }
        if (shotPath != null && ShotAfter <= 0) ui.Settle();
    }

    private void HostSession()
    {
        var ui = _versusUi!;
        _lan?.Dispose();
        _lan = null;
        try
        {
            _net = NetSession.Host(MyPort, ui.Name, NetSim, log: l => Console.WriteLine("\n" + l));
        }
        catch (System.Net.Sockets.SocketException e)
        {
            ui.ShowMessage($"PORT {MyPort} IN USE", Versus.Screen.Online);
            Console.WriteLine($"\n[Versus] Host auf Port {MyPort} nicht möglich: {e.Message}");
            return;
        }
        var ips = NetLink.LocalAddresses().Select(a => a.ToString()).ToArray();
        ui.HostInfo = $"{(ips.Length > 0 ? string.Join("  ", ips.Take(2)) : "127.0.0.1")} : {_net.Link.Port}";
        ui.OpenLobby(_net, _settings.Car, _settings.Paint);
        _vsLoaded = -1;
    }

    private void JoinSession()
    {
        var ui = _versusUi!;
        var target = ui.JoinLan?.EndPoint ?? NetLink.Resolve(ui.Address, MyPort);
        if (target == null)
        {
            ui.ShowMessage("UNKNOWN ADDRESS", Versus.Screen.Online);
            return;
        }
        _lan?.Dispose();
        _lan = null;
        _net = NetSession.Join(target, ui.Name, NetSim, log: l => Console.WriteLine("\n" + l));
        _vsLoaded = -1;
        ui.ShowConnecting();
    }

    private void CloseSession()
    {
        _net?.Dispose();
        _net = null;
        EndVersusRace();
    }

    /// <summary>
    ///     Every frame, before anything else: the session (receive, send, timeouts), the LAN list, the versus screens, the host's
    ///     phases (load, countdown, results), player 2's controls. True when a versus screen took the frame.
    /// </summary>
    private bool VersusUpdate((int X, int Y, bool Ok, bool Back) keys, float dt)
    {
        if (_versusUi == null) return false;
        var ui = _versusUi;
        _frameDt = dt;
        if (shotPath != null && ShotAfter > 0 && !_vsShotTaken && _menuTime >= ShotAfter && _shotState == 0) (_shotState, _vsShotTaken) = (1, true);
        _net?.Update();
        if (ui.Current == Versus.Screen.Online)
        {
            _lan ??= new NetDiscovery(MyPort);
            _lan.Update();
            ui.Lan = _lan.Games;
        }
        ui.Pads = Input.Pads.Count;
        if (_net != null) FollowSession(ui);
        if (_vsSplit && _p2Input != null)
        {
            _p2Input.Update(Input, dt);
            var p2 = _vsCars.Count > 0 ? _vsCars[0] : null;
            if (p2?.Race != null && _vsRace != null && !Frozen)
            {
                if (_p2Input.Shift(p2.Race.Vehicle) is var shift and not 0) _p2Shift = shift;
                if (_p2Input.Pressed(Control.ResetCar)) ResetP2();
                if (_p2Input.Pressed(Control.Camera)) (_cam2.View, _cam2.Snap) = (CameraRig.Next(_cam2.View), true);
                if (_p2Input.Pressed(Control.Lights)) p2.Lights.Toggle();
                if (_p2Input.Pressed(Control.HighBeam)) p2.Lights.ToggleHigh();
            }
        }
        if (_netRace != null && _menu?.Current == Menu.Screen.Intro) _menu.SyncIntro(_net!.RaceTime);
        if (StartMenu == "pause" && !_vsPauseShown && _netRace != null && _net!.RaceTime > 2 && _menu?.Current == Menu.Screen.None)
        {
            _vsPauseShown = true; // --versus … --menu pause: the pause over a running online race (screenshots)
            OpenMenu(Menu.Screen.Pause);
        }
        if (VersusBot && _netRace != null && (_vsLog += dt) >= 1)
        {
            _vsLog = 0;
            LogNetRace();
        }
        if (_vsLoadPending && ui.Current == Versus.Screen.Loading && ui.Shown)
        {
            _vsLoadPending = false;
            LoadVersusRace();
        }
        FreeBattleLoadStep();
        if (!ui.Active) return false;
        // from here the frame's keys are the versus screens' (a BACK that leaves them must not also act on the main menu)
        var split = ui.Split && ui.Current == Versus.Screen.Lobby;
        var p2Pad = ui.P2Device < Input.Pads.Count ? Input.Pads[ui.P2Device] : null;
        var k1 = split ? _k1.Read(Input, dt, p2Pad == null ? SeatKeys.Wasd : SeatKeys.All, [.. Input.Pads.Where(p => p != p2Pad)]) : keys;
        var k2 = split ? _k2.Read(Input, dt, p2Pad == null ? SeatKeys.Arrows : SeatKeys.None, p2Pad == null ? [] : [p2Pad]) : default;
        if (Flow != null) (k1, k2) = (keys, split ? keys : default); // --flow --versus flow: the script plays both
        var k = Input.Keyboard;
        var text = new Versus.TextKeys(Input.TypedText, k.IsKeyRepeating(Key.Backspace, dt), k.IsKeyPressed(Key.Enter), k.IsKeyPressed(Key.Escape));
        if (VersusBot && ui.Current == Versus.Screen.Lobby && _net != null)
        {
            // --bot: a guest is ready at once, a hosting bot starts when enough players are in
            if (!_net.IsHost && !ui.Seats[0].Ready) ui.ForceReady();
            if (_net.IsHost && _net.CanStart && _net.Players.Count(p => p.Connected) >= VersusPlayers) _net.StartRace();
        }
        var action = ui.Update(k1, k2, text, dt);
        if (FreeBattleAction(action)) return true; // VS CPU (TougeGame.FreeBattle)
        switch (action)
        {
            case Versus.Action.PreviewCar:
                SwitchCar(Array.IndexOf(CarPaint.Cars, ui.PickCarId), ui.PickPaint);
                break;
            case Versus.Action.Split:
                ui.OpenSplit(_settings.Car, _settings.Paint);
                ui.Vertical = _settings.SplitVertical;
                break;
            case Versus.Action.Host:
                SaveVersusChoice();
                HostSession();
                break;
            case Versus.Action.Join:
                SaveVersusChoice();
                JoinSession();
                break;
            case Versus.Action.Start when ui.Split:
                SaveVersusChoice();
                StartSplit();
                break;
            case Versus.Action.Start:
                SaveVersusChoice();
                _net?.StartRace();
                break;
            case Versus.Action.Rematch when ui.Split:
                StartSplit();
                break;
            case Versus.Action.Rematch when _net is { CanStart: true }:
                _net.StartRace();
                break;
            case Versus.Action.Rematch: // the others left (or are not ready): back to the lobby instead
                _menuAudio?.Play("BEEP001");
                _net?.BackToLobby();
                EndVersusRace();
                ui.BackToLobby();
                break;
            case Versus.Action.ToLobby:
                if (_net?.IsHost == true) _net.BackToLobby();
                EndVersusRace();
                ui.BackToLobby();
                break;
            case Versus.Action.Leave:
                CloseSession();
                ui.ShowOnline();
                break;
            case Versus.Action.Exit:
                CloseSession();
                _lan?.Dispose();
                _lan = null;
                ui.Close();
                DisposeVersusCars(); // the last loaded: their textures go first
                _vsCars.Clear();
                _vsLoadedKey = null;
                _vsSplit = false;
                if (_front == null) Window.ShouldClose = true;
                else _front.Open(FrontEnd.Step.Modes);
                break;
        }
        return true;
    }

    /// <summary>What the menus remember of a versus choice: name, address, split layout, player 1's car.</summary>
    private void SaveVersusChoice()
    {
        var ui = _versusUi!;
        (_settings.PlayerName, _settings.JoinAddress, _settings.SplitVertical) = (ui.Name, ui.Address, ui.Vertical);
        if (NetPort == null || ui.Port != NetPort) _settings.NetPort = ui.Port; // a --port run keeps the saved port unless changed on screen
        if (ui.Active && ui.Current == Versus.Screen.Lobby) (_settings.Car, _settings.Paint) = (ui.CarId(0), ui.Seats[0].Paint);
        if (SavesRuns) _settings.Save();
    }

    /// <summary>The client side of the host's phases (and the host's own): joined → lobby, loading, countdown, results, back to the lobby, lost.</summary>
    private void FollowSession(Versus ui)
    {
        var net = _net!;
        if (net.Ended != null)
        {
            var reason = net.Ended;
            CloseSession();
            CloseRaceMenus();
            if (reason is not ("LEFT" or "DONE")) ui.ShowMessage(reason, Versus.Screen.Online);
            else ui.ShowOnline();
            return;
        }
        if (ui.Current == Versus.Screen.Connecting && net.Joined) ui.OpenLobby(net, _settings.Car, _settings.Paint);
        switch (net.Phase)
        {
            case Phase.Lobby when ui.Current is Versus.Screen.Result or Versus.Screen.Loading || (_vsRace != null && !ui.Active):
                EndVersusRace();
                CloseRaceMenus();
                ui.BackToLobby();
                break;
            case Phase.Loading when _vsLoaded != net.RaceId && ui.Current != Versus.Screen.Loading:
                EndVersusRace();
                CloseRaceMenus();
                ui.ShowLoading();
                _vsLoadPending = true;
                break;
            case Phase.Countdown or Phase.Race when _vsLoaded == net.RaceId && _netRace == null:
                ui.Close();
                NewVersusRace();
                OpenMenu(Menu.Screen.Intro);
                _menu!.SyncIntro(net.RaceTime);
                break;
        }
    }

    private void CloseRaceMenus()
    {
        if (_menu?.Current is Menu.Screen.Pause or Menu.Screen.Intro) _menu.Close();
    }

    /// <summary>Split screen: the lobby's choice is loaded behind the white loading screen (next frame).</summary>
    private void StartSplit()
    {
        EndVersusRace();
        _versusUi!.ShowLoading();
        _vsLoadPending = true;
    }

    /// <summary>
    ///     Loads the race of the lobby: the course with player 1's car, the other cars (player 2, or every remote player), then
    ///     split screen starts its race at once (telop, countdown), online reports "loaded" and waits for the host's countdown.
    /// </summary>
    private void LoadVersusRace()
    {
        var ui = _versusUi!;
        _vsSplit = ui.Split;
        var config = ui.Split ? ui.SplitConfig : _net!.Config;
        List<VsCar> cars = ui.Split
            ? [new VsCar(1, "PLAYER 2", ui.CarId(1), ui.Seats[1].Paint)]
            : [.. _net!.Players.Where(p => !p.IsLocal && p.Connected).Take(MaxVersusCars).Select(p => new VsCar(p.Id, p.Name, CarSpecs.All.ContainsKey(p.Car) ? p.Car : "AE86T", p.Paint))];
        var key = $"{config} {ui.CarId(0)}/{ui.Seats[0].Paint} {string.Join(' ', cars.Select(c => $"{c.Id}:{c.Name}:{c.Car}/{c.Paint}"))} {_settings.Livery}";
        if (key == _vsLoadedKey && _vsCars.Count > 0 && _carName == ui.CarId(0) && _paint == ui.Seats[0].Paint) // the car select may have shown another car
        {
            // RETRY / rematch with the same course and cars: only a fresh start
            (_hud, _fx, _simTime) = (NewHud(), new Effects(), 0);
            _drive.ResetTo(0);
        }
        else
        {
            DisposeVersusCars();
            _vsCars.Clear();
            _vsCars.AddRange(cars);
            _vsConfig = config;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var iso = new Iso9660(isoPath)) LoadCourse(iso, _vsConfig.CourseTime, _vsConfig.Reverse, ui.CarId(0), ui.Seats[0].Paint, fog: _vsConfig.Fog);
            _vsLoadedKey = key;
            Console.WriteLine($"\n[Versus] {(ui.Split ? "Geteilter Bildschirm" : "Online")}: {_vsConfig.CourseTime}, {1 + _vsCars.Count} Autos geladen in {sw.ElapsedMilliseconds} ms");
        }
        if (_front != null) _drive.Car.AutomaticGearbox = !_settings.Manual;
        if (ui.Split)
        {
            SetupSplitControls();
            ui.Close();
            NewVersusRace();
            OpenMenu(Menu.Screen.Intro);
            if (shotPath != null && ShotAfter <= 0) _menu!.Settle(Menu.IntroEnd);
            return;
        }
        _vsLoaded = _net!.RaceId;
        _net.MarkLoaded();
    }

    /// <summary>
    ///     Split screen's controllers: player 2 on its pad (only the pad bindings) or the keyboard's arrow half (arrows, right
    ///     Ctrl handbrake, right Shift/Alt gears, Backspace reset, Enter camera); player 1 keeps everything else (wheel, the other
    ///     pads, the keyboard — without the arrow half when player 2 has it).
    /// </summary>
    private void SetupSplitControls()
    {
        var ui = _versusUi!;
        var cfg = _settings.Controls;
        var pad = ui.P2Device < Input.Pads.Count ? ui.P2Device : -1;
        var p2Cfg = Clone(cfg);
        var p1Cfg = Clone(cfg);
        (p2Cfg.Wheel, p2Cfg.Keyboard) = (Unbound(), pad >= 0 ? Unbound() : SplitKeys.P2Keyboard());
        if (pad < 0) (p2Cfg.Pad, p1Cfg.Keyboard) = (Unbound(), SplitKeys.WithoutP2(cfg));
        _p2ResetKey = (pad >= 0 ? p2Cfg.Pad : p2Cfg.Keyboard)[Control.ResetCar][0].Label;
        _p2Input = new DriverInput(p2Cfg) { PadOf = pad >= 0 ? i => i.Pads.ElementAtOrDefault(pad) : _ => null };
        _savedDriver ??= _driver;
        _driver = new DriverInput(p1Cfg) { PadOf = pad >= 0 ? i => i.Pads.Where((_, n) => n != pad).FirstOrDefault() : null };
        Console.WriteLine($"\n[Versus] Spieler 2: {ui.P2DeviceName}");
    }

    private static ControlSettings Clone(ControlSettings c) => JsonSerializer.Deserialize<ControlSettings>(JsonSerializer.Serialize(c))!;

    private static Dictionary<Control, Bind[]> Unbound() => Enum.GetValues<Control>().ToDictionary(c => c, _ => new[] { Bind.None, Bind.None });

    // ------------------------------------------------------------ the race

    /// <summary>
    ///     The --bot driver of player 2's <paramref name="car"/>: the style of the character who drives that car (a generic
    ///     one for the others) at the free battle's NORMAL level.
    /// </summary>
    internal static RivalStyle BotStyle(string car)
    {
        var style = Rivals.All.FirstOrDefault(r => r.Car == car)?.Style ?? new RivalStyle(0.6f, 0.5f, 0.4f);
        return style with { Skill = Ui.FreeBattle.SkillAt(Ui.AiLevel.Normal, style.Skill) };
    }

    /// <summary>A fresh versus race on the grid (also RETRY): the session with every car, split's referee and player 2's HUD and camera.</summary>
    private void NewVersusRace()
    {
        if (_versusUi == null || _vsCars.Count == 0 && _vsSplit || _net == null && !_vsSplit) return;
        ICarDriver p1 = VersusBot ? new AiDriver(new RivalPilot(_drive.Line, BattleRun.Autopilot)) : _p1;
        RaceSession race;
        if (_vsSplit)
        {
            race = new RaceSession(_drive.Ground, _drive.Line, _drive.RunOutLine);
            race.Add("PLAYER 1", _drive.Car, p1);
            var p2 = _vsCars[0];
            var v2 = new Vehicle(_settings.Assisted(CarSpecs.All[p2.Car])) { SurfaceGrip = _drive.Car.SurfaceGrip, AutomaticGearbox = true };
            p2.Race = race.Add("PLAYER 2", v2, VersusBot ? new AiDriver(new RivalPilot(_drive.Line, BotStyle(p2.Car))) : _p2);
            _drive.ResetTo(0);
            NetRace.Grid(race, race.Cars[0].Track.Track(_drive.Car.Position).Along, [0, 1]);
            _vsReferee = new Referee(_vsConfig.Rule, race.Goal);
            _hud2 = new Hud(_course.Road, _drive.Line, new LinePilot(_drive.Line), null, _drive.Start)
            {
                Visible = _settings.HudOn, Mode = _settings.MapMode, Scale = _settings.HudScale, Night = _courseTime.EndsWith("_NIT"), Mph = _settings.Mph, ResetKey = _p2ResetKey,
            };
            _cam2 = new CamState { Snap = true, Fov = MathF.PI / 3, View = _cam2.View };
        }
        else
        {
            _netRace = NetRace.Create(_drive, _net!, p1);
            race = _netRace.Race;
            foreach (var c in _vsCars) c.Race = _netRace.ByPlayer.GetValueOrDefault(c.Id);
        }
        _vsRace = race;
        (_vsResult, _vsDecidedAt) = (null, -1);
        if (_menu != null) (_menu.Versus, _menu.NoRetry, _menu.NoReplay) = (string.Join(" / ", _vsCars.Select(c => c.Name)), _netRace != null, true); // the telop's "VS ..."
        foreach (var c in _vsCars)
        {
            Array.Clear(c.Smoke);
            Array.Clear(c.Spray);
        }
        _hud.Rival = null;
        SyncPose();
        UpdateVersusMatrices(1);
    }

    /// <summary>Race over or left: the session goes, player 1's own controls come back (the cars stay loaded for a rematch).</summary>
    private void EndVersusRace()
    {
        (_vsRace, _netRace, _vsReferee, _vsResult, _vsDecidedAt) = (null, null, null, null, -1);
        foreach (var c in _vsCars) c.Race = null;
        if (_savedDriver != null) (_driver, _savedDriver) = (_savedDriver, null);
        _p2Input = null;
        _finished = false;
        if (_menu != null) (_menu.Versus, _menu.NoRetry, _menu.NoReplay) = (null, false, false);
    }

    /// <summary>EXIT from the pause menu: split screen back to its lobby; online the host takes everybody back to the lobby, a guest leaves.</summary>
    private void VersusExitRace()
    {
        var ui = _versusUi!;
        _inRace = false;
        if (_net is { IsHost: false })
        {
            CloseSession();
            ui.ShowOnline();
            return;
        }
        _net?.BackToLobby();
        EndVersusRace();
        ui.BackToLobby();
    }

    /// <summary>One physics tick of the versus race (every car), the referee (split screen) and the other cars' lamps and sound.</summary>
    private VehicleInput VersusStep(VehicleInput input, float dt)
    {
        var race = _vsRace!;
        race.Cars[0].Vehicle = _drive.Car;
        _p1.Input = _netRace != null && _menu?.Current == Menu.Screen.Pause ? new VehicleInput(0, 1, 0) : input; // online pause: the car waits on the brake
        if (_vsSplit && _p2Input != null)
        {
            _p2.Input = _p2Input.Vehicle(_p2Shift);
            _p2Shift = 0;
        }
        // HUD clocks run from GO, not from each car's start-gate crossing: the same time for everybody, as on the result
        if (_hud.Timer.Phase == LapTimer.State.Ready && (_netRace == null || _net!.RaceTime >= 0))
        {
            _hud.Timer.Go();
            if (_vsSplit) _hud2?.Timer.Go();
        }
        if (_netRace != null)
        {
            _netRace.Lights = _lights.State;
            if (_net!.RaceTime >= 0) _netRace.Tick(dt);
        }
        else
        {
            race.Tick(dt);
            if (_vsReferee != null && _vsResult == null &&
                _vsReferee.Update(race.Time, dt, [.. race.Cars.Select((c, i) => new Referee.Car((byte)i, true, c.FinishedAt, c.Along, race.Time))], 0) is { } r)
                (_vsResult, race.Ended) = (r, true);
        }
        foreach (var c in _vsCars)
        {
            if (c.Race == null) continue;
            if (c.Race.Driver is RemoteDriver rd) SetLights(c.Lights, rd.Lights);
            c.Lights.Tick(dt);
            OtherCarSound(c, dt);
        }
        if (_vsSplit && _vsCars[0].Race is { } p2Car) _hud2?.Tick(p2Car.Vehicle, dt);
        if (race.LastContact is { } hit) _audio?.Bump(hit.ImpactSpeed);
        return race.Cars[0].Input;
    }

    private static void SetLights(Headlights l, Headlights.Mode m)
    {
        if (m == Headlights.Mode.Off ? l.State != m : l.State == Headlights.Mode.Off) l.Toggle();
        if (m != Headlights.Mode.Off && l.State != m) l.ToggleHigh();
    }

    /// <summary>
    ///     Another car's engine, tyres and walls: heard from player 1's camera with distance, doppler and pan like the battle
    ///     rival; in split screen player 2's car plays at full level a little to its side of the stereo picture.
    /// </summary>
    private void OtherCarSound(VsCar c, float dt)
    {
        if (c.Audio == null || c.Race == null) return;
        var v = c.Race.Vehicle;
        if (_vsSplit)
            c.Audio.Spatial(c.Gone ? 0 : 1, 1, Vector3.Normalize(_versusUi!.Vertical ? new Vector3(0.5f, 0, -1) : new Vector3(0.25f, 0, -1)));
        else
        {
            var to = v.Position - _pos;
            var d = MathF.Max(to.Length(), 0.1f);
            var dir = to / d;
            var gain = c.Gone ? 0 : MathF.Min(1, RivalNear / d) * Math.Clamp((300 - d) / 100, 0, 1);
            var doppler = Math.Clamp((SoundSpeed + Vector3.Dot(_camVelocity, dir)) / (SoundSpeed + Vector3.Dot(v.Velocity, dir)), 0.5f, 2);
            var fwd = Vector3.Normalize(_camLook - _pos);
            var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
            var pan = new Vector3(Vector3.Dot(dir, right), 0, -Vector3.Dot(dir, fwd));
            c.Audio.Spatial(gain, doppler, pan.LengthSquared() > 1e-6f ? Vector3.Normalize(pan) : -Vector3.UnitZ);
        }
        c.Audio.Update(v, c.Race.Input.Throttle, c.Race.Input.Handbrake, dt);
    }

    /// <summary>Player 2's R: back onto the line where it is (a little further back if that spot is walled).</summary>
    private void ResetP2()
    {
        if (_vsRace == null || _vsCars[0].Race is not { } car) return;
        for (var back = 0f; back < 30; back += 4)
            if (_vsRace.Place(car, car.Along - back, 0))
                break;
        _cam2.Snap = true;
    }

    /// <summary>Player 2's pause button (its pad's START) pauses the split-screen race too.</summary>
    private bool P2Pause() => _vsSplit && _vsRace != null && _p2Input?.Pressed(Control.Pause) == true;

    /// <summary>
    ///     Once decided (split: our referee; online: the host's result) the cars coast while the HUD shows the verdict; 3.5 s
    ///     later the standings. True when it took over this frame.
    /// </summary>
    private bool VersusFinished()
    {
        var result = _netRace != null ? _net?.Result : _vsResult;
        if (_vsRace == null || result == null || _finished || _versusUi == null) return false;
        if (_vsDecidedAt < 0) _vsDecidedAt = _menuTime;
        if (_menuTime - _vsDecidedAt < 3.5f && (shotPath == null || ShotAfter > 0)) return false;
        _finished = true;
        _inRace = false;
        if (_menu?.Current is Menu.Screen.Pause) _menu.Close();
        _versusUi.ShowResult(Standings(result));
        if (shotPath != null && ShotAfter <= 0) _versusUi.Settle();
        Console.WriteLine($"\n[Versus] Ergebnis ({result.Reason}): {string.Join(", ", Standings(result).Lines.Select(l => $"{l.Place}. {l.Name} {l.Value}"))}");
        return true;
    }

    /// <summary>The race's music, or once a versus race is decided the original's WIN/LOSE jingle for player 1 / us.</summary>
    private string? VersusMusic(string? menu)
    {
        var result = _netRace != null ? _net?.Result : _vsResult;
        return _vsRace == null || result == null || _vsDecidedAt < 0 || menu != Menu.RaceMusic ? menu : Won(result) || _vsSplit ? "WIN.adx" : "LOSE.adx";
    }

    private byte MyId => _netRace != null ? _net!.Local.Id : (byte)0;

    private bool Won(Result r) => _vsSplit || r.Entries.FirstOrDefault(e => e.Place == 1).Id == MyId;

    private string NameOf(byte id) => _vsSplit ? id == 0 ? "PLAYER 1" : "PLAYER 2" : _net?.NameOf(id) ?? $"#{id}";

    private string CarOf(byte id)
    {
        var car = id == MyId ? _carName : _vsCars.FirstOrDefault(c => c.Id == id)?.Car ?? "";
        return _catalog?.Cars.FirstOrDefault(c => c.Id == car)?.Name ?? car;
    }

    private Versus.Standings Standings(Result r)
    {
        var lines = r.Entries.OrderBy(e => e.Place)
            .Select(e => new Versus.Standing(e.Place, NameOf(e.Id), CarOf(e.Id), e.Time >= 0 ? e.Time : null, Versus.ValueOf(r, e), !_vsSplit && e.Id == MyId)).ToArray();
        var winner = r.Entries.First(e => e.Place == 1).Id;
        var mine = r.Entries.FirstOrDefault(e => e.Id == MyId).Place;
        var title = _vsSplit ? $"{NameOf(winner)} WINS!!" : mine == 1 ? "YOU WIN!!" : r.Entries.Length == 2 ? "YOU LOSE" : $"{Place(mine)} PLACE";
        var reason = r.Reason switch
        {
            "GOAL" => _vsConfig.Rule == NetRule.Battle || r.Entries.Length == 2 ? "FIRST TO THE GOAL" : "EVERYBODY HOME",
            "BREAKAWAY" => $"PULLED AWAY  ({new Battle(BattleRule.Race, 1).Breakaway:0} S GAP)", "TIME UP" => $"TIME UP  ({Referee.DnfAfter:0} S AFTER THE WINNER)",
            "OPPONENTS LEFT" => "THE OTHERS LEFT", _ => r.Reason,
        };
        return new Versus.Standings(title, Won(r), reason, lines);
    }

    private static string Place(int p) => p switch { 1 => "1ST", 2 => "2ND", 3 => "3RD", _ => $"{p}TH" };

    /// <summary>--versus split --bot --autodrive s with --shot: both autopilots race that long before the first frame.</summary>
    private void VersusAutoDrive(float seconds)
    {
        _menu?.Close();
        for (var t = 0f; t < seconds; t += Drive.Dt) Tick(Drive.Dt);
        _cam2.Snap = _camSnap = true;
    }

    /// <summary>
    ///     --flow &lt;dir&gt; --versus flow: the main menu → VERSUS → SPLIT SCREEN → lobby (next course, player 1 picks the R32 in the car select, both down to START/READY: START
    ///     beeps while player 2 is not ready, player 2 gets ready, START) → telop, countdown → race (16×) → pause → RETRY → race →
    ///     pause → EXIT (back in the lobby) → back to the main menu, a PNG per step (both players get the script's keys).
    /// </summary>
    private static readonly (string At, float Wait, string? Shot, int X, int Y, bool Ok, bool Back)[] VersusFlowScript =
    [
        ("Boot", 1.2f, null, 0, 0, true, false), ("Logo", 1, null, 0, 0, true, false), ("Title", 1.5f, null, 0, 0, true, false),
        ("Modes", 1, null, 0, 1, false, false), ("Modes", 0.5f, null, 0, 1, false, false), ("Modes", 0.6f, "vs_main_menu", 0, 0, true, false),
        ("VsMode", 1, "vs_mode", 0, 0, true, false),
        ("VsLobby", 1, "vs_lobby", 1, 0, false, false),
        .. Enumerable.Repeat(("VsLobby", 0.25f, (string?)null, 0, 1, false, false), 6),
        // player 1's CAR → the car select (makers, NISSAN's cars, the R32), then on to START
        ("VsLobby", 0.5f, "vs_lobby_car", 0, 0, true, false), ("VsLobby", 1, "vs_pick_maker", 0, 1, false, false),
        ("VsLobby", 0.6f, null, 0, 0, true, false), ("VsLobby", 1.5f, "vs_pick_car", 0, 0, true, false),
        .. Enumerable.Repeat(("VsLobby", 0.25f, (string?)null, 0, 1, false, false), 2),
        ("VsLobby", 0.5f, "vs_lobby_start", 0, 0, true, false), ("VsLobby", 0.6f, "vs_lobby_ready", 0, 0, true, false),
        ("Intro", 1, "vs_telop", 0, 0, false, false), ("Intro", 2.5f, "vs_countdown", 0, 0, false, false),
        ("Race", 1.5f, "vs_race", 0, 0, false, true), ("Pause", 0.8f, "vs_pause", 1, 0, false, false), ("Pause", 0.4f, null, 0, 0, true, false),
        ("Race", 1.5f, "vs_race_retry", 0, 0, false, true), ("Pause", 0.8f, null, 1, 0, false, false), ("Pause", 0.4f, null, 1, 0, false, false), // Replay/Photo greyed: skipped
        ("Pause", 0.4f, "vs_pause_exit", 0, 0, true, false),
        ("VsLobby", 1.2f, "vs_lobby_back", 0, 0, false, true), ("VsMode", 0.8f, null, 0, 0, false, true), ("Modes", 1, "vs_modes_back", 0, 0, false, false),
    ];

    /// <summary>--bot online: one log line a second like the headless peer (place, speed, how each remote car is sampled, ping, losses).</summary>
    private void LogNetRace()
    {
        var race = _netRace!;
        var net = _net!;
        var line = $"[Fenster] {net.RaceTime,5:F1} {race.Local.Along,6:F0} m {race.Local.Vehicle.SpeedKmh,4:F0} km/h";
        foreach (var (id, car) in race.ByPlayer)
            if (car.Driver is RemoteDriver rd)
                line += $" | #{id} {car.Along,6:F0} m {car.Vehicle.SpeedKmh,4:F0} {rd.LastKind,-12} Korr {rd.LastError * 100,3:F0} cm Ping {net.PingTo(rd.Player)} ms verl {rd.Player.Snapshots.Lost}/{rd.Player.Snapshots.Received}";
        Console.WriteLine("\n" + line + $" | fps {1 / MathF.Max(_frameDt, 1e-3f):F0}");
    }

    private float _frameDt;

    // ------------------------------------------------------------ cars: models, poses, drawing, effects

    /// <summary>The other cars' models (after the player's car: textures are freed in reverse order), lamps and sound.</summary>
    private void LoadVersusCars(Iso9660 iso)
    {
        foreach (var c in _vsCars)
        {
            c.Model = CarModel.Load(iso, c.Car, c.Paint, _renderer, _settings.Livery);
            c.ModelToBody = ModelToBody(c.Model, CarSpecs.All[c.Car]);
            c.Lights = new Headlights(Lights ?? Headlights.For(_courseTime, _fog));
            c.Audio?.Dispose();
            c.Audio = _audioDevice != null ? new GameAudio(iso, _courseTime, _audioDevice, c.Car, other: true) : null;
        }
    }

    private void DisposeVersusCars()
    {
        for (var i = _vsCars.Count - 1; i >= 0; i--)
        {
            _vsCars[i].Model?.Dispose();
            _vsCars[i].Model = null;
            _vsCars[i].Audio?.Dispose();
            _vsCars[i].Audio = null;
        }
    }

    private void DisposeVersus()
    {
        _net?.Dispose();
        _lan?.Dispose();
        foreach (var c in _vsCars) c.Audio?.Dispose();
    }

    private void UpdateVersusMatrices(float alpha)
    {
        foreach (var c in _vsCars)
            if (c is { Race: { } r, Model: { } m })
                (c.Pose, c.Body) = PoseCar(r.Vehicle, m, c.ModelToBody, r.PrevPosition, r.PrevOrientation, alpha, c.Wheels, Matrix4x4.Identity);
    }

    private IEnumerable<VsCar> RacingCars => _vsCars.Where(c => c is { Race: not null, Model: not null, Gone: false });

    private int VersusCasters(Span<(StaticMesh, Matrix4x4)> dst)
    {
        var n = 0;
        foreach (var c in RacingCars)
        {
            var shell = c.Lights.State != Headlights.Mode.Off ? c.Model!.Lit : c.Model!.Day;
            dst[n++] = (shell.Body, c.Body);
            for (var i = 0; i < 4; i++) dst[n++] = (c.Model.Wheel, c.Wheels[i]);
        }
        return n;
    }

    /// <summary>Every other car into view <paramref name="view"/> with its own lamps glowing; in player 2's view player 1's car is one of them.</summary>
    private void DrawVersusCars(Penelope.IRenderPassEncoder pass, in Matrix4x4 viewProj, int view)
    {
        if (_vsRace == null) return;
        foreach (var c in RacingCars)
            if (!(view == 1 && c == _vsCars[0]))
                DrawOtherCar(pass, viewProj, c.Model!, c.Body, c.Wheels, c.Lights, c.Lamps, c.Race!.Input.Brake, c.Race.Vehicle.Gear < 0);
        if (view == 1) DrawOtherCar(pass, viewProj, _car, _carBody, _carWheels, _lights, _p1Lamps, _brakeLight, _drive.Car.Gear < 0);
    }

    private void DrawOtherCar(Penelope.IRenderPassEncoder pass, in Matrix4x4 viewProj, CarModel model, in Matrix4x4 body, Matrix4x4[] wheels, Headlights lights,
        SceneLights lamps, float brake, bool reverse)
    {
        var l = _renderer.Lights;
        lights.Apply(lamps, model.Lamp, body, _renderer.Atmosphere.LocalLightShare, brake, reverse);
        var (glow, b, r) = (l.LampGlow, l.Brake, l.Reverse);
        (l.LampGlow, l.Brake, l.Reverse) = (lamps.LampGlow, lamps.Brake, lamps.Reverse);
        var shell = model.ShellFor(lights.State != Headlights.Mode.Off, InCabin(body));
        _carRenderer.Draw(pass, shell.Body, shell.Decals, model.Wheel, body, wheels, viewProj, _pos);
        if (shell.PopUp is { } popUp) _carRenderer.DrawPart(pass, popUp, model.Lamp.PopUpAt(lights.Open) * body, viewProj, _pos);
        (l.LampGlow, l.Brake, l.Reverse) = (glow, b, r);
    }

    /// <summary>Per tick: the other cars' smoke, skids (strips 4 + 4·i), spray, wall sparks; sparks and a knock at contacts.</summary>
    private void VersusEffects(float dt)
    {
        if (_vsRace == null) return;
        for (var i = 0; i < _vsCars.Count; i++)
        {
            var c = _vsCars[i];
            if (c.Race == null || c.Gone) continue;
            WheelEffects(c.Race.Vehicle, c.Smoke, c.Spray, 4 + 4 * i, dt);
            if (c.Race.Vehicle.WallContacts > 0)
            {
                WallSparks(c.Race.Vehicle);
                if (_vsSplit && i == 0) _cam2.Shake = MathF.Max(_cam2.Shake, Math.Clamp(c.Race.Vehicle.WallImpactSpeed / 6, 0, 1));
            }
        }
        if (_vsRace.LastContact is not { ImpactSpeed: > 0.5f } hit) return;
        var knock = Math.Clamp((hit.ImpactSpeed - 0.5f) / 5, 0, 1);
        _shake = MathF.Max(_shake, knock);
        if (_vsSplit) _cam2.Shake = MathF.Max(_cam2.Shake, knock);
        var count = (int)MathF.Min(hit.ImpactSpeed * 2, 10);
        for (var i = 0; i < count; i++)
        {
            var r = new Vector3(_rng.NextSingle() - 0.5f, _rng.NextSingle(), _rng.NextSingle() - 0.5f);
            _fx.EmitSpark(hit.Point - Vector3.UnitY * 0.1f, hit.Normal * (2 * r.X) + Vector3.UnitY * (1 + r.Y) + r * 3);
        }
    }

    // ------------------------------------------------------------ split screen: views, player 2's camera and HUD

    /// <summary>The two views of a split-screen race (top/bottom or left/right, 2 px apart), null otherwise.</summary>
    private (Viewport First, Viewport Second)? SplitViews(int w, int h)
    {
        if (!_vsSplit || _vsRace == null || _versusUi is { Active: true } || _vsCars.Count == 0 || _vsCars[0].Race == null) return null;
        if (_versusUi!.Vertical)
        {
            var half = (w - 2) / 2;
            return (new Viewport(0, 0, half, h), new Viewport(w - half, 0, half, h));
        }
        var hh = (h - 2) / 2;
        return (new Viewport(0, 0, w, hh), new Viewport(0, h - hh, w, hh));
    }

    private void SwapCamera()
    {
        var c = _cam2;
        _cam2 = new CamState
        {
            Pos = _pos, Look = _camLook, Fov = _fov, Snap = _camSnap, Shake = _shake, ShakeOffset = _shakeOffset, Velocity = _camVelocity, Last = _lastCamPos, View = _camView, OnBoard = _onBoard, Follow = _follow,
        };
        (_pos, _camLook, _fov, _camSnap, _shake, _shakeOffset, _camVelocity, _lastCamPos, _camView, _onBoard, _follow) =
            (c.Pos, c.Look, c.Fov, c.Snap, c.Shake, c.ShakeOffset, c.Velocity, c.Last, c.View, c.OnBoard, c.Follow);
    }

    /// <summary>Player 2's view: its chase camera, its car's lamps lighting the road, player 1's car as one of the others.</summary>
    private void RenderP2View(in FrameContext ctx, FrameCapture? shot, FrameCapture? frame, Viewport viewport)
    {
        var p2 = _vsCars[0];
        var car = p2.Race!;
        SwapCamera();
        _frameDt = ctx.Time.DeltaTime;
        var menu = _menu?.Current ?? Menu.Screen.None;
        if (!_fly && menu is Menu.Screen.None or Menu.Screen.Intro or Menu.Screen.Finish) UpdateDriveCamera(shot != null ? 0 : ctx.Time.DeltaTime, car.Vehicle, p2.Pose, p2.Body, p2.Model!);
        else if (_camSnap) UpdateDriveCamera(0, car.Vehicle, p2.Pose, p2.Body, p2.Model!);
        RenderView(ctx, shot, frame, viewport, new ViewCar(p2.Model!, p2.Body, p2.Wheels, p2.Lights, car.Input.Brake, car.Vehicle.Gear < 0, car.Vehicle), 1);
        SwapCamera();
    }

    /// <summary>The seam between the two views: a dark bar with a thin red line (the views leave 2 px between them).</summary>
    private void SplitSeam(Viewport first, Viewport second)
    {
        var o = _overlay;
        var u = MathF.Max(1, MathF.Round(Device.SwapchainHeight / 900f));
        if (_versusUi!.Vertical)
        {
            float x0 = first.X + first.Width, x1 = second.X;
            o.Rect(new Vector2(x0 - 2 * u, 0), new Vector2(x1 + 2 * u, first.Height), Overlay.Rgba(0.02f, 0.02f, 0.03f));
            o.Rect(new Vector2((x0 + x1) / 2 - 0.5f * u, 0), new Vector2((x0 + x1) / 2 + 0.5f * u, first.Height), Overlay.Rgba(0.8f, 0.07f, 0.06f));
        }
        else
        {
            float y0 = first.Y + first.Height, y1 = second.Y;
            o.Rect(new Vector2(0, y0 - 2 * u), new Vector2(first.Width, y1 + 2 * u), Overlay.Rgba(0.02f, 0.02f, 0.03f));
            o.Rect(new Vector2(0, (y0 + y1) / 2 - 0.5f * u), new Vector2(first.Width, (y0 + y1) / 2 + 0.5f * u), Overlay.Rgba(0.8f, 0.07f, 0.06f));
        }
    }

    private void BuildP2Hud(Viewport vp)
    {
        _p2Overlay.Font = _overlay.Font;
        _p2Overlay.Clear();
        var p2 = _vsCars[0];
        if (_hud2 == null || p2.Race == null) return;
        (_hud2.Lights, _hud2.Dashboard) = (p2.Lights.State, _cam2.OnBoard == CameraView.Cockpit);
        _hud2.Rival = VersusRival(1);
        _hud2.Build(_p2Overlay, vp.Width, vp.Height, p2.Pose.Translation, Vector3.TransformNormal(Vector3.UnitZ, p2.Pose), p2.Race.Vehicle, p2.Car, _menuTime);
        BuildVersusHud(_p2Overlay, vp.Width, vp.Height, 1);
        _p2Overlay.Shift(new Vector2(vp.X, vp.Y));
    }

    /// <summary>The red marker on view <paramref name="view"/>'s course gauge: the nearest other car.</summary>
    private (Vector3 Position, float Along)? VersusRival(int view)
    {
        if (_vsRace == null) return null;
        var me = view == 0 ? _vsRace.Cars[0] : _vsCars[0].Race;
        if (me == null) return null;
        (Vector3, float)? best = null;
        var bestD = float.MaxValue;
        void Consider(RaceCar c, Vector3 pos)
        {
            var d = MathF.Abs(c.Along - me.Along);
            if (c != me && d < bestD) (best, bestD) = ((pos, c.Along), d);
        }
        Consider(_vsRace.Cars[0], _carPose.Translation);
        foreach (var c in RacingCars) Consider(c.Race!, c.Pose.Translation);
        return best;
    }

    /// <summary>Versus HUD of view <paramref name="view"/> (0: player 1 / us, 1: player 2) and the verdict once decided.</summary>
    private void BuildVersusHud(Overlay o, int w, int h, int view)
    {
        if (_vsRace == null) return;
        var me = view == 0 ? _vsRace.Cars[0] : _vsCars[0].Race;
        var players = new List<(string, float, bool, int?, bool)> { (_vsSplit ? "PLAYER 1" : MyName, _vsRace.Cars[0].Along, false, null, me == _vsRace.Cars[0]) };
        foreach (var c in _vsCars)
            if (c.Race != null)
                players.Add((c.Name, c.Race.Along, c.Gone, c.Race.Driver is RemoteDriver rd ? _net!.PingTo(rd.Player) : null, me == c.Race));
        VersusHud.Build(o, w, h, players);
        var result = _netRace != null ? _net?.Result : _vsResult;
        if (result == null || _vsDecidedAt < 0) return;
        var id = view == 0 ? MyId : (byte)1;
        var place = result.Entries.FirstOrDefault(e => e.Id == id).Place;
        VersusHud.Verdict(o, w, h, place == 1 ? "WIN!!" : result.Entries.Length == 2 ? "LOSE" : Place(place), place == 1, _menuTime - _vsDecidedAt);
    }

    /// <summary>Menu keys of one split-screen player from a part of the keyboard and some pads (D-pad/stick with repeat, A/START, B).</summary>
    private sealed class SeatKeys
    {
        public sealed record Keys(Key[] Up, Key[] Down, Key[] Left, Key[] Right, Key[] Ok, Key[] Back);

        public static readonly Keys All = new([Key.Up, Key.W], [Key.Down, Key.S], [Key.Left, Key.A], [Key.Right, Key.D], [Key.Enter, Key.Space], [Key.Escape, Key.Backspace]);
        public static readonly Keys Wasd = new([Key.W], [Key.S], [Key.A], [Key.D], [Key.Space, Key.E], [Key.Escape, Key.Q]);
        public static readonly Keys Arrows = new([Key.Up], [Key.Down], [Key.Left], [Key.Right], [Key.Enter, Key.RightCtrl], [Key.Backspace]);
        public static readonly Keys None = new([], [], [], [], [], []);

        private readonly DirRepeat _dirs = new();

        public (int X, int Y, bool Ok, bool Back) Read(InputSnapshot input, float dt, Keys keys, GamepadState[] pads)
        {
            var k = input.Keyboard;
            bool Rep(Key[] ks) => ks.Any(x => k.IsKeyRepeating(x, dt));
            bool Hit(Key[] ks) => ks.Any(k.IsKeyPressed);
            int x = (Rep(keys.Right) ? 1 : 0) - (Rep(keys.Left) ? 1 : 0), y = (Rep(keys.Down) ? 1 : 0) - (Rep(keys.Up) ? 1 : 0);
            bool ok = Hit(keys.Ok), back = Hit(keys.Back);
            bool up = false, down = false, left = false, right = false;
            foreach (var pad in pads)
            {
                var (u, d, l, r) = DirRepeat.Held(pad); // D-pad and stick repeat as the keys do
                (up, down, left, right) = (up | u, down | d, left | l, right | r);
                ok |= pad.IsButtonPressed(GamepadButton.A) || pad.IsButtonPressed(GamepadButton.Start);
                back |= pad.IsButtonPressed(GamepadButton.B);
            }
            var (dx, dy) = _dirs.Step(up, down, left, right, dt);
            return (Math.Sign(x + dx), Math.Sign(y + dy), ok, back);
        }
    }
}

/// <summary>The keyboard shared by two split-screen players: player 2 gets the arrow half, player 1 keeps the rest.</summary>
public static class SplitKeys
{
    /// <summary>Keys of player 2's half.</summary>
    public static readonly Key[] P2 = [Key.Left, Key.Right, Key.Up, Key.Down, Key.RightCtrl, Key.RightShift, Key.RightAlt, Key.Backspace, Key.Enter];

    /// <summary>Player 2 on the keyboard: arrows steer/throttle/brake, right Ctrl handbrake, right Shift/Alt gear up/down, Backspace back onto the road, Enter camera.</summary>
    public static Dictionary<Control, Bind[]> P2Keyboard()
    {
        Bind[] One(Key k) => [Bind.OfKey(k), Bind.None];
        var all = Enum.GetValues<Control>().ToDictionary(c => c, _ => new[] { Bind.None, Bind.None });
        (all[Control.SteerLeft], all[Control.SteerRight], all[Control.Throttle], all[Control.Brake]) = (One(Key.Left), One(Key.Right), One(Key.Up), One(Key.Down));
        (all[Control.Handbrake], all[Control.ShiftUp], all[Control.ShiftDown], all[Control.ResetCar], all[Control.Camera]) =
            (One(Key.RightCtrl), One(Key.RightShift), One(Key.RightAlt), One(Key.Backspace), One(Key.Enter));
        return all;
    }

    /// <summary>Player 1's keyboard bindings without any key of player 2's half.</summary>
    public static Dictionary<Control, Bind[]> WithoutP2(ControlSettings cfg) =>
        Enum.GetValues<Control>().ToDictionary(c => c, c =>
            cfg.Get(DeviceKind.Keyboard, c).Select(x => x.Source == Source.Key && P2.Contains((Key)x.Code) ? Bind.None : x).ToArray());
}
