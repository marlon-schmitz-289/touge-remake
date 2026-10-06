using System.Numerics;
using Kansei.Graphics;
using Touge.Formats;
using Touge.Race;
using Touge.Ui;

namespace Touge.Story;

/// <summary>
///     STORY (main menu), after the original's story mode: 31 chapters in three parts (ELF 0x2A2990), each a scene before the
///     battle, the battle (or a run alone) with the chapter's goal, and a scene after it. The menus are rebuilt with
///     <see cref="Canvas"/> (no original textures): chapter select (part tabs, list, info panel), the story's own finish
///     banner/result with RETRY on a loss, progress (<see cref="Progress"/>, "story/nn") unlocking the next chapter, and an
///     ending after the last. The scenes are the original's (<see cref="StoryMedia"/>, <see cref="Shows"/>): manga panels
///     with the audio drama and portraits with lip sync to the voices, English subtitles in a rebuilt talk window; without
///     the disc's media they fall back to text panels over the course at night. Music: chapter select WORRY.adx as the
///     original, the voice tracks' own in the shows (text panels: ST_BGM_N STORY_STnn), the race's Eurobeat,
///     WIN/LOSE/TIMEUP.adx, JOY on a clear, THERACEISOVER at the end.
///     The game (<see cref="TougeGame"/>) loads the chapter, runs the race and reports the outcome.
/// </summary>
public sealed class StoryMode(Catalog catalog)
{
    /// <summary>Scene: the text panels (no disc media); Show: the original's manga sequences and portrait scenes (<see cref="StoryMedia" />).</summary>
    public enum Phase { Select, Loading, Scene, Show, Racing, Banner, Result, Ending }
    /// <summary>
    ///     Load: load <see cref="Chapter"/> (course, cars, battle); Race: the scene is over, start the race (telop, countdown);
    ///     Retry: same chapter again from the grid; Leave: the run is over (back on the chapter select); Exit: to the main menu;
    ///     Save: progress changed.
    /// </summary>
    public enum Action { None, Load, Race, Retry, Leave, Exit, Save }

    public const float Fade = 30 / 60f, BannerHold = 3.5f, TypeRate = 40;

    public bool Open { get; private set; }
    public Phase Current { get; private set; }
    /// <summary>The story owns the screen (everything but the race itself).</summary>
    public bool Active => Open && Current != Phase.Racing;
    /// <summary>A chapter is loaded (from loading until it is left): its HUD panel, no time-attack records.</summary>
    public bool InRun { get; private set; }
    /// <summary>The game is held (the banner lets the cars run out, unless a run alone failed on the way).</summary>
    public bool Freezes => Active && !(Current == Phase.Banner && _coast);
    /// <summary>Drawn over the race with its HUD.</summary>
    public bool OverRace => Current == Phase.Banner;
    /// <summary>Camera: the flight along the road behind the select and the scenes; the car turns on the result.</summary>
    public bool Flyover => Active && Current is Phase.Select or Phase.Loading or Phase.Scene or Phase.Ending;
    public bool Showcase => Active && Current == Phase.Result;
    /// <summary>Engine and tyres heard: only while the cars run out under the banner.</summary>
    public bool Mutes => Active && !(Current == Phase.Banner && _coast);

    public Action<string>? Sound { get; set; }
    /// <summary>The original's manga sequences and portrait scenes (null: the text panels).</summary>
    public StoryMedia? Media { get; set; }
    /// <summary>The ELF's manga tables per chapter (<see cref="Shows" />).</summary>
    public Manga.Chapter[] MangaChapters { get; set; } = [];
    public Progress Progress { get; set; } = new();
    public StoryScript.Chapter[] Chapters { get; private set; } = [];
    public int Chapter { get; private set; }
    public StoryScript.Chapter Data => Chapters[Chapter];
    public StoryText.Chapter Text => StoryText.Chapters[Chapter];
    public Goal Goal => StoryRules.Of(Data.Rule, Data.Rival >= 0);
    /// <summary>The chapter's battle (null: a run alone).</summary>
    public BattleSetup? Battle => Data.Rival < 0 || StoryRules.Solo(Goal) ? null
        : StoryRules.Battle(Goal, Data.Param, StoryRivals.Find(Text.Rival!, CarPaint.Cars[Data.Rival], Chapter));
    public string CourseTime => StoryHeadless.CourseTime(Data);
    public string HeroCar => CarPaint.Cars[Data.Hero];

    private int _part, _line, _scenePart, _sceneChapter;
    private int? _nextChapter;
    private float _t, _clock, _leave = -1;
    private Phase _next;
    private Action _then;
    private BattleOutcome _outcome;
    private string _reason = "";
    private BattleReport? _report;
    private SoloJudge? _solo;
    private int _row;
    private bool _coast;

    public static string Key(int chapter) => $"story/{chapter:00}";
    public bool Cleared(int n) => Progress.IsCleared(Key(n));
    public bool Unlocked(int n) => n == 0 || Cleared(n) || Cleared(n - 1);

    /// <summary>Opens the chapter select on <paramref name="chapter"/> (default: the first chapter not cleared yet).</summary>
    public void Enter(StoryScript.Chapter[] chapters, int? chapter = null)
    {
        Chapters = chapters;
        Chapter = chapter ?? Enumerable.Range(0, chapters.Length).FirstOrDefault(n => !Cleared(n) && Unlocked(n), chapters.Length - 1);
        _part = PartOf(Chapter);
        (Open, InRun, _leave) = (true, false, -1);
        Go(Phase.Select);
    }

    public void Close() => (Open, InRun) = (false, false);

    private static int PartOf(int chapter) => StoryText.Parts.Count(p => p.First <= chapter) - 1;
    private static int PartEnd(int part) => part + 1 < StoryText.Parts.Length ? StoryText.Parts[part + 1].First : StoryText.Chapters.Length;

