using System.Numerics;
using Touge.Formats;
using Touge.Ui;

namespace Touge.Tests;

public class FourPassesTests
{
    private static readonly FourPasses.Stage[] Stages = [new("AKAGI", false), new("AKINA", false), new("HAPPOU", true), new("IROHA", false)];

    private static float[] Stage(float t) => [t / 4, t / 2, t * 3 / 4, t];

    [Fact]
    public void TotalSplitsAndDeltas()
    {
        var f = new FourPasses(Stages) { Best = [.. Stage(100), .. Stage(100).Select(s => s + 100), .. Stage(100).Select(s => s + 200), .. Stage(100).Select(s => s + 300)] };
        Assert.Equal([25, 50, 75, 100], f.StageBest()!);
        f.Finish(Stage(90), 500);
        f.Finish(Stage(80), 999); // once per stage
        Assert.Equal(1, f.Finished);
        Assert.Equal(90, f.Total);
        Assert.Equal(-10, f.Delta(0)!.Value, 3);
        Assert.Null(f.Delta(1)); // not driven yet
        Assert.True(f.Next());
        Assert.Equal([25, 50, 75, 100], f.StageBest()!); // stage 2 from its own start
        f.Finish(Stage(120), 300);
        Assert.Equal(210, f.Total);
        Assert.Equal(120, f.StageTime(1), 3);
        Assert.Equal(10, f.Delta(1)!.Value, 3); // the total after it against the record's
        Assert.Equal(100, f.BestStageTime(1)!.Value, 3);
        Assert.Equal(800, f.Drift);
        Assert.False(f.NewRecord); // not done
        f.Next();
        f.Finish(Stage(95), 0);
        f.Next();
        f.Finish(Stage(80), 0);
        Assert.True(f.Done);
        Assert.Equal(385, f.Total);
        Assert.Equal(16, f.Splits.Length);
        Assert.Equal(f.Total, f.Splits[^1]); // last = total like every Settings.Best entry
        Assert.True(f.NewRecord); // 385 < 400
        Assert.False(f.Next());
    }

    [Fact]
    public void FirstRunIsARecord_SlowerIsNot()
    {
        var f = new FourPasses(Stages);
        for (var i = 0; i < 4; i++)
        {
            Assert.False(f.Done);
            f.Finish(Stage(100), 0);
            f.Next();
        }
        Assert.True(f.NewRecord);
        Assert.Null(f.Delta(3));
        var g = new FourPasses(Stages) { Best = f.Splits };
        for (var i = 0; i < 4; i++)
        {
            g.Finish(Stage(101), 0);
            g.Next();
        }
        Assert.False(g.NewRecord);
        Assert.Equal(4, g.Delta(3)!.Value, 3);
    }

    [Fact]
    public void SequencingNeedsAFinishedStage_ResetStartsOver()
    {
        var f = new FourPasses(Stages);
        Assert.False(f.Next()); // stage 1 not finished
        Assert.Equal("AKAGI", f.Current.Course);
        f.Finish(Stage(100), 0);
        Assert.True(f.Next());
        Assert.False(f.Next()); // stage 2 not finished
        Assert.Equal(("AKINA", false), (f.Current.Course, f.Current.Reverse));
        f.Reset();
        Assert.Equal((0, 0, 0f), (f.Index, f.Finished, f.Total));
    }

    [Fact]
    public void RecordKeys_PerWeatherAndAssists()
    {
        var s = new Settings();
        Assert.Equal("FOURPASS_A", s.RunKey(FourPasses.CourseKey(false), false));
        Assert.Equal("FOURPASS_WET_A", Settings.BestKey(FourPasses.CourseKey(true), false));
        Assert.DoesNotContain(s.RunKey(FourPasses.CourseKey(false), false), Enumerable.Range(0, 2).Select(r => Settings.BestKey("AKAGI", r == 1)));
        s.SteerAssist = 0;
        Assert.NotEqual("FOURPASS_A", s.RunKey(FourPasses.CourseKey(false), false)); // other assists race their own record
    }

    [Fact]
    public void StageTableFromTheElf()
    {
        var elf = new byte[FourPasses.TableAt - StoryScript.ElfBase + 32];
        byte[] table = [255, 1, 2, 0, 0, 1, 255, 0, 255, 1, 3, 0, 0, 1, 255, 0, 255, 1, 4, 1, 0, 1, 255, 0, 255, 1, 5, 0, 0, 1, 255, 0];
        table.CopyTo(elf, FourPasses.TableAt - StoryScript.ElfBase);
        Assert.Equal(Stages, FourPasses.Read(elf));
        elf[FourPasses.TableAt - StoryScript.ElfBase + 2] = 40;
        Assert.Throws<InvalidDataException>(() => FourPasses.Read(elf));
    }

