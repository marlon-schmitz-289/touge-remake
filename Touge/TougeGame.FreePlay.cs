using System.Numerics;
using Kansei.Graphics;
using Kansei.Physics;
using Touge.Formats;
using Touge.Race;
using Touge.Ui;

namespace Touge;

/// <summary>
///     FREE PLAY (<see cref="FreePlay"/> lobby, hosted by <see cref="Versus"/>): the lobby's choice is loaded behind the white
///     loading screen, the telop says FREE PLAY (no countdown), and the course is the player's — with 0–3 AI cars set off ahead
///     (<see cref="RaceSession"/> in free play: nobody finishes, the AI goes back to the start at the course end). Past the goal
///     the car coasts to a stop on the run-out, then (<see cref="FreePlayChoice.End"/>) behind a short black fade it is turned onto
///     the other direction's line at that end (B does the same anywhere), put back at the start, or left there. Pause:
///     CONTINUE / RESET (back onto the road) / CHANGE (the lobby: course, time, weather, car … then on) / PHOTO / EXIT.
///     Not recorded, no records. Two players: VERSUS with the rule FREE RUN (<see cref="Net.NetRule.Free"/>).
/// </summary>
public sealed partial class TougeGame
{
    /// <summary>--flow … --freeplay: the scripted pass goes through FREE PLAY (<see cref="FreePlayFlowScript"/>).</summary>
    public bool FreePlayFlow { get; init; }

    /// <summary>The solo free run being driven (null: none); its AI field and how often it was turned or restarted.</summary>
    private FreePlayChoice? _free;
    private Rivals.Rival[] _freeField = [];
    private int _freeLegs;
    private bool _fpLoadPending, _freeActed;
    /// <summary>What the course end asked for, done at the middle of the fade (<see cref="FreeFade"/> s out, as long back in).</summary>
    private CourseEndAction? _freePending;
    private float _freeFade;
    private const float FreeFade = 0.35f;

    /// <summary>A free run of any kind (solo, split screen or online with the rule FREE RUN): no race state.</summary>
    private bool FreeRun => _vsRace != null && (_free != null || _vsConfig.Rule == Net.NetRule.Free);

    /// <summary>FREE PLAY from the main menu (or --menu freeplay): the lobby on the remembered choice and the saved car.</summary>
    private void OpenFreePlay()
    {
        OpenVersus();
        _versusUi!.OpenFree(_settings.FreePlay, _settings.Car, _settings.Paint, _settings.Manual);
        if (StartMenu == "freeplay" && shotPath != null) _versusUi.Settle();
    }

    /// <summary>The lobby's START: remembered, then loaded behind the white screen (<see cref="FreePlayLoadStep"/>).</summary>
    private bool FreePlayAction(Versus.Action action)
    {
        if (action != Versus.Action.FreeStart) return false;
        var lobby = _versusUi!.FreeLobby;
        _settings.FreePlay = lobby.Choice.Copy();
        (_settings.Car, _settings.Paint, _settings.Manual) = (lobby.CarId, lobby.Paint, lobby.Manual);
        if (SavesRuns) _settings.Save();
        _versusUi.ShowLoading();
        _fpLoadPending = true;
        return true;
    }