    private void Go(Phase p) => (Current, _t, _row) = (p, 0, 0);

    /// <summary>Fade out, then show <paramref name="next"/> and return <paramref name="then"/>.</summary>
    private void Leave(Phase next, Action then) => (_leave, _next, _then) = (0, next, then);

    // ---------------------------------------------------------------- calls from the game

    /// <summary>The chapter is loaded: the scene before the race.</summary>
    public void Loaded()
    {
        InRun = true;
        if (!StartShows(false)) StartScene(0);
    }

    /// <summary>The run alone of this chapter is judged by <paramref name="judge"/> (the game feeds it; the HUD panel shows it).</summary>
    public SoloJudge? NewJudge() => _solo = StoryRules.Solo(Goal) ? new SoloJudge(Goal, StoryRules.Limit(Data)) : null;

    /// <summary>The race starts (after the scene, a retry or straight from --menu story:n:race); its judge for a run alone.</summary>
    public SoloJudge? BeginRace()
    {
        Current = Phase.Racing;
        return NewJudge();
    }

    /// <summary>The race was decided: banner, then the result; <paramref name="coast"/>: the cars run out meanwhile.</summary>
    public void Finish(BattleOutcome outcome, string reason, BattleReport? report, bool coast)
    {
        (_outcome, _reason, _report, _coast) = (outcome, reason, report, coast);
        Progress.Add(Key(Chapter) + "/tries");
        Go(Phase.Banner);
    }

    /// <summary>Skips fades, entrances and typing (screenshots).</summary>
    public void Settle(float at = 99) => (_t, _leave) = (MathF.Max(_t, at), -1);

    /// <summary>--menu story:n:end — the THE END screen (screenshots).</summary>
    public void ShowEnding() => Go(Phase.Ending);

    /// <summary>--menu story:n:part:line — the scene of chapter n at that line (screenshots).</summary>
    public void ShowScene(int part, int line)
    {
        InRun = true;
        StartScene(part);
        _line = Math.Clamp(line, 0, Lines.Length - 1);
    }

    // ---------------------------------------------------------------- music

    /// <summary>Music for the screen (BGM.AFS / ST_BGM_N.AFS name; <see cref="Menu.RaceMusic"/> in the race).</summary>
    public string? Music => Current switch
    {
        Phase.Select => "WORRY.adx",
        Phase.Loading => null,
        Phase.Scene => SceneTrack(_sceneChapter),
        Phase.Show => null, // the voice tracks carry their own music
        Phase.Banner or Phase.Result when _outcome != BattleOutcome.Win => _reason is "TIME UP" or "TIME" ? "TIMEUP.adx" : "LOSE.adx",
        Phase.Banner => "WIN.adx",
        Phase.Result => "JOY.adx",
        Phase.Ending => "THERACEISOVER.adx",
        _ => Menu.RaceMusic,
    };

    /// <summary>
    ///     Story track of a chapter: ST_BGM_N holds STORY_ST01–31 (no 13; 19 in two halves), the music of each chapter's scene
    ///     before the race without the voices (<see cref="StoryMedia.MusicOf" />), numbered from 1
    ///     (our reading; the code picks them by a table we have not traced).
    /// </summary>
    public static string SceneTrack(int chapter) => (chapter + 1) switch
    {
        13 => "STORY_ST12.adx",
        19 => "STORY_ST19_1.adx",
        var n => $"STORY_ST{n:00}.adx",
    };

    // ---------------------------------------------------------------- input

    public Action Update((int X, int Y, bool Ok, bool Back) k, float dt)
    {
        if (!Active) return Action.None;
        dt = MathF.Min(dt, 1 / 20f);
        _t += dt;
        _clock += dt;
        if (_leave >= 0)
        {
            if ((_leave += dt) < Fade)
            {
                if (Current == Phase.Show && _media != null) Media?.Play(_media, dt, false, 1 - _leave / Fade);
                return Action.None;
            }
            _leave = -1;
            if (Current == Phase.Show) DropShows();
            if (_nextChapter is { } n) (Chapter, _part, _nextChapter) = (n, PartOf(n), null);
            if (_then == Action.Exit) Close();
            else Go(_next);
            return _then;
        }
        switch (Current)
        {
            case Phase.Select:
                return Select(k);
            case Phase.Loading:
                if (_t >= Fade + 0.3f && _t - dt < Fade + 0.3f) return Action.Load; // the white screen is up: load (blocking)
                break;
            case Phase.Scene:
                return Scene(k);
            case Phase.Show:
                return ShowStep(k, dt);
            case Phase.Banner:
                if (_t >= BannerHold || (k.Ok && _t > 1)) Go(Phase.Result);
                break;
            case Phase.Result:
                return Result(k);
            case Phase.Ending:
                if (_t > 3 && (k.Ok || k.Back))
                {
                    Sound?.Invoke("SYS006");
                    Leave(Phase.Select, Action.None);
                }
                break;
        }
        return Action.None;
    }

    private Action Select((int X, int Y, bool Ok, bool Back) k)
    {
        if (k.X != 0)
        {
            var p = Math.Clamp(_part + k.X, 0, StoryText.Parts.Length - 1);
            if (p == _part) return Action.None;
            Sound?.Invoke("SYS005");
            _part = p;
            var first = StoryText.Parts[p].First;
            Chapter = Enumerable.Range(first, PartEnd(p) - first).LastOrDefault(Unlocked, first);
        }
        else if (k.Y != 0)
        {
            var n = Math.Clamp(Chapter + k.Y, StoryText.Parts[_part].First, PartEnd(_part) - 1);
            if (n == Chapter) return Action.None;
            Sound?.Invoke("SYS005");
            Chapter = n;
        }
        else if (k.Ok)
        {
            if (!Unlocked(Chapter))
            {
                Sound?.Invoke("BEEP001");
                return Action.None;
            }
            Sound?.Invoke("SYS006");
            Leave(Phase.Loading, Action.None);
        }
        else if (k.Back)
        {
            Sound?.Invoke("BEEP001");
            Leave(Phase.Select, Action.Exit);
        }
        return Action.None;
    }

