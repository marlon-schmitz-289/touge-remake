using System.Numerics;
using Kansei.Graphics;
using Kansei.Input;
using Touge.Formats;

namespace Touge.Ui;

/// <summary>
///     The game-flow screens behind the main menu, rebuilt in the original's style with <see cref="Canvas"/> (no original
///     textures), flow after its game-flow controller 0x1702A0 (Time Attack: course first, then the car):
///     course select (3 × 4 grid as K_CRSSEL, map line in the carbon "monitor" instead of the photo) → route → time of
///     day → weather (choice pairs as T_TRIAL, steps with one option skipped; FOG, an addition, over the day or night course) → car select in two steps (<see cref="CarPicker"/>:
///     maker as T_MKSEL's 7 chrome plates, then that maker's cars beside the turning 3D car, body colour) → transmission → loading (white, "Now Loading...")
///     → course telop and 3-2-1-GO (TLP_STG, CAR010/CAR011) → race; pause bar (Continue/Retry/Exit, PAUSE.PAC) →
///     finish banner (FINISH.PAC) → result sheet with tallied rows (RESULT.PAC, NAME001) and the action buttons
///     (ACTCHOICE: Retry, Course Select, Car Select, Exit). Records (REC_TEX) and Options (<see cref="Ui.Options"/>: OPSL sections, OPGM rows) hang off the main
///     menu. 30-frame black fades between modules, steps inside a module switch in place. Back walks the visited steps.
///     UI sounds by SYSSE name through <see cref="Sound"/> (SYS005 move, SYS006 decide, BEEP001 back/locked); the
///     game reads <see cref="Music"/>, reacts to the returned <see cref="Action"/> and reads the selection.
/// </summary>
public sealed partial class Menu(Catalog catalog, Settings settings)
{
    public enum Screen { None, Course, Route, Time, Weather, Maker, Car, Gearbox, Loading, Intro, Pause, Finish, Result, Records, Options, Controls }

    /// <summary>
    ///     Load: load <see cref="CourseTime"/>/<see cref="CarId"/>… (the menu goes on into the telop); Restart: car back to the
    ///     start (the telop follows); Exit: back to the main menu; PreviewCar: show <see cref="CarId"/>/<see cref="Paint"/>;
    ///     Quit: close the game (confirmed in the pause menu); Replay: watch the run (pause, result); Photo: photo mode (pause);
    ///     Rivals: back to the Legend of the Streets rival ladder.
    /// </summary>
    /// <remarks>ResetCar: free play's pause RESET (back onto the road where the car is).</remarks>
    public enum Action { None, Load, Resume, Restart, Exit, PreviewCar, SettingsChanged, Quit, Replay, Photo, Rivals, ResetCar, TurnAround }

    /// <summary>A finished run for the result sheet.</summary>
    /// <param name="Deltas">Per sector against the best run it was compared with (null without one).</param>
    /// <param name="Previous">Best total before this run (null if none).</param>
    public sealed record Run(float Time, float[] Splits, float?[] Deltas, float? Previous, bool NewRecord, float Drift);

    /// <summary><see cref="Music"/> value for "the race's Eurobeat".</summary>
    public const string RaceMusic = "race";

    public const float Fade = 30 / 60f;
    /// <summary>Intro timeline (s): telop until the count, "3" "2" "1" a second apart, GO, the GO sign gone.</summary>
    public const float CountAt = 2.6f, GoAt = CountAt + 3, IntroEnd = GoAt + 0.8f;
    /// <summary>Loading: the load is asked for once the white screen has been up this long.</summary>
    public const float LoadAt = Fade + 0.4f;
    /// <summary>Finish banner time before the result sheet; result rows one after another, then the buttons.</summary>
    public const float FinishHold = 3.5f, RowFirst = 0.5f, RowStep = 0.3f;
    private const int ResultRows = 8;
    public static float ButtonsAt => RowFirst + RowStep * ResultRows + 0.2f;

    /// <summary>Pause buttons; "Quit Game" only on desktop builds (<see cref="QuitPrompt"/>).</summary>
    public static readonly string[] PauseButtons = ["Continue", "Retry", "Replay", "Photo", "Exit", .. QuitPrompt.Available ? new[] { "Quit Game" } : []],
        ResultButtons = ["RETRY", "REPLAY", "COURSE SELECT", "CAR SELECT", "EXIT"],
        /// <summary>Free play's pause: back onto the road, CHANGE (the free play lobby: course, time, weather, car …), photo.</summary>
        FreePauseButtons = ["Continue", "Reset", "Change", "Turn", "Photo", "Exit", .. QuitPrompt.Available ? new[] { "Quit Game" } : []];
    private static readonly Dictionary<string, string> PauseCaptions = new()
    {
        ["Continue"] = "Return to the race.", ["Retry"] = "Restart the race from the beginning.", ["Replay"] = "Watch the run so far.",
        ["Photo"] = "Free camera, take a picture.", ["Exit"] = "Quit this race.", ["Quit Game"] = "Close the game.",
        ["Reset"] = "Back onto the road where you are.", ["Turn"] = "Turn around: drive the other way from here.", ["Change"] = "Course, time, weather, car, AI cars: change and drive on.",
    };
    /// <summary>Free play's captions where the race's would say "race".</summary>
    private static readonly Dictionary<string, string> FreeCaptions = new() { ["Continue"] = "Back to driving.", ["Exit"] = "Back to the main menu." };
    private string[] Pause => FreePause ? FreePauseButtons : PauseButtons;
    private readonly QuitPrompt _quit = new();

    public Screen Current { get; private set; }
    /// <summary>The OPTIONS sections and pages (<see cref="Ui.Options"/>; other features add their pages here).</summary>
    public Options Options { get; } = new(settings);
    /// <summary>Options → CONTROLLER (needs the live input; without it the page shows the default layout as text).</summary>
    public ControlsScreen? Controls { get; set; }
    private const string ControllerPage = "CONTROLLER";

