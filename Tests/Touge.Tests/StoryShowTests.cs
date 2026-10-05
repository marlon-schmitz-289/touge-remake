using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Touge.Formats;
using Touge.Race;
using Touge.Story;
using Touge.Ui;

namespace Touge.Tests;

/// <summary>The original's story presentation: script staging, manga timelines, lip sync, the show clock and controls, the chapter's show order.</summary>
public class StoryShowTests
{
    private static readonly Encoding Sjis = GetSjis();

    private static Encoding GetSjis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

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
    ///     Staging: portraits, who talks and the utterances at the cue in effect (1/30 s); pages counted by N (an empty page
    ///     too, as the lip lists number them), a page starts at its first balloon; a cue earlier than the last passes at once.
    /// </summary>
    [Fact]
    public void Staging_PicturesTalkPagesLines()
    {
        var d = Robj("P_10", "C_0", "A_30", "一", "W_0", "N", "K_0", "A_90", "N", "A_60", "P_11", "C_1", "二", "WF_150", "三", "W_0", "N", "E_60",
            "P_12", "A_15", "四", "E_60", "KST02");
        var stage = StoryScript.Staging(d);
        Assert.Equal(2, stage.Count);
        Assert.Equal(
        [
            new(0, StoryScript.Step.Picture, 10), new(0, StoryScript.Step.Talk, 0), new(1, StoryScript.Step.Page, 0), new(1, StoryScript.Step.Line, 0),
            new(1, StoryScript.Step.Quiet, 0), new(3, StoryScript.Step.Picture, 11), new(3, StoryScript.Step.Talk, 1), new(3, StoryScript.Step.Page, 2),
            new(3, StoryScript.Step.Line, 1), new(5, StoryScript.Step.Line, 2), new(5, StoryScript.Step.End, 0),
        ], stage[0]);
        Assert.Equal([new(0, StoryScript.Step.Picture, 12), new(0.5, StoryScript.Step.Page, 0), new(0.5, StoryScript.Step.Line, 0), new(0.5, StoryScript.Step.End, 0)], stage[1]);
        Assert.Equal([[1, 3, 5], [0.5]], StoryScript.Times(d)); // A_60 after A_90 waits for nothing
        Assert.Equal(3, StoryScript.ParseScript(d)[0].Count);
    }

    /// <summary>
    ///     A page without an A_ of its own (W_0 | N chains, STR25 pages 9–12) starts when the last page's lip string or W_n
    ///     (60-Hz frames) is over, at least 2 s after the last utterance; an A_ page keeps its cue.
    /// </summary>
    [Fact]
    public void Times_UncuedPagesFollowTheLipStrings()
    {
        var d = Robj("P_1", "C_0", "A_30", "一", "W_0", "N", "二", "W_0", "N", "三", "W_0", "N", "A_600", "四", "W_200", "N", "五", "W_0", "N", "六", "E_60", "KST02");
        string[][] lips = [[new string('1', 180), "", ""], .. Enumerable.Repeat(Array.Empty<string>(), Manga.Slots - 1)];
        Assert.Equal([[1, 4, 6, 20, 70 / 3.0, 76 / 3.0]], StoryScript.Times(d, lips));
        Assert.Equal([[1, 3, 5, 20, 70 / 3.0, 76 / 3.0]], StoryScript.Times(d)); // no lip strings: the W_n and the 2 s
        Assert.Equal(StoryScript.Times(d, lips)[0].Take(3), StoryScript.Staging(d, lips)[0].Where(x => x.Step == StoryScript.Step.Page).Select(x => x.Time).Take(3));
    }

