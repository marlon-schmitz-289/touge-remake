using Touge.Formats;
using Touge.Race;
using Touge.Story;
using Touge.Ui;

namespace Touge;

/// <summary>
///     STORY (<see cref="StoryMode"/>): chapter select and scenes over the course flight, a chapter loads its course, the
///     driver's car and the battle (<see cref="Battle"/> set at runtime), the race runs as any battle (telop, countdown,
///     pause), the story judges it (battle outcome or <see cref="SoloJudge"/>) and takes over with its banner and result;
///     progress in <see cref="Progress"/> (saved with the menus). Leaving a chapter drops the battle again, so time attack is
///     plain time attack.
/// </summary>
public sealed partial class TougeGame
{
    private StoryMode? _story;
    private StoryScript.Chapter[]? _storyChapters;
    private SoloJudge? _storyJudge;
    /// <summary>A wet chapter at night: the disc has no rainy night course, so the dry one gets rain (drops, wet road, sound).</summary>
    private bool _storyRain;
    /// <summary>--progress n: for test runs, chapters 0…n−1 count as cleared (the real file is not touched).</summary>
    public int StoryProgress { get; init; }
    /// <summary>--flow … --story: the scripted pass goes through the story instead of time attack.</summary>
    public bool StoryFlow { get; init; }

    /// <summary>With the menus: the chapter table from the ELF and the saved progress (or the test run's).</summary>
    private void LoadStory(Iso9660 iso)
    {
        try
        {
            _storyChapters = StoryHeadless.Chapters(iso);
        }
        catch (Exception e) when (e is InvalidDataException or FileNotFoundException or ArgumentOutOfRangeException)
        {
            Console.WriteLine($"[Story] Kapiteltabelle nicht lesbar ({e.Message}), STORY aus");
            return;
        }
        if (_persist && StoryProgress > 0) Console.WriteLine("[Story] --progress gilt nur für Testläufe ohne gespeicherten Fortschritt, ignoriert");
        else for (var n = 0; n < StoryProgress; n++) _progress.Clear(StoryMode.Key(n)); // the progress Legend loaded (one store)
        _story = new StoryMode(_catalog!) { Sound = n => _menuAudio?.Play(n), Progress = _progress };
    }

    private void OpenStory(int? chapter = null)
    {
        if (_story == null)
        {
            _front?.Open(FrontEnd.Step.Modes);
            return;
        }
        _story.Enter(_storyChapters!, chapter);
        _inRace = false;
    }

    /// <summary>--menu story[:n[:scene[:part[:line]]|:race|:end]]: chapter select, a chapter's scene, its race start or THE END (screenshots).</summary>
    private void StartStoryMenu(string arg)
    {
        var p = arg.Split(':');
        OpenStory(p.Length > 1 ? Math.Clamp(int.Parse(p[1]), 0, StoryText.Chapters.Length - 1) : null);
        if (p.Length > 2 && p[2] == "end") _story!.ShowEnding();
        else if (p.Length > 2)
        {
            StoryLoad();
            if (p[2] == "race") StoryRace();
            else _story!.ShowScene(p.Length > 3 ? int.Parse(p[3]) : 0, p.Length > 4 ? int.Parse(p[4]) : 0);
        }
        if (shotPath != null)
        {
            _story!.Settle();
            _menu!.Settle();
        }
    }

    /// <summary>Story input and its actions.</summary>
    private void UpdateStory((int X, int Y, bool Ok, bool Back) keys, float dt)
    {
        var s = _story!;
        switch (s.Update(keys, dt))
        {
            case StoryMode.Action.Load:
                StoryLoad();
                break;
            case StoryMode.Action.Race:
                StoryRace();
                break;
            case StoryMode.Action.Retry:
                ResetRun();
                StoryRace();
                break;
            case StoryMode.Action.Save:
                SaveProgress();
                break;
            case StoryMode.Action.Leave:
                EndBattle();
                break;
            case StoryMode.Action.Exit:
                EndBattle();
                if (_front == null) Window.ShouldClose = true; // a --menu start has no main menu to go back to
                else _front.Open(FrontEnd.Step.Modes);
                break;
        }
    }

