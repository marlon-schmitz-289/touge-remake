using Touge.Ui;

namespace Touge.Tests;

public class FrontEndTests
{
    /// <summary>
    ///     The original's flow and timings: boot → cards → title on their own, START → main menu, the drum wraps (a step while
    ///     it rolls is taken at once), unbuilt modes beep, Time Attack comes out after the 30-frame fade, idle goes back to the title.
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
        Assert.Equal(0, f.Index);
        Run(0.5f, (0, 1, false, false)); // still rolling: taken at once (the original dropped it), TIME ATTACK
        Assert.Equal(1, f.Index);
        Run(0.5f, (0, 1, false, false)); // VERSUS
        Assert.Equal("VERSUS", FrontEnd.Modes[f.Index]);
        Run(0.5f, (0, 1, false, false)); // FREE PLAY
        Assert.Equal("FREE PLAY", FrontEnd.Modes[f.Index]);
        Run(0.5f, (0, 1, false, false)); // STORY
        Run(FrontEnd.Fade + 0.1f, (0, 0, true, false)); // STORY is built: leaves for it
        Assert.Equal(FrontEnd.Result.Story, r);
        f.Open(FrontEnd.Step.Modes); // the story hands back: same entry selected
        for (var i = 0; i < 3; i++) Run(0.5f, (0, -1, false, false));
        Assert.Equal(["sys002", "SYS005", "SYS005", "SYS005", "SYS005", "SYS005", "SYS005", "SYS006", "SYS005", "SYS005", "SYS005"], sounds);

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

    /// <summary>
    ///     Quick taps, reversals and a held key: every press steps at once with one sound, a held key (repeats) steps
    ///     every 0.1 s, the drum's scroll position never moves more than its speed in a frame and comes to rest on the index.
    /// </summary>
    [Fact]
    public void Drum_FollowsQuickStepsWithoutJumps()
    {
        var sounds = new List<string>();
        var f = new FrontEnd { Sound = sounds.Add };
        f.Open(FrontEnd.Step.Modes);
        for (var i = 0; i < 40; i++) f.Update(default, 1 / 60f);
        const float dt = 1 / 60f;
        int target = 0, steps = 0, frame = 0; // target: index without wrapping
        float pos = 0;
        void Frame(int y, bool repeat = false)
        {
            var (before, lag) = (f.Index, f.Lag);
            f.Update((0, y, false, false), dt, repeat);
            if (f.Index != before) (target, steps) = (target + y, steps + 1);
            var now = target - f.Lag; // where the drum is
            Assert.True(MathF.Abs(now - pos) <= MathF.Max(9 * 60 / 48f, 12 * (MathF.Abs(lag) + 1)) * dt + 1e-4f, $"jump at frame {frame}: {pos} -> {now}");
            Assert.InRange(f.Lag, -2, 2);
            pos = now;
            frame++;
        }

        // taps 5 frames apart (faster than anyone taps), reversing twice: all taken
        int[] taps = [1, 1, 1, -1, -1, 1, -1, -1, -1, -1, 1];
        foreach (var y in taps)
        {
            Frame(y);
            for (var i = 0; i < 4; i++) Frame(0);
        }
        Assert.Equal(taps.Length, steps);
        Assert.Equal(taps.Sum(), target);
        // a held key: the press, then key repeats from 0.35 s every other frame for a second
        var held = steps;
        Frame(1);
        for (var i = 1; i < 60; i++) Frame(i >= 21 && i % 2 == 1 ? 1 : 0, true);
        Assert.InRange(steps - held, 7, 9); // press, first repeat, then one per 0.1 s
        // flip-flopping while it still rolls: a turn is always a new press, then rest
        held = steps;
        Frame(-1);
        Frame(0);
        Frame(1);
        Assert.Equal(2, steps - held);
        for (var i = 0; i < 60; i++) Frame(0);
        Assert.Equal(0f, f.Lag);
        Assert.Equal(((target % FrontEnd.Modes.Length) + FrontEnd.Modes.Length) % FrontEnd.Modes.Length, f.Index);
        Assert.Equal(steps, sounds.Count(s => s == "SYS005"));
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