    /// <summary>Lip sync: one digit per 60-Hz frame from the page's first balloon, for the faces talking (C_), closed (0) for the others and after the string.</summary>
    [Fact]
    public void PortraitScene_MouthFromTheLipDigits()
    {
        StoryScript.Stage[] stages =
        [
            new(0, StoryScript.Step.Picture, 10), new(0, StoryScript.Step.Talk, 1), new(1, StoryScript.Step.Page, 0), new(1, StoryScript.Step.Line, 0),
            new(2, StoryScript.Step.Quiet, 1), new(3, StoryScript.Step.Picture, 12), new(3, StoryScript.Step.Talk, 0), new(3, StoryScript.Step.Page, 2),
        ];
        var s = new PortraitScene(stages, ["012345", "", new string('5', 60)]);
        Assert.Equal([10, 12], s.Pictures);
        Assert.Equal(-1, s.At(-1).Picture);
        Assert.Equal([0, 0, 0], s.At(-1).Mouth);
        Assert.Equal([0, 3, 0], s.At(1 + 3.5 / 60).Mouth);
        Assert.Equal([0, 0, 0], s.At(1 + 10 / 60.0).Mouth); // past the string: closed
        Assert.Equal([0, 0, 0], s.At(2.5).Mouth); // K_1
        var (picture, mouth) = s.At(3.001);
        Assert.Equal(12, picture);
        Assert.Equal([5, 0, 0], mouth);
        // a portrait the archive lacks (STR21 P_11): the one before stays with its mouth shut
        var lacking = new PortraitScene([.. stages, new(3.5, StoryScript.Step.Picture, 99)], ["012345", "", new string('5', 60)], new HashSet<int> { 99 });
        Assert.Equal([10, 12], lacking.Pictures);
        Assert.Equal([5, 0, 0], s.At(3.6).Mouth);
        Assert.Equal(12, lacking.At(3.6).Picture);
        Assert.Equal([0, 0, 0], lacking.At(3.6).Mouth);
        // white padding rows of a backdrop take the picture's nearest row
        var grey = Enumerable.Repeat((byte)90, 4 * 8 * 4).ToArray();
        Array.Fill(grey, (byte)255, 0, 4 * 4);
        Assert.All(StoryMedia.FillWhiteEdges((4, 8, grey)).Item3, b => Assert.Equal(90, b));
        // eyes blink now and then: mostly open, half/shut for a few frames
        var frames = Enumerable.Range(0, 600).Select(f => PortraitScene.Eyes(f / 60.0, 0)).ToList();
        Assert.True(frames.Count(f => f == 0) > 540);
        Assert.Contains(2, frames);
    }

    private static readonly Manga.Cue[] Timeline =
    [
        new(0, ["BG_summer_n", "GI_30", "BS_(512,0,8000)", "P_(20,20)", "I_60", "L_140", "O_120", "F_00_09"]),
        new(100, ["S_(-150,80,100)", "P_(178,164)", "I_60", "L_200", "O_100", "F_00_10"]),
        new(150, ["P_(40,40)", "I_0", "L_50", "O_0", "F_00_09"]),
        new(400, ["BO_120"]),
        new(600, ["BG_MT00", "BI_(96,0)"]),
        new(800, ["BO_120"]),
        new(1000, ["QUIT"]),
    ];

    /// <summary>Panels fade in, stay L frames from their start, fade out O frames, slide in from P + S; the same name again replaces the panel; backgrounds scroll and fade.</summary>
    [Fact]
    public void KomaSequence_PanelsAndBackdrops()
    {
        var k = KomaSequence.Parse(Timeline);
        Assert.Equal(1000, k.Quit);
        Assert.Equal(3, k.Panels.Count);
        Assert.Equal([0, 100 / 60.0, 150 / 60.0], k.Steps);
        var p = k.PanelsAt(30).Single();
        Assert.Equal(("00_09", new Vector2(20, 20), 0.5f), (p.Panel.Name, p.At, p.Alpha));
        var at = k.PanelsAt(120).ToList();
        Assert.Equal(2, at.Count);
        Assert.True(Vector2.Distance(new Vector2(178 - 120, 164 + 64), at[1].At) < 1e-3); // 20 of 100 slide frames
        Assert.Equal(1 / 3f, at[1].Alpha, 4);
        at = k.PanelsAt(160).ToList(); // 00_09 again replaces the first one
        Assert.Equal([new Vector2(118, 196), new Vector2(40, 40)], at.Select(x => Vector2.Round(x.At)));
        Assert.Equal(0.5f, k.PanelsAt(350).Single().Alpha, 4); // 00_10 half faded out
        Assert.Empty(k.PanelsAt(400));

        var (sky, offset, a) = k.BackdropAt(250)!.Value;
        Assert.True(sky.Sky);
        Assert.Equal((new Vector2(16, 0), 1f), (offset, a)); // 512 px in 8000 frames
        Assert.Equal(0.5f, k.BackdropAt(460)!.Value.Alpha, 4); // BO_120 from 400
        var (card, _, ca) = k.BackdropAt(615)!.Value;
        Assert.Equal(("MT00", new Vector2(96, 0), false, 0.5f), (card.Name, card.At, card.Sky, ca));
        Assert.Equal(0f, k.BackdropAt(950)!.Value.Alpha);
    }

    private static readonly ShowLine[] Lines = [new(1, "A|one"), new(3, "B|two"), new(6, "A|three")];