    /// <summary>Loads the chosen chapter: its course and direction at night, the driver's car, the battle (or none).</summary>
    private void StoryLoad()
    {
        var s = _story!;
        EndBattle();
        Battle = s.Battle;
        _storyRain = _menu!.Rain = s.Data.Wet && s.Data.Night;
        using (var iso = new Iso9660(isoPath)) LoadCourse(iso, s.CourseTime, s.Data.Reverse, s.HeroCar, 0);
        if (_storyRain) _renderer.Atmosphere.Wetness = 1; // rain over the night course (no _RIN night on the disc)
        ResetRun();
        _inRace = false;
        Console.WriteLine($"[Story] Kapitel {s.Chapter} {s.Text.Title}: {s.CourseTime}{(s.Data.Reverse ? " bergauf" : "")}, {s.HeroCar} gegen " +
                          $"{(Battle is { } b ? $"{b.Rival.Name} ({b.Rival.Car}, Fähigkeit {b.Rival.Style.Skill:0.00})" : "-")}, Ziel {s.Goal}");
        s.Loaded();
    }

    /// <summary>The scene is over: telop with the rival, countdown, race (the judge starts fresh).</summary>
    private void StoryRace()
    {
        _storyJudge = _story!.BeginRace();
        _finished = false;
        OpenMenu(Menu.Screen.Intro);
        _inRace = true;
    }

    /// <summary>Per tick: the judge of a run alone sees the timer, wall contact and the drift score.</summary>
    private void StoryTick(float dt)
    {
        if (_storyJudge == null || _story is not { InRun: true } || _finished) return;
        var t = _hud.Timer;
        _storyJudge.Update(t.Phase == LapTimer.State.Running, t.Phase == LapTimer.State.Finished, t.Time, _drive.Car.WallContacts > 0,
            _hud.Drift.Total + _hud.Drift.Score, dt);
    }

    /// <summary>
    ///     A story run is decided: the story's banner and result instead of the time attack/battle ones. A battle a second after
    ///     it was decided (the cars run out), a run alone at once (failed mid-run: the game holds). True when it took over.
    /// </summary>
    private bool StoryFinished()
    {
        if (_story is not { InRun: true, Current: StoryMode.Phase.Racing } s || _finished || bench != null) return false;
        if (_race?.Battle is { } b)
        {
            if (b.Outcome == BattleOutcome.None || b.Time - b.DecidedAt < 1) return false;
            var rival = Battle!.Rival;
            var name = _catalog?.Cars.FirstOrDefault(c => c.Id == rival.Car)?.Name ?? rival.Car;
            _finished = true;
            s.Finish(b.Outcome, b.Reason, BattleReport.Of(_race, rival, name), coast: true);
        }
        else if (_storyJudge is { Outcome: not BattleOutcome.None } j)
        {
            _finished = true;
            s.Finish(j.Outcome, j.Reason, null, coast: _hud.Timer.Phase == LapTimer.State.Finished);
        }
        else return false;
        Console.WriteLine($"[Story] Kapitel {s.Chapter} entschieden: " + (_race?.Battle is { } d ? $"{d.Outcome} ({d.Reason}) nach {d.DecidedAt:F1} s"
            : $"{_storyJudge!.Outcome} ({_storyJudge.Reason}), Zeit {Style.Time(_storyJudge.Time)}, Wandtreffer {_storyJudge.WallHits}, Drift {_storyJudge.Drift:0}"));
        return true;
    }

    /// <summary>Drops the battle of a story chapter (rival model, sound, HUD) so the next run is plain time attack.</summary>
    private void EndBattle()
    {
        EndRecording(); // the chapter's run is saved now (replay, autosave)
        if (Battle == null && _race == null) return;
        Device.WaitIdle(); // the rival's textures may still be in flight
        Battle = null;
        _storyRain = false;
        _race = null;
        _rivalAudio?.Dispose();
        _rivalAudio = null;
        DisposeRival();
        _battleHud = null;
        if (_menu != null) (_menu.Versus, _menu.Battle, _menu.Rain) = (null, null, false);
        _finished = false;
    }

