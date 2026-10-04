using System.Numerics;
using Kansei.Physics;
using Touge.Race;
using Touge.Ui;

namespace Touge.Tests;

public class LegendTests
{
    private static Legend.Progress Beat(params IEnumerable<Legend.Entry>[] groups)
    {
        var p = new Legend.Progress();
        foreach (var e in groups.SelectMany(g => g)) p.Add(e.Key, BattleOutcome.Win, 1);
        return p;
    }

    private static IEnumerable<Legend.Entry> Regulars(int slot) => Legend.Of(slot).Where(e => !e.Secret);

    /// <summary>The roster as the ELF tables give it: 34 rivals, figures 0–33 once each, every course slot filled, cars and keys valid.</summary>
    [Fact]
    public void Roster_MatchesTheOriginalTables()
    {
        Assert.Equal(34, Legend.All.Length);
        Assert.Equal(Enumerable.Range(0, 34), Legend.All.Select(e => e.Figure).Order());
        Assert.Equal(Legend.All.Length, Legend.All.Select(e => e.Key).Distinct().Count());
        Assert.All(Legend.All, e => Assert.True(CarSpecs.All.ContainsKey(e.Rival.Car), e.Rival.Car));
        Assert.Equal([4, 4, 4, 5, 4, 5, 2, 1, 1, 2, 2], Enumerable.Range(0, Legend.CourseIds.Length).Select(s => Legend.Of(s).Length));
        Assert.Equal(["AKINA/bunta", "IROHA/takumi"], Legend.All.Where(e => e.Secret).Select(e => e.Key).Order());
        // Kyoko's FD in her own colour, Sakamoto in the wet at night, Kenta in the wet by day (0x2A2D90 bytes 4/5)
        Assert.Equal(3, Legend.Find("AKAGI/kyoko")!.Rival.Paint);
        Assert.True(Legend.Find("USUI0/sakamoto") is { Wet: true, Night: true, Reverse: true });
        Assert.True(Legend.Find("AKAGI/kenta") is { Wet: true, Night: false });
        // the first three rungs of a main course run detuned, the rest stock; the rival's spec carries it
        Assert.Equal([0.8f, 0.88f, 0.95f, 1, 1], Legend.Of(3).Select(e => e.Rival.Power));
        Assert.Equal([1f, 1], Legend.Of(6).Select(e => e.Rival.Power));
        var kenji = Legend.Find("AKINA/kenji")!.Rival;
        Assert.Equal(CarSpecs.All["ONE80"].TorqueNm.Max() * 0.8f, kenji.Spec.TorqueNm.Max(), 3);
        Assert.Same(CarSpecs.All["IMP3"], Legend.Find("AKINA/bunta")!.Rival.Spec);
    }

    /// <summary>A ladder per course: only the first rival is open, each win opens the next; a loss or draw opens nothing.</summary>
    [Fact]
    public void Ladder_OpensOneRivalAfterAnother()
    {
        var p = new Legend.Progress();
        var akina = Legend.Of(3);
        Assert.Equal(["kenji", "iketani", "wataru", "takumi", "bunta"], akina.Select(e => e.Rival.Id));
        Assert.True(Legend.Unlocked(akina[0], p));
        Assert.False(Legend.Unlocked(akina[1], p));
        Assert.False(Legend.Visible(akina[4], p)); // Bunta is not even listed yet
        p.Add(akina[0].Key, BattleOutcome.Lose, -2);
        p.Add(akina[0].Key, BattleOutcome.Draw, 0);
        Assert.False(Legend.Unlocked(akina[1], p));
        Assert.Equal((0, 1), (p.Get(akina[0].Key).Wins, p.Get(akina[0].Key).Losses));
        p.Add(akina[0].Key, BattleOutcome.Win, 3.5f);
        Assert.True(Legend.Unlocked(akina[1], p));
        Assert.False(Legend.Unlocked(akina[2], p));
        Assert.Equal(3.5f, p.Get(akina[0].Key).BestGap);
        Assert.All(Enumerable.Range(0, Legend.MainCourses), s => Assert.True(Legend.CourseOpen(s, p)));
    }