    private string[] Lines => Text.Scene[Math.Min(_scenePart, Text.Scene.Length - 1)];
    private int Typed => (int)(MathF.Max(0, _t - Intro) * TypeRate);
    /// <summary>Seconds of the title card before the first line (the chapter's first scene, the aftermath's heading).</summary>
    private float Intro => _line == 0 ? _scenePart == 0 ? 2.2f : 1.4f : 0;

    private void StartScene(int part)
    {
        (_scenePart, _line, _sceneChapter) = (part, 0, Chapter);
        Go(Phase.Scene);
    }

    private Action Scene((int X, int Y, bool Ok, bool Back) k)
    {
        var line = StoryText.Split(Lines[_line]).Text;
        if (k.Ok && _t < Intro) _t = Intro; // decide skips the title card
        else if (k.Ok && Typed < line.Length) _t = 99; // first completes the line
        else if (k.Back && _scenePart == 0)
        {
            // before the race BACK leaves the chapter (RIGHT skips to the race)
            Sound?.Invoke("BEEP001");
            InRun = false;
            Leave(Phase.Select, Action.Leave);
        }
        else if (k.Ok || k.Back || k.X > 0)
        {
            Sound?.Invoke(k.Ok ? "SYS006" : "BEEP001");
            if (k.Ok && _line + 1 < Lines.Length)
            {
                (_line, _t) = (_line + 1, 0);
                return Action.None;
            }
            return EndScene();
        }
        return Action.None;
    }

    /// <summary>The scene is over: before the race the race starts; after it the next part or back to the select.</summary>
    private Action EndScene()
    {
        if (_scenePart == 0)
        {
            BeginRace();
            return Action.Race;
        }
        if (_scenePart + 1 < Text.Scene.Length)
        {
            StartScene(_scenePart + 1);
            return Action.None;
        }
        ChapterDone();
        return Action.None;
    }

    /// <summary>Chapter done: the next one is selected; the last one ends the story.</summary>
    private void ChapterDone()
    {
        InRun = false;
        var last = Chapter == Chapters.Length - 1;
        if (!last) _nextChapter = Chapter + 1; // switched once faded out (the scene still draws this chapter's lines)
        Leave(last && _firstClear ? Phase.Ending : Phase.Select, Action.Leave);
    }

    // ---------------------------------------------------------------- shows (the original's manga and portrait scenes)

    /// <summary>
    ///     The shows of a chapter as the original plays them (ELF code driving the story scenes): before the race the manga
    ///     sequence KOMATC[c], then (chapters 2–30) the portrait scene slot 0; after a won race slot 3, chapter 30's
    ///     epilogue slot 4, and chapter 9's second manga sequence (KOMATC32).
    /// </summary>
    public static List<ShowRequest> Shows(Manga.Chapter c, bool after)
    {
        var list = new List<ShowRequest>();
        if (!after)
        {
            if (c.Before >= 0) list.Add(new ShowRequest(c.Index, true, c.Before));
            if (c.Scene) list.Add(new ShowRequest(c.Index, false, 0));
            return list;
        }
        if (c.Scene) list.Add(new ShowRequest(c.Index, false, 3));
        if (c.Scene && StoryText.Chapters[c.Index].Scene.Length > 2) list.Add(new ShowRequest(c.Index, false, 4));
        if (c.After >= 0) list.Add(new ShowRequest(c.Index, true, c.After));
        return list;
    }

    private List<ShowRequest> _shows = [];
    private List<Task<ShowMedia>> _loads = [];
    private int _show, _lastLine = -2;
    private bool _afterRace, _auto = true;
    private ShowMedia? _media;
    private float _out = -1, _shownFor;

    /// <summary>The chapter's shows before (or after) the race, all decoding at once; false = none or no media (text panels).</summary>
    private bool StartShows(bool after)
    {
        if (Media == null || Chapter >= MangaChapters.Length) return false;
        var shows = Shows(MangaChapters[Chapter], after);
        if (shows.Count == 0 && !after) return false;
        DropShows();
        (_shows, _afterRace, _show) = (shows, after, 0);
        _loads = [.. shows.Select(Media.Load)];
        if (shows.Count == 0)
        {
            ChapterDone(); // chapters 0/1: nothing after the race in the original
            return true;
        }
        StartShow();
        return true;
    }

    private void StartShow()
    {
        (_media, _out, _shownFor, _lastLine) = (null, -1, 0, -2);
        Go(Phase.Show);
    }

    /// <summary>Frees the show on screen (pictures, voice); shows still decoding are dropped when done.</summary>
    private void DropShows()
    {
        DropShowMedia();
        _loads = [];
    }

    /// <summary>The show in progress at the given time (screenshots): --menu story:n:show:i[:seconds], i over the before and after shows.</summary>
    public void ShowShow(int index, double seconds)
    {
        InRun = true;
        var c = MangaChapters[Chapter];
        var before = Shows(c, false);
        var after = index >= before.Count;
        if (!StartShows(after) || _shows.Count == 0) return;
        _show = Math.Clamp(after ? index - before.Count : index, 0, _shows.Count - 1);
        var m = _loads[_show].Result;
        Media!.Upload(m, all: true);
        (_media, _shownFor) = (m, 99);
        m.Show.Auto = _auto;
        m.Show.Seek(seconds);
        Media.Play(m, 0, true);
    }

