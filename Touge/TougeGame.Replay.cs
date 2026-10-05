using System.Numerics;
using Kansei.Graphics;
using Kansei.Physics;
using Penelope;
using Touge.Formats;
using Touge.Replays;
using Touge.Ui;

namespace Touge;

/// <summary>
///     REPLAY &amp; RECORD, SAVE &amp; LOAD, ghost and photo mode:
///     every run and battle is recorded (<see cref="ReplayRecorder"/>: inputs per tick of all cars + keyframes), saved when
///     finished (<see cref="ReplayStore"/>, a new record also as the best run = the time attack ghost); the viewer
///     (<see cref="ReplayViewer"/>) plays a recording on the game's own cars from the pause menu, the result sheet or the
///     REPLAY &amp; RECORD list, with TV cameras (<see cref="TvCameras"/>), chase, bumper and free camera, speed and rewind;
///     the ghost is the best run's car driven by its replay next to the player, see-through; photo mode freezes the game
///     under a free camera and writes PNGs. Cars beyond the game's own (a replayed battle's rival, the ghost) are
///     <see cref="ShowCar"/>s, loaded after the player's and the rival's models and freed before them (<see cref="DisposeRival"/>).
/// </summary>
public sealed partial class TougeGame
{
    /// <summary>--replay: a replay file to open in the viewer at start (with --replay-at seconds, --replay-cam tv|chase|bumper|free).</summary>
    public string? ReplayFile { get; init; }
    public float ReplayAt { get; init; }
    public ReplayViewer.Camera ReplayCam { get; init; } = ReplayViewer.Camera.Tv;
    /// <summary>--replay-focus: the car the viewer follows first (1 = the rival of a battle).</summary>
    public int ReplayFocus { get; init; }
    /// <summary>--ghost: a replay file to drive as the ghost (test runs; with the menus the best run of the course).</summary>
    public string? GhostFile { get; init; }
    /// <summary>--data-dir: finished runs, best runs and autosaves are written also in a test run (into that folder).</summary>
    public bool SaveRuns { get; init; }
    private bool SavesRuns => _persist || SaveRuns;

    private ReplayMenu? _replayMenu;
    private SaveLoadScreen? _saveMenu;
    private readonly ReplayViewer _viewer = new();
    private readonly PhotoMode _photo = new();
    private readonly Canvas _replayCanvas = new();
    private double _playSeconds;

    // ---------------------------------------------------------------- set-up and hooks

    /// <summary>With the menus: the two main-menu screens and the GHOST option (GAME SETTING).</summary>
    private void SetupReplay()
    {
        if (_menu == null || _catalog == null) return;
        _replayMenu = new ReplayMenu(_catalog, _settings) { Sound = n => _menuAudio?.Play(n) };
        _saveMenu = new SaveLoadScreen(SaveSlots.Default) { Sound = n => _menuAudio?.Play(n), PlaySeconds = () => _playSeconds, Flush = FlushProfile };
        _playSeconds = SavesRuns ? SaveSlots.Default.ReadState().PlaySeconds : 0;
        _menu.Options.Find("GAME SETTING")?.Rows.Add(Options.Row.Toggle("GHOST", () => _settings.Ghost, v => _settings.Ghost = v,
            "Time attack: your best run on this course and route", "drives along as a see-through car."));
    }

    /// <summary>--menu replay|replay-best|records-list|saveload|photo and --replay at start (screenshots).</summary>
    private bool OpenReplayStart()
    {
        switch (StartMenu)
        {
            case "replay" or "replay-best" or "replay-records" or "replay-delete" when _replayMenu != null:
                _replayMenu.Open(StartMenu is "replay" or "replay-delete" ? 0 : StartMenu == "replay-best" ? 1 : 2, StartMenu != "replay-records");
                if (StartMenu == "replay-delete") _replayMenu.AskDelete();
                if (shotPath != null) _replayMenu.Settle();
                _inRace = false;
                return true;
            case "saveload" or "saveload-actions" or "saveload-name" when _saveMenu != null:
                _saveMenu.Open();
                _saveMenu.Show(StartMenu[(StartMenu.IndexOf('-') + 1)..]);
                if (shotPath != null) _saveMenu.Settle();
                _inRace = false;
                return true;
            case "photo":
                UpdateCarMatrices(1);
                OpenPhoto();
                _inRace = true;
                return true;
        }
        if (ReplayFile == null) return false;
        OpenReplay(Replay.Load(ReplayFile), Back.Menu);
        _viewer.Open(ReplayCam, ReplayFocus);
        if (ReplayCam == ReplayViewer.Camera.Free) _viewerCam.Place(_pos, _camLook);
        if (ReplayAt > 0) SeekReplay((int)(ReplayAt / Drive.Dt));
        return true;
    }

