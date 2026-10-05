using System.Numerics;
using Touge.Race;
using Touge.Ui;

namespace Touge.Tests;

public class FreeBattleTests
{
    private static Catalog TestCatalog() => new(
        [
            new Catalog.Course("AKINA", "AKINA", ["DAY", "NIT", "RIN"], [Vector2.Zero, Vector2.One], 7400, 300, true, false),
            new Catalog.Course("IROHA", "IROHAZAKA", ["DAY", "NIT"], [Vector2.Zero, Vector2.One], 4900, 200, true, false),
        ],
        [
            new Catalog.Car("AE86T", "TOYOTA", "TRUENO", "FR", 130, 940, [1, 2, 3]),
            new Catalog.Car("FD3S", "MAZDA", "RX-7", "FR", 255, 1260, [4, 5]),
        ]);

    private static readonly (int, int, bool, bool) Ok = (0, 0, true, false), Back = (0, 0, false, true), Down = (0, 1, false, false), Up = (0, -1, false, false),
        Right = (1, 0, false, false), Left = (-1, 0, false, false);

    /// <summary>Rows, values and wrapping: WHO LEADS only for lead/chase, a new course keeps WET only where it has rain, rivals and levels wrap.</summary>
    [Fact]
    public void Lobby_Values()
    {
        var sounds = new List<string>();
        var f = new FreeBattle(TestCatalog());
        f.Open(new FreeBattleChoice { Course = "AKINA_RIN", Rival = "takumi" }, "FD3S", 1, false);
        Assert.Contains(FreeBattle.Row.Lead, f.Rows);
        Assert.Equal(FreeBattle.Row.Course, f.Selected);
        f.Update(Right, sounds.Add); // IROHAZAKA has no WET: its first condition
        Assert.Equal(("IROHA_DAY", false), (f.Choice.Course, f.Choice.Fog));
        f.Update(Down, sounds.Add);
        f.Update(Down, sounds.Add);
        f.Update(Left, sounds.Add); // conditions wrap: NIGHT FOG
        Assert.Equal(("IROHA_NIT", true), (f.Choice.Course, f.Choice.Fog));
        f.Update(Down, sounds.Add);
        f.Update(Ok, sounds.Add); // RULE: RACE, the WHO LEADS row goes
        Assert.Equal(BattleRule.Race, f.Choice.Rule);
        Assert.DoesNotContain(FreeBattle.Row.Lead, f.Rows);
        f.Update(Down, sounds.Add);
        Assert.Equal(FreeBattle.Row.Level, f.Selected);
        f.Update(Left, sounds.Add);
        Assert.Equal(AiLevel.Easy, f.Choice.Level);
        f.Update(Down, sounds.Add);
        f.Update(Right, sounds.Add); // takumi → bunta → wraps to itsuki
        f.Update(Right, sounds.Add);
        Assert.Equal("itsuki", f.Choice.Rival);
        f.Update(Down, sounds.Add);
        f.Update(Right, sounds.Add); // car wraps, paint back to the first
        Assert.Equal(("AE86T", 0), (f.CarId, f.Paint));
        for (var i = 0; i < 9; i++) f.Update(Down, sounds.Add); // clamps on START
        Assert.Equal(FreeBattle.Row.Start, f.Selected);
        Assert.Equal(FreeBattle.Result.None, f.Update(Right, sounds.Add));
        Assert.Equal(FreeBattle.Result.Start, f.Update(Ok, sounds.Add));
        Assert.Equal(FreeBattle.Result.Back, f.Update(Back, sounds.Add));
        Assert.Equal(["SYS006", "BEEP001"], sounds.TakeLast(2));
        f.Update(Up, sounds.Add);
        Assert.Equal(FreeBattle.Row.Gearbox, f.Selected);
        f.Update(Ok, sounds.Add);
        Assert.True(f.Manual);
    }

    /// <summary>A locked car (IMP3 before it is won) is never offered: the saved one falls back, stepping skips it.</summary>
    [Fact]
    public void Lobby_SkipsLockedCar()
    {
        var f = new FreeBattle(TestCatalog()) { CarLocked = id => id == "FD3S" };
        f.Open(new FreeBattleChoice { Rival = "takumi" }, "FD3S", 1, false);
        Assert.Equal("AE86T", f.CarId);
        while (f.Selected != FreeBattle.Row.Car) f.Update(Down, _ => { });
        f.Update(Right, _ => { });
        Assert.Equal("AE86T", f.CarId);
    }