    // ---- battle (Touge/Race): set by the game for a battle run, null in time attack
    /// <summary>The decided battle: finish banner YOU WIN/LOSE (WIN.adx/LOSE.adx) and the battle result sheet.</summary>
    public BattleReport? Battle { get; set; }
    /// <summary>Rival shown in the telop ("VS …").</summary>
    public string? Versus { get; set; }
    /// <summary>Online race: the pause's RETRY is greyed out and skipped (one player cannot restart a shared race).</summary>
    public bool NoRetry { get; set; }
    /// <summary>Versus: the pause's Replay and Photo are greyed out (versus runs are not recorded; an online race runs on).</summary>
    public bool NoReplay { get; set; }
    private bool PauseOff(int i) => Pause[i] switch { "Retry" => NoRetry, "Replay" or "Photo" => NoReplay, _ => false };
    // ---- free play (Ui/FreePlay, VERSUS rule FREE RUN): the telop says FREE PLAY and there is no countdown; FreePause: the solo run's pause buttons
    public bool Free { get; set; }
    public bool FreePause { get; set; }
    /// <summary>Seconds into the intro when the cars may go: GO, or in free play the telop's end.</summary>
    private float IntroGo => Free ? CountAt : GoAt;
    /// <summary>Original UI sound by SYSSE name.</summary>
    public Action<string>? Sound { get; set; }

    // ---- Legend of the Streets (Ui/LegendScreen): its battles leave to the rival ladder, not the main menu
    /// <summary>A Legend battle: backing out of the car select, pause Exit and the result's RIVAL SELECT return <see cref="Action.Rivals"/>.</summary>
    public bool Legend { get; set; }
    // ---- free battle (VS CPU, Ui/FreeBattle): pause Exit and the result's CHANGE SETTINGS return Action.Rivals (its lobby)
    public bool FreeBattle { get; set; }
    public static readonly string[] FreeBattleButtons = ["RETRY", "REPLAY", "CHANGE SETTINGS", "EXIT"];
    /// <summary>Cars not won yet (Legend's secret car): shown as ?????, not selectable.</summary>
    public Func<string, bool>? CarLocked { get => _picker.CarLocked; set => _picker.CarLocked = value; }

    /// <summary>MAKER and CAR (<see cref="CarPicker"/>, shared with the lobbies).</summary>
    private readonly CarPicker _picker = new(catalog);
    private int _slot, _choice, _row;
    private bool _reverse, _night, _wet, _fog, _manual, _loadAsked, _fadeIn;
    private float _t, _clock, _leave = -1;
    private Screen _next;
    private Action _then;
    private readonly Stack<Screen> _back = new();
    private Run? _run;

    private const int Slots = 12; // the original's grid; slot 11 = four-pass run (Menu.FourPasses.cs)
    private Catalog.Course SelectedCourse => InFourPass ? CourseOf(FourPass!.Current) : catalog.Courses[Math.Min(_slot, catalog.Courses.Count - 1)];
    public string CourseTime => $"{SelectedCourse.Id}_{(_night ? "NIT" : _wet ? "RIN" : "DAY")}";
    public bool Reverse => InFourPass ? FourPass!.Current.Reverse : _reverse;
    /// <summary>Fog over <see cref="CourseTime"/> (always a _DAY or _NIT course).</summary>
    public bool Fog => _fog;
    public string CarId => catalog.Cars[_picker.Car].Id;
    public int Paint => _picker.Paint;
    public bool Manual => _manual;

    /// <summary>The game is held (no physics, no driving) while this shows; the intro lets go at GO.</summary>
    public bool Freezes => Current is not (Screen.None or Screen.Finish) && !(Current == Screen.Intro && _t >= IntroGo);

    /// <summary>Drawn over the running race with its HUD (the game builds the HUD first).</summary>
    public bool OverRace => Current is Screen.Intro or Screen.Pause;

    /// <summary>
    ///     Music for this screen as the original: course flow TOKYO ("LIVE IN TOKYO"), car flow and records WORRY, loading
    ///     silent, finish WIN (jingle; a lost battle LOSE), result JOY, intro/pause the race's Eurobeat (<see cref="RaceMusic"/>); options keep
    ///     <paramref name="playing"/>.
    /// </summary>
    public string? Music(string? playing) => Current switch
    {
        Screen.Course or Screen.Route or Screen.Time or Screen.Weather => "TOKYO.adx",
        Screen.Maker or Screen.Car or Screen.Gearbox or Screen.Records => "WORRY.adx",
        Screen.Loading => null, Screen.Finish => Battle is { Outcome: Race.BattleOutcome.Lose } ? "LOSE.adx" : "WIN.adx", Screen.Result => Legend || FreeBattle ? LegendResultMusic : "JOY.adx", Screen.Options or Screen.Controls => playing,
        _ => RaceMusic,
    };

    /// <summary>Legend result: the mode's own tracks from MG_BGM.AFS (R_WIN01 / R_LOSE), a draw keeps JOY.</summary>
    private string LegendResultMusic => Battle?.Outcome switch { Race.BattleOutcome.Win => "R_WIN01.adx", Race.BattleOutcome.Lose => "R_LOSE.adx", _ => "JOY.adx" };
    /// <summary>Rain over a dry course (a story chapter): the telop says WET.</summary>
    public bool Rain { get; set; }

    /// <summary>
    ///     Screenshots: on the maker screen maker <paramref name="row"/>, on the car screen the maker's car in row
    ///     <paramref name="row"/>; PreviewCar when that car has to be loaded.
    /// </summary>
    public Action ShowRow(int row)
    {
        if (Current == Screen.Maker) _picker.Open(_picker.Cars(Math.Clamp(row, 0, Catalog.Makers.Length - 1))[0], 0);
        else if (Current == Screen.Car && _picker.Cars(_picker.Maker) is var cars)
        {
            _picker.Pick(cars[Math.Clamp(row, 0, cars.Length - 1)]);
            if (!_picker.Locked(_picker.Car)) return Action.PreviewCar;
        }
        return Action.None;
    }

