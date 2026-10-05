using Touge.Formats;
using Touge.Ui;

namespace Touge;

/// <summary>
///     FOUR PASSES (<see cref="FourPasses"/>, flow in <see cref="Menu"/>): the stage table from the ELF, the record of the
///     whole run (written when the fourth stage ends, per weather and assists), rain over the night courses for WET, the
///     stage/total panel in the HUD, the replay label, --menu fourpasses… and the --flow --fourpasses script.
/// </summary>
public sealed partial class TougeGame
{
    /// <summary>--flow with --fourpasses: the scripted pass drives all four stages (<see cref="FourPassFlowScript"/>).</summary>
    public bool FourPassFlow { get; init; }
    /// <summary>--fourpasses-wet: the flow picks WET (rain over the night courses).</summary>
    public bool FourPassWet { get; init; }

    /// <summary>The run under way (null: not in FOUR PASSES).</summary>
    private FourPasses? FourPass => _menu?.FourPass;

    /// <summary>With the menus: the stage table (unreadable: the slot stays locked).</summary>
    private void LoadFourPasses(Iso9660 iso)
    {
        try
        {
            _menu!.FourPassStages = FourPasses.Read(iso.ReadFile(StoryScript.ElfPath));
        }
        catch (Exception e) when (e is InvalidDataException or FileNotFoundException or ArgumentOutOfRangeException)
        {
            Console.WriteLine($"[FourPasses] Etappentabelle nicht lesbar ({e.Message}), FOUR PASSES gesperrt");
        }
    }

    /// <summary>WET four passes: rain over the night course (the disc has no rainy night course, as in the story).</summary>
    private bool FourPassRain => FourPass is { Wet: true };

    /// <summary>A stage finished: into the run; after the fourth a new record is kept (and saved with the menus).</summary>
    private void FourPassStage()
    {
        if (FourPass is not { } f) return;
        f.Finish(_hud.Timer.Splits, _hud.Drift.Total);
        Console.WriteLine($"\n[FourPasses] Etappe {f.Finished}/{f.Stages.Count} {f.Current.Course}: {Style.Time(f.StageTime(f.Finished - 1))}, gesamt {Style.Time(f.Total)}");
        if (!f.NewRecord) return;
        _settings.Best[_settings.RunKey(FourPasses.CourseKey(f.Wet), false)] = f.Splits;
        if (SavesRuns) _settings.Save();
    }

    /// <summary>Replay label of a stage, e.g. "FOUR PASSES 2/4".</summary>
    private string? FourPassLabel => FourPass is { } f ? $"FOUR PASSES {f.Index + 1}/{f.Stages.Count}" : null;

    private void BuildFourPassHud(int width, int height)
    {
        if (FourPass is { } f && _race == null) FourPassHud.Build(_overlay, width, height, f, _catalog!.Courses.First(c => c.Id == f.Current.Course).Name, _hud.Timer);
    }

    private float FourPassHudBelow => FourPass != null && _race == null ? FourPassHud.H + 14 : 0;

    /// <summary>
    ///     --menu fourpasses (course grid on the slot), fourpasses-weather, fourpasses-stage (sheet after stage 2),
    ///     fourpasses-finish / fourpasses-result (after stage 4, a new record over a stored one): made-up times for screenshots.
    /// </summary>
    private void OpenFourPassMenu(string arg)
    {
        var m = _menu!;
        float[] Stage(float t) => [t * 0.24f, t * 0.5f, t * 0.77f, t];
        float[][] run = [Stage(262.4f), Stage(318.9f), Stage(301.2f), Stage(276.5f)];
        float[] best = [.. new[] { 265.1f, 315.0f, 309.8f, 279.3f }.SelectMany((t, i) => Stage(t).Select(s => s + new[] { 0, 265.1f, 580.1f, 889.9f }[i]))];
        switch (arg)
        {
            case "fourpasses-stage":
                m.ShowFourPass(false, run[..2], best, true);
                break;
            case "fourpasses-finish" or "fourpasses-result":
                m.ShowFourPass(false, run, best, arg == "fourpasses-result");
                break;
            default:
                m.Open(Menu.Screen.Course, _courseTime, false, _carName, _paint);
                m.ShowFourPassSlot();
                if (arg == "fourpasses-weather") m.Update((0, 0, true, false), 0); // decide the slot: the weather step
                break;
        }
        m.Settle(arg is "fourpasses-stage" or "fourpasses-result" ? Menu.ButtonsAt + 1 : 1);
        _inRace = false;
    }