    private Action ShowStep((int X, int Y, bool Ok, bool Back) k, float dt)
    {
        var task = _loads[_show];
        if (_media == null)
        {
            if (!task.IsCompleted) return Action.None; // still decoding: black screen, "Now Loading"
            if (task.IsFaulted)
            {
                Console.WriteLine($"[Story] {_shows[_show]} nicht ladbar: {task.Exception?.InnerException?.Message}");
                return NextShow();
            }
            _media = task.Result;
            _media.Show.Auto = _auto;
            Console.WriteLine($"[Story] {_shows[_show]}: Stimme {_media.Voice} ({_media.VoiceSeconds:0.0} s), {_media.Show.Lines.Count} Untertitel");
        }
        var m = _media;
        if (!Media!.Upload(m)) return Action.None;
        _shownFor += dt;
        var s = m.Show;
        if (_out >= 0)
        {
            Media.Play(m, dt, false, 1 - _out / Fade); // the sound fades out with the picture
            return (_out += dt) < Fade ? Action.None : NextShow();
        }
        if (k.Back && !_afterRace)
        {
            // before the race BACK leaves the chapter (RIGHT skips the show)
            Sound?.Invoke("BEEP001");
            // the picture and the sound fade out with the leave, freed after it (Update)
            InRun = false;
            Leave(Phase.Select, Action.Leave);
            return Action.None;
        }
        if (k.Ok) s.Next();
        if (k.Y != 0)
        {
            Sound?.Invoke("SYS005");
            s.Auto = _auto = !_auto;
        }
        if (k.X > 0 || k.Back)
        {
            Sound?.Invoke("SYS006");
            s.End();
        }
        var held = s.Held;
        Media.Play(m, dt, true);
        if (s.Held && !held) Console.WriteLine($"[Story]   hält bei {s.Time:0.00} s, Musik läuft weiter ohne Stimmen ({m.Music})");
        if (s.Line != _lastLine && s.Line >= 0 && !s.Done)
            Console.WriteLine($"[Story]   {m.Voice} {s.Lines[s.Line].Time,7:0.00} s (Uhr {s.Time:0.00} s): {s.Lines[s.Line].Line}");
        _lastLine = s.Line;
        if (s.Done) _out = 0;
        return Action.None;
    }

    /// <summary>The show is over: the next one, or the race (before it) / the chapter's end (after it).</summary>
    private Action NextShow()
    {
        DropShowMedia();
        if (++_show < _shows.Count)
        {
            StartShow();
            return Action.None;
        }
        _loads = [];
        if (_afterRace)
        {
            ChapterDone();
            return Action.None;
        }
        BeginRace();
        return Action.Race;
    }

    private void DropShowMedia()
    {
        if (_media != null) Media?.Free(_media);
        _media = null;
    }

    private bool _firstClear;
    private static readonly string[] WinButtons = ["CONTINUE"], LoseButtons = ["RETRY", "CHAPTER SELECT"];
    private string[] Buttons => _outcome == BattleOutcome.Win ? WinButtons : LoseButtons;

    private Action Result((int X, int Y, bool Ok, bool Back) k)
    {
        if (_t < 0.6f) return Action.None;
        if (k.X != 0)
        {
            var n = Math.Clamp(_row + k.X, 0, Buttons.Length - 1);
            if (n != _row) Sound?.Invoke("SYS005");
            _row = n;
            return Action.None;
        }
        if (!k.Ok && !(k.Back && _outcome != BattleOutcome.Win)) return Action.None;
        Sound?.Invoke(k.Ok ? "SYS006" : "BEEP001");
        if (_outcome == BattleOutcome.Win)
        {
            _firstClear = Progress.Clear(Key(Chapter));
            Progress.Add(Key(Chapter) + "/wins");
            if (!StartShows(true)) StartScene(1);
            return Action.Save;
        }
        if (k.Ok && _row == 0)
        {
            BeginRace();
            return Action.Retry;
        }
        InRun = false;
        Leave(Phase.Select, Action.Leave);
        return Action.Save;
    }

    // ---------------------------------------------------------------- drawing

    private readonly Canvas _c = new();
    private float Theta => _clock * 300 % 360;
    private static readonly uint Grey = Overlay.Rgba(0.72f, 0.73f, 0.75f), Ink = Overlay.Rgba(0.1f, 0.1f, 0.12f), Thought = Overlay.Rgba(0.68f, 0.82f, 1f),
        Locked = Overlay.Rgba(1, 1, 1, 0.3f), Amber = Overlay.Rgba(1, 0.78f, 0.2f);

    public void Build(Overlay o, int width, int height)
    {
        if (!Active) return;
        var c = _c;
        c.Begin(o, width, height);
        switch (Current)
        {
            case Phase.Select:
                c.Backdrop(_clock);
                SelectScreen(c);
                c.Marquee("STORY", false, _clock);
                break;
            case Phase.Loading:
                LoadingArt.Draw(c, _t);
                c.Text($"CHAPTER {Chapter + 1}  {Text.Title}", 36, 428, 12, Grey, 0, 0.15f);
                break;
            case Phase.Scene:
                SceneScreen(c);
                break;
            case Phase.Show:
                ShowScreen(c);
                break;
            case Phase.Banner:
                Banner(c);
                break;
            case Phase.Result:
                ResultScreen(c);
                break;
            case Phase.Ending:
                EndingScreen(c);
                break;
        }
        c.Fade(Blackout);
    }

