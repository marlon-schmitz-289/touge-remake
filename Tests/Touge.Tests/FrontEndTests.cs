using Touge.Ui;

namespace Touge.Tests;

public class FrontEndTests
{
    /// <summary>
    ///     The original's flow and timings: boot → cards → title on their own, START → main menu, the drum wraps and locks
    ///     input while it rolls, unbuilt modes beep, Time Attack comes out after the 30-frame fade, idle goes back to the title.
    /// </summary>
    [Fact]
    public void Flow_TimingsSoundsAndDrum()
    {
        var sounds = new List<string>();
        var f = new FrontEnd { Sound = sounds.Add };
        var none = (0, 0, false, false);
        var r = FrontEnd.Result.None;
        void Run(float seconds, (int, int, bool, bool) k = default)
        {
            r = f.Update(k, 1 / 60f);
            for (var t = 1 / 60f; t < seconds; t += 1 / 60f)
                if (f.Update(none, 1 / 60f) is var x and not FrontEnd.Result.None) r = x;
        }

        f.Open(FrontEnd.Step.Boot);
        Assert.Null(f.Music);
        Run(FrontEnd.Fade + FrontEnd.BootHold + FrontEnd.Fade + 0.05f);
        Assert.Equal(FrontEnd.Step.Logo, f.Current);
        Run(FrontEnd.Fade + FrontEnd.CardHold + FrontEnd.Fade + 0.05f);
        Assert.Equal(FrontEnd.Step.Disclaimer, f.Current);
        Run(FrontEnd.Fade + FrontEnd.CardHold + FrontEnd.Fade + 0.05f);
        Assert.Equal(FrontEnd.Step.Title, f.Current);
        Assert.Equal("gam.adx", f.Music);

        Run(FrontEnd.Fade + 0.05f, (0, 0, true, false)); // START
        Assert.Equal(FrontEnd.Step.Modes, f.Current);
        Assert.Equal(["sys002"], sounds);

        Run(1, (0, -1, false, false)); // up from LEGEND OF THE STREETS wraps to OPTIONS
        Assert.Equal(FrontEnd.Modes.Length - 1, f.Index);
        Run(0.1f, (0, 1, false, false));
        f.Update((0, 1, false, false), 1 / 60f); // still rolling: ignored
        Assert.Equal(0, f.Index);
        Run(0.5f);
        Run(1, (0, 1, false, false)); // TIME ATTACK
        Run(0.5f, (0, 1, false, false)); // VERSUS
        Assert.Equal("VERSUS", FrontEnd.Modes[f.Index]);
        Run(0.5f, (0, 1, false, false)); // STORY
        Run(FrontEnd.Fade + 0.1f, (0, 0, true, false)); // STORY is built: leaves for it
        Assert.Equal(FrontEnd.Result.Story, r);
        f.Open(FrontEnd.Step.Modes); // the story hands back: same entry selected
        Run(0.5f, (0, -1, false, false));
        Run(0.5f, (0, -1, false, false));
        Assert.Equal(["sys002", "SYS005", "SYS005", "SYS005", "SYS005", "SYS005", "SYS006", "SYS005", "SYS005"], sounds);

        f.Update((0, 0, true, false), 1 / 60f); // decide TIME ATTACK
        Run(FrontEnd.Fade - 0.1f);
        Assert.Equal(FrontEnd.Result.None, r);
        Run(0.15f);
        Assert.Equal(FrontEnd.Result.TimeAttack, r);
        Assert.False(f.Active);
        Assert.Equal("SYS006", sounds[^1]);

        f.Open(FrontEnd.Step.Modes); // the game hands back: same entry selected
        Assert.Equal(1, f.Index);
        Run(FrontEnd.ModesIdle + FrontEnd.Fade + 0.1f);
        Assert.Equal(FrontEnd.Step.Title, f.Current);
    }

    /// <summary>QUIT GAME (desktop): last on the drum, asks first with NO selected; NO/back stay, YES quits after the fade.</summary>
    [Fact]
    public void QuitGame_AsksFirst()
    {
        Assert.True(QuitPrompt.Available);
        Assert.Equal("QUIT GAME", FrontEnd.Modes[^1]);
        var sounds = new List<string>();
        var f = new FrontEnd { Sound = sounds.Add };
        var r = FrontEnd.Result.None;
        void Run(float seconds, (int, int, bool, bool) k = default)
        {
            r = f.Update(k, 1 / 60f);
            for (var t = 1 / 60f; t < seconds; t += 1 / 60f)
                if (f.Update(default, 1 / 60f) is var x and not FrontEnd.Result.None) r = x;
        }
        (int, int, bool, bool) ok = (0, 0, true, false), back = (0, 0, false, true);

        f.Open(FrontEnd.Step.Modes);
        Run(FrontEnd.Fade + 0.1f);
        Run(0.5f, (0, -1, false, false)); // up from the first mode wraps to QUIT GAME
        Assert.Equal(FrontEnd.Modes.Length - 1, f.Index);
        Run(0.1f, ok);
        Run(0.1f, ok); // NO is preselected
        Run(0.1f, ok);
        Run(0.1f, back); // back = NO
        Run(0.1f, ok);
        Run(0.1f, (-1, 0, false, false)); // YES
        Run(FrontEnd.Fade + 0.1f, ok);
        Assert.Equal(FrontEnd.Result.Quit, r);
        Assert.Equal(["SYS005", "SYS006", "SYS006", "SYS006", "BEEP001", "SYS006", "SYS005", "SYS006"], sounds);
    }
}