    /// <summary>The slot in the Time Attack flow: weather only, the four courses one after another with NEXT, RETRY back to stage 1, the record buttons at the end.</summary>
    [Fact]
    public void MenuFlow_FourStagesWithNextAndRetry()
    {
        Vector2[] line = [new(0, 0), new(100, 0), new(100, 100)];
        var catalog = new Catalog(
            [.. new[] { "MYOUGI0", "USUI0", "AKAGI", "AKINA", "HAPPOU", "IROHA", "MYOUGI", "USUI", "SHOMARU", "MOMIJI", "SHIONA" }
                .Select(id => new Catalog.Course(id, id, ["DAY", "NIT", "RIN"], line, 5000, 300, true, id.EndsWith('0')))],
            [new Catalog.Car("AE86T", "TOYOTA", "TRUENO GT-APEX [AE86]", "FR", 130, 940, [1])]);
        var sounds = new List<string>();
        var actions = new List<Menu.Action>();
        var m = new Menu(catalog, new Settings()) { Sound = sounds.Add, FourPassStages = Stages };
        void Run(float seconds, (int, int, bool, bool) k = default)
        {
            actions.Add(m.Update(k, 1 / 60f));
            for (var t = 1 / 60f; t < seconds; t += 1 / 60f) actions.Add(m.Update(default, 1 / 60f));
        }
        (int, int, bool, bool) ok = (0, 0, true, false), right = (1, 0, false, false);

        m.Open(Menu.Screen.Course, "AKINA_DAY", false, "AE86T", 0);
        Run(0.1f, (0, -1, false, false)); // AKINA (slot 3) up to slot 0, left wraps to the twelfth slot
        Run(0.1f, (-1, 0, false, false));
        Assert.Equal(Menu.Screen.Course, m.Current);
        Run(0.1f, ok);
        Assert.Equal(Menu.Screen.Weather, m.Current); // no route, no time of day
        Assert.NotNull(m.FourPass);
        Run(0.1f, right); // WET
        Run(Menu.Fade + 0.1f, ok);
        Assert.Equal(Menu.Screen.Maker, m.Current);
        Assert.True(m.FourPass!.Wet);
        Run(0.1f, ok);
        Run(Menu.Fade + 0.1f, ok); // model → car
        Run(0.1f, ok); // gearbox
        actions.Clear();
        Run(Menu.Fade + Menu.LoadAt + 0.1f, ok);
        Assert.Single(actions, a => a == Menu.Action.Load);
        Assert.Equal(("AKAGI_NIT", false), (m.CourseTime, m.Reverse)); // night: the wet record, rain over the night course
        Run(Menu.Fade + Menu.IntroEnd + 0.1f);
        Assert.Equal(Menu.Screen.None, m.Current);

        string[] courses = ["AKAGI_NIT", "AKINA_NIT", "HAPPOU_NIT", "IROHA_NIT"];
        for (var stage = 0; stage < 4; stage++)
        {
            Assert.Equal((courses[stage], stage == 2), (m.CourseTime, m.Reverse));
            m.FourPass.Finish(Stage(100 + stage), 0); // the game records the stage before the menu's finish
            m.Finish(new Menu.Run(100 + stage, Stage(100 + stage), new float?[4], null, false, 0));
            Run(Menu.FinishHold + Menu.Fade + Menu.ButtonsAt + 0.1f);
            Assert.Equal(Menu.Screen.Result, m.Current);
            if (stage == 3) break;
            if (stage == 1)
            {
                // the pause's RETRY after stage 1 goes back to AKAGI through the loading screen
                m.Open(Menu.Screen.Pause, courses[stage], false, "AE86T", 0);
                Run(0.1f, right);
                actions.Clear();
                Run(Menu.Fade + Menu.LoadAt + 0.1f, ok);
                Assert.Equal((0, "AKAGI_NIT"), (m.FourPass.Index, m.CourseTime));
                Assert.Contains(Menu.Action.Load, actions);
                Run(Menu.Fade + Menu.IntroEnd + 0.1f);
                m.FourPass.Finish(Stage(100), 0);
                m.Finish(new Menu.Run(100, Stage(100), new float?[4], null, false, 0));
                Run(Menu.FinishHold + Menu.Fade + Menu.ButtonsAt + 0.1f);
                actions.Clear();
                Run(Menu.Fade + Menu.LoadAt + 0.1f, ok); // NEXT
                Assert.Contains(Menu.Action.Load, actions);
                Run(Menu.Fade + Menu.IntroEnd + 0.1f);
                m.FourPass.Finish(Stage(101), 0);
                m.Finish(new Menu.Run(101, Stage(101), new float?[4], null, false, 0));
                Run(Menu.FinishHold + Menu.Fade + Menu.ButtonsAt + 0.1f);
            }
            actions.Clear();
            Run(Menu.Fade + Menu.LoadAt + 0.1f, ok); // NEXT (the first button)
            Assert.Contains(Menu.Action.Load, actions);
            Assert.Equal(stage + 1, m.FourPass.Index);
            Run(Menu.Fade + Menu.IntroEnd + 0.1f);
        }
        Assert.True(m.FourPass.Done);
        Assert.Equal(406, m.FourPass.Total, 3);
        // the final sheet has the Time Attack buttons: RETRY starts at stage 1 again
        actions.Clear();
        Run(Menu.Fade + Menu.LoadAt + 0.1f, ok);
        Assert.Equal((0, 0, "AKAGI_NIT"), (m.FourPass.Index, m.FourPass.Finished, m.CourseTime));
        Assert.Contains(Menu.Action.Load, actions);

        // another slot is plain Time Attack again
        m.Open(Menu.Screen.Course, "AKINA_DAY", false, "AE86T", 0);
        Assert.Null(m.FourPass);
        Assert.Equal("AKINA_DAY", m.CourseTime);
    }
}
