using System.Numerics;
using Touge.Formats;
using Touge.Race;
using Touge.Ui;

namespace Touge;

/// <summary>
///     LEGEND OF THE STREETS (<see cref="LegendScreen"/>, <see cref="Legend"/>): main menu → course grid → rival ladder → VS card
///     (the rival's car loaded and shown turning, its theme playing) → the usual car select, loading, telop with VS and
///     3-2-1-GO → battle (<see cref="TougeGame.Battle"/> set per rival) → finish banner and battle sheet, whose RIVAL SELECT,
///     the pause's Exit and backing out of the car select return to the ladder (<see cref="Menu.Legend"/>). Each decided
///     battle is counted into the game's one <see cref="Progress"/> (progress.json next to the settings, shared with Story and
///     SAVE &amp; LOAD; or --legend-progress).
/// </summary>
public sealed partial class TougeGame
{
    /// <summary>--legend-progress: progress file (Legend and Story) to read and write; test runs otherwise keep it in memory only (or in --data-dir).</summary>
    public string? LegendProgressPath { get; init; }
    /// <summary>--flow with --legend: the Legend of the Streets flow script (<see cref="LegendFlowScript"/>).</summary>
    public bool LegendFlow { get; init; }

    private LegendScreen? _legend;
    /// <summary>The career progress of Legend and Story (one object: SAVE &amp; LOAD swaps it via <see cref="LoadProgress"/>).</summary>
    private Progress _progress = new();
    /// <summary>The progress is read from and written to a file: with the menus, --data-dir or --legend-progress.</summary>
    private bool ProgressSaved => LegendProgressPath != null || SavesRuns;
    /// <summary>The rival of the battle set up from the ladder (null: no Legend battle).</summary>
    private Legend.Entry? _legendRival;

    /// <summary>(Re)reads the progress (start, a loaded save slot) and hands it to every mode that shows it.</summary>
    private void LoadProgress()
    {
        _progress = ProgressSaved ? Progress.Load(LegendProgressPath) : new();
        if (_legend != null) _legend.Progress = _progress;
        if (_story != null) _story.Progress = _progress;
    }

    /// <summary>Writes the progress and autosaves it into the active SAVE &amp; LOAD slot.</summary>
    private void SaveProgress()
    {
        if (!ProgressSaved) return;
        _progress.Save(LegendProgressPath);
        Autosave();
    }

    private void LoadLegend()
    {
        LoadProgress();
        _legend = new LegendScreen(_catalog!, _progress) { Sound = n => _menuAudio?.Play(n) };
        _menu!.CarLocked = id => _menu.Legend && Legend.CarLocked(id, _progress); // the reward car only in Legend; time attack keeps every car
    }

    /// <summary>--menu legend[-rivals|-card][:COURSE/rival]: opens that Legend step (screenshots).</summary>
    private void OpenLegendAtStart(string arg)
    {
        var (step, key) = arg.Split(':') is [var s, var k] ? (s, k) : (arg, null);
        var at = step switch { "legend-rivals" => LegendScreen.Step.Rivals, "legend-card" => LegendScreen.Step.Card, _ => LegendScreen.Step.Course };
        if (key != null && Legend.Find(key) == null) throw new ArgumentException($"--menu {arg}: Rivale unbekannt, z. B. AKINA/takumi");
        _legend!.Open(at, key);
        if (at == LegendScreen.Step.Card) SetUpLegendBattle(_legend.Selected);
        if (shotPath != null) _legend.Settle();
    }

    private void UpdateLegend((int X, int Y, bool Ok, bool Back) keys, float dt)
    {
        var l = _legend!;
        switch (l.Update(keys, dt))
        {
            case LegendScreen.Action.PreviewRival:
                SetUpLegendBattle(l.Selected);
                break;
            case LegendScreen.Action.Challenge:
            {
                var e = _legendRival!;
                var course = _catalog!.Courses.First(c => c.Id == e.CourseId);
                var (courseTime, _) = Legend.Conditions(e, course.Times, _progress);
                _menu!.Legend = true;
                _menu.Open(Menu.Screen.Maker, courseTime, e.Reverse, _settings.Car, _settings.Paint, _settings.Manual);
                break;
            }
            case LegendScreen.Action.Exit:
                EndLegendBattle();
                if (_front == null) Window.ShouldClose = true; // a --menu start has no main menu to go back to
                else _front.Open(FrontEnd.Step.Modes);
                break;
        }
    }

    /// <summary>The ladder again after a battle (or backing out of the car select), the cursor on the next rival to beat.</summary>
    private void ReturnToLadder()
    {
        EndRecording(); // the battle's replay is saved now, not at the next battle
        _inRace = false;
        _legend!.Open(LegendScreen.Step.Rivals, _legendRival?.Key);
    }