    /// <summary>Opens <paramref name="s"/> with the selection at the given course/direction/car/paint; backing out of it leaves to the main menu.</summary>
    public void Open(Screen s, string courseTime, bool reverse, string car, int paint, bool manual = false, bool fog = false)
    {
        var id = courseTime[..courseTime.LastIndexOf('_')];
        if (s is not (Screen.Pause or Screen.Intro)) FourPass = null; // over a four-pass stage the run goes on
        _slot = FourPass != null ? FourSlot : Math.Max(0, catalog.Courses.ToList().FindIndex(c => c.Id == id));
        (_night, _wet, _fog, _reverse) = (courseTime.EndsWith("_NIT"), courseTime.EndsWith("_RIN"), fog, reverse);
        _picker.Open(catalog.Cars.ToList().FindIndex(c => c.Id == car), paint);
        _manual = manual;
        _back.Clear();
        _leave = -1;
        Enter(s, s is not (Screen.Pause or Screen.Intro));
    }

    /// <summary>The race ended: finish banner for <paramref name="run"/>, then the result sheet.</summary>
    public void Finish(Run run)
    {
        _run = run;
        _back.Clear();
        Enter(Screen.Finish, false);
    }

    /// <summary>Online: the intro's clock set so GO falls on the shared race clock (<paramref name="raceTime"/> = seconds since GO, negative before).</summary>
    public void SyncIntro(float raceTime)
    {
        if (Current == Screen.Intro) _t = MathF.Max(0, GoAt + raceTime);
    }

    /// <summary>Skips the fade-in and entrance (screenshots).</summary>
    public void Settle(float at = 1) => (_t, _fadeIn) = (MathF.Max(_t, at), false);

    public void Close() => Current = Screen.None;

    /// <summary>The cursor on the pause/result button <paramref name="label"/> (back from the replay viewer or photo mode).</summary>
    public void Select(string label) => _row = Math.Max(0, Array.IndexOf(Current == Screen.Pause ? Pause : Buttons, label));

    private void Enter(Screen s, bool fadeIn)
    {
        (Current, _t, _fadeIn, _loadAsked) = (s, 0, fadeIn, false);
        if (s is Screen.Maker or Screen.Car) _picker.Current = s == Screen.Maker ? CarPicker.Step.Maker : CarPicker.Step.Car;
        _choice = s switch
        {
            Screen.Route => _reverse ? 1 : 0, Screen.Time => _night && Times().Length > 1 ? 1 : 0, Screen.Weather => Math.Max(0, Array.IndexOf(Weathers(), _fog ? "FOG" : _wet ? "WET" : "DRY")),
            Screen.Gearbox => _manual ? 1 : 0, _ => 0,
        };
        _row = 0;
        if (s == Screen.Options) Options.Open();
    }

    /// <summary>Screens of one module switch in place, between modules the screen fades through black.</summary>
    private static int Module(Screen s) => s switch
    {
        Screen.Course or Screen.Route or Screen.Time or Screen.Weather => 1, Screen.Car or Screen.Gearbox => 2, Screen.Options or Screen.Controls => 3, _ => 10 + (int)s,
    };

    private void Go(Screen s, bool remember = true)
    {
        if (remember) _back.Push(Current);
        if (Module(s) == Module(Current)) Enter(s, false);
        else (_leave, _next, _then) = (0, s, Action.None);
    }

    /// <summary>Fade out, then <paramref name="then"/> is returned and <paramref name="next"/> shows.</summary>
    private void Leave(Screen next, Action then) => (_leave, _next, _then) = (0, next, then);

    private void Back()
    {
        Sound?.Invoke("BEEP001");
        if (_back.TryPop(out var s) && s != Screen.None) Go(s, false);
        else Leave(Screen.None, Legend ? Action.Rivals : Action.Exit);
    }

    private string[] Times()
    {
        var t = SelectedCourse.Times;
        return [.. new[] { t.Contains("DAY") || t.Contains("RIN") ? "DAY" : null, t.Contains("NIT") ? "NIGHT" : null }.OfType<string>()];
    }

    private string[] Choices() => Current switch
    {
        Screen.Route => [Catalog.DirectionName(SelectedCourse, false), Catalog.DirectionName(SelectedCourse, true)],
        Screen.Time => Times(), Screen.Weather => Weathers(), Screen.Gearbox => ["AT", "MT"], _ => [],
    };

    /// <summary>Time of day, or straight on when the course has only one.</summary>
    private void StepTime()
    {
        var times = Times();
        if (times.Length > 1) Go(Screen.Time);
        else
        {
            _night = times[0] == "NIGHT";
            StepWeather();
        }
    }

    /// <summary>DRY (the day or night course), WET (its _RIN variant, day only) and FOG over the dry course, as present.</summary>
    private string[] Weathers()
    {
        if (OnFourSlot) return ["DRY", "WET"]; // four passes: the original's weather choice, all at night
        var t = SelectedCourse.Times;
        var dry = t.Contains(_night ? "NIT" : "DAY");
        return [.. new[] { dry ? "DRY" : null, !_night && t.Contains("RIN") ? "WET" : null, dry ? "FOG" : null }.OfType<string>()];
    }

    private void StepWeather()
    {
        var w = Weathers();
        if (w.Length > 1) Go(Screen.Weather);
        else
        {
            (_wet, _fog) = (w[0] == "WET", false);
            Go(Screen.Maker);
        }
    }

    private static int Wrap(int i, int n) => (i % n + n) % n;