    /// <summary>AUTO plays through the lines with the track; DECIDE jumps to the next line (the track restarts there), after the last to the end.</summary>
    [Fact]
    public void Show_AutoFollowsTheTrackAndDecideJumps()
    {
        var s = new Show(Lines, 10);
        Assert.Equal(-1, s.Line);
        s.Tick(0.5, null);
        Assert.Equal(-1, s.Line);
        s.Tick(1 / 60.0, 3.2); // the voice track leads
        Assert.Equal((3.2, 1), (s.Time, s.Line));
        s.Next();
        Assert.Equal((6.0, 2, true), (s.Time, s.Line, s.Seeked));
        s.Next();
        Assert.True(s.Done);
        s.Tick(1, null);
        Assert.Equal(10, s.Time);
    }

    /// <summary>AUTO off: the show stops at the end of each line (the line stays up) until DECIDE, which goes on with the next line.</summary>
    [Fact]
    public void Show_AutoOffHoldsAtEachLineEnd()
    {
        var s = new Show(Lines, 10) { Auto = false };
        for (var i = 0; i < 120; i++) s.Tick(1 / 60.0, null); // before the first line it runs
        Assert.False(s.Held);
        Assert.Equal(0, s.Line);
        for (var i = 0; i < 120; i++) s.Tick(1 / 60.0, null);
        Assert.True(s.Held);
        Assert.Equal(0, s.Line);
        Assert.True(s.Time < 3);
        s.Tick(1, null);
        Assert.True(s.Time < 3);
        s.Next();
        Assert.Equal((3.0, 1, false), (s.Time, s.Line, s.Held));
        s.Tick(1 / 60.0, 2.99); // the restarted track reports a hair before the seek
        s.Tick(1 / 60.0, 3.01);
        Assert.False(s.Held); // not caught again at the line it was released at
        Assert.Equal(1, s.Line);
        s.Auto = true;
        s.Tick(5, null);
        Assert.Equal(2, s.Line);
        // no subtitles: DECIDE steps through the given panel starts
        var panels = new Show([], 20, [0, 4, 9]);
        panels.Next();
        Assert.Equal(4, panels.Time);
        panels.End();
        Assert.True(panels.Done);
    }

    /// <summary>
    ///     The chapter's shows in the original's order: manga, then the scene (2–30); after a win the scene after it, chapter
    ///     30's epilogue, chapter 9's second manga (KOMATC32); chapters 0/1 only a manga sequence.
    /// </summary>
    [Fact]
    public void Shows_InTheOriginalsOrder()
    {
        Assert.Equal([new ShowRequest(0, true, 0)], StoryMode.Shows(new Manga.Chapter(0, 0, -1, false, 0), false));
        Assert.Empty(StoryMode.Shows(new Manga.Chapter(0, 0, -1, false, 0), true));
        Assert.Equal([new ShowRequest(22, true, 22), new ShowRequest(22, false, 0)], StoryMode.Shows(new Manga.Chapter(22, 22, -1, true, 23), false));
        Assert.Equal([new ShowRequest(9, false, 3), new ShowRequest(9, true, 32)], StoryMode.Shows(new Manga.Chapter(9, 9, 32, true, 9), true));
        Assert.Equal([new ShowRequest(30, false, 3), new ShowRequest(30, false, 4)], StoryMode.Shows(new Manga.Chapter(30, 30, -1, true, 30), true));
        Assert.Equal(0, StoryMedia.Part([[], [], [], ["1"], ["2"]], 3));
        Assert.Equal(1, StoryMedia.Part([["0"], [], [], ["1"], ["2"]], 3));
    }

    /// <summary>The manga dramas' English: per timeline in time order, each "seconds|SPEAKER|text" with a speaker plate the scenes know.</summary>
    [Fact]
    public void MangaText_LinesInTimeOrder()
    {
        foreach (var (n, _) in MangaText.Koma)
        {
            var lines = MangaText.Lines(n);
            Assert.NotEmpty(lines);
            for (var i = 1; i < lines.Count; i++) Assert.True(lines[i].Time >= lines[i - 1].Time, $"KOMATC{n} line {i}");
            Assert.All(lines, l => Assert.Contains('|', l.Line));
            Assert.All(lines, l => Assert.False(string.IsNullOrWhiteSpace(StoryText.Split(l.Line).Text)));
        }
        Assert.Equal(31, Enumerable.Range(0, 31).Count(n => MangaText.Card($"MT{n:00}") != null)); // every episode title card
        Assert.Null(MangaText.Card("summer_d"));
        Assert.DoesNotContain(27, MangaText.Koma.Keys); // KOMATC27 plays no drama
    }

    private static ShowMedia Fake(ShowRequest r) => new()
    {
        Request = r,
        Show = new Show(r.Koma ? [new(0.5, "TAKUMI|Manga line")] : [new(0.2, "IKETANI|Scene line"), new(1, "TAKUMI|Another")], 2),
    };