    /// <summary>How far the screen is faded to black (0..1): leaving, a show fading out, or a screen fading in.</summary>
    public float Blackout
    {
        get
        {
            if (_leave >= 0) return Math.Clamp(_leave / Fade, 0, 1);
            if (Current == Phase.Show && _out >= 0) return Math.Clamp(_out / Fade, 0, 1); // only in a show: _out outlives the last one
            var fadeIn = Current is Phase.Select or Phase.Loading or Phase.Ending || (Current == Phase.Scene && _line == 0) || (Current == Phase.Show && _media is { Uploaded: true });
            return fadeIn ? 1 - Math.Clamp((Current == Phase.Show ? _shownFor : _t) / Fade, 0, 1) : 0;
        }
    }

    private void SelectScreen(Canvas c)
    {
        // part tabs: chrome plates, the chosen one lit with the pulsing frame
        for (var p = 0; p < StoryText.Parts.Length; p++)
        {
            var x = 20 + p * 162;
            c.Plate(x, 68, 148, 24, p == _part ? 1 : 0.55f);
            c.Text(StoryText.Parts[p].Title, x + 74, 85, 13, Ink, 0.5f, 0.18f);
        }
        c.Glow(20 + _part * 162 - 4, 64, 20 + _part * 162 + 152, 96, Canvas.Pulse(Theta) * 0.8f);

        // chapter list of the part
        var (_, subtitle, first) = StoryText.Parts[_part];
        var end = PartEnd(_part);
        c.Carbon(16, 104, 236, 424);
        c.Text(subtitle, 28, 124, 11, Grey, 0, 0.15f);
        var cleared = Enumerable.Range(0, Chapters.Length).Count(Cleared);
        c.Text($"{cleared} / {Chapters.Length}", 224, 124, 10, Amber, 1, 0.12f);
        var step = MathF.Min(15.5f, 286f / (end - first));
        for (var n = first; n < end; n++)
        {
            var y = 144 + (n - first) * step;
            var open = Unlocked(n);
            var on = n == Chapter;
            if (on) c.Diamond(26, y - 4, 3.5f);
            c.Text($"{n + 1:00}", 34, y, 10.5f, open ? Grey : Locked, 0, 0.12f);
            c.Fit(open ? StoryText.Chapters[n].Title : "- - - - - - - -", 54, y, 140, 0, on ? Canvas.Yellow : open ? Canvas.White : Locked, 0.12f, 0.05f, 11);
            if (Cleared(n)) c.Text("CLEAR", 226, y, 8.5f, Amber, 1, 0.15f);
        }
        var sy = 144 + (Chapter - first) * step;
        c.Glow(20, sy - 12, 232, sy + 4, Canvas.Pulse(Theta));

        InfoPanel(c);
        Menu.Hint(c, "UP/DOWN: Chapter    LEFT/RIGHT: Part    DECIDE: Start    BACK: Main menu");
    }

    private void InfoPanel(Canvas c)
    {
        c.Carbon(248, 104, 498, 424);
        var d = Data;
        var t = Text;
        c.Text($"CHAPTER {Chapter + 1}", 262, 126, 11, Grey, 0, 0.15f);
        if (!Unlocked(Chapter))
        {
            c.Text("LOCKED", 373, 250, 26, Locked, 0.5f, 0.2f);
            c.Text($"Clear chapter {Chapter} to open it.", 373, 276, 11, Grey, 0.5f, 0.1f);
            return;
        }
        c.Fit(t.Title, 262, 152, 224, 0, Canvas.White, 0.18f, 0.08f, 21);
        var y = 172f;
        foreach (var l in CarGuide.Wrap(c.O.Font!, t.Blurb, 10.5f, 222))
        {
            c.Text(l, 262, y, 10.5f, Canvas.White, 0, 0.08f);
            y += 13.5f;
        }
        var course = catalog.Courses.FirstOrDefault(x => x.Id == StoryScript.Courses[d.Course]);
        var hero = catalog.Cars.FirstOrDefault(x => x.Id == HeroCar)?.Name ?? HeroCar;
        Row(c, 254, "COURSE", course == null ? StoryScript.Courses[d.Course]
            : $"{course.Name}  {Catalog.DirectionName(course, d.Reverse)}  {(d.Night ? "NIGHT" : "DAY")}{(d.Wet ? "  RAIN" : "")}");
        Row(c, 278, "DRIVER", $"{t.Hero} - {hero}");
        if (Battle is { } b)
        {
            var car = catalog.Cars.FirstOrDefault(x => x.Id == b.Rival.Car)?.Name ?? b.Rival.Car;
            Row(c, 302, "RIVAL", $"{b.Rival.Name} - {car}");
            c.Text(b.Rival.Team, 486, 316, 9, Grey, 1, 0.12f);
        }
        else Row(c, 302, "RIVAL", "NONE - A RUN ALONE");
        c.Text("GOAL", 262, 340, 9, Grey, 0, 0.12f);
        var gy = 356f;
        foreach (var l in CarGuide.Wrap(c.O.Font!, StoryRules.Describe(Goal, StoryRules.Limit(Data)), 12, 224))
        {
            c.Text(l, 262, gy, 12, Amber, 0, 0.15f, 0.06f);
            gy += 15;
        }
        var tries = Progress.Count(Key(Chapter) + "/tries");
        c.Text(Cleared(Chapter) ? "CLEARED" : "NEW", 262, 410, 13, Cleared(Chapter) ? Amber : Canvas.White, 0, 0.2f, 0.06f);
        if (tries > 0) c.Text($"RACES {tries}", 486, 410, 10, Grey, 1, 0.12f);
    }

    private static void Row(Canvas c, float y, string label, string value)
    {
        c.Text(label, 262, y, 9, Grey, 0, 0.12f);
        c.Fit(value, 306, y, 180, 0, Canvas.White, 0.12f, 0.05f, 11.5f);
        c.Rule(258, 488, y + 6, 0.6f);
    }