    /// <summary>A hand-edited or old choice: unknown course, rival and a fog the course cannot have fall back to valid values.</summary>
    [Fact]
    public void Open_FallsBack()
    {
        var f = new FreeBattle(TestCatalog());
        f.Open(new FreeBattleChoice { Course = "NOWHERE_DAY", Rival = "nobody", Fog = true, Level = (AiLevel)7 }, "XX", 9, true);
        Assert.Equal(("AKINA_DAY", false, "itsuki", AiLevel.Normal), (f.Choice.Course, f.Choice.Fog, f.Choice.Rival, f.Choice.Level));
        Assert.Equal(("AE86T", 2), (f.CarId, f.Paint));
        f.Open(new FreeBattleChoice { Course = "AKINA_RIN", Fog = true }, "AE86T", 0, false);
        Assert.Equal(("AKINA_DAY", false), (f.Choice.Course, f.Choice.Fog)); // no fog over the rain course
    }

    /// <summary>
    ///     AI level onto a skill band (the character placed within it), rubber band and mistakes; the leader only for
    ///     lead/chase; the rival's theme as on Legend's VS card.
    /// </summary>
    [Fact]
    public void Setup_MapsLevelRuleAndLeader()
    {
        var takumi = Rivals.Find("takumi");
        var easy = FreeBattle.Setup(new FreeBattleChoice { Rival = "takumi", Level = AiLevel.Easy, Rule = BattleRule.LeadChase, PlayerLeads = true });
        Assert.Equal((true, 0, BattleRule.LeadChase), (easy.RubberBand, easy.Leader, easy.Rule));
        Assert.InRange(easy.Rival.Style.Skill, 0.17f, 0.2f); // near the top of EASY: the best character
        Assert.Equal(FreeBattle.EasyPower, easy.Rival.Power); // and his engine detuned
        Assert.Equal((takumi.Car, takumi.Style.Drift), (easy.Rival.Car, easy.Rival.Style.Drift)); // his car and style stay
        var hard = FreeBattle.Setup(new FreeBattleChoice { Rival = "takumi", Level = AiLevel.Hard, Rule = BattleRule.Race, PlayerLeads = true });
        Assert.Equal((true, 0.015f, 0f, 1), (hard.RubberBand, hard.BandUp, hard.BandDown, hard.Leader)); // catch-up only; a race has no leader choice
        Assert.InRange(hard.Rival.Style.Skill, 0.8f, 0.85f);
        var legend = FreeBattle.Setup(new FreeBattleChoice { Rival = "takumi", Level = AiLevel.Legend });
        Assert.Equal((false, 0.5f), (legend.RubberBand, legend.Mistakes));
        Assert.InRange(legend.Rival.Style.Skill, 0.98f, 1f);
        var normal = FreeBattle.Setup(new FreeBattleChoice { Rival = "itsuki" });
        Assert.Equal((0.35f, true, 1, 1f), (normal.Rival.Style.Skill, normal.RubberBand, normal.Leader, normal.Mistakes)); // the weakest: NORMAL's bottom
        // one scale: every character's level bands meet without overlap, in order
        foreach (var r in Rivals.All)
            Assert.True(FreeBattle.SkillAt(AiLevel.Easy, r.Style.Skill) < FreeBattle.SkillAt(AiLevel.Normal, r.Style.Skill)
                        && FreeBattle.SkillAt(AiLevel.Normal, r.Style.Skill) <= FreeBattle.SkillAt(AiLevel.Hard, r.Style.Skill)
                        && FreeBattle.SkillAt(AiLevel.Hard, r.Style.Skill) < FreeBattle.SkillAt(AiLevel.Legend, r.Style.Skill), r.Id);
        Assert.Equal("TAKUMI02.adx", FreeBattle.Theme(takumi)); // his Akina Trueno
        Assert.All(Rivals.All, r => Assert.EndsWith(".adx", FreeBattle.Theme(r)));
        Assert.DoesNotContain(Rivals.All, r => FreeBattle.Theme(r) == "TOKYO.adx"); // every rival has his own theme
    }