    /// <summary>--flow: where the script is (null: not in this part).</summary>
    private string? ReplayFlowAt => _photo.Active ? "Photo" : _player != null ? "Replay" : _replayMenu is { Active: true } ? "ReplayMenu" : _saveMenu is { Active: true } ? "SaveLoad" : null;

    /// <summary>Holds the game: the replay/save screens and photo mode (the viewer drives the cars itself, <see cref="ReplayTick"/>).</summary>
    private bool ReplayFreezes => _replayMenu is { Active: true } || _saveMenu is { Active: true } || _photo.Active;

    /// <summary>Screens of this part that fly the camera along the road like the front end.</summary>
    private bool ReplayBackdrop => _replayMenu is { Active: true } || _saveMenu is { Active: true };

    /// <summary>Music of this part's screens (WORRY on REPLAY &amp; RECORD as the original, and on SAVE &amp; LOAD), null: not ours.</summary>
    private string? ReplayMusic => ReplayBackdrop ? "WORRY.adx" : null;

    /// <summary>Game sound off: photo mode, a paused or scrubbing replay, our menus.</summary>
    private bool ReplayMutes => ReplayBackdrop || _photo.Active || _player != null && _viewer.Speed == 0;

    /// <summary>The overlay of the viewer/photo/our screens instead of HUD and menus; true if it took the frame.</summary>
    private bool ReplayOverlay(int w, int h)
    {
        if (_photo.Active) _photo.Build(_overlay, w, h, _replayCanvas);
        else if (_player != null)
        {
            var (car, name) = FocusCar();
            _viewer.Build(_overlay, w, h, _replayCanvas, _player.Tick * Drive.Dt, _player.Replay.Seconds, name, car.SpeedKmh, Drive.Gear(car), _settings.Mph);
        }
        else if (_replayMenu is { Active: true }) _replayMenu.Build(_overlay, w, h, _replayCanvas);
        else if (_saveMenu is { Active: true }) _saveMenu.Build(_overlay, w, h, _replayCanvas);
        else return false;
        return true;
    }

    /// <summary>No song caption over photos, a hidden replay overlay or our screens.</summary>
    private bool OverlayQuiet => _photo.Active || _player != null && _viewer.OverlayHidden || ReplayBackdrop;

