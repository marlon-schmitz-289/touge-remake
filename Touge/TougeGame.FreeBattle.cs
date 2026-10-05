using Touge.Formats;
using Touge.Race;
using Touge.Ui;

namespace Touge;

/// <summary>
///     VERSUS → VS CPU (<see cref="FreeBattle"/>, hosted by <see cref="Versus"/>): the lobby's choice is loaded behind the
///     white loading screen (the course unless it is already there, the player's car, the rival with his livery and sound),
///     then the usual telop with VS, countdown and battle (<see cref="TougeGame.Battle"/>), finish banner (WIN/LOSE) and
///     battle sheet with RETRY / REPLAY / CHANGE SETTINGS / EXIT (<see cref="Menu.FreeBattle"/>). Pause → Exit and CHANGE
///     SETTINGS return to the lobby, EXIT to the main menu. The lobby's choice is kept in <see cref="Settings.FreeBattle"/>.
/// </summary>
public sealed partial class TougeGame
{
    /// <summary>--flow … --freebattle: the scripted pass goes through VS CPU (<see cref="FreeBattleFlowScript"/>).</summary>
    public bool FreeBattleFlow { get; init; }

    /// <summary>A free battle is set up (loaded, running or in its result): the menus route back to its lobby.</summary>
    private bool _inFreeBattle;
    private bool _fbLoadPending;

    /// <summary>VS CPU chosen on the versus tiles (or --menu freebattle): the lobby on the remembered choice and the saved car.</summary>
    private void OpenFreeBattle()
    {
        _versusUi!.OpenCpu(_settings.FreeBattle, _settings.Car, _settings.Paint, _settings.Manual);
        _inRace = false;
    }

    /// <summary>--menu freebattle: the lobby at start (screenshots).</summary>
    private void OpenFreeBattleAtStart()
    {
        OpenVersus();
        OpenFreeBattle();
        if (shotPath != null) _versusUi!.Settle();
    }

    /// <summary>The versus screens' VS CPU actions; true when handled.</summary>
    private bool FreeBattleAction(Versus.Action action)
    {
        switch (action)
        {
            case Versus.Action.Cpu:
                OpenFreeBattle();
                return true;
            case Versus.Action.CpuStart:
                var lobby = _versusUi!.CpuLobby;
                _settings.FreeBattle = lobby.Choice.Copy();
                (_settings.Car, _settings.Paint, _settings.Manual) = (lobby.CarId, lobby.Paint, lobby.Manual);
                if (SavesRuns) _settings.Save();
                _versusUi.ShowLoading();
                _fbLoadPending = true;
                return true;
            case Versus.Action.CpuLeave:
                EndFreeBattle();
                return true;
        }
        return false;
    }