    /// <summary>One frame of menu input (<see cref="MenuKeys"/>); returns what the game should do now.</summary>
    public Action Update((int X, int Y, bool Ok, bool Back) k, float dt)
    {
        if (Current == Screen.None) return Action.None;
        dt = MathF.Min(dt, 1 / 20f); // a blocking load must not skip the fades
        var t0 = _t;
        _t += dt;
        _clock += dt;
        if (_leave >= 0)
        {
            if ((_leave += dt) < Fade) return Action.None;
            _leave = -1;
            if (_next == Screen.None) Current = Screen.None;
            else Enter(_next, true);
            return _then;
        }
        bool Crossed(float at) => t0 < at && _t >= at;
        switch (Current)
        {
            case Screen.Course:
                if (k.X != 0 || k.Y != 0)
                {
                    _slot = Wrap(_slot + k.X + 3 * k.Y, Slots);
                    Sound?.Invoke("SYS005");
                }
                else if (k.Ok && OnFourSlot)
                {
                    Sound?.Invoke("SYS006");
                    StartFourPass();
                }
                else if (k.Ok && _slot >= catalog.Courses.Count) Sound?.Invoke("BEEP001");
                else if (k.Ok)
                {
                    Sound?.Invoke("SYS006");
                    FourPass = null;
                    Go(Screen.Route);
                }
                else if (k.Back) Back();
                break;
            case Screen.Route or Screen.Time or Screen.Weather or Screen.Gearbox:
                if (k.X != 0)
                {
                    var n = Wrap(_choice + k.X, Choices().Length);
                    if (n != _choice) Sound?.Invoke("SYS005");
                    _choice = n;
                }
                else if (k.Back) Back();
                else if (k.Ok)
                {
                    Sound?.Invoke("SYS006");
                    switch (Current)
                    {
                        case Screen.Route:
                            _reverse = _choice == 1;
                            StepTime();
                            break;
                        case Screen.Time:
                            _night = Choices()[_choice] == "NIGHT";
                            StepWeather();
                            break;
                        case Screen.Weather:
                            (_wet, _fog) = (Choices()[_choice] == "WET", Choices()[_choice] == "FOG");
                            if (InFourPass) FourPassWeather();
                            Go(Screen.Maker);
                            break;
                        default:
                            _manual = _choice == 1;
                            if (InFourPass)
                            {
                                FourPass!.Reset(); // a new car starts the four passes again
                                FourPassWeather();
                            }
                            Go(Screen.Loading);
                            break;
                    }
                }
                break;
            case Screen.Maker or Screen.Car:
                switch (_picker.Update(k, Sound))
                {
                    case CarPicker.Result.MakerChosen:
                        Go(Screen.Car);
                        return _picker.Locked(_picker.Car) ? Action.None : Action.PreviewCar;
                    case CarPicker.Result.Moved when !_picker.Locked(_picker.Car): return Action.PreviewCar;
                    case CarPicker.Result.Decide:
                        Go(Screen.Gearbox);
                        break;
                    case CarPicker.Result.Back:
                        Back();
                        break;
                }
                break;
            case Screen.Loading:
                if (_loadAsked)
                {
                    Leave(Screen.Intro, Action.None);
                    return Action.None;
                }
                if (_t < LoadAt) break;
                _loadAsked = true;
                return Action.Load;
            case Screen.Intro:
                if (k.Ok && _t < CountAt) _t = CountAt - dt; // START skips the telop
                if (Free)
                {
                    if (_t >= CountAt) Current = Screen.None;
                    break;
                }
                if (Crossed(CountAt) || Crossed(CountAt + 1) || Crossed(CountAt + 2)) Sound?.Invoke("CAR010");
                if (Crossed(GoAt)) Sound?.Invoke("CAR011");
                if (_t >= IntroEnd) Current = Screen.None;
                break;
            case Screen.Pause:
                if (_quit.Open)
                {
                    if (_quit.Update(k, Sound)) return Action.Quit;
                }
                else if (k.X != 0)
                {
                    var n = Math.Clamp(_row + k.X, 0, Pause.Length - 1);
                    while (PauseOff(n) && n + k.X >= 0 && n + k.X < Pause.Length) n += k.X;
                    if (PauseOff(n)) n = _row;
                    if (n != _row) Sound?.Invoke("SYS005");
                    _row = n;
                }
                else if (k.Back) return Resume();
                else if (k.Ok)
                {
                    Sound?.Invoke("SYS006");
                    switch (Pause[_row])
                    {
                        case "Continue": return Resume();
                        case "Reset":
                            Resume();
                            return Action.ResetCar;
                        case "Turn":
                            Resume();
                            return Action.TurnAround;
                        case "Change":
                            Leave(Screen.None, Action.Rivals);
                            break;
                        case "Retry" when InFourPass && FourPass!.Index > 0:
                            RestartFourPass(); // back to stage 1: another course
                            break;
                        case "Retry":
                            if (InFourPass) FourPass!.Reset();
                            Enter(Screen.Intro, false);
                            return Action.Restart;
                        case "Replay": return Action.Replay;
                        case "Photo": return Action.Photo;
                        case "Exit":
                            Leave(Screen.None, Legend || FreeBattle ? Action.Rivals : Action.Exit);
                            break;
                        default:
                            _quit.Show();
                            break;
                    }
                }
                break;
            case Screen.Finish:
                if (_t >= FinishHold || (k.Ok && _t > 1)) Leave(Screen.Result, Action.None);
                break;
            case Screen.Result:
                for (var i = 0; i < ResultRows; i++)
                    if (Crossed(RowFirst + RowStep * i)) Sound?.Invoke("NAME001");
                if (_t < ButtonsAt)
                {
                    if (k.Ok) _t = ButtonsAt; // decide skips the tally
                    break;
                }
                if (k.X != 0)
                {
                    var n = Math.Clamp(_row + k.X, 0, Buttons.Length - 1);
                    if (n != _row) Sound?.Invoke("SYS005");
                    _row = n;
                }
                else if (k.Ok)
                {
                    Sound?.Invoke("SYS006");
                    switch (Buttons[_row])
                    {
                        case "NEXT":
                            FourPass!.Next();
                            Leave(Screen.Loading, Action.None);
                            break;
                        case "RETRY" when InFourPass:
                            RestartFourPass();
                            break;
                        case "RETRY":
                            Leave(Screen.Intro, Action.Restart);
                            break;
                        case "REPLAY": return Action.Replay;
                        case "CHANGE SETTINGS":
                            Leave(Screen.None, Action.Rivals);
                            break;
                        case "COURSE SELECT":
                            if (Legend) Leave(Screen.None, Action.Rivals);
                            else Go(Screen.Course, false);
                            break;
                        case "CAR SELECT":
                            if (!Legend) _back.Push(Screen.Course);
                            Go(Screen.Maker, false);
                            break;
                        default:
                            Leave(Screen.None, Action.Exit);
                            break;
                    }
                }
                break;
            case Screen.Records:
                if (k.Back || k.Ok) Back();
                break;
            case Screen.Options:
                // CONTROLLER opens the controls screen (bindings, wheel, force feedback) when the game gave it the live input
                if (Controls != null && Options.Current == null && k is { Ok: true, Y: 0 } && Options.Pages[Options.Section].Title == ControllerPage)
                {
                    Sound?.Invoke("SYS006");
                    Controls.Open();
                    Go(Screen.Controls);
                    break;
                }
                switch (Options.Update(k, Sound))
                {
                    case Options.Result.Changed: return Action.SettingsChanged;
                    case Options.Result.Leave:
                        Back();
                        break;
                }
                break;
            case Screen.Controls:
                if (Controls!.Update(k, dt, Sound))
                {
                    Back();
                    return Action.SettingsChanged;
                }
                break;
        }
        return Action.None;
    }