    /// <summary>
    ///     With the disc's shows: loading → manga → scene → race (DECIDE steps lines, RIGHT skips a show, UP toggles AUTO);
    ///     after a win the shows after the race, then the next chapter; BACK in a show before the race leaves the chapter.
    /// </summary>
    [Fact]
    public void Flow_ShowsBeforeAndAfterTheRace()
    {
        Vector2[] line = [new(0, 0), new(100, 0)];
        var catalog = new Catalog([new Catalog.Course("AKINA", "AKINA", ["DAY", "NIT"], line, 7700, 465, true, false)],
            [new Catalog.Car("AE86T", "TOYOTA", "TRUENO", "FR", 130, 940, [1]), new Catalog.Car("FD3S", "MAZDA", "RX-7", "FR", 280, 1260, [1])]);
        var chapters = Enumerable.Range(0, 31).Select(n => new StoryScript.Chapter(n, 3, false, false, true, 1, 220, 0, 23)).ToArray();
        var requests = new List<ShowRequest>();
        var s = new StoryMode(catalog)
        {
            MangaChapters = Enumerable.Range(0, 31).Select(n => new Manga.Chapter(n, n, n == 9 ? 32 : -1, n >= 2, n)).ToArray(),
            Media = new StoryMedia("", null) { Decoder = r => { lock (requests) requests.Add(r); return Fake(r); } },
        };
        s.Progress.Clear(StoryMode.Key(8));
        s.Enter(chapters, 9);
        var actions = new List<StoryMode.Action>();
        StoryMode.Action Run(float seconds, (int, int, bool, bool) k = default)
        {
            var first = s.Update(k, 1 / 60f);
            actions.Add(first);
            for (var t = 1 / 60f; t < seconds; t += 1 / 60f)
                actions.Add(s.Update(default, 1 / 60f));
            return first;
        }
        Run(1.5f, (0, 0, true, false));
        Assert.Equal(StoryMode.Phase.Loading, s.Current);
        s.Loaded();
        Assert.Equal(StoryMode.Phase.Show, s.Current);
        Assert.Null(s.Music); // the voice tracks have their own music
        Run(0.3f);
        Run(0.1f, (0, 1, false, false)); // AUTO off
        Run(0.6f);
        Run(0.1f, (1, 0, false, false)); // RIGHT: skip the manga
        Run(1);
        Assert.Equal(StoryMode.Phase.Show, s.Current); // the scene
        Run(0.1f, (0, 0, true, false)); // DECIDE: next line
        Run(0.1f, (0, 0, true, false)); // past the last: the end
        Run(1);
        Assert.Equal(StoryMode.Phase.Racing, s.Current);
        Assert.Contains(StoryMode.Action.Race, actions);
        Assert.Equal([new ShowRequest(9, true, 9), new ShowRequest(9, false, 0)], requests.Order(Comparer<ShowRequest>.Create((a, b) => a.Koma == b.Koma ? 0 : a.Koma ? -1 : 1)));

        s.Finish(BattleOutcome.Win, "WIN", null, coast: true);
        Run(4.5f);
        Assert.Equal(StoryMode.Action.Save, Run(0.1f, (0, 0, true, false)));
        Assert.Equal(StoryMode.Phase.Show, s.Current);
        Run(0.1f, (0, -1, false, false)); // AUTO back on: the scene after the race and KOMATC32 play out
        Run(4);
        Run(4);
        Assert.Equal(StoryMode.Phase.Select, s.Current);
        Assert.Equal(10, s.Chapter);
        Assert.Contains(new ShowRequest(9, true, 32), requests);

        Run(1.5f, (0, 0, true, false));
        s.Loaded();
        Run(0.5f);
        Run(0.1f, (0, 0, false, true)); // BACK before the race: the chapter select
        Run(1);
        Assert.Equal(StoryMode.Phase.Select, s.Current);
        Assert.False(s.InRun);
    }

    /// <summary>The VOICE slider: in the SOUND page, kept in 0..1.</summary>
    [Fact]
    public void VoiceVolume_SliderAndRange()
    {
        var settings = new Settings { VoiceVolume = 3 };
        settings.Sanitize();
        Assert.Equal(1, settings.VoiceVolume);
        settings.VoiceVolume = float.NaN;
        settings.Sanitize();
        Assert.Equal(1, settings.VoiceVolume);
        var voice = new Options(settings).Pages.First(p => p.Title == "SOUND").Rows.Single(r => r.Label == "VOICE");
        voice.Set(5);
        Assert.Equal(0.5f, settings.VoiceVolume, 3);
    }
}