    /// <summary>The lobby's choice survives settings.json; an old file without it (or null) gets the defaults.</summary>
    [Fact]
    public void Settings_RememberChoice()
    {
        var s = new Settings { FreeBattle = new FreeBattleChoice { Course = "IROHA_NIT", Reverse = true, Fog = true, Rival = "kai", Rule = BattleRule.Race, PlayerLeads = true, Level = AiLevel.Hard } };
        var back = Settings.FromJson(s.ToJson()).FreeBattle;
        Assert.Equal(("IROHA_NIT", true, true, "kai", BattleRule.Race, true, AiLevel.Hard), (back.Course, back.Reverse, back.Fog, back.Rival, back.Rule, back.PlayerLeads, back.Level));
        Assert.Contains("\"LeadChase\"", new Settings().ToJson()); // enums as names
        Assert.Equal("keisuke", Settings.FromJson("{}").FreeBattle.Rival);
        Assert.NotNull(Settings.FromJson("{\"FreeBattle\": null}").FreeBattle);
    }

    /// <summary>VERSUS: the third tile VS CPU asks for the lobby; START and BACK come back as actions, BACK on the tiles again.</summary>
    [Fact]
    public void Versus_VsCpuTile()
    {
        var v = new Versus(TestCatalog());
        v.Open();
        Assert.Equal(Versus.Action.Cpu, Step(v, Left, Ok)); // left from SPLIT SCREEN wraps to VS CPU
        v.OpenCpu(new FreeBattleChoice(), "AE86T", 0, false);
        Assert.Equal(Versus.Screen.Cpu, v.Current);
        Assert.Equal("KEISUKE01.adx", v.Music);
        for (var i = 0; i < 10; i++) Step(v, Down);
        Assert.Equal(Versus.Action.CpuStart, Step(v, Ok));
        Assert.Equal(Versus.Action.CpuLeave, Step(v, Back));
        Assert.Equal(Versus.Screen.Mode, v.Current);
    }

    private static Versus.Action Step(Versus v, params (int, int, bool, bool)[] keys)
    {
        var a = Versus.Action.None;
        for (var i = 0; i < 8; i++) v.Update(default, default, new Versus.TextKeys("", false, false, false), 1 / 20f); // past the half fade
        foreach (var k in keys)
            if (v.Update(k, default, new Versus.TextKeys("", false, false, false), 1 / 20f) is var x and not Versus.Action.None)
                a = x;
        return a;
    }

    /// <summary>Free battle result: RETRY / REPLAY / CHANGE SETTINGS / EXIT, CHANGE SETTINGS and pause Exit back to the lobby, Legend's result music.</summary>
    [Fact]
    public void Menu_FreeBattleRouting()
    {
        var actions = new List<Menu.Action>();
        var m = new Menu(TestCatalog(), new Settings()) { FreeBattle = true };
        void Run(float seconds, (int, int, bool, bool) k = default)
        {
            actions.Add(m.Update(k, 1 / 60f));
            for (var t = 1 / 60f; t < seconds; t += 1 / 60f) actions.Add(m.Update(default, 1 / 60f));
        }
        m.Battle = new BattleReport(BattleOutcome.Win, "OVERTAKE", BattleRule.LeadChase, false, "TAKUMI FUJIWARA", "PROJECT D", "TRUENO", 1, 10, 60, null, null, 1, 1, 0, 0);
        m.Finish(new Menu.Run(100, [25, 50, 75, 100], [null, null, null, null], null, false, 0));
        Run(Menu.FinishHold + Menu.Fade + 0.1f);
        Assert.Equal(Menu.Screen.Result, m.Current);
        Assert.Equal("R_WIN01.adx", m.Music(null));
        Run(Menu.ButtonsAt);
        Run(0.1f, Right);
        Run(0.1f, Right);
        Run(0.1f, Right);
        Run(0.1f, Right); // clamps on EXIT (4 buttons)
        Run(0.1f, Left); // CHANGE SETTINGS
        actions.Clear();
        Run(Menu.Fade + 0.1f, Ok);
        Assert.Contains(Menu.Action.Rivals, actions);
        m.Open(Menu.Screen.Pause, "AKINA_DAY", false, "AE86T", 0);
        for (var i = 0; i < 4; i++) Run(0.1f, Right); // Exit
        actions.Clear();
        Run(Menu.Fade + 0.1f, Ok);
        Assert.Contains(Menu.Action.Rivals, actions);
    }
}