    private Action Resume()
    {
        Current = Screen.None;
        return Action.Resume;
    }

    // ---------------------------------------------------------------- drawing

    private readonly Canvas _c = new();
    private float Theta => _clock * 300 % 360; // 5° per frame
    private static readonly uint HintRed = Overlay.Rgba(0.92f, 0.08f, 0.06f), Grey = Overlay.Rgba(0.72f, 0.73f, 0.75f);

    /// <summary>Draws the current screen into <paramref name="o"/> (not cleared: the game may have put the HUD under it).</summary>
    public void Build(Overlay o, int width, int height)
    {
        if (Current == Screen.None) return;
        var c = _c;
        c.Begin(o, width, height);
        switch (Current)
        {
            case Screen.Course or Screen.Route or Screen.Time or Screen.Weather:
                c.Backdrop(_clock);
                CourseScreen(c);
                c.Marquee(Current switch { Screen.Route => "SELECT A ROUTE", Screen.Time => "SELECT TIME OF DAY", Screen.Weather => "SELECT WEATHER", _ => "SELECT A COURSE" }, false, _clock);
                break;
            case Screen.Maker:
                c.Backdrop(_clock);
                _picker.DrawMaker(c, Theta, Legend ? "Rivals" : "Return");
                c.Marquee("SELECT A MAKER", false, _clock);
                break;
            case Screen.Car or Screen.Gearbox:
                _picker.DrawCar(c, Theta, In(0.25f), Current == Screen.Car);
                if (Current == Screen.Gearbox)
                {
                    c.Fill(Overlay.Rgba(0, 0, 0, 0.45f));
                    ChoiceBox(c, 158, 300);
                }
                c.Marquee(Current == Screen.Gearbox ? "SELECT TRANSMISSION" : "SELECT A CAR", false, _clock);
                break;
            case Screen.Loading:
                LoadingArt.Draw(c, _t);
                break;
            case Screen.Intro:
                Telop(c);
                Countdown(c);
                break;
            case Screen.Pause:
                PauseScreen(c);
                break;
            case Screen.Finish:
                FinishBanner(c);
                break;
            case Screen.Result:
                ResultScreen(c);
                break;
            case Screen.Records:
                c.Backdrop(_clock);
                RecordsScreen(c);
                c.Marquee("RECORDS", true, _clock);
                break;
            case Screen.Options:
                c.Backdrop(_clock);
                Options.Draw(c, Theta);
                c.Marquee(Options.Title, true, _clock);
                break;
            case Screen.Controls:
                c.Backdrop(_clock);
                Controls!.Draw(c, Theta);
                Hint(c, Controls!.Footer, false);
                c.Marquee("CONTROLLER", true, _clock);
                break;
        }
        c.Fade(_leave >= 0 ? Math.Clamp(_leave / Fade, 0, 1) : _fadeIn ? 1 - Math.Clamp(_t / Fade, 0, 1) : 0);
    }

    /// <summary>Red hint line along the bottom, as the original's small red help strips.</summary>
    /// <remarks>DECIDE, BACK and the arrows become the keys of the device used last (<see cref="Hints.Menu"/>) unless <paramref name="translate"/> is off.</remarks>
    internal static void Hint(Canvas c, string text, bool translate = true)
    {
        if (translate) text = Hints.Menu(text);
        c.O.Rect(new Vector2(0, MathF.Round(c.P(0, 428).Y)), new Vector2(c.Width, MathF.Round(c.P(0, 448).Y)), Overlay.Rgba(0, 0, 0, 0.65f));
        var size = MathF.Min(11.5f, 496 * c.Kx / c.O.Font!.Measure(text, 1) / c.Ky); // pad/wheel names are longer: shrink to the width
        c.Text(text, 256, 442, size, Overlay.Rgba(1, 0.2f, 0.15f), 0.5f, 0.15f, 0, 0.3f);
    }

    /// <summary>Entrance 0 → 1 over <paramref name="seconds"/>.</summary>
    private float In(float seconds, float delay = 0) => Style.Ease((_t - delay) / seconds);