    /// <summary>
    ///     --flow --fourpasses: Time Attack → the twelfth slot → DRY → the car as it stands → AT → four stages driven by the pilot at
    ///     16× (telop, race, stage clear and stage sheet each, a pause in stage 2) → final sheet → EXIT → REPLAY &amp; RECORD
    ///     (the stage replays, RECORDS with the FOUR PASSES row). Start with the course argument AKINA_NIT (the grid opens on AKINA).
    /// </summary>
    private (string At, float Wait, string? Shot, int X, int Y, bool Ok, bool Back)[] FourPassFlowScript =>
    [
        ("Boot", 1.2f, null, 0, 0, true, false), ("Logo", 1, null, 0, 0, true, false), ("Title", 1.5f, null, 0, 0, true, false),
        ("Modes", 1, null, 0, 1, false, false), ("Modes", 0.6f, null, 0, 0, true, false),
        ("Course", 1, null, 0, 1, false, false), ("Course", 0.3f, null, 0, 1, false, false), ("Course", 0.3f, null, 1, 0, false, false),
        ("Course", 0.3f, null, 1, 0, false, false), ("Course", 0.8f, "fp_course", 0, 0, true, false),
        .. FourPassWet ? new[] { ("Weather", 0.5f, (string?)null, 1, 0, false, false) } : [], ("Weather", 0.8f, "fp_weather", 0, 0, true, false),
        ("Maker", 0.8f, null, 0, 0, true, false), ("Maker", 0.5f, null, 0, 0, true, false), ("Car", 1.2f, "fp_car", 0, 0, true, false),
        ("Gearbox", 0.6f, null, 0, 0, true, false),
        .. FourPassFlowStage(1, pause: false), .. FourPassFlowStage(2, pause: true), .. FourPassFlowStage(3, pause: false),
        ("Loading", 0.4f, null, 0, 0, false, false), ("Intro", 1, "fp_telop_4", 0, 0, false, false), ("Race", 1.5f, "fp_race_4", 0, 0, false, false),
        ("Finish", 1.2f, "fp_finish", 0, 0, false, false), ("Result", 4f, "fp_result", 1, 0, false, false), ("Result", 0.3f, null, 1, 0, false, false),
        ("Result", 0.3f, null, 1, 0, false, false), ("Result", 0.3f, null, 1, 0, false, false), ("Result", 0.5f, null, 0, 0, true, false),
        ("Modes", 1, null, 0, 1, false, false), ("Modes", 0.5f, null, 0, 1, false, false), ("Modes", 0.5f, null, 0, 1, false, false), ("Modes", 0.5f, null, 0, 0, true, false),
        ("ReplayMenu", 1, "fp_replays", 0, -1, false, false), ("ReplayMenu", 1, "fp_records", 0, 0, false, true), ("Modes", 1, "fp_back", 0, 0, false, false),
    ];

    /// <summary>One stage before the last: load, telop, race (a pause in between), STAGE n CLEAR, the stage sheet, NEXT.</summary>
    private static (string, float, string?, int, int, bool, bool)[] FourPassFlowStage(int n, bool pause) =>
    [
        ("Loading", 0.4f, null, 0, 0, false, false), ("Intro", 1, $"fp_telop_{n}", 0, 0, false, false),
        .. pause
            ? new (string, float, string?, int, int, bool, bool)[] { ("Race", 2, $"fp_race_{n}", 0, 0, false, true), ("Pause", 0.8f, $"fp_pause_{n}", 0, 0, true, false) }
            : [("Race", 1.5f, $"fp_race_{n}", 0, 0, false, false)],
        ("Finish", 1.2f, $"fp_stage_clear_{n}", 0, 0, false, false), ("Result", 3.6f, $"fp_stage_{n}", 0, 0, true, false),
    ];
}