    /// <summary>Per frame from the versus update: once the loading screen is up, the run loads (the course unless it is there, the car, the AI cars) and the telop starts.</summary>
    private void FreePlayLoadStep()
    {
        if (!_fpLoadPending || _versusUi is not { Current: Versus.Screen.Loading, Shown: true } ui) return;
        _fpLoadPending = false;
        var lobby = ui.FreeLobby;
        var c = lobby.Choice.Copy();
        EndVersusRace();
        EndFreeBattle();
        EndRecording();
        Device.WaitIdle();
        DisposeVersusCars();
        _vsCars.Clear();
        (_vsLoadedKey, _vsSplit, _storyRain) = (null, false, false);
        _freeField = FreePlay.Field(c);
        _vsCars.AddRange(_freeField.Select((r, i) => new VsCar((byte)(i + 1), r.Name, r.Car, r.Paint)));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var iso = new Iso9660(isoPath))
        {
            var fog = c.Fog && !c.Course.EndsWith("_RIN");
            if (c.Course != _courseTime || c.Reverse != _drive.Reverse || fog != _fog) LoadCourse(iso, c.Course, c.Reverse, lobby.CarId, lobby.Paint, fog: fog); // the AI cars come with it
            else if (lobby.CarId != _carName || lobby.Paint != _paint) SwitchCar(Array.IndexOf(CarPaint.Cars, lobby.CarId), lobby.Paint);
            else LoadVersusCars(iso);
        }
        _free = c;
        _freeLegs = 0;
        var menu = _menu!;
        (menu.Legend, menu.Rain, menu.FreeBattle) = (false, false, false);
        ui.Close();
        _drive.Car.AutomaticGearbox = !lobby.Manual;
        _drive.ResetTo(0);
        StartFreeRun();
        OpenMenu(Menu.Screen.Intro);
        Console.WriteLine($"\n[FreePlay] {c.Course}{(c.Reverse ? " rückwärts" : "")}{(_fog ? " Nebel" : "")}: {lobby.CarId}, Kursende {c.End}, Zeit {(c.Timer ? "an" : "aus")}, " +
                          $"{_freeField.Length} KI-Autos ({string.Join(", ", _freeField.Select(r => $"{r.Name} {r.Car}"))}, {c.Traffic}), geladen in {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>A fresh leg where the car stands now (start, turned around, B): HUD and timing, the session with the AI cars ahead.</summary>
    private void StartFreeRun()
    {
        _hud = NewHud();
        (_hud.ShowTiming, _hud.Records) = (_free!.Timer, false);
        (_fx, _simTime, _finished) = (new Effects(), 0, false);
        NewFreeRace();
        var menu = _menu!;
        (menu.Versus, menu.NoRetry, menu.NoReplay, menu.Free, menu.FreePause) = (null, false, false, true, true);
        (_inRace, _camSnap, _fly) = (true, true, false);
    }

    /// <summary>The free run's session: the player's car where it stands, each AI car 45 m + 40 m per car further along (behind if the road ends first).</summary>
    private void NewFreeRace()
    {
        var race = new RaceSession(_drive.Ground, _drive.Line, _drive.RunOutLine) { AtCourseEnd = FreeCourseEnd };
        var me = race.Add("YOU", _drive.Car, _p1);
        me.Track.Nearest(_drive.Car.Position);
        (me.Along, me.Lateral) = me.Track.Track(_drive.Car.Position);
        for (var i = 0; i < _freeField.Length && i < _vsCars.Count; i++)
        {
            var r = _freeField[i];
            var car = race.Add(r.Name, new Vehicle(r.Spec) { SurfaceGrip = _drive.Car.SurfaceGrip, AutomaticGearbox = true },
                new AiDriver(new RivalPilot(_drive.Line, r.Style) { Seed = 17 + 31 * i }));
            var placed = false;
            foreach (var s in Enumerable.Range(0, 8).Select(k => me.Along + 45 + 40 * i + 6 * k).Concat(Enumerable.Range(0, 8).Select(k => me.Along - 30 - 40 * i - 6 * k)))
                if (placed = race.Place(car, s, 0))
                    break;
            if (!placed) race.BackToStart(car);
            _vsCars[i].Race = car;
            Array.Clear(_vsCars[i].Smoke);
            Array.Clear(_vsCars[i].Spray);
        }
        _vsRace = race;
        (_netRace, _vsReferee, _vsResult, _vsDecidedAt, _vsJumps) = (null, null, null, -1, (me.Jumps, 0));
        _hud.Rival = null;
        SyncPose();
        UpdateVersusMatrices(1);
    }

    /// <summary>The session at the course end: an AI car back to the start; the player's car as the lobby says (behind the fade, <see cref="FreePlayStep"/>).</summary>
    private bool FreeCourseEnd(RaceCar car)
    {
        if (car != _vsRace!.Cars[0]) return _vsRace.BackToStart(car);
        if (_free!.End == CourseEndAction.Stop || _freePending != null) return false; // stays: its driver's again (R, B, drive back)
        (_freePending, _freeFade, _freeActed) = (_free.End, 0, false);
        return false;
    }

    /// <summary>Per frame: the course end's fade — out, then the car turned or back at the start, then in again.</summary>
    private void FreePlayStep(float dt)
    {
        if (_freePending is not { } action || _free == null) return;
        _freeFade += MathF.Min(dt, 1 / 20f);
        if (!_freeActed && _freeFade >= FreeFade)
        {
            _freeActed = true;
            if (action == CourseEndAction.TurnAround) TurnFree(atEnd: true);
            else
            {
                _drive.ResetTo(0);
                _freeLegs++;
                StartFreeRun();
                Console.WriteLine($"\n[FreePlay] Kursende: zurück zum Start ({_freeLegs}.)");
            }
        }
        if (_freeFade >= 2 * FreeFade) _freePending = null;
    }

    /// <summary>Turned onto the other direction's line: at the course end its start (where the car stopped), else (B) where the car is.</summary>
    private void TurnFree(bool atEnd)
    {
        using (var iso = new Iso9660(isoPath)) _drive.SetDirection(iso, !_drive.Reverse);
        if (atEnd) _drive.ResetTo(0);
        else _drive.ResetNearest();
        _freeLegs++;
        StartFreeRun();
        Console.WriteLine($"\n[FreePlay] {(atEnd ? "Kursende: umgedreht" : "Richtung gewechselt")}, jetzt {(_drive.Reverse ? "rückwärts" : "vorwärts")} ({_freeLegs}.)");
    }

    /// <summary>Black over everything while the course end turns the car (0 without).</summary>
    private float FreeFadeAlpha => _freePending == null ? 0 : Math.Clamp(1 - MathF.Abs(_freeFade - FreeFade) / FreeFade, 0, 1);

    /// <summary>Pause → CHANGE: the lobby on this run's choice (the run stays loaded; START drives on with the new choice).</summary>
    private void ReturnToFreePlayLobby()
    {
        _inRace = false;
        _freePending = null;
        _versusUi!.OpenFree(_settings.FreePlay, _carName, _paint, !_drive.Car.AutomaticGearbox);
    }

    /// <summary>Pause → EXIT: the run goes, back to the main menu.</summary>
    private void ExitFreePlay()
    {
        EndFreePlay();
        _inRace = false;
        if (_front == null) Window.ShouldClose = true; // a --menu start has no main menu to go back to
        else _front.Open(FrontEnd.Step.Modes);
    }

    /// <summary>Drops the free run (AI cars, session, the menus' routing): what follows is plain time attack again.</summary>
    private void EndFreePlay()
    {
        if (_free == null) return;
        (_free, _freePending, _freeField) = (null, null, []);
        EndVersusRace();
        Device.WaitIdle();
        DisposeVersusCars();
        _vsCars.Clear();
        _vsLoadedKey = null;
        _hud.ShowTiming = true;
        _drive.ResetTo(0);
        SyncPose();
    }

    /// <summary>--flow … --freeplay: the state of the free run (course end, fade, leg after a turn) as the script waits for it.</summary>
    private string FreeFlowAt => _freePending != null ? "FreeTurn"
        : _vsRace?.Cars[0] is { } me && me.Along >= _vsRace.Goal ? "FreeEnd"
        : _freeLegs > 0 ? $"FreeLeg{_freeLegs}" : "Race";

    /// <summary>
    ///     --flow … --freeplay: main menu → FREE PLAY → lobby (AI CARS 1, CAR → maker → car) → START → telop → free run (autopilot, 16×)
    ///     past the goal → stopped → turned around (fade) → back up the other way → pause → CHANGE → lobby (NIGHT) → START → night run →
    ///     pause → EXIT → main menu.
    /// </summary>
    private static readonly (string At, float Wait, string? Shot, int X, int Y, bool Ok, bool Back)[] FreePlayFlowScript =
    [
        ("Boot", 1.2f, null, 0, 0, true, false), ("Logo", 1, null, 0, 0, true, false), ("Title", 1.5f, null, 0, 0, true, false),
        ("Modes", 1, null, 0, 1, false, false), ("Modes", 0.5f, null, 0, 1, false, false), ("Modes", 0.5f, null, 0, 1, false, false),
        ("Modes", 0.6f, "fp_main_menu", 0, 0, true, false),
        ("VsFree", 1.5f, "fp_lobby", 0, 1, false, false), .. Enumerable.Repeat(("VsFree", 0.25f, (string?)null, 0, 1, false, false), 4),
        ("VsFree", 0.5f, "fp_lobby_cars", 1, 0, false, false), ("VsFree", 0.6f, "fp_lobby_ai", 0, 1, false, false),
        ("VsFree", 0.5f, "fp_lobby_traffic", 0, 1, false, false),
        ("VsFree", 0.5f, null, 0, 0, true, false), ("VsFree", 1, "fp_pick_maker", 0, 0, true, false), ("VsFree", 1.5f, "fp_pick_car", 0, 0, true, false),
        ("VsFree", 0.5f, null, 0, 1, false, false), ("VsFree", 0.25f, null, 0, 1, false, false), ("VsFree", 0.25f, null, 0, 1, false, false),
        ("VsFree", 0.6f, "fp_lobby_start", 0, 0, true, false),
        ("VsLoading", 0.3f, null, 0, 0, false, false), ("Intro", 1.2f, "fp_telop", 0, 0, false, false),
        ("Race", 2, "fp_race", 0, 0, false, false), ("Race", 2.5f, "fp_race_ai", 0, 0, false, false),
        ("FreeEnd", 0, "fp_past_goal", 0, 0, false, false), ("FreeTurn", 0, null, 0, 0, false, false),
        ("FreeLeg1", 0.4f, "fp_turned_around", 0, 0, false, false), ("FreeLeg1", 2, "fp_back_up", 0, 0, false, true),
        ("Pause", 0.8f, "fp_pause", 1, 0, false, false), ("Pause", 0.3f, null, 1, 0, false, false), ("Pause", 0.5f, "fp_pause_change", 0, 0, true, false),
        ("VsFree", 1.2f, "fp_change_lobby", 0, -1, false, false), .. Enumerable.Repeat(("VsFree", 0.25f, (string?)null, 0, -1, false, false), 7),
        ("VsFree", 0.5f, null, 1, 0, false, false), ("VsFree", 0.6f, "fp_change_night", 0, 1, false, false),
        .. Enumerable.Repeat(("VsFree", 0.25f, (string?)null, 0, 1, false, false), 7), ("VsFree", 0.5f, null, 0, 0, true, false),
        ("VsLoading", 0.3f, null, 0, 0, false, false), ("Intro", 1, null, 0, 0, false, false), ("Race", 3, "fp_race_night", 0, 0, false, true),
        ("Pause", 0.6f, null, 1, 0, false, false), .. Enumerable.Repeat(("Pause", 0.25f, (string?)null, 1, 0, false, false), 3), ("Pause", 0.5f, "fp_pause_exit", 0, 0, true, false),
        ("Modes", 1.2f, "fp_modes_back", 0, 0, false, false),
    ];
}