    private void CourseScreen(Canvas c)
    {
        var o = c.O;
        var locked = _slot >= catalog.Courses.Count && FourPassStages == null;
        var course = SelectedCourse;
        // carbon "monitor" with the course map: white line with green start (and red goal) ticks, as h_selm00-05
        c.Carbon(16, 72, 250, 306);
        var four = OnFourSlot;
        if (four) FourPassMaps(c);
        else if (!locked) MapLine(c, course, Current == Screen.Route ? _choice == 1 : _reverse, 34, 90, 232, 288);
        else c.Text("LOCKED", 133, 196, 22, Grey, 0.5f, 0.2f);
        // 3 × 4 grid of dark-steel buttons with the course names
        for (var i = 0; i < Slots; i++)
        {
            float x = 266 + i % 3 * 76, y = 74 + i / 3 * 38;
            var name = i < catalog.Courses.Count ? catalog.Courses[i].Name : "FOUR PASSES";
            Vector2 min = Vector2.Round(c.P(x, y)), max = Vector2.Round(c.P(x + 70, y + 30));
            o.Rect(min, max, Overlay.Rgba(0.55f, 0.56f, 0.58f));
            o.RectGradient(min + new Vector2(1.5f, 1.5f) * c.S, max - new Vector2(1.5f, 1.5f) * c.S, Overlay.Rgba(0.2f, 0.21f, 0.22f), Overlay.Rgba(0.08f, 0.08f, 0.09f));
            o.Line(new Vector2(min.X + 2 * c.S, min.Y + 2 * c.S), new Vector2(max.X - 2 * c.S, min.Y + 2 * c.S), 1, Overlay.Rgba(1, 1, 1, 0.35f));
            c.Fit(name, x + 35, y + 20, 60, 0.5f, i < catalog.Courses.Count || FourPassStages != null ? Canvas.White : Overlay.Rgba(1, 1, 1, 0.35f), 0.12f, 0.05f, 13);
        }
        float sx = 266 + _slot % 3 * 76, sy = 74 + _slot / 3 * 38;
        c.Glow(sx - 3, sy - 3, sx + 73, sy + 33, Current == Screen.Course ? Canvas.Pulse(Theta) : 0.5f);
        // info panel: length, elevation, best of the shown direction
        c.Carbon(262, 232, 496, 306, 1, false);
        if (four) FourPassStats(c);
        else if (!locked)
        {
            var best = settings.Best.GetValueOrDefault(Settings.BestKey(course.Id, Current == Screen.Route ? _choice == 1 : _reverse));
            Stat(c, "LENGTH", FormattableString.Invariant($"{course.LengthM / 1000:0.0} km"), 274);
            Stat(c, "ELEVATION", $"{course.ClimbM:0} m", 352);
            Stat(c, "BEST", Style.Time(best?[^1]), 418);
        }
        // the choice steps put their box where the name is (text lies above every shape, so nothing may sit under it)
        if (Current != Screen.Course)
        {
            ChoiceBox(c, 314, 426);
            Hint(c, "LEFT/RIGHT: Select    DECIDE: OK    BACK: Return");
            return;
        }
        // course name in big blue lettering with a white outline, yellow arrows either side
        var label = locked || four ? "FOUR PASSES" : course.Name;
        c.Lettering(label, 256, 372, MathF.Min(54, 380 * c.Kx / o.Font!.Measure(label, c.Ky)), Overlay.Rgba(0.35f, 0.45f, 1), Canvas.BrushBlue, 0.5f, 0.12f, true);
        c.Arrow(40, 340, 40, 370, 24, 355);
        c.Arrow(472, 340, 472, 370, 488, 355);
        var times = locked ? "" : string.Join(" / ", course.Times.Select(Catalog.TimeName));
        c.Text(locked ? "Not available in this remake" : four ? FourPassRoute() : $"{times}   {Catalog.DirectionName(course, false)} / {Catalog.DirectionName(course, true)}", 256, 404, 12, Canvas.White, 0.5f, 0.15f, 0.08f);
        Hint(c, "ARROWS: Select course    DECIDE: OK    BACK: Main menu");
    }

    private static void Stat(Canvas c, string label, string value, float x)
    {
        c.Text(label, x, 256, 10, Grey, 0, 0.1f);
        c.Text(value, x, 286, 19, Canvas.White, 0, 0.15f, 0, 0.3f);
    }

    /// <summary>Driving line fitted north-up into the canvas box, the start tick green, the goal tick red (circuits: start only).</summary>
    internal static void MapLine(Canvas c, Catalog.Course course, bool reverse, float x0, float y0, float x1, float y1)
    {
        var line = course.Line;
        Vector2 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (var p in line) (lo, hi) = (Vector2.Min(lo, p), Vector2.Max(hi, p));
        Vector2 min = c.P(x0, y0), max = c.P(x1, y1);
        var k = MathF.Min((max.X - min.X) / (hi.X - lo.X), (max.Y - min.Y) / (hi.Y - lo.Y));
        var off = (min + max) / 2 - (lo + hi) / 2 * k;
        Vector2 S(Vector2 p) => off + p * k;
        for (var pass = 0; pass < 2; pass++)
        for (var i = 1; i < line.Length; i++)
            c.O.Line(S(line[i - 1]), S(line[i]), (pass == 0 ? 6 : 3) * c.S, pass == 0 ? Overlay.Rgba(0, 0, 0, 0.9f) : Overlay.Rgba(0.95f, 0.96f, 0.97f));
        void Tick(int i, int j, uint color)
        {
            var d = Vector2.Normalize(S(line[j]) - S(line[i]));
            var n = new Vector2(-d.Y, d.X) * 7 * c.S;
            c.O.Line(S(line[i]) - n, S(line[i]) + n, 3 * c.S, color);
        }
        var (green, red) = (Overlay.Rgba(0.15f, 0.9f, 0.25f), Overlay.Rgba(0.95f, 0.15f, 0.1f));
        var (start, goal) = reverse ? (line.Length - 1, 0) : (0, line.Length - 1);
        Tick(start, start == 0 ? 1 : start - 1, green);
        if (!course.Circuit) Tick(goal, goal == 0 ? 1 : goal - 1, red);
    }

    /// <summary>The original's choice pair: two big gradient words in a carbon box (canvas y0..y1), left red, right blue, the chosen one in the yellow frame.</summary>
    private void ChoiceBox(Canvas c, float y0, float y1)
    {
        var words = Choices();
        var a = In(0.15f);
        var mid = (y0 + y1) / 2;
        var sub = Current == Screen.Gearbox ? 14 : 0; // room for the subtitles
        c.Carbon(56, y0, 456, y1, a);
        for (var i = 0; i < words.Length; i++)
        {
            // two words at 156/356 as the original, three (weather with FOG) at 133 apart in narrower frames
            var step = words.Length > 2 ? 133 : 200;
            var x = 256 + (i - (words.Length - 1) / 2f) * step;
            var half = step == 200 ? 92 : 62;
            var sel = i == _choice;
            var size = MathF.Min(46, (2 * half - 14) * c.Kx / c.O.Font!.Measure(words[i], c.Ky)) * (0.7f + 0.3f * a);
            var (top, bottom) = words[i] == "FOG" ? (Overlay.Rgba(0.95f, 0.96f, 0.98f), Overlay.Rgba(0.42f, 0.45f, 0.5f))
                : i == 0 ? (Overlay.Rgba(1, 0.55f, 0.3f), Canvas.WordRed) : (Overlay.Rgba(0.45f, 0.6f, 1), Canvas.WordBlue);
            c.Lettering(words[i], x, mid + 16 - sub, size, top, bottom, 0.5f, 0.15f, false, true, a * (sel ? 1 : 0.4f));
            if (sel) c.Glow(x - half, mid - 30 - sub, x + half, mid + 32 - sub, Canvas.Pulse(Theta), a);
        }
        if (sub > 0)
            for (var i = 0; i < 2; i++) c.Text(i == 0 ? "Automatic" : "Manual, shift yourself", 156 + i * 200, mid + 44, 12, Style.Fade(Canvas.White, a), 0.5f, 0.15f);
    }