    /// <summary>
    ///     Battle against <paramref name="e"/> from now on: his course at his time/weather/direction (loaded unless already
    ///     there), his car model (with the character's livery and paint) and sound, a fresh session on the grid — the VS card
    ///     shows that car on his own course; the car select then only swaps the player's car.
    /// </summary>
    private void SetUpLegendBattle(Legend.Entry e)
    {
        var course = _catalog!.Courses.First(c => c.Id == e.CourseId);
        var (courseTime, _) = Legend.Conditions(e, course.Times, _progress);
        if (_legendRival == e && _race != null && _courseTime == courseTime) return;
        _legendRival = e;
        Battle = Legend.Setup(e);
        using var iso = new Iso9660(isoPath);
        if (courseTime != _courseTime || e.Reverse != _drive.Reverse || _fog)
        {
            LoadCourse(iso, courseTime, e.Reverse, _carName, _paint); // sets up the battle too
            return;
        }
        Device.WaitIdle(); // the last frame may still draw the previous rival
        DisposeRival();
        LoadBattle(iso);
        if (_audioDevice != null) StartRivalAudio(iso);
        SyncPose();
    }

    /// <summary>Back to time attack: no rival, no session, menus without the Legend routing.</summary>
    private void EndLegendBattle()
    {
        if (_menu != null) _menu.Legend = false;
        if (_legendRival == null) return;
        _legendRival = null;
        Battle = null;
        _race = null;
        Device.WaitIdle();
        DisposeRival();
        _rivalAudio?.Dispose();
        _rivalAudio = null;
        _battleHud = null;
        if (_menu != null) (_menu.Versus, _menu.Battle) = (null, null);
        _drive.ResetTo(0);
        SyncPose();
    }

    /// <summary>A decided battle (once per run): counted for the Legend rival (a draw counts as neither), saved.</summary>
    private void LegendRecord(BattleReport report)
    {
        if (_legendRival is not { } e) return;
        _progress.Add(e.Key, report.Outcome, report.Gap);
        Console.WriteLine($"\n[Legend] {e.Key}: {report.Outcome} ({report.Reason}, {report.Gap:+0.00;-0.00} s), Bilanz {_progress.Get(e.Key).Wins}:{_progress.Get(e.Key).Losses}");
        SaveProgress();
    }

    /// <summary>
    ///     VS card: the camera sweeps slowly along the rival's car on the grid, low and close like a showroom, on its outer
    ///     side (the player's car stands to its left; angle 0 = front, +90° = left, as <see cref="OrbitCar"/>).
    /// </summary>
    private void LegendCamera()
    {
        var target = Vector3.Transform(new Vector3(0, 0.4f, 0), _rivalBody);
        var angle = -MathF.PI / 2 + 1.1f * MathF.Sin(0.6f + _menuTime * 0.22f);
        var dir = Vector3.TransformNormal(new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)), _rivalBody);
        _pos = target + Vector3.Normalize(dir with { Y = 0 }) * 6.2f + Vector3.UnitY * 1.1f;
        (_camLook, _fov) = (target - Vector3.UnitY * 0.35f, MathF.PI / 4);
    }

    /// <summary>
    ///     --flow --legend: main menu → LEGEND → Akina → the ladder (Kenji) → VS card → car select (Trueno, AT) → battle (the
    ///     autopilot at 16×) → result → RIVAL SELECT → the next rival → its battle → result → ladder → courses → main menu.
    /// </summary>
    private static readonly (string At, float Wait, string? Shot, int X, int Y, bool Ok, bool Back)[] LegendFlowScript =
    [
        ("Boot", 1.2f, null, 0, 0, true, false), ("Logo", 1, null, 0, 0, true, false), ("Title", 1.5f, null, 0, 0, true, false),
        ("Modes", 1, "legend_modes", 0, 0, true, false),
        ("LegendCourse", 1.2f, "legend_courses", 0, 1, false, false), ("LegendCourse", 0.6f, "legend_course_akina", 0, 0, true, false),
        .. LegendFlowBattle("1"),
        ("LegendRivals", 1.5f, "legend_ladder_after_1", 0, 0, false, false),
        .. LegendFlowBattle("2"),
        ("LegendRivals", 1.5f, "legend_ladder_after_2", 0, 0, false, true),
        ("LegendCourse", 1, "legend_courses_after", 0, 0, false, true), ("Modes", 1.2f, "legend_back_to_modes", 0, 0, false, false),
    ];

    /// <summary>One Legend battle in the flow script: ladder → card → maker/model/car/gearbox → load → race → result → RIVAL SELECT.</summary>
    private static (string, float, string?, int, int, bool, bool)[] LegendFlowBattle(string n) =>
    [
        ("LegendRivals", 1, $"legend_ladder_{n}", 0, 0, true, false), ("LegendCard", 1.5f, $"legend_card_{n}", 0, 0, true, false),
        ("Maker", 1, $"legend_maker_{n}", 0, 0, true, false), ("Maker", 0.5f, null, 0, 0, true, false), ("Car", 1.2f, $"legend_car_{n}", 0, 0, true, false),
        ("Gearbox", 0.6f, null, 0, 0, true, false), ("Loading", 0.5f, null, 0, 0, false, false),
        ("Intro", 1.2f, $"legend_telop_{n}", 0, 0, false, false), ("Race", 2.5f, $"legend_race_{n}", 0, 0, false, false),
        ("Finish", 0.8f, $"legend_finish_{n}", 0, 0, false, false), ("Result", 3.6f, $"legend_result_{n}", 1, 0, false, false), ("Result", 0.3f, null, 1, 0, false, false),
        ("Result", 0.4f, null, 0, 0, true, false),
    ];
}
