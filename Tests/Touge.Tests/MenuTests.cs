using System.Numerics;
using Touge.Ui;

namespace Touge.Tests;

public class MenuTests
{
    /// <summary>
    ///     The game flow as the original's Time Attack: course → route → (time skipped when the course has one) → weather (DRY/FOG at night) →
    ///     maker → car (that maker's models) → transmission → loading asks once for the load → telop and 3-2-1-GO hold the game until GO →
    ///     finish → result tally → Exit; back walks the visited steps, the locked grid slot beeps, music per screen.
    /// </summary>
    [Fact]
    public void TimeAttackFlow_StepsSoundsAndActions()
    {
        Vector2[] line = [new(0, 0), new(100, 0), new(100, 100)];
        var catalog = new Catalog(
            [
                new Catalog.Course("AKINA", "AKINA", ["DAY", "NIT", "RIN"], line, 7700, 465, true, false),
                new Catalog.Course("HAPPOU", "HAPPOGAHARA", ["NIT"], line, 5000, 300, true, false),
            ],
            [
                new Catalog.Car("AE86T", "TOYOTA", "TRUENO GT-APEX [AE86]", "FR", 130, 940, [1, 2, 3]),
                new Catalog.Car("AE85", "TOYOTA", "LEVIN SR [AE85]", "FR", 83, 900, [1, 2]),
                new Catalog.Car("R32", "NISSAN", "SKYLINE GT-R V-spec II [BNR32]", "4WD", 280, 1480, [1]),
            ]);
        var sounds = new List<string>();
        var actions = new List<Menu.Action>();
        var m = new Menu(catalog, new Settings()) { Sound = sounds.Add };
        void Run(float seconds, (int, int, bool, bool) k = default)
        {
            actions.Add(m.Update(k, 1 / 60f));
            for (var t = 1 / 60f; t < seconds; t += 1 / 60f) actions.Add(m.Update(default, 1 / 60f));
        }
        (int, int, bool, bool) ok = (0, 0, true, false), back = (0, 0, false, true);

        m.Open(Menu.Screen.Course, "AKINA_DAY", false, "AE86T", 0);
        Assert.Equal("TOKYO.adx", m.Music(null));
        Run(0.1f, (-1, 0, false, false)); // left from slot 0 wraps to the locked four-pass slot
        Run(0.1f, ok);
        Assert.Equal(["SYS005", "BEEP001"], sounds);
        Assert.Equal(Menu.Screen.Course, m.Current);
        m.Open(Menu.Screen.Course, "HAPPOU_NIT", false, "AE86T", 0);
        Run(0.1f, ok);
        Assert.Equal(Menu.Screen.Route, m.Current); // same module: no fade
        Run(0.1f, ok); // night only: time skipped, weather DRY / FOG
        Assert.Equal(Menu.Screen.Weather, m.Current);
        Run(0.1f, (1, 0, false, false));
        Run(Menu.Fade + 0.1f, ok); // FOG over the night course, fade to the maker
        Assert.Equal(Menu.Screen.Maker, m.Current);
        Assert.Equal(("HAPPOU_NIT", true), (m.CourseTime, m.Fog));
        Assert.Equal("WORRY.adx", m.Music(null));
        Run(Menu.Fade + 0.1f, back); // back across modules: the weather again
        Assert.Equal(Menu.Screen.Weather, m.Current);
        Run(Menu.Fade + 0.1f, ok);
        Run(0.1f, (0, 1, false, false)); // NISSAN
        actions.Clear();
        Run(Menu.Fade + 0.1f, ok); // its cars: the first one shown
        Assert.Equal(Menu.Action.PreviewCar, actions[0]);
        Assert.Equal(("R32", Menu.Screen.Car), (m.CarId, m.Current));
        Run(Menu.Fade + 0.1f, back); // back to the makers, NISSAN still lit
        Assert.Equal(Menu.Screen.Maker, m.Current);
        Run(0.1f, (0, -1, false, false)); // TOYOTA
        Run(Menu.Fade + 0.1f, ok);
        Assert.Equal(("AE86T", Menu.Screen.Car), (m.CarId, m.Current)); // the saved car's maker opens on it
        actions.Clear();
        Run(0.1f, (0, 1, false, false)); // LEVIN SR
        Assert.Equal(Menu.Action.PreviewCar, actions[0]);
        Assert.Equal("AE85", m.CarId);
        Run(0.1f, (-1, 0, false, false)); // body colour wraps to the last
        Assert.Equal(1, m.Paint);
        Run(0.1f, ok);
        Run(0.1f, (1, 0, false, false)); // MT
        actions.Clear();
        Run(Menu.Fade + Menu.LoadAt + 0.1f, ok);
        Assert.Equal(Menu.Screen.Loading, m.Current);
        Assert.Single(actions, a => a == Menu.Action.Load);
        Assert.True(m.Manual);
        Assert.Null(m.Music(null)); // loading is silent

        sounds.Clear();
        Run(Menu.Fade + 0.1f);
        Assert.Equal(Menu.Screen.Intro, m.Current);
        Assert.True(m.Freezes);
        Run(Menu.GoAt - 0.2f);
        Assert.True(m.Freezes);
        Assert.Equal(["CAR010", "CAR010", "CAR010"], sounds);
        Run(0.3f);
        Assert.False(m.Freezes); // GO: the race runs while the sign fades
        Assert.Equal("CAR011", sounds[^1]);
        Run(Menu.IntroEnd - Menu.GoAt);
        Assert.Equal(Menu.Screen.None, m.Current);

        sounds.Clear();
        m.Finish(new Menu.Run(200, [50, 100, 150, 200], [null, null, null, null], null, true, 0));
        Assert.False(m.Freezes); // the car coasts to its stop behind the banner
        Assert.Equal("WIN.adx", m.Music(null));
        Run(Menu.FinishHold + Menu.Fade + 0.1f);
        Assert.Equal(Menu.Screen.Result, m.Current);
        Assert.Equal("JOY.adx", m.Music(null));
        Run(Menu.ButtonsAt);
        Assert.Equal(8, sounds.Count(s => s == "NAME001"));
        Run(0.1f, (1, 0, false, false)); // REPLAY: the game opens the viewer, the sheet stays
        actions.Clear();
        Run(0.1f, ok);
        Assert.Contains(Menu.Action.Replay, actions);
        Assert.Equal(Menu.Screen.Result, m.Current);
        for (var i = 1; i < Menu.ResultButtons.Length - 1; i++) Run(0.1f, (1, 0, false, false)); // EXIT
        actions.Clear();
        Run(Menu.Fade + 0.1f, ok);
        Assert.Contains(Menu.Action.Exit, actions);
        Assert.Equal(Menu.Screen.None, m.Current);

        // pause: Quit Game (desktop) asks first; NO keeps the pause, YES quits
        m.Open(Menu.Screen.Pause, "AKINA_DAY", false, "AE86T", 0);
        for (var i = 0; i < 2; i++) Run(0.1f, (1, 0, false, false));
        actions.Clear();
        Run(0.1f, ok); // Replay
        Run(0.1f, (1, 0, false, false));
        Run(0.1f, ok); // Photo
        Assert.Equal([Menu.Action.Replay, Menu.Action.Photo], actions.Where(a => a != Menu.Action.None));
        for (var i = 3; i < Menu.PauseButtons.Length - 1; i++) Run(0.1f, (1, 0, false, false));
        actions.Clear();
        Run(0.1f, ok);
        Run(0.1f, ok);
        Assert.Equal(Menu.Screen.Pause, m.Current);
        Run(0.1f, ok);
        Run(0.1f, (-1, 0, false, false));
        Run(0.1f, ok);
        Assert.Equal([Menu.Action.Quit], actions.Where(a => a != Menu.Action.None));
    }