    /// <summary>The additions (MYOGI+ … SHIONA) open once three main courses are cleared.</summary>
    [Fact]
    public void ExtraCourses_OpenAfterThreeMainCourses()
    {
        var p = Beat(Regulars(0), Regulars(1));
        Assert.Equal(2, Legend.ClearedMain(p));
        Assert.False(Legend.CourseOpen(6, p));
        Assert.False(Legend.Unlocked(Legend.Of(6)[0], p));
        p = Beat(Regulars(0), Regulars(1), Regulars(4));
        Assert.True(Legend.CourseOpen(6, p));
        Assert.True(Legend.CourseOpen(10, p));
        Assert.True(Legend.Unlocked(Legend.Of(9)[0], p));
        Assert.False(Legend.Unlocked(Legend.Of(9)[1], p));
        Assert.False(Legend.CourseOpen(11, p)); // the four-pass slot has no course
    }

    /// <summary>
    ///     The original's two conditions (0x1663B0): Bunta once every regular of the six main courses is beaten, Takumi of
    ///     Project D once every other rival but Bunta is; Bunta's Impreza unlocks with his defeat.
    /// </summary>
    [Fact]
    public void SecretRivals_AndTheSecretCar()
    {
        var bunta = Legend.Find("AKINA/bunta")!;
        var takumi = Legend.Find("IROHA/takumi")!;
        var main = Enumerable.Range(0, Legend.MainCourses).SelectMany(Regulars).ToArray();
        var p = Beat(main.SkipLast(1));
        Assert.False(Legend.Unlocked(bunta, p));
        p = Beat(main);
        Assert.True(Legend.Unlocked(bunta, p));
        Assert.True(Legend.Visible(bunta, p));
        Assert.False(Legend.Unlocked(takumi, p)); // the additions are still to beat
        Assert.True(Legend.CarLocked(Legend.SecretCar, p));
        Assert.False(Legend.CarLocked("AE86T", p));
        p = Beat(Legend.All.Where(e => e != bunta && e != takumi));
        Assert.True(Legend.Unlocked(takumi, p));
        Assert.True(Legend.CarLocked(Legend.SecretCar, p));
        p.Add(bunta.Key, BattleOutcome.Win, 0.4f);
        Assert.False(Legend.CarLocked(Legend.SecretCar, p));
    }

    /// <summary>Conditions to load: wet → _RIN (rain only exists by day), night → _NIT; a rematch is wet (win counter → weather byte).</summary>
    [Fact]
    public void Conditions_PickTheCourseVariant()
    {
        string[] all = ["DAY", "NIT", "RIN"], night = ["NIT"];
        var p = new Legend.Progress();
        Assert.Equal(("USUI0_RIN", true), Legend.Conditions(Legend.Find("USUI0/sakamoto")!, all, p));
        Assert.Equal(("AKAGI_RIN", true), Legend.Conditions(Legend.Find("AKAGI/kenta")!, all, p));
        Assert.Equal(("AKINA_NIT", false), Legend.Conditions(Legend.Find("AKINA/wataru")!, all, p));
        Assert.Equal(("AKINA_DAY", false), Legend.Conditions(Legend.Find("AKINA/kenji")!, all, p));
        p.Add("AKINA/kenji", BattleOutcome.Win, 1);
        Assert.Equal(("AKINA_RIN", true), Legend.Conditions(Legend.Find("AKINA/kenji")!, all, p));
        p.Add("HAPPOU/suetsugu", BattleOutcome.Win, 1);
        Assert.Equal(("HAPPOU_NIT", false), Legend.Conditions(Legend.Find("HAPPOU/suetsugu")!, night, p));
    }