    /// <summary>Speaker plate tint: the teams' colours.</summary>
    private static uint Tint(string who) => who switch
    {
        _ when who.StartsWith("TAKUMI") || who.StartsWith("BUNTA") => Overlay.Rgba(0.95f, 0.95f, 0.95f),
        _ when who.StartsWith("KEISUKE") => Overlay.Rgba(1, 0.82f, 0.1f),
        _ when who is "RYOSUKE" or "FUMIHIRO" or "KENTA" or "RED SUNS MEMBER" => Overlay.Rgba(0.85f, 0.15f, 0.12f),
        _ when who is "NAKAZATO" or "SHINGO" or "NIGHTKIDS MEMBER" => Overlay.Rgba(0.25f, 0.3f, 0.45f),
        _ when who is "KYOICHI" or "SEIJI" or "EMPEROR MEMBER" => Overlay.Rgba(0.55f, 0.58f, 0.62f),
        _ when who is "IKETANI" or "ITSUKI" or "KENJI" or "IKETANI & ITSUKI" => Overlay.Rgba(0.2f, 0.55f, 0.95f),
        _ when who is "MAKO" or "SAYUKI" => Overlay.Rgba(0.3f, 0.6f, 1),
        _ when who.StartsWith("TODO") || who is "DAIKI" or "SAKAI" or "TOMO" or "PRESIDENT" => Overlay.Rgba(0.15f, 0.65f, 0.35f),
        _ when who is "TORU" or "ATSURO" => Overlay.Rgba(0.6f, 0.3f, 0.75f),
        _ => Overlay.Rgba(0.9f, 0.45f, 0.15f),
    };

    private void SceneScreen(Canvas c)
    {
        c.Fill(Overlay.Rgba(0.01f, 0.01f, 0.03f, 0.35f));
        c.Marquee($"CHAPTER {Chapter + 1}   {Text.Title}", false, _clock);
        // title card: the chapter's name before its first line, a heading before the aftermath
        if (_line == 0 && _t < Intro + 0.3f)
        {
            var a = Style.Ease(_t / 0.4f) * Style.Ease((Intro + 0.3f - _t) / 0.3f);
            if (_scenePart == 0)
            {
                c.Text($"CHAPTER {Chapter + 1}", 256, 190, 16, Style.Fade(Grey, a), 0.5f, 0.2f, 0.08f);
                c.Lettering(Text.Title, 256, 236, MathF.Min(40, 440 * c.Kx / c.O.Font!.Measure(Text.Title, c.Ky)), Overlay.Rgba(1, 0.35f, 0.3f), Overlay.Rgba(0.75f, 0, 0), 0.5f, 0.2f, true, true, a);
                c.Text(StoryText.Parts[PartOf(Chapter)].Subtitle, 256, 262, 12, Style.Fade(Canvas.White, a), 0.5f, 0.15f, 0.08f);
            }
            else
                c.Lettering(_scenePart == 1 ? "AFTER THE BATTLE" : "EPILOGUE", 256, 236, 34, Overlay.Rgba(0.45f, 0.6f, 1), Canvas.BrushBlue, 0.5f, 0.2f, true, true, a);
            if (_t < Intro) return;
        }
        var (who, line) = StoryText.Split(Lines[_line]);
        var thought = line.StartsWith('(');
        var a2 = Style.Ease((_t - Intro) / 0.2f);
        c.Carbon(28, 300, 484, 410, a2);
        c.Plate(40, 286, 150, 26, 1, a2);
        var tint = Tint(who);
        c.O.Rect(Vector2.Round(c.P(46, 291)), Vector2.Round(c.P(50, 307)), Style.Fade(tint, a2));
        c.Fit(who, 118, 305, 128, 0.5f, Style.Fade(Ink, a2), 0.15f, 0, 14);
        var shown = Typed;
        var y = 336f;
        foreach (var l in CarGuide.Wrap(c.O.Font!, line, 15, 420))
        {
            if (shown <= 0) break;
            c.Text(l.Length <= shown ? l : l[..shown], 50, y, 15, thought ? Thought : Canvas.White, 0, thought ? 0.22f : 0.1f);
            shown -= l.Length + 1;
            y += 21;
        }
        if (Typed >= line.Length) c.Arrow(458, 392, 472, 392, 465, 403, Canvas.Pulse(Theta)); // ▼ more
        c.Text($"{_line + 1} / {Lines.Length}", 472, 318, 9, Grey, 1, 0.12f);
        Menu.Hint(c, _scenePart == 0 ? "DECIDE: Next    RIGHT: Skip to the race    BACK: Chapter select" : "DECIDE: Next    RIGHT/BACK: Skip");
    }

