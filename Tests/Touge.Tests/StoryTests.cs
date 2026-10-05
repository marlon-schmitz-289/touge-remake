using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Touge.Formats;
using Touge.Race;
using Touge.Story;
using Touge.Ui;

namespace Touge.Tests;

public class StoryTests
{
    private static readonly Encoding Sjis = GetSjis();

    private static Encoding GetSjis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    /// <summary>A ROBJ script as on the disc: header, the pointer at 0x18 to a 12-byte block, then the NUL-separated tokens.</summary>
    private static byte[] Robj(params string[] tokens)
    {
        var pool = tokens.SelectMany(t => Sjis.GetBytes(t).Append((byte)0)).ToArray();
        var d = new byte[0x40 + 12 + pool.Length];
        "ROBJ"u8.CopyTo(d);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(0x18), 0x40);
        pool.CopyTo(d, 0x40 + 12);
        return d;
    }

    /// <summary>
    ///     Utterances end at waits, page breaks and speaker changes ('\n' is a line break inside a balloon, several balloons up
    ///     to the next wait are one utterance), parts at fades (E_n, empty parts dropped), the font name ends the script.
    /// </summary>
    [Fact]
    public void ParseScript_UtterancesPartsAndSpeakers()
    {
        var d = Robj("P_10", "C_0", "A_24", "ずいぶん走りこんだな…", "オレもう\nガスねーや\n", "WF_191", "F_走り屋", "もうすぐ四時ですよ\n", "W_0", "N", "K_0",
            "E_60", "E_60", "P_13", "（信じられん…）", "W_0", "N", "F_啓介", "またな", "E_60", "KST02", "あいうえお");
        var parts = StoryScript.ParseScript(d);
        Assert.Equal(2, parts.Count);
        Assert.Equal([new("", "ずいぶん走りこんだな…オレもうガスねーや"), new("走り屋", "もうすぐ四時ですよ")], parts[0]);
        Assert.Equal([new("", "（信じられん…）"), new("啓介", "またな")], parts[1]);
        Assert.Throws<InvalidDataException>(() => StoryScript.ParseScript(new byte[64]));
        // voice track time of each utterance: last A_/WF_ before it, in 1/30 s; a page without its own A_ at least 2 s on
        Assert.Equal([[24 / 30.0, 191 / 30.0], [0, 2]], StoryScript.Times(d));
    }

    /// <summary>The chapter table: 8-byte selection, 22-byte objective, driver through the pointer table, opponent by figure (−1 = none).</summary>
    [Fact]
    public void ReadChapters_FromTheElfTables()
    {
        int At(int address) => address - StoryScript.ElfBase;
        var elf = new byte[At(0x2A3800)];
        var heroRecord = At(0x2A36F0);
        elf[heroRecord + 1] = 23; // FD3S
        for (var n = 0; n < StoryScript.Chapters; n++)
        {
            var sel = elf.AsSpan(At(0x2A3290) + 8 * n);
            (sel[0], sel[2], sel[3], sel[5]) = ((byte)n, 3, (byte)(n & 1), 1);
            sel[6] = n == 0 ? (byte)0xFF : (byte)2;
            var obj = elf.AsSpan(At(0x2A33C0) + 22 * n);
            obj[14] = 5;
            BinaryPrimitives.WriteUInt16LittleEndian(obj[16..], 220);
            BinaryPrimitives.WriteInt32LittleEndian(elf.AsSpan(At(0x2A3770) + 4 * n), 0x2A36F0);
        }
        elf[At(0x2A2EA0) + 16 * 2 + 1] = 7; // figure 2: R32
        var c = StoryScript.ReadChapters(elf);
        Assert.Equal(31, c.Length);
        Assert.Equal(new StoryScript.Chapter(0, 3, false, false, true, 5, 220, 23, -1), c[0]);
        Assert.Equal(new StoryScript.Chapter(1, 3, true, false, true, 5, 220, 23, 7), c[1]);
        elf[At(0x2A3290) + 8 * 4] = 9; // a table that does not count up is not the chapter table
        Assert.Throws<InvalidDataException>(() => StoryScript.ReadChapters(elf));
    }

    [Fact]
    public void Goals_FromTheObjectiveCodes()
    {
        Assert.Equal(Goal.Delivery, StoryRules.Of(7, false));
        Assert.Equal(Goal.TimeLimit, StoryRules.Of(5, false));
        Assert.Equal(Goal.TimeLimit, StoryRules.Of(5, true)); // chapter 5: Kenji's car listed, a race against the clock
        Assert.Equal(Goal.Thrill, StoryRules.Of(8, false));
        Assert.Equal(Goal.Race, StoryRules.Of(1, true));
        Assert.Equal(Goal.Race, StoryRules.Of(13, true));
        Assert.Equal(Goal.Escape, StoryRules.Of(2, true));
        Assert.Equal(Goal.Chase, StoryRules.Of(3, true));
        Assert.Equal(Goal.Chase, StoryRules.Of(9, true));
        Assert.Equal(Goal.ChaseInTime, StoryRules.Of(6, true));
        Assert.Equal(Goal.Survive, StoryRules.Of(4, true));
        var r = StoryRivals.Find("keisuke", "FD3S", 2);
        Assert.True(r.Style.Skill <= StoryRules.RivalSkill(2));
        Assert.Equal("FD3S", r.Car);
        Assert.Equal(BattleRule.LeadChase, StoryRules.Battle(Goal.Escape, 0, r)!.Rule);
        Assert.Equal(0, StoryRules.Battle(Goal.Escape, 0, r)!.Leader);
        Assert.Equal(new BattleTerms(Breakaway: 4, TimeLimit: 100, TimeLimitWinner: 0), StoryRules.Battle(Goal.Survive, 100, r)!.Terms);
        Assert.Null(StoryRules.Battle(Goal.TimeLimit, 220, r));
    }

    /// <summary>A time limit: still undecided at the limit, the named side wins (survive = the player, pass-in-time = the rival).</summary>
    [Fact]
    public void BattleTimeLimit_DecidesForTheNamedSide()
    {
        var survive = new Battle(BattleRule.Race, 10000) { TimeLimit = 2, TimeLimitWinner = 0, Breakaway = 4 };
        var chase = new Battle(BattleRule.LeadChase, 10000) { TimeLimit = 2, TimeLimitWinner = 1 };
        for (var t = 0f; t < 3; t += 0.01f)
        {
            survive.Update(0.01f, 5 * t, 6 * t); // a little behind, never 4 s
            chase.Update(0.01f, 5 * t, 5 * t + 10); // never gets past
        }
        Assert.Equal((BattleOutcome.Win, "TIME"), (survive.Outcome, survive.Reason));
        Assert.Equal((BattleOutcome.Lose, "TIME"), (chase.Outcome, chase.Reason));
        Assert.InRange(survive.DecidedAt, 2, 2.02f);
    }

    [Fact]
    public void SoloJudge_TimeLimitTofuAndDrift()
    {
        var time = new SoloJudge(Goal.TimeLimit, 10);
        time.Update(true, false, 9, false, 0, 0.1f);
        Assert.Equal(BattleOutcome.None, time.Outcome);
        time.Update(true, false, 10.1f, false, 0, 0.1f);
        Assert.Equal((BattleOutcome.Lose, "TIME UP"), (time.Outcome, time.Reason));

        var made = new SoloJudge(Goal.TimeLimit, 10);
        made.Update(false, true, 9.5f, false, 0, 0.1f);
        Assert.Equal((BattleOutcome.Win, "CLEAR"), (made.Outcome, made.Reason));

        // tofu: a scrape over many ticks is one hit; four hits are too many
        var tofu = new SoloJudge(Goal.Delivery, 0);
        for (var hit = 0; hit < 4; hit++)
        {
            for (var i = 0; i < 20; i++) tofu.Update(true, false, 1, true, 0, 0.01f);
            for (var i = 0; i < 60; i++) tofu.Update(true, false, 1, false, 0, 0.01f);
            Assert.Equal(hit + 1, tofu.WallHits);
        }
        Assert.Equal((BattleOutcome.Lose, "TOO MANY HITS"), (tofu.Outcome, tofu.Reason));

        var thrill = new SoloJudge(Goal.Thrill, 100);
        thrill.Update(false, true, 200, false, StoryRules.ThrillPoints - 1, 0.01f);
        Assert.Equal((BattleOutcome.Lose, "NOT ENOUGH"), (thrill.Outcome, thrill.Reason));
    }

    [Fact]
    public void Progress_ClearsCountsAndRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"progress_{Guid.NewGuid():N}.json");
        try
        {
            var p = new Progress();
            Assert.True(p.Clear("story/03"));
            Assert.False(p.Clear("story/03"));
            p.Add("story/03/tries");
            p.Add("story/03/tries");
            p.Save(path);
            var q = Progress.Load(path);
            Assert.True(q.IsCleared("story/03"));
            Assert.Equal(2, q.Count("story/03/tries"));
            File.WriteAllText(path, "{ \"Version\": 1 }"); // older or hand-edited file: no collections
            Assert.Empty(Progress.Load(path).Cleared);
            File.WriteAllText(path, "not json");
            Assert.Empty(Progress.Load(path).Cleared);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Every chapter has English: two or three parts, "SPEAKER|text" lines, a rival the roster knows (or none).</summary>
    [Fact]
    public void Text_EveryChapterComplete()
    {
        Assert.Equal(StoryScript.Chapters, StoryText.Chapters.Length);
        foreach (var c in StoryText.Chapters)
        {
            Assert.InRange(c.Scene.Length, 2, 3);
            Assert.All(c.Scene.SelectMany(p => p), l => Assert.Matches(@"^[A-Z0-9 &]+\|\S", l));
            if (c.Rival != null) StoryRivals.Find(c.Rival, "AE86T", 0);
        }
        Assert.Equal([0, 19, 24], StoryText.Parts.Select(p => p.First));
    }

    private static (StoryMode Story, List<string> Sounds) NewStory()
    {
        Vector2[] line = [new(0, 0), new(100, 0)];
        var catalog = new Catalog([new Catalog.Course("AKINA", "AKINA", ["DAY", "NIT"], line, 7700, 465, true, false)],
            [new Catalog.Car("AE86T", "TOYOTA", "TRUENO", "FR", 130, 940, [1]), new Catalog.Car("FD3S", "MAZDA", "RX-7", "FR", 280, 1260, [1])]);
        var sounds = new List<string>();
        var chapters = Enumerable.Range(0, 31).Select(n => new StoryScript.Chapter(n, 3, false, false, true, n == 0 ? 5 : 1, 220, 0, n == 0 ? -1 : 23)).ToArray();
        var s = new StoryMode(catalog) { Sound = sounds.Add };
        s.Enter(chapters);
        return (s, sounds);
    }

    /// <summary>
    ///     Select → loading asks for the load → scene → the race; a loss offers a retry; a win plays the scene after it, saves the
    ///     progress, opens the next chapter and selects it; locked chapters beep; BACK in the scene before the race returns to the select, RIGHT skips it; back leaves to the main menu.
    /// </summary>
    [Fact]
    public void Flow_SelectSceneRaceResultProgress()
    {
        var (s, sounds) = NewStory();
        (int, int, bool, bool) ok = (0, 0, true, false), back = (0, 0, false, true), down = (0, 1, false, false);
        var actions = new List<StoryMode.Action>();
        StoryMode.Action Run(float seconds, (int, int, bool, bool) k = default)
        {
            var first = s.Update(k, 1 / 60f);
            actions.Add(first);
            for (var t = 1 / 60f; t < seconds; t += 1 / 60f) actions.Add(s.Update(default, 1 / 60f));
            return first;
        }
        Assert.Equal(0, s.Chapter);
        Assert.Equal("WORRY.adx", s.Music);
        Run(0.1f, down);
        Run(0.1f, ok); // chapter 2 is locked
        Assert.Equal("BEEP001", sounds[^1]);
        Run(0.1f, (0, -1, false, false));
        Run(1.5f, ok);
        Assert.Equal(StoryMode.Phase.Loading, s.Current);
        Assert.Single(actions, StoryMode.Action.Load);
        s.Loaded();
        Assert.True(s.InRun);
        Assert.Equal(StoryMode.Phase.Scene, s.Current);
        Assert.StartsWith("STORY_ST", s.Music);
        Run(0.1f, back); // BACK before the race: out of the chapter
        Run(1);
        Assert.Equal(StoryMode.Action.Leave, actions.Last(a => a != StoryMode.Action.None));
        Assert.Equal(StoryMode.Phase.Select, s.Current);
        Assert.False(s.InRun);
        Run(1.5f, ok);
        s.Loaded();
        Assert.Equal(StoryMode.Action.Race, Run(0.1f, (1, 0, false, false))); // RIGHT: skip to the race
        Assert.Equal(StoryMode.Phase.Racing, s.Current);
        Assert.False(s.Active);

        s.Finish(BattleOutcome.Lose, "TIME UP", null, coast: false);
        Assert.True(s.Freezes); // failed on the way: the game holds
        Assert.Equal("TIMEUP.adx", s.Music);
        Run(4.3f);
        Assert.Equal(StoryMode.Phase.Result, s.Current);
        Assert.Equal(StoryMode.Action.Retry, Run(0.1f, ok)); // RETRY
        Assert.Equal(StoryMode.Phase.Racing, s.Current);

        s.Finish(BattleOutcome.Win, "CLEAR", null, coast: true);
        Assert.False(s.Freezes); // the car runs out under the banner
        Run(4);
        Run(0.7f);
        Assert.Equal(StoryMode.Action.Save, Run(0.1f, ok)); // CONTINUE: the scene after the race
        Assert.True(s.Cleared(0));
        Assert.Equal(2, s.Progress.Count("story/00/tries"));
        Assert.Equal(StoryMode.Phase.Scene, s.Current);
        Run(0.1f, back);
        Run(1);
        Assert.Contains(StoryMode.Action.Leave, actions);
        Assert.Equal(StoryMode.Phase.Select, s.Current);
        Assert.Equal(1, s.Chapter);
        Assert.True(s.Unlocked(1));
        Assert.False(s.InRun);
        Run(0.1f, back);
        Run(1);
        Assert.Equal(StoryMode.Action.Exit, actions.Last(a => a != StoryMode.Action.None));
        Assert.False(s.Open);
    }
}