    /// <summary>Course-name telop: a black band wipes in from the right with the name in blue lettering, the conditions below; wipes out before the count.</summary>
    private void Telop(Canvas c)
    {
        var wipe = Style.Ease(_t / 0.35f) * (1 - Style.Ease((_t - (CountAt - 0.4f)) / 0.35f));
        if (wipe <= 0) return;
        var course = SelectedCourse;
        var x0 = c.Right - (c.Right - 110) * wipe;
        c.O.Rect(Vector2.Round(c.P(x0, 150)), Vector2.Round(c.P(c.Right, 214)), Overlay.Rgba(0, 0, 0, 0.88f));
        c.O.Rect(Vector2.Round(c.P(x0, 214)), Vector2.Round(c.P(c.Right, 216)), Overlay.Rgba(0.8f, 0.07f, 0.06f));
        var shift = (1 - wipe) * 400;
        c.Lettering(course.Name, 490 + shift, 200, MathF.Min(46, 330 * c.Kx / c.O.Font!.Measure(course.Name, c.Ky)), Overlay.Rgba(0.35f, 0.45f, 1), Canvas.BrushBlue, 1, 0.12f, true);
        var tags = $"{(_night ? "NIGHT" : "DAY")}    [{Catalog.DirectionName(course, Reverse)}]    [{(_wet || Rain ? "WET" : "DRY")}]";
        c.Text(tags, 490 + shift, 234, 13, Canvas.White, 1, 0.12f, 0.08f);
        FourPassTelop(c, shift);
        if (Versus != null) c.Text($"VS  {Versus}", 490 + shift, 258, 17, Canvas.WordRed, 1, 0.15f, 0.08f, 0.3f);
        else if (Free) c.Text("FREE PLAY", 490 + shift, 258, 17, Canvas.WordRed, 1, 0.15f, 0.08f, 0.3f);
    }

    /// <summary>3, 2, 1 in big red, GO! in the racing orange; each pops in from 1.5× and fades at its end.</summary>
    private void Countdown(Canvas c)
    {
        if (_t < CountAt) return;
        var n = (int)(_t - CountAt);
        var local = _t - CountAt - n;
        var text = n < 3 ? (3 - n).ToString() : "GO!";
        var size = (n < 3 ? 120 : 96) * (1 + 0.5f * (1 - Style.Ease(local / 0.15f)));
        var a = n < 3 ? 1 - Style.Ease((local - 0.8f) / 0.2f) : 1 - Style.Ease((_t - GoAt - 0.5f) / 0.3f);
        if (n < 3) c.Lettering(text, 256, 270, size, Overlay.Rgba(1, 0.35f, 0.3f), Overlay.Rgba(0.75f, 0, 0), 0.5f, 0.15f, true, true, a);
        else c.Lettering(text, 256, 260, size, Overlay.Rgba(1, 0.82f, 0.25f), Overlay.Rgba(1, 0.38f, 0), 0.5f, 0.18f, false, true, a);
    }

    private void PauseScreen(Canvas c)
    {
        c.Fill(Overlay.Rgba(0, 0, 0, 0.5f));
        c.O.FadeText(0.5f); // the HUD's text lies above every shape: dim it too
        c.Lettering("PAUSE", 256, 128, 44, Overlay.Rgba(1, 0.25f, 0.2f), Overlay.Rgba(0.75f, 0, 0), 0.5f, 0.2f, true);
        // caption bar, then the strip with the "Pause" tab and the chrome buttons (centred)
        var buttons = Pause;
        var step = MathF.Min(86, 470f / buttons.Length);
        var x0 = 256 - (buttons.Length * step - 10) / 2f;
        c.Carbon(x0 - 16, 328, 512 - x0 + 16, 352, 1, false);
        c.Text(FreePause && FreeCaptions.TryGetValue(buttons[_row], out var fc) ? fc : PauseCaptions[buttons[_row]], 256, 345, 12, Canvas.White, 0.5f, 0.12f);
        var grey = Canvas.Shade(0.08f, 0.08f, 0.08f, 1, 0.4f);
        c.Carbon(x0 - 16, 358, 512 - x0 + 16, 412, 1, false);
        c.Text("Pause", x0 - 6, 372, 11, Canvas.White, 0, 0.2f);
        for (var i = 0; i < buttons.Length; i++)
        {
            var x = x0 + i * step;
            var off = PauseOff(i);
            c.Plate(x, 380, step - 10, 22, off ? 0.5f : 1);
            c.Text(buttons[i], x + (step - 10) / 2, 396, 12, off ? grey : Canvas.Shade(0.08f, 0.08f, 0.08f, 1), 0.5f, 0.18f);
        }
        var sx = x0 + _row * step;
        c.Glow(sx - 4, 376, sx + step - 6, 406, Canvas.Pulse(Theta));
        _quit.Draw(c, Theta);
    }

    private void FinishBanner(Canvas c)
    {
        if (Battle is { } battle)
        {
            BattleScreens.Banner(c, battle, _t);
            return;
        }
        if (InFourPass)
        {
            FourPassBanner(c);
            return;
        }
        var run = _run!;
        var pop = Style.Ease(_t / 0.25f);
        var text = run.NewRecord ? "NEW RECORD!!" : "FINISH!!";
        c.Lettering(text, 256, 196, 58 * (1.8f - 0.8f * pop), Overlay.Rgba(1, 0.85f, 0.3f), Overlay.Rgba(1, 0.38f, 0), 0.5f, 0.2f, false, true, pop);
        if (run.NewRecord) c.Text("Personal best updated", 256, 222, 15, Style.Fade(Overlay.Rgba(1, 0.15f, 0.1f), pop), 0.5f, 0.15f, 0.1f, 0.3f);
        c.Text(Style.Time(run.Time), 256, 262, 30, Style.Fade(Canvas.White, pop), 0.5f, 0.15f, 0.06f, 0.5f);
    }