    /// <summary>
    ///     --flow … --story (with --progress 7): title → STORY → chapter select (parts, a locked part) → chapter 7, GT-R VS
    ///     HACHI-ROKU → loading → scene lines → telop and countdown → the battle (autopilot, 16×) → banner → result → the scene
    ///     after it → select (cleared, chapter 8 open); then chapter 3, THE GHOST OF AKINA (the autopilot does not pass within
    ///     120 s) → TIME UP → RETRY → again → CHAPTER SELECT → main menu. Scenes end with BACK (skip) so typing speed cannot shift the script.
    /// </summary>
    private static readonly (string At, float Wait, string? Shot, int X, int Y, bool Ok, bool Back)[] StoryFlowScript =
    [
        ("Boot", 1.2f, null, 0, 0, true, false), ("Logo", 1, null, 0, 0, true, false), ("Title", 1.5f, null, 0, 0, true, false),
        ("Modes", 1, null, 0, 1, false, false), ("Modes", 0.6f, null, 0, 1, false, false), ("Modes", 0.6f, null, 0, 1, false, false), ("Modes", 0.8f, "modes_story", 0, 0, true, false),
        ("StorySelect", 1.5f, "select", 0, -1, false, false), ("StorySelect", 0.8f, "select_ch7", 1, 0, false, false),
        ("StorySelect", 0.8f, "select_part2_locked", -1, 0, false, false), ("StorySelect", 0.5f, null, 0, -1, false, false),
        ("StorySelect", 0.6f, null, 0, 0, true, false),
        ("StoryLoading", 0.4f, "loading", 0, 0, false, false),
        ("StoryScene", 3, "scene_title", 0, 0, false, false), ("StoryScene", 2.5f, "scene_line1", 0, 0, true, false),
        ("StoryScene", 2.5f, null, 0, 0, true, false), ("StoryScene", 2.5f, "scene_line3", 0, 0, true, false),
        ("StoryScene", 2.5f, "scene_line4", 1, 0, false, false),
        ("Intro", 1, "telop_vs", 0, 0, false, false), ("Intro", 2, "countdown", 0, 0, false, false), ("Race", 1.5f, "race", 0, 0, false, false),
        ("StoryBanner", 1.2f, "banner", 0, 0, true, false), ("StoryResult", 2, "result", 0, 0, true, false),
        ("StoryScene", 1, "after_title", 0, 0, false, false), ("StoryScene", 3, "after_line1", 0, 0, true, false),
        ("StoryScene", 2.5f, null, 0, 0, true, false), ("StoryScene", 2.5f, null, 0, 0, true, false), ("StoryScene", 2.5f, null, 0, 0, true, false),
        ("StoryScene", 2.5f, "after_line5", 0, 0, false, true),
        ("StorySelect", 1.5f, "select_cleared", 0, -5, false, false), ("StorySelect", 0.8f, "select_ch3", 0, 0, true, false),
        ("StoryLoading", 0.4f, null, 0, 0, false, false), ("StoryScene", 1, null, 0, 0, false, false), ("StoryScene", 4, "ghost_scene", 1, 0, false, false),
        ("Intro", 1, "ghost_telop", 0, 0, false, false), ("Race", 1.5f, "ghost_race", 0, 0, false, false),
        ("StoryBanner", 1.2f, "ghost_lose", 0, 0, true, false), ("StoryResult", 2, "ghost_result", 0, 0, true, false),
        ("Intro", 1, null, 0, 0, false, false), ("StoryBanner", 1, null, 0, 0, true, false), ("StoryResult", 1, null, 1, 0, false, false),
        ("StoryResult", 0.6f, "ghost_result_select", 0, 0, true, false), ("StorySelect", 1.2f, "select_after", 0, 0, true, false),
        // chapter 3 again: BACK in the scene returns to the select; then into the race and out through Pause → Exit
        ("StoryLoading", 0.4f, null, 0, 0, false, false), ("StoryScene", 2, "ghost_scene_back", 0, 0, false, true),
        ("StorySelect", 1.2f, "select_from_scene", 0, 0, true, false), ("StoryLoading", 0.4f, null, 0, 0, false, false),
        ("StoryScene", 1, null, 1, 0, false, false), ("Intro", 1, null, 0, 0, false, false), ("Race", 1.5f, "ghost_race2", 0, 0, false, true),
        ("Pause", 0.6f, null, 1, 0, false, false), ("Pause", 0.4f, null, 1, 0, false, false), ("Pause", 0.4f, null, 1, 0, false, false), ("Pause", 0.4f, "pause_exit", 1, 0, false, false), ("Pause", 0.4f, null, 0, 0, true, false),
        ("StorySelect", 1.5f, "select_from_pause", 0, 0, false, true),
        ("Modes", 1.2f, "modes_back", 0, 0, false, false),
    ];
}