    [Fact]
    public void Progress_SavesAndLoads_DroppingUnknownRivals()
    {
        var path = Path.Combine(Path.GetTempPath(), $"legend_{Guid.NewGuid():N}.json");
        try
        {
            var p = new Legend.Progress();
            p.Add("AKINA/kenji", BattleOutcome.Win, -2.25f);
            p.Add("AKINA/iketani", BattleOutcome.Lose, -1);
            p.Rivals["NOWHERE/ghost"] = new Legend.Progress.Record { Wins = 5 };
            p.Save(path);
            var q = Legend.Progress.Load(path);
            Assert.True(q.Beaten("AKINA/kenji"));
            Assert.Equal(2.25f, q.Get("AKINA/kenji").BestGap);
            Assert.Equal(1, q.Get("AKINA/iketani").Losses);
            Assert.False(q.Rivals.ContainsKey("NOWHERE/ghost"));
            File.WriteAllText(path, "{ not json");
            Assert.Empty(Legend.Progress.Load(path).Rivals);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Catalog TestCatalog()
    {
        Vector2[] line = [new(0, 0), new(100, 0), new(100, 100)];
        return new Catalog(
            [.. Legend.CourseIds.Select(id => new Catalog.Course(id, id, ["DAY", "NIT", "RIN"], line, 5000, 300, true, id.EndsWith('0')))],
            [
                new Catalog.Car("AE86T", "TOYOTA", "TRUENO GT-APEX [AE86]", "FR", 130, 940, [1]),
                new Catalog.Car("IMP3", "SUBARU", "IMPREZA WRX type R STi Version V [GC8]", "4WD", 280, 1230, [1]),
                new Catalog.Car("IMP", "SUBARU", "IMPREZA WRX STi Version VI [GC8]", "4WD", 280, 1270, [1]),
            ]);
    }

    /// <summary>
    ///     Screens: course grid (locked addition beeps) → ladder (locked rival beeps, cursor on the first open one) → VS card
    ///     (rival's theme) → Challenge after the fade; back walks card → ladder → grid → main menu.
    /// </summary>
    [Fact]
    public void Screens_StepsSoundsAndActions()
    {
        var sounds = new List<string>();
        var actions = new List<LegendScreen.Action>();
        var l = new LegendScreen(TestCatalog(), new Legend.Progress()) { Sound = sounds.Add };
        void Run(float seconds, (int, int, bool, bool) k = default)
        {
            actions.Add(l.Update(k, 1 / 60f));
            for (var t = 1 / 60f; t < seconds; t += 1 / 60f) actions.Add(l.Update(default, 1 / 60f));
        }
        (int, int, bool, bool) ok = (0, 0, true, false), back = (0, 0, false, true), down = (0, 1, false, false);

        l.Open(LegendScreen.Step.Course);
        Assert.Equal("TOKYO.adx", l.Music);
        Run(0.1f, (0, -1, false, false)); // up from MYOGI → MOMIJI LINE (slot 9)
        Run(0.1f, (1, 0, false, false)); // → SHIONA (10)
        Run(0.1f, (1, 0, false, false)); // the grid's 12th cell is empty: wraps to MYOGI (0)
        Run(0.1f, ok);
        Assert.Equal((LegendScreen.Step.Rivals, "MYOUGI0"), (l.Current, l.Selected.CourseId));
        Run(0.1f, back);
        sounds.Clear();
        Run(0.1f, (0, 2, false, false)); // MYOGI+: locked
        Run(0.1f, ok);
        Assert.Equal(["SYS005", "BEEP001"], sounds);
        Assert.Equal(LegendScreen.Step.Course, l.Current);
        Run(0.1f, (0, -1, false, false)); // AKINA
        Run(0.1f, ok);
        Assert.Equal(LegendScreen.Step.Rivals, l.Current);
        Assert.Equal("AKINA/kenji", l.Selected.Key);
        Run(0.1f, down);
        Run(0.1f, ok); // Iketani: locked
        Assert.Equal("BEEP001", sounds[^1]);
        Run(0.1f, (0, -1, false, false));
        actions.Clear();
        Run(LegendScreen.Fade + 0.1f, ok); // the rival's course loads behind the fade
        Assert.Equal([LegendScreen.Action.PreviewRival], actions.Where(a => a != LegendScreen.Action.None));
        Assert.Equal((LegendScreen.Step.Card, "KENJI.adx"), (l.Current, l.Music));
        Assert.True(l.ShowsCar);
        Run(0.4f, back);
        Assert.Equal(LegendScreen.Step.Rivals, l.Current);
        Run(LegendScreen.Fade + 0.1f, ok);
        Run(0.4f); // the card takes a moment before deciding counts
        actions.Clear();
        Run(LegendScreen.Fade + 0.1f, ok);
        Assert.Contains(LegendScreen.Action.Challenge, actions);
        Assert.False(l.Active);

        // after a won battle the ladder opens on the next rival, tagged as news; back twice leaves to the main menu
        l.Progress.Add("AKINA/kenji", BattleOutcome.Win, 2);
        l.Open(LegendScreen.Step.Rivals, "AKINA/kenji");
        Assert.Equal("AKINA/iketani", l.Selected.Key);
        l.Open(LegendScreen.Step.Card, "AKINA/kenji"); // the card shows exactly the rival asked for, beaten or not
        Assert.Equal(("AKINA/kenji", "KENJI.adx"), (l.Selected.Key, l.Music));
        l.Open(LegendScreen.Step.Rivals, "AKINA/kenji");
        Run(0.1f, back);
        Assert.Equal(LegendScreen.Step.Course, l.Current);
        actions.Clear();
        Run(LegendScreen.Fade + 0.1f, back);
        Assert.Contains(LegendScreen.Action.Exit, actions);
        Assert.False(l.Active);
    }

    /// <summary>
    ///     In a Legend battle the menus lead back to the ladder (backing out of the car select, the result's RIVAL SELECT)
    ///     and a locked car cannot be picked.
    /// </summary>
    [Fact]
    public void Menu_LegendRoutingAndLockedCar()
    {
        var sounds = new List<string>();
        var actions = new List<Menu.Action>();
        var m = new Menu(TestCatalog(), new Settings()) { Sound = sounds.Add, Legend = true, CarLocked = id => id == "IMP3" };
        void Run(float seconds, (int, int, bool, bool) k = default)
        {
            actions.Add(m.Update(k, 1 / 60f));
            for (var t = 1 / 60f; t < seconds; t += 1 / 60f) actions.Add(m.Update(default, 1 / 60f));
        }
        (int, int, bool, bool) ok = (0, 0, true, false), back = (0, 0, false, true);

        m.Open(Menu.Screen.Maker, "AKINA_NIT", true, "IMP3", 0); // a saved locked car falls back to an open one
        Assert.Equal("AE86T", m.CarId);
        Run(0.1f, (0, 5, false, false)); // SUBARU
        Run(0.1f, ok);
        sounds.Clear();
        Run(0.1f, ok); // IMP3 is the first SUBARU: locked
        Assert.Equal(["BEEP001"], sounds);
        Assert.Equal(Menu.Screen.Maker, m.Current);
        Run(0.1f, back); // out of the model list
        actions.Clear();
        Run(Menu.Fade + 0.1f, back);
        Assert.Contains(Menu.Action.Rivals, actions);
        Assert.Equal(Menu.Screen.None, m.Current);

        m.Finish(new Menu.Run(200, [50, 100, 150, 200], [null, null, null, null], null, false, 0));
        Run(Menu.FinishHold + Menu.Fade + 0.1f);
        Run(Menu.ButtonsAt);
        Run(0.1f, (1, 0, false, false)); // REPLAY
        Run(0.1f, (1, 0, false, false)); // RIVAL SELECT (in place of COURSE SELECT)
        actions.Clear();
        Run(Menu.Fade + 0.1f, ok);
        Assert.Contains(Menu.Action.Rivals, actions);
    }
}