    /// <summary>Per frame: our screens, the viewer and photo mode take the input; true when one of them is up.</summary>
    private bool UpdateReplay((int X, int Y, bool Ok, bool Back) keys, float dt)
    {
        if (SavesRuns) _playSeconds += dt;
        if (_photo.Active)
        {
            UpdatePhoto(keys, dt);
            return true;
        }
        if (_player != null)
        {
            UpdateViewer(keys, dt);
            return true;
        }
        if (_replayMenu is { Active: true } rm)
        {
            switch (rm.Update(keys, Input, dt))
            {
                case ReplayMenu.Result.Play:
                    try
                    {
                        OpenReplay(Replay.Load(rm.Chosen!), Back.Menu);
                    }
                    catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException or System.Text.Json.JsonException or KeyNotFoundException)
                    {
                        Console.WriteLine($"\n[Replay] {rm.Chosen}: {e.Message}");
                        _menuAudio?.Play("BEEP001");
                        rm.Open(rm.Tab, true);
                    }
                    break;
                case ReplayMenu.Result.Exit:
                    _front?.Open(FrontEnd.Step.Modes);
                    if (_front == null) Window.ShouldClose = true;
                    break;
            }
            return true;
        }
        if (_saveMenu is { Active: true } sm)
        {
            switch (sm.Update(keys, Input, dt))
            {
                case SaveLoadScreen.Result.Loaded:
                    ReloadProgress();
                    break;
                case SaveLoadScreen.Result.Exit:
                    _front?.Open(FrontEnd.Step.Modes);
                    if (_front == null) Window.ShouldClose = true;
                    break;
            }
            return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- recording

    private ReplayRecorder? _rec;
    private Drive? _recDrive;
    private bool _recReverse;
    private Vector3[] _recLast = [];
    private string? _recBestKey;
    private int _recRespawns, _recEnd = -1;

    private Vehicle[] _liveCars = [];

    /// <summary>The race's cars now (the player's first); one array reused per tick.</summary>
    private Vehicle[] LiveCars()
    {
        var n = _race?.Cars.Count ?? 1;
        if (_liveCars.Length != n) _liveCars = new Vehicle[n];
        for (var i = 0; i < n; i++) _liveCars[i] = _race != null ? _race.Cars[i].Vehicle : _drive.Car;
        return _liveCars;
    }

    private int Respawns()
    {
        var n = 0;
        if (_race != null)
            foreach (var c in _race.Cars) n += c.Respawns;
        return n;
    }

    /// <summary>Before each physics tick of the race: a fresh recording when none runs (or the course/direction changed), keyframe on teleports.</summary>
    private void RecordBefore()
    {
        if (_probe != null || _sheet != null || _vsRace != null) return; // versus races are not recorded (other players' cars)
        var cars = LiveCars();
        if (_rec is not { Valid: true } || _recDrive != _drive || _recReverse != _drive.Reverse || _recLast.Length != cars.Length) StartRecording(cars);
        var rec = _rec!;
        if (_recEnd >= 0 && rec.Replay.Ticks >= _recEnd) return;
        for (var i = 0; i < cars.Length; i++)
            if (Vector3.DistanceSquared(cars[i].Position, _recLast[i]) > 0.05f * 0.05f) rec.Mark(); // R, B, car swap: put somewhere between ticks
        rec.Before(cars);
    }

    /// <summary>After the tick: the inputs the cars got; the finish (time, record, outcome) into the header; stops 10 s after the end.</summary>
    private void RecordAfter(VehicleInput input)
    {
        if (_rec is not { Valid: true } rec || _recEnd >= 0 && rec.Replay.Ticks >= _recEnd) return;
        var info = rec.Replay.Info;
        if (_race is { } race)
        {
            Span<VehicleInput> inputs = stackalloc VehicleInput[race.Cars.Count];
            for (var i = 0; i < inputs.Length; i++) inputs[i] = race.Cars[i].Input;
            rec.After(inputs);
            var respawns = Respawns();
            if (respawns != _recRespawns)
            {
                _recRespawns = respawns;
                rec.Mark(); // a respawn inside the tick: the next keyframe carries it
            }
            if (race.Battle is { Outcome: not Race.BattleOutcome.None } b)
            {
                info.Result ??= b.Outcome.ToString().ToUpperInvariant();
                info.Time = race.Cars[0].FinishedAt ?? b.DecidedAt;
            }
        }
        else
        {
            rec.After([input]);
            if (info.Time == null && _hud.Timer.Phase == LapTimer.State.Finished)
            {
                info.Time = _hud.Timer.Time;
                if (_hud.Timer.NewRecord && _front != null && FourPass == null) _recBestKey = _settings.RunKey(_courseTime[.._courseTime.LastIndexOf('_')], _drive.Reverse);
            }
        }
        if (info.Time != null && _recEnd < 0) _recEnd = rec.Replay.Ticks + 10 * 120;
        var cars = LiveCars();
        for (var i = 0; i < cars.Length; i++) _recLast[i] = cars[i].Position;
    }

    private void StartRecording(Vehicle[] cars)
    {
        EndRecording();
        var info = new ReplayInfo
        {
            Course = _courseTime, Reverse = _drive.Reverse, Fog = _fog, Date = DateTime.Now,
            Mode = _legendRival != null ? "LEGEND" : _story is { InRun: true } ? "STORY" : _inFreeBattle ? "FREE BATTLE" : _race != null ? "BATTLE" : FourPassLabel ?? "TIME ATTACK",
            Chapter = _story is { InRun: true } s ? s.Chapter + 1 : null, Title = _story is { InRun: true } t ? t.Text.Title : null,
            Cars = [new ReplayCar("YOU", _carName, _paint, _settings.SteerAssist, _settings.DriftAssist)],
        };
        if (_race != null && Battle != null) info.Cars.Add(new ReplayCar(Battle.Rival.Name, Battle.Rival.Car, 0));
        (_rec, _recDrive, _recReverse, _recLast, _recBestKey, _recEnd) = (new ReplayRecorder(new Replay { Info = info }, cars), _drive, _drive.Reverse, [.. cars.Select(c => c.Position)], null, -1);
        _recRespawns = Respawns();
    }

    /// <summary>The recording ends (new run, course change, quit): a finished one is saved with the menus, a new record also as the best run.</summary>
    private void EndRecording()
    {
        var rec = _rec;
        _rec = null;
        if (rec is not { Valid: true } || rec.Replay.Info.Time == null || !SavesRuns) return;
        try
        {
            ReplayStore.SaveRecent(rec.Replay);
            if (_recBestKey != null) rec.Replay.Save(ReplayStore.BestPath(_recBestKey));
            Console.WriteLine($"\n[Replay] gespeichert: {rec.Replay.Info.Course} {rec.Replay.Seconds:F1} s{(_recBestKey != null ? ", Bestzeit-Lauf " + _recBestKey : "")}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"\n[Replay] nicht gespeichert: {e.Message}");
        }
        Autosave();
    }

    /// <summary>A new run (<see cref="ResetRun"/>): the last one's recording ends, the ghost (re)starts.</summary>
    private void RestartRecording()
    {
        EndRecording();
        LoadGhost();
    }

    // ---------------------------------------------------------------- save & load

    /// <summary>What lives in memory onto disk before a slot copies the files (a test run's settings never were).</summary>
    private void FlushProfile()
    {
        if (!SavesRuns) return;
        _settings.Save();
        if (ProgressSaved) _progress.Save(LegendProgressPath);
    }

    /// <summary>Autosave into the active slot (SAVE &amp; LOAD) after a finished run, with the menus.</summary>
    private void Autosave()
    {
        if (!SavesRuns) return;
        var slots = SaveSlots.Default;
        var s = slots.ReadState();
        if (!s.Autosave || s.Active < 0 || slots.Read(s.Active) is not { } meta) return;
        try
        {
            FlushProfile();
            slots.Save(s.Active, meta.Name, _playSeconds);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"\n[Save] Autosave fehlgeschlagen: {e.Message}");
        }
    }

    /// <summary>
    ///     A slot was loaded: its settings.json replaces the progress and options of this one (not the controls and the
    ///     display, which belong to this machine), everything applied; the play time is the slot's.
    /// </summary>
    private void ReloadProgress()
    {
        SaveSlots.CopyProfile(Settings.Load(), _settings);
        _settings.Save();
        _playSeconds = SaveSlots.Default.ReadState().PlaySeconds;
        ApplySettings();
        _hud = NewHud(); // the records of the loaded profile
        LoadProgress(); // Legend and Story of the loaded profile
    }

    // ---------------------------------------------------------------- extra cars (replayed rival, ghost)

    /// <summary>A car drawn besides the game's own: its model, physics body and interpolation state; see-through as the ghost.</summary>
    private sealed class ShowCar(CarModel model, Matrix4x4 modelToBody, Vehicle vehicle, bool ghost) : IDisposable
    {
        public CarModel Model => model;
        public Matrix4x4 ModelToBody => modelToBody;
        public Vehicle Vehicle => vehicle;
        public bool Ghost => ghost;
        public string Car = "";
        public int Paint;
        public Vector3 PrevPosition;
        public Quaternion PrevOrientation = Quaternion.Identity;
        public Matrix4x4 Pose, Body;
        public readonly Matrix4x4[] Wheels = new Matrix4x4[4];
        public VehicleInput Input;
        public Headlights Lights = new(Headlights.Mode.Off);
        public readonly float[] Smoke = new float[4], Spray = new float[4];
        public void Dispose() => model.Dispose();
    }

    private readonly List<ShowCar> _showCars = [];
    private ShowCar? _ghost;
    private ReplayPlayer? _ghostPlayer;
    private readonly SceneLights _showLamps = new();

    private ShowCar NewShowCar(Iso9660 iso, string car, int paint, CarSpec spec, bool ghost)
    {
        var model = CarModel.Load(iso, car, paint, _renderer, ghost ? Livery.None : Livery.Rival);
        var v = new Vehicle(spec) { SurfaceGrip = _drive.Car.SurfaceGrip };
        var s = new ShowCar(model, ModelToBody(model, spec), v, ghost) { Car = car, Paint = paint, Lights = new Headlights(Lights ?? Headlights.For(_courseTime, _fog)) };
        _showCars.Add(s);
        return s;
    }

    /// <summary>Frees the extra cars (their textures came last: before the rival's and the player's go, <see cref="DisposeRival"/>).</summary>
    private void DisposeShowCars()
    {
        foreach (var s in Enumerable.Reverse(_showCars)) s.Dispose();
        _showCars.Clear();
        (_ghost, _ghostPlayer) = (null, null);
    }

    private static CarSpec SpecOf(ReplayCar c) =>
        new Settings { SteerAssist = c.SteerAssist, DriftAssist = c.DriftAssist }.Assisted(CarSpecs.All[c.Car]);

    /// <summary>
    ///     Time attack with GHOST on: the best run of this course, route and assists (<see cref="ReplayStore.BestPath"/>) drives
    ///     from the start with the player, see-through, as its own car (the model stays loaded over retries with the same car).
    /// </summary>
    private void LoadGhost()
    {
        var path = GhostFile ?? ReplayStore.BestPath(_settings.RunKey(_courseTime[.._courseTime.LastIndexOf('_')], _drive.Reverse));
        if (GhostFile == null && (!SavesRuns || !_settings.Ghost || _race != null || _menu == null) || !File.Exists(path))
        {
            DisposeShowCars();
            return;
        }
        try
        {
            var replay = Replay.Load(path);
            if (replay.CarCount != 1 || !CarSpecs.All.ContainsKey(replay.Info.Cars[0].Car)) throw new InvalidDataException("kein Einzellauf");
            var c = replay.Info.Cars[0];
            if (_ghost is not { } g || _showCars.Count != 1 || g.Car != c.Car || g.Paint != c.Paint || g.Vehicle.Spec != SpecOf(c))
            {
                DisposeShowCars();
                using var iso = new Iso9660(isoPath);
                _ghost = NewShowCar(iso, c.Car, c.Paint, SpecOf(c), true);
            }
            _ghostPlayer = new ReplayPlayer(replay, [_ghost!.Vehicle], _drive.Ground);
            _ghostPlayer.Seek(0);
            (_ghost.PrevPosition, _ghost.PrevOrientation) = (_ghost.Vehicle.Position, _ghost.Vehicle.Orientation);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException or System.Text.Json.JsonException)
        {
            Console.WriteLine($"\n[Replay] Geist {path}: {e.Message}");
            DisposeShowCars();
        }
    }

    /// <summary>The ghost's tick, in step with the player's run (it parks once its run is over).</summary>
    private void GhostTick()
    {
        if (_ghostPlayer is not { } p || _ghost == null) return;
        if (!p.Step()) return;
        (_ghost.PrevPosition, _ghost.PrevOrientation, _ghost.Input) = (p.PrevPosition[0], p.PrevOrientation[0], p.LastInput(0));
    }

    private void UpdateShowMatrices(float alpha)
    {
        foreach (var s in _showCars)
            (s.Pose, s.Body) = PoseCar(s.Vehicle, s.Model, s.ModelToBody, s.PrevPosition, s.PrevOrientation, alpha, s.Wheels, Matrix4x4.Identity);
    }

    /// <summary>Sun-shadow casters of the solid extra cars (the ghost casts none).</summary>
    private int ShowCasters(Span<(StaticMesh, Matrix4x4)> dst)
    {
        foreach (var s in _showCars)
        {
            if (s.Ghost || dst.Length < 5) continue;
            dst[0] = (s.Model.Day.Body, s.Body);
            for (var i = 0; i < 4; i++) dst[1 + i] = (s.Model.Wheel, s.Wheels[i]);
            return 5;
        }
        return 0;
    }

    /// <summary>
    ///     The extra cars into the scene pass: solid ones with their own lamps (as <see cref="DrawRival"/>), the ghost
    ///     see-through, fading out within 10 m of the camera so it never blocks the view.
    /// </summary>
    private void DrawShowCars(IRenderPassEncoder pass, in Matrix4x4 viewProj)
    {
        if (_probe != null) return;
        var l = _renderer.Lights;
        foreach (var s in _showCars)
        {
            if (s.Ghost)
            {
                if (_player != null || _ghostPlayer is { Done: true } && s.Vehicle.Velocity.LengthSquared() < 0.01f) continue;
                var a = 0.5f * Math.Clamp((Vector3.Distance(_pos, s.Body.Translation) - 3) / 7, 0, 1);
                if (a > 0.01f) _carRenderer.DrawGhost(pass, s.Model.Day.Body, s.Model.Wheel, s.Body, s.Wheels, viewProj, _pos, a, new Vector3(0.75f, 0.95f, 1.3f)); // cool blue-white
                continue;
            }
            var lights = s.Lights;
            lights.Apply(_showLamps, s.Model.Lamp, s.Body, _renderer.Atmosphere.LocalLightShare, s.Input.Brake, s.Vehicle.Gear < 0);
            var (glow, brake, reverse) = (l.LampGlow, l.Brake, l.Reverse);
            (l.LampGlow, l.Brake, l.Reverse) = (_showLamps.LampGlow, _showLamps.Brake, _showLamps.Reverse);
            var shell = s.Model.ShellFor(lights.State != Headlights.Mode.Off, InCabin(s.Body));
            _carRenderer.Draw(pass, shell.Body, shell.Decals, s.Model.Wheel, s.Body, s.Wheels, viewProj, _pos);
            (l.LampGlow, l.Brake, l.Reverse) = (glow, brake, reverse);
        }
    }

    // ---------------------------------------------------------------- viewer

    private enum Back { Pause, Result, Menu }

    private ReplayPlayer? _player;
    private Back _viewerBack;
    private byte[][]? _liveStates;
    private Effects? _liveFx;
    private TvCameras? _tv;
    private float _replayAcc;
    private int _tvIndex = -1;
    private readonly FreeCam _viewerCam = new();

    /// <summary>
    ///     Opens the viewer on <paramref name="replay"/>. From the pause menu or the result sheet it plays on the cars of the
    ///     race (their live state is kept and comes back on exit); from the list it loads the replay's course and car (and
    ///     the other cars as <see cref="ShowCar"/>s).
    /// </summary>
    private void OpenReplay(Replay replay, Back back)
    {
        _viewerBack = back;
        using var iso = new Iso9660(isoPath);
        Vehicle[] cars;
        if (back != Back.Menu)
        {
            cars = [.. LiveCars()];
            if (cars.Length != replay.CarCount || replay.Ticks == 0) return;
            _liveStates = [.. cars.Select(c => c.SaveState())];
            (_liveFx, _fx) = (_fx, new Effects());
        }
        else
        {
            var i = replay.Info;
            CheckPlayable(i);
            var me = i.Cars[0];
            if (i.Course != _courseTime || i.Reverse != _drive.Reverse || i.Fog != _fog) LoadCourse(iso, i.Course, i.Reverse, me.Car, me.Paint, fog: i.Fog);
            else if (me.Car != _carName || me.Paint != _paint) SwitchCar(Array.IndexOf(CarPaint.Cars, me.Car), me.Paint);
            if (_drive.Car.Spec != SpecOf(me)) _drive.ChangeCar(SpecOf(me));
            DisposeShowCars();
            foreach (var c in i.Cars.Skip(1)) NewShowCar(iso, c.Car, c.Paint, SpecOf(c), false);
            cars = [_drive.Car, .. _showCars.Select(s => s.Vehicle)];
            _fx = new Effects();
            _inRace = true;
        }
        _menu?.Close();
        _replayMenu?.Close();
        _player = new ReplayPlayer(replay, cars, _drive.Ground);
        _tv = new TvCameras(ReplayCameras.Load(iso, _courseTime[.._courseTime.LastIndexOf('_')], _drive.Reverse), _course.Road, _drive.Reverse);
        Console.WriteLine($"\n[Replay] {replay.Info.Course} {replay.Seconds:F1} s, {replay.CarCount} Autos, {_tv.Count} TV-Kameras");
        _fly = false;
        _viewer.Open();
        SeekReplay(0);
        _music = ""; // the race music plays on (or starts)
    }

    private void CloseReplay()
    {
        var back = _viewerBack;
        var cars = _player!.Cars;
        _player = null;
        _viewer.Close();
        _tvIndex = -1;
        if (back != Back.Menu && _liveStates != null)
        {
            for (var i = 0; i < cars.Length; i++) cars[i].LoadState(_liveStates[i]);
            if (_race != null)
                foreach (var c in _race.Cars) (c.PrevPosition, c.PrevOrientation) = (c.Vehicle.Position, c.Vehicle.Orientation);
            _fx = _liveFx ?? new Effects();
            (_liveStates, _liveFx) = (null, null);
        }
        SyncPose();
        switch (back)
        {
            case Back.Pause:
                OpenMenu(Menu.Screen.Pause);
                _menu!.Select("Replay");
                break;
            case Back.Result:
                OpenMenu(Menu.Screen.Result);
                _menu!.Settle(Menu.ButtonsAt + 0.3f);
                _menu.Select("REPLAY");
                break;
            default:
                DisposeShowCars();
                _inRace = false;
                _replayMenu!.Open(_replayMenu.Tab, true);
                break;
        }
    }

    /// <summary>The viewer drives the cars instead of the race: <see cref="ReplayViewer.Speed"/> ticks of the replay per game tick.</summary>
    private bool ReplayTick(float dt)
    {
        if (_player == null) return false;
        if (_photo.Active) return true;
        _replayAcc += _viewer.Speed;
        for (; _replayAcc >= 1; _replayAcc--)
            if (!StepReplay(dt))
            {
                (_viewer.Paused, _replayAcc) = (true, 0); // the end: stops there
                break;
            }
        return true;
    }

    private bool StepReplay(float dt)
    {
        var p = _player!;
        if (!p.Step()) return false;
        (_prevPos, _prevRot) = (p.PrevPosition[0], p.PrevOrientation[0]);
        var input = p.LastInput(0);
        _brakeLight = input.Brake;
        for (var i = 1; i < p.Cars.Length; i++)
        {
            if (_race != null && _viewerBack != Back.Menu)
            {
                var r = _race.Cars[i];
                (r.PrevPosition, r.PrevOrientation, r.Input) = (p.PrevPosition[i], p.PrevOrientation[i], p.LastInput(i));
                RivalSound(r.Vehicle, r.Input, dt);
            }
            else if (i - 1 < _showCars.Count)
            {
                var s = _showCars[i - 1];
                (s.PrevPosition, s.PrevOrientation, s.Input) = (p.PrevPosition[i], p.PrevOrientation[i], p.LastInput(i));
                s.Lights.Tick(dt);
                WheelEffects(s.Vehicle, s.Smoke, s.Spray, 4, dt);
            }
        }
        _simTime += dt;
        _lights.Tick(dt);
        TickEffects(dt);
        _audio?.Update(_drive.Car, input.Throttle, input.Handbrake, dt);
        return true;
    }

    /// <summary>Jumps to <paramref name="tick"/> (keyframe + simulation), no interpolation across the jump; going back clears the skid marks.</summary>
    private void SeekReplay(int tick)
    {
        var p = _player!;
        if (tick < p.Tick) _fx = new Effects();
        if (tick > p.Tick && tick - p.Tick < 240)
            while (p.Tick < tick && StepReplay(Drive.Dt)) { }
        else p.Seek(tick);
        p.Sync();
        (_prevPos, _prevRot) = (p.PrevPosition[0], p.PrevOrientation[0]);
        for (var i = 1; i < p.Cars.Length; i++)
            if (_race != null && _viewerBack != Back.Menu) (_race.Cars[i].PrevPosition, _race.Cars[i].PrevOrientation) = (p.PrevPosition[i], p.PrevOrientation[i]);
            else if (i - 1 < _showCars.Count) (_showCars[i - 1].PrevPosition, _showCars[i - 1].PrevOrientation) = (p.PrevPosition[i], p.PrevOrientation[i]);
        _camSnap = true;
        _replayAcc = 0;
    }

    /// <summary>A replay from the list must be playable here: known course and cars (other builds may know more).</summary>
    private void CheckPlayable(ReplayInfo i)
    {
        var id = i.Course[..Math.Max(0, i.Course.LastIndexOf('_'))];
        if (_catalog?.Courses.Any(c => c.Id == id && c.Times.Contains(i.Course[(i.Course.LastIndexOf('_') + 1)..])) == false || i.Cars.Count == 0 ||
            i.Cars.Any(c => !CarSpecs.All.ContainsKey(c.Car)))
            throw new InvalidDataException($"Replay {i.Course} mit {string.Join(", ", i.Cars.Select(c => c.Car))} ist hier nicht spielbar");
    }

    private void UpdateViewer((int X, int Y, bool Ok, bool Back) keys, float dt)
    {
        var p = _player!;
        switch (_viewer.Update(keys, Input, dt, p.Cars.Length, n => _menuAudio?.Play(n)))
        {
            case ReplayViewer.Command.Exit:
                CloseReplay();
                return;
            case ReplayViewer.Command.Restart:
                SeekReplay(0);
                _viewer.Paused = false;
                break;
            case ReplayViewer.Command.Photo:
                _photo.Open(_pos, _camLook, _fov * 180 / MathF.PI);
                return;
        }
        if (_viewer.Scrub != 0) SeekReplay(Math.Clamp(p.Tick + (int)(_viewer.Scrub * 4 * dt * 120 + _viewer.Scrub), 0, p.Replay.Ticks));
        if (_viewer.Scrub > 0 && p.Done) _viewer.Paused = true;
        if (_viewer.Cam == ReplayViewer.Camera.Free) _viewerCam.Update(Input, dt);
        else _viewerCam.Place(_pos, _camLook); // the free camera starts where the last one was
    }

    /// <summary>Interpolation for the replay: slow motion steps the cars every few ticks, the picture glides between them.</summary>
    private float ReplayAlpha(float tickAlpha) =>
        _player == null ? tickAlpha : _viewer.Speed == 0 ? 1 : Math.Clamp(_replayAcc + tickAlpha * MathF.Min(_viewer.Speed, 1), 0, 1);

    private (Vehicle Car, string Name) FocusCar()
    {
        var p = _player!;
        var i = Math.Min(_viewer.Focus, p.Cars.Length - 1);
        var info = p.Replay.Info.Cars[i];
        return (p.Cars[i], (_replayMenu?.CarName(info.Car) ?? info.Car) + (p.Cars.Length > 1 ? "   " + info.Name : ""));
    }

    private Matrix4x4 FocusPose()
    {
        var i = Math.Min(_viewer.Focus, _player!.Cars.Length - 1);
        if (i == 0) return _carPose;
        if (_race != null && _viewerBack != Back.Menu) return _rivalPose;
        return i - 1 < _showCars.Count ? _showCars[i - 1].Pose : _carPose;
    }

    /// <summary>Model matrix and model of the focused car (as <see cref="FocusPose"/>).</summary>
    private (Matrix4x4 Body, CarModel Model) FocusModel()
    {
        var i = Math.Min(_viewer.Focus, _player!.Cars.Length - 1);
        if (i == 0) return (_carBody, _car);
        if (_race != null && _viewerBack != Back.Menu) return (_rivalBody, _rivalModel!);
        return i - 1 < _showCars.Count ? (_showCars[i - 1].Body, _showCars[i - 1].Model) : (_carBody, _car);
    }

    /// <summary>The viewer's and photo mode's camera; false when the game's own runs.</summary>
    private bool ReplayCamera(float dt)
    {
        if (_photo.Active)
        {
            (_pos, _camLook, _fov) = (_photo.Cam.Position, _photo.Cam.Position + _photo.Cam.Forward, _photo.Fov * MathF.PI / 180);
            return true;
        }
        if (_player == null) return false;
        var pose = FocusPose();
        var car = pose.Translation;
        var fwd = Vector3.TransformNormal(Vector3.UnitZ, pose);
        switch (_viewer.Cam)
        {
            case ReplayViewer.Camera.Tv:
                var (eye, fov, index) = _tv!.At(car);
                var snap = index != _tvIndex || _camSnap;
                _tvIndex = index;
                var look = car + Vector3.UnitY * 0.5f;
                // a TV operator: the aim lags a touch behind the car, cuts are hard
                (_pos, _camLook, _fov) = (eye, snap ? look : Vector3.Lerp(_camLook, look, 1 - MathF.Exp(-14 * dt)), fov * MathF.PI / 180);
                break;
            case ReplayViewer.Camera.Free:
                (_pos, _camLook, _fov) = (_viewerCam.Position, _viewerCam.Position + _viewerCam.Forward, MathF.PI / 3);
                break;
            default: // the driving cameras on the focused car
                var view = ReplayViewer.Driving(_viewer.Cam)!.Value;
                var (body, model) = FocusModel();
                (_pos, _camLook, _fov) = CameraRig.Place(view, _pos, _camLook, _camSnap, dt, pose, body, model.Mounts,
                    _player.Cars[Math.Min(_viewer.Focus, _player.Cars.Length - 1)].SpeedKmh, _settings.Fov * MathF.PI / 180);
                _onBoard = view is CameraView.Hood or CameraView.Cockpit ? view : null;
                break;
        }
        _camSnap = false;
        return true;
    }

    // ---------------------------------------------------------------- photo mode

    private float _photoExposure;
    private string? _photoPath;
    private Atmosphere? _photoAtmosphere;

    private void OpenPhoto()
    {
        _menu?.Close();
        if (!_fly) UpdateDriveCamera(0); // the race view it starts from
        _photo.Open(_pos, _camLook, _fov * 180 / MathF.PI);
    }

    private void UpdatePhoto((int X, int Y, bool Ok, bool Back) keys, float dt)
    {
        var a = _renderer.Atmosphere;
        if (_photoAtmosphere != a) (_photoAtmosphere, _photoExposure) = (a, a.Exposure);
        switch (_photo.Update(keys, Input, dt, n => _menuAudio?.Play(n)))
        {
            case PhotoMode.Command.Exit:
                a.Exposure = _photoExposure;
                _photoAtmosphere = null;
                _photo.Close();
                if (_player == null)
                {
                    OpenMenu(Menu.Screen.Pause);
                    _menu!.Select("Photo");
                }
                return;
            case PhotoMode.Command.Capture when _shotState == 0:
                Directory.CreateDirectory(PhotoMode.Folder);
                _photoPath = Path.Combine(PhotoMode.Folder, $"photo_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
                if (Flow == null) _capture?.Dispose();
                (_capture, _shotState, _photo.Capturing) = (new FrameCapture(Device, Device.SwapchainWidth, Device.SwapchainHeight), 1, true);
                break;
        }
        a.Exposure = _photoExposure * MathF.Pow(2, _photo.Ev);
    }

    /// <summary>Where the captured frame goes: a photo's path (then photo mode goes on), else null (--shot/--flow).</summary>
    private string? PhotoShotDone()
    {
        if (_photoPath is not { } path) return null;
        _photoPath = null;
        _photo.Capturing = false;
        _photo.Saved(path);
        return path;
    }
}