    /// <summary>Versus pause: Replay and Photo greyed out (online also Retry), the cursor jumps over them both ways.</summary>
    [Fact]
    public void VersusPause_SkipsGreyedButtons()
    {
        var catalog = new Catalog([new Catalog.Course("AKINA", "AKINA", ["DAY"], [new(0, 0), new(100, 0)], 7700, 465, true, false)],
            [new Catalog.Car("AE86T", "TOYOTA", "TRUENO GT-APEX [AE86]", "FR", 130, 940, [1])]);
        var m = new Menu(catalog, new Settings()) { NoReplay = true };
        m.Open(Menu.Screen.Pause, "AKINA_DAY", false, "AE86T", 0);
        void Key(int x) => m.Update((x, 0, false, false), 0.1f);
        Key(1);
        Key(1);
        m.Select("Exit");
        m.NoRetry = true;
        Key(-1);
        Assert.Equal(Menu.Action.Resume, m.Update((0, 0, true, false), 0.1f)); // Exit → Continue in one step
        m.Open(Menu.Screen.Pause, "AKINA_DAY", false, "AE86T", 0);
        Key(1);
        Key(-1);
        Key(1);
        Assert.Equal(Menu.Action.None, m.Update((0, 0, true, false), 0.1f)); // Exit: leaves after the fade
        Assert.Contains(Menu.Action.Exit, Enumerable.Range(0, 60).Select(_ => m.Update(default, 1 / 60f)));
    }
}