    /// <summary>
    ///     A show: the original's pictures (<see cref="StoryMedia.Draw" />, below the overlay), the chapter's English title under
    ///     the Japanese title card, the subtitle of the line being spoken (manga: a band at the bottom; portraits: the talk
    ///     window with the speaker's plate), AUTO and the controls.
    /// </summary>
    private void ShowScreen(Canvas c)
    {
        var m = _media;
        if (m == null || !m.Uploaded)
        {
            Media?.Draw(c, m ?? EmptyShow);
            c.Text("Now Loading...", 476, 428, 15, Grey, 1, 0.22f, 0, 0.4f);
            return;
        }
        Media!.Draw(c, m);
        var s = m.Show;
        if (m.Koma?.BackdropAt(s.Time * 60) is var (b, _, ba) && MangaText.Card(b.Name) is { } card)
        {
            // a title card (the episode's title, a part or a date, in Japanese): its English below
            var size = MathF.Min(26, 440 * c.Kx / c.O.Font!.Measure(card, c.Ky));
            c.Lettering(card, 256, 300, size, Overlay.Rgba(1, 0.35f, 0.3f), Overlay.Rgba(0.75f, 0, 0), 0.5f, 0.2f, true, true, ba);
        }
        var i = s.Line;
        // a balloon of the scenes stays until the next; a drama line goes after a while when the next is far off
        if (i >= 0 && (m.Scene != null || s.Held || s.Time - s.Lines[i].Time < 3 + s.Lines[i].Line.Length / 12.0))
        {
            var (who, line) = StoryText.Split(s.Lines[i].Line);
            var shown = (int)((s.Time - s.Lines[i].Time) * TypeRate * 1.5f + 1);
            if (s.Held) shown = line.Length;
            var thought = line.StartsWith('(');
            var color = thought ? Thought : Canvas.White;
            if (m.Scene != null)
            {
                // the talk window (TALKWIN of the original, rebuilt): carbon panel, speaker plate with the team colour
                c.Carbon(28, 318, 484, 418);
                c.Plate(40, 304, 150, 26, 1);
                c.O.Rect(Vector2.Round(c.P(46, 309)), Vector2.Round(c.P(50, 325)), Tint(who));
                c.Fit(who, 118, 323, 128, 0.5f, Ink, 0.15f, 0, 14);
                var y = 352f;
                foreach (var l in CarGuide.Wrap(c.O.Font!, line, 15, 420))
                {
                    if (shown <= 0) break;
                    c.Text(l.Length <= shown ? l : l[..shown], 50, y, 15, color, 0, thought ? 0.22f : 0.1f);
                    shown -= l.Length + 1;
                    y += 21;
                }
                if (s.Held) c.Arrow(458, 402, 472, 402, 465, 413, Canvas.Pulse(Theta)); // ▼ waiting for DECIDE
            }
            else
            {
                // manga: subtitles in a band at the bottom, the speaker in the team colour
                var wrapped = CarGuide.Wrap(c.O.Font!, line, 14, 440);
                var top = 412 - 19 * wrapped.Count;
                c.O.Rect(c.P(c.Left, top - 20), c.P(c.Right, 422), Overlay.Rgba(0, 0, 0, 0.62f));
                if (who != "") c.Text(who, 256, top - 4, 10, Tint(who), 0.5f, 0.15f, 0.1f);
                var y = top + 13f;
                foreach (var l in wrapped)
                {
                    c.Text(l, 256, y, 14, color, 0.5f, thought ? 0.22f : 0.1f, 0.1f);
                    y += 19;
                }
                if (s.Held) c.Arrow(476, 412, 490, 412, 483, 423, Canvas.Pulse(Theta));
            }
        }
        c.Text(s.Auto ? "AUTO" : "AUTO OFF", 498, 30, 10, s.Auto ? Amber : Grey, 1, 0.15f, 0.1f);
        Menu.Hint(c, _afterRace ? "DECIDE: Next line    UP/DOWN: Auto    START/RIGHT/BACK: Skip"
            : "DECIDE: Next line    UP/DOWN: Auto    START/RIGHT: Skip    BACK: Chapter select");
    }

    private static readonly ShowMedia EmptyShow = new() { Request = new ShowRequest(0, true, 0), Show = new Show([], 0) };

    private void Banner(Canvas c)
    {
        if (_report != null)
        {
            BattleScreens.Banner(c, _report, _t);
            return;
        }
        var pop = Style.Ease(_t / 0.25f);
        var win = _outcome == BattleOutcome.Win;
        var (text, top, bottom) = win ? ("CLEAR!!", Overlay.Rgba(1, 0.85f, 0.3f), Overlay.Rgba(1, 0.38f, 0))
            : _reason == "TIME UP" ? ("TIME UP", Overlay.Rgba(1, 0.35f, 0.3f), Overlay.Rgba(0.75f, 0, 0)) : ("FAILED", Overlay.Rgba(0.55f, 0.65f, 1), Canvas.WordBlue);
        c.Lettering(text, 256, 196, 58 * (1.8f - 0.8f * pop), top, bottom, 0.5f, 0.2f, false, true, pop);
        c.Text(SoloVerdict(), 256, 226, 15, Style.Fade(win ? Style.Amber : Canvas.White, pop), 0.5f, 0.15f, 0.1f, 0.3f);
    }

    private string SoloVerdict() => _solo == null ? "" : (_solo.Goal, _outcome) switch
    {
        (Goal.Delivery, BattleOutcome.Win) => _solo.WallHits == 0 ? "NOT A SCRATCH ON THE TOFU" : "THE TOFU MADE IT",
        (Goal.Delivery, _) => "THE TOFU IS RUINED",
        (Goal.Thrill, BattleOutcome.Win) => "THEY WILL NEVER FORGET THIS RIDE",
        (Goal.Thrill, _) => "THEY WERE NOT IMPRESSED",
        (_, BattleOutcome.Win) => $"WITH {_solo.Param - _solo.Time:0.0} s TO SPARE",
        _ => "TOO SLOW",
    };