    /// <summary>Per frame from the versus update: once the loading screen is up, the battle loads (blocking) and the telop starts.</summary>
    private void FreeBattleLoadStep()
    {
        if (!_fbLoadPending || _versusUi is not { Current: Versus.Screen.Loading, Shown: true } ui) return;
        _fbLoadPending = false;
        var lobby = ui.CpuLobby;
        var c = lobby.Choice;
        var setup = FreeBattle.Setup(c);
        // whatever a split-screen race left loaded goes (its cars would come back with the course)
        EndVersusRace();
        DisposeVersusCars();
        _vsCars.Clear();
        (_vsLoadedKey, _vsSplit) = (null, false);
        EndRecording();
        _storyRain = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var iso = new Iso9660(isoPath))
        {
            var fog = c.Fog && !c.Course.EndsWith("_RIN");
            if (c.Course != _courseTime || c.Reverse != _drive.Reverse || fog != _fog)
            {
                Battle = setup;
                LoadCourse(iso, c.Course, c.Reverse, lobby.CarId, lobby.Paint, fog: fog); // the rival comes with it
            }
            else
            {
                // same course: only the cars change (the rival's textures sit after the player's, so it goes first)
                Device.WaitIdle();
                Battle = null;
                DisposeRival();
                if (lobby.CarId != _carName || lobby.Paint != _paint) SwitchCar(Array.IndexOf(CarPaint.Cars, lobby.CarId), lobby.Paint);
                Battle = setup;
                LoadBattle(iso);
                if (_audioDevice != null) StartRivalAudio(iso);
            }
        }
        _inFreeBattle = _menu!.FreeBattle = true;
        (_menu.Legend, _menu.Rain) = (false, false);
        ui.Close();
        ResetRun();
        _drive.Car.AutomaticGearbox = !lobby.Manual;
        OpenMenu(Menu.Screen.Intro);
        Console.WriteLine($"\n[FreeBattle] {c.Course}{(c.Reverse ? " bergauf" : "")}{(c.Fog ? " Nebel" : "")}: {lobby.CarId} gegen {setup.Rival.Name} ({setup.Rival.Car}, " +
                          $"Fähigkeit {setup.Rival.Style.Skill:0.00}, {c.Level}, Gummiband {(setup.RubberBand ? "an" : "aus")}), Regel {c.Rule}" +
                          $"{(c.Rule == BattleRule.LeadChase ? $", führt {(setup.Leader == 0 ? "Spieler" : "Rivale")}" : "")}, geladen in {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>Pause → Exit or the result's CHANGE SETTINGS: the run is saved, the lobby comes back (the battle stays loaded for the next START).</summary>
    private void ReturnToFreeBattleLobby()
    {
        EndRecording();
        _inRace = false;
        _versusUi!.OpenCpu(_settings.FreeBattle, _settings.Car, _settings.Paint, _settings.Manual);
    }

    /// <summary>The result's EXIT: no more battle, back to the main menu.</summary>
    private void ExitFreeBattle()
    {
        EndFreeBattle();
        _inRace = false;
        if (_front == null) Window.ShouldClose = true; // a --menu start has no main menu to go back to
        else _front.Open(FrontEnd.Step.Modes);
    }

    /// <summary>Drops the battle (rival, session, menus' routing): what follows is plain time attack again.</summary>
    private void EndFreeBattle()
    {
        if (!_inFreeBattle) return;
        _inFreeBattle = false;
        if (_menu != null) _menu.FreeBattle = false;
        EndBattle();
        _drive.ResetTo(0);
        SyncPose();
    }

    /// <summary>
    ///     --flow … --freebattle: main menu → VERSUS → VS CPU → lobby (RULE stays LEAD / CHASE, you lead, rival Ryosuke) → START →
    ///     telop, countdown → battle (autopilot, 16×) → finish → result → RETRY → battle → pause → Exit (the lobby) → RULE RACE →
    ///     START → battle → result → EXIT → main menu.
    /// </summary>
    private static readonly (string At, float Wait, string? Shot, int X, int Y, bool Ok, bool Back)[] FreeBattleFlowScript =
    [
        ("Boot", 1.2f, null, 0, 0, true, false), ("Logo", 1, null, 0, 0, true, false), ("Title", 1.5f, null, 0, 0, true, false),
        ("Modes", 1, null, 0, 1, false, false), ("Modes", 0.5f, null, 0, 1, false, false), ("Modes", 0.6f, "fb_main_menu", 0, 0, true, false),
        ("VsMode", 1, null, -1, 0, false, false), ("VsMode", 0.8f, "fb_tiles", 0, 0, true, false),
        ("VsCpu", 1.5f, "fb_lobby", 0, 1, false, false), ("VsCpu", 0.3f, null, 0, 1, false, false), ("VsCpu", 0.3f, null, 0, 1, false, false),
        ("VsCpu", 0.3f, null, 0, 1, false, false), ("VsCpu", 0.4f, "fb_lobby_lead", 1, 0, false, false),
        ("VsCpu", 0.3f, null, 0, 1, false, false), ("VsCpu", 0.3f, null, 0, 1, false, false), ("VsCpu", 0.6f, "fb_lobby_rival", 1, 0, false, false),
        .. Enumerable.Repeat(("VsCpu", 0.25f, (string?)null, 0, 1, false, false), 4),
        ("VsCpu", 0.8f, "fb_lobby_start", 0, 0, true, false),
        ("VsLoading", 0.3f, "fb_loading", 0, 0, false, false),
        ("Intro", 1.2f, "fb_telop", 0, 0, false, false), ("Intro", 2.5f, "fb_countdown", 0, 0, false, false), ("Race", 2, "fb_race", 0, 0, false, false),
        ("Finish", 0.8f, "fb_finish", 0, 0, false, false), ("Result", 3.6f, "fb_result", 0, 0, true, false),
        ("Intro", 1, null, 0, 0, false, false), ("Race", 1.5f, "fb_race_retry", 0, 0, false, true),
        .. Enumerable.Repeat(("Pause", 0.3f, (string?)null, 1, 0, false, false), 4),
        ("Pause", 0.5f, "fb_pause_exit", 0, 0, true, false),
        ("VsCpu", 1.2f, "fb_lobby_back", 0, -1, false, false), .. Enumerable.Repeat(("VsCpu", 0.25f, (string?)null, 0, -1, false, false), 6),
        ("VsCpu", 0.6f, "fb_lobby_race", 1, 0, false, false), .. Enumerable.Repeat(("VsCpu", 0.25f, (string?)null, 0, 1, false, false), 6),
        ("VsCpu", 0.6f, null, 0, 0, true, false),
        ("VsLoading", 0.3f, null, 0, 0, false, false), ("Intro", 1, null, 0, 0, false, false), ("Race", 2, "fb_race_rule_race", 0, 0, false, false),
        ("Finish", 0.8f, "fb_finish_race", 0, 0, false, false), ("Result", 3.6f, "fb_result_race", 1, 0, false, false),
        ("Result", 0.3f, null, 1, 0, false, false), ("Result", 0.3f, null, 1, 0, false, false), ("Result", 0.5f, "fb_result_exit", 0, 0, true, false),
        ("Modes", 1.2f, "fb_modes_back", 0, 0, false, false),
    ];
}