    private void ResultScreen(Canvas c)
    {
        if (Battle is { } battle)
        {
            BattleScreens.Sheet(c, battle, i => Style.Ease((_t - (RowFirst + RowStep * i)) / 0.15f));
            ResultChoice(c);
            return;
        }
        if (InFourPass)
        {
            FourPassSheet(c);
            return;
        }
        var run = _run!;
        c.Fill(Overlay.Rgba(0, 0, 0, 0.25f));
        float Row(int i) => Style.Ease((_t - (RowFirst + RowStep * i)) / 0.15f);
        void Line(int i, float x0, float x1, float y, string label, string value, uint color, string? extra = null, uint extraColor = 0)
        {
            var a = Row(i);
            c.Rule(x0, x1, y + 6, 1);
            if (a <= 0) return;
            c.Text(label, x0 + 6, y - 2, 9.5f, Style.Fade(Canvas.White, a), 0, 0.2f, 0, 0.2f);
            c.Text(value, x1 - (extra != null ? 52 : 6), y + 2, 17, Style.Fade(color, a), 1, 0.15f, 0, 0.3f);
            if (extra != null) c.Text(extra, x1 - 6, y + 2, 11, Style.Fade(extraColor, a), 1, 0.15f);
        }
        c.Sheet(30, 78, 252, 300, "Result");
        Line(0, 30, 252, 104, "TOTAL TIME", Style.Time(run.Time), Canvas.White);
        for (var i = 0; i < LapTimer.Sectors; i++)
        {
            var d = run.Deltas[i];
            Line(1 + i, 30, 252, 134 + i * 30, $"SECTION TIME {i + 1}", Style.Time(run.Splits[i] - (i > 0 ? run.Splits[i - 1] : 0)), Canvas.White,
                d is { } dd ? Style.Delta(dd) : null, d is <= 0 ? Style.Green : Style.Red);
        }
        var course = SelectedCourse;
        c.Sheet(268, 78, 486, 194, "Record");
        c.Text($"{course.Name}  {Catalog.DirectionName(course, _reverse)}", 274, 98, 11, Canvas.White, 0, 0.15f, 0, 0.2f);
        Line(5, 268, 486, 130, "BEST TIME", Style.Time(run.NewRecord ? run.Time : run.Previous), Canvas.White);
        Line(6, 268, 486, 162, "DIFFERENCE", run.Previous is { } p ? Style.Delta(run.Time - p) : "-", run.Previous is { } q && run.Time <= q ? Style.Green : Canvas.White);
        c.Sheet(268, 220, 486, 262, "Point");
        Line(7, 268, 486, 248, "DRIFT POINTS", $"{(int)run.Drift} pts", Canvas.White);
        if (run.NewRecord && Row(ResultRows - 1) > 0)
        {
            var s = Row(ResultRows - 1);
            c.Lettering("NEW RECORD!!", 377, 300, 26 * (1.6f - 0.6f * s), Overlay.Rgba(1, 0.85f, 0.3f), Overlay.Rgba(1, 0.38f, 0), 0.5f, 0.2f, false, true, s);
        }
        ResultChoice(c);
    }

    /// <summary>The result screen's action buttons (ACTCHOICE).</summary>
    private void ResultChoice(Canvas c)
    {
        var b = Style.Ease((_t - ButtonsAt) / 0.2f);
        if (b <= 0) return;
        var buttons = Buttons;
        float step = 470f / buttons.Length, w = step - 6;
        for (var i = 0; i < buttons.Length; i++)
            c.Button(24 + i * step, 392, w, 30, Legend && buttons[i] == "COURSE SELECT" ? "RIVAL SELECT" : buttons[i],
                i == 0 ? Canvas.ButtonKind.Positive : buttons[i] == "EXIT" ? Canvas.ButtonKind.Negative : Canvas.ButtonKind.Neutral, b);
        var x = 24 + _row * step;
        c.Glow(x - 4, 388, x + w + 4, 426, Canvas.Pulse(Theta), b);
    }

    private void RecordsScreen(Canvas c)
    {
        c.Carbon(20, 72, 492, 424);
        // column heads as the original's RANK/NAME/TIME strip: course, then per direction a white tag and the time
        c.Text("COURSE", 36, 96, 10, Grey, 0, 0.1f);
        for (var r = 0; r < 2; r++) c.Text("ROUTE / TIME", 190 + r * 150, 96, 10, Grey, 0, 0.1f);
        for (var i = 0; i < catalog.Courses.Count; i++)
        {
            var course = catalog.Courses[i];
            var y = 106 + i * 26; // 12 rows with FOUR PASSES
            c.Rule(30, 482, y + 26);
            c.Fit(course.Name, 36, y + 21, 140, 0, Canvas.White, 0.15f, 0.06f, 17);
            for (var r = 0; r < 2; r++)
            {
                var best = settings.Best.GetValueOrDefault(Settings.BestKey(course.Id, r == 1));
                float x = 190 + r * 150, ty = y + 2;
                var dir = Catalog.DirectionName(course, r == 1);
                c.O.Rect(Vector2.Round(c.P(x, ty + 4)), Vector2.Round(c.P(x + 64, ty + 14)), Overlay.Rgba(0.95f, 0.95f, 0.96f));
                c.Fit(dir, x + 32, ty + 12.5f, 58, 0.5f, Canvas.Shade(0.1f, 0.1f, 0.12f, 1), 0, 0, 9);
                c.Text(Style.Time(best?[^1]), x + 70, y + 19, 13, best == null ? Overlay.Rgba(1, 1, 1, 0.35f) : Canvas.White, 0, 0.15f);
            }
        }
        if (FourPassStages != null) FourPassRecords(c, settings, 106 + catalog.Courses.Count * 26, 36, 140, r => 190 + r * 150, 13, true);
        Hint(c, "Best time per course and route    BACK: Main menu");
    }
}