    private void ResultScreen(Canvas c)
    {
        float Row(int i) => Style.Ease((_t - (0.15f + 0.12f * i)) / 0.15f);
        if (_report != null) BattleScreens.Sheet(c, _report, Row);
        else
        {
            c.Fill(Overlay.Rgba(0, 0, 0, 0.25f));
            var s = _solo!;
            c.Sheet(30, 78, 252, 300, "Result");
            void Line(int i, float y, string label, string value, uint color)
            {
                var a = Row(i);
                c.Rule(30, 252, y + 6);
                if (a <= 0) return;
                c.Text(label, 36, y - 2, 9.5f, Style.Fade(Canvas.White, a), 0, 0.2f, 0, 0.2f);
                c.Fit(value, 246, y + 2, 140, 1, Style.Fade(color, a), 0.15f, 0, 17);
            }
            Line(0, 104, "RESULT", _outcome == BattleOutcome.Win ? "CLEAR" : _reason, _outcome == BattleOutcome.Win ? Style.Amber : Canvas.White);
            Line(1, 134, "TIME", Style.Time(s.Time), Canvas.White);
            Line(2, 164, "LIMIT", s.Goal == Goal.TimeLimit ? Style.Time(s.Param) : "-", Canvas.White);
            Line(3, 194, "WALL HITS", s.Goal == Goal.Delivery ? $"{s.WallHits} / {StoryRules.DeliveryHits}" : $"{s.WallHits}", Canvas.White);
            Line(4, 224, "DRIFT", s.Goal == Goal.Thrill ? $"{s.Drift:#,0} / {StoryRules.ThrillPoints:#,0}" : $"{s.Drift:#,0}", Canvas.White);
        }
        // the story's own sheet: chapter, goal, how often raced
        var b = Style.Ease((_t - 0.4f) / 0.2f);
        c.Sheet(268, 270, 486, 350, "Story", b);
        c.Fit($"CHAPTER {Chapter + 1}  {Text.Title}", 276, 292, 202, 0, Style.Fade(Canvas.White, b), 0.15f, 0, 13);
        var gy = 312f;
        foreach (var l in CarGuide.Wrap(c.O.Font!, StoryRules.Describe(Goal, StoryRules.Limit(Data)), 10.5f, 200))
        {
            c.Text(l, 276, gy, 10.5f, Style.Fade(Amber, b), 0, 0.12f);
            gy += 13;
        }
        c.Text($"RACES {Progress.Count(Key(Chapter) + "/tries")}", 478, 340, 10, Style.Fade(Grey, b), 1, 0.12f);
        var win = _outcome == BattleOutcome.Win;
        c.Lettering(win ? "CHAPTER CLEAR" : "TRY AGAIN", 377, 384, 24, win ? Overlay.Rgba(1, 0.85f, 0.3f) : Overlay.Rgba(0.55f, 0.65f, 1),
            win ? Overlay.Rgba(1, 0.38f, 0) : Canvas.WordBlue, 0.5f, 0.2f, false, true, b);
        for (var i = 0; i < Buttons.Length; i++)
            c.Button(24 + i * 128, 392, 120, 30, Buttons[i], i == 0 ? Canvas.ButtonKind.Positive : Canvas.ButtonKind.Negative, b);
        var x = 24 + _row * 128;
        c.Glow(x - 4, 388, x + 124, 426, Canvas.Pulse(Theta), b);
    }

    private void EndingScreen(Canvas c)
    {
        c.Fill(Overlay.Rgba(0, 0, 0, 0.7f));
        var a = Style.Ease(_t / 1.2f);
        c.Lettering("THE END", 256, 200, 56, Overlay.Rgba(1, 0.85f, 0.3f), Overlay.Rgba(1, 0.38f, 0), 0.5f, 0.2f, true, true, a);
        c.Text("STORY COMPLETE - ALL 31 CHAPTERS CLEARED", 256, 240, 14, Style.Fade(Canvas.White, a), 0.5f, 0.15f, 0.08f);
        c.Text("From the ghost of Akina to Project D. Thank you for playing.", 256, 266, 11.5f, Style.Fade(Grey, a), 0.5f, 0.1f);
        if (_t > 3) Menu.Hint(c, "DECIDE: Chapter select");
    }

    /// <summary>
    ///     The chapter's goal in the race HUD, top right (below the battle panel when there is one): time left of a time limit,
    ///     tofu wall hits or the drift score of a run alone.
    /// </summary>
    public void BuildHud(Overlay o, int width, int height, Battle? battle)
    {
        if (!InRun || Current is not (Phase.Racing or Phase.Banner)) return;
        var line = _solo is { } s ? s.Goal switch
            {
                Goal.TimeLimit => ("TIME LEFT", Style.Time(MathF.Max(0, s.Param - s.Time)), s.Param - s.Time < 15),
                Goal.Delivery => ("WALL HITS", $"{s.WallHits} / {StoryRules.DeliveryHits}", s.WallHits >= StoryRules.DeliveryHits),
                _ => ("DRIFT", $"{s.Drift:#,0} / {StoryRules.ThrillPoints:#,0}", false),
            }
            : battle is { TimeLimit: > 0 } b ? ("TIME LEFT", Style.Time(MathF.Max(0, b.TimeLimit - b.Time)), b.TimeLimitWinner == 1 && b.TimeLimit - b.Time < 15)
            : ((string, string, bool)?)null;
        if (line is not var (label, value, warn)) return;
        var g = Style.Safe(width, height);
        var u = g.U;
        var top = g.Top + (battle != null ? BattleHud.H + 14 : 0) * u;
        Vector2 min = new(g.Right - 300 * u, top), max = new(g.Right, top + 64 * u);
        Style.Slanted(o, min, max, Style.Panel, -0.22f);
        o.Rect(Vector2.Round(new Vector2(max.X - 5 * u, min.Y)), Vector2.Round(max), Style.Red);
        Style.Label(o, $"CHAPTER {Chapter + 1}", new Vector2(min.X + 40 * u, min.Y + 22 * u), 13 * u, Style.Dim);
        Style.Label(o, label, new Vector2(max.X - 22 * u, min.Y + 22 * u), 13 * u, Style.Dim, 1);
        Style.Label(o, value, new Vector2(max.X - 22 * u, min.Y + 54 * u), 28 * u, warn ? Style.Red : Style.Text, 1, Style.Slant, 0.3f * u);
    }

    /// <summary>Height of <see cref="BuildHud"/>'s panel in HUD units (the music toast goes below it), 0 when none.</summary>
    public float HudHeight(Battle? battle) => InRun && (_solo != null || battle is { TimeLimit: > 0 }) ? 64 + 14 : 0;
}
