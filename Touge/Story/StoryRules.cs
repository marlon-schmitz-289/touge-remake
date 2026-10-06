using Kansei.Physics;
using Touge.Formats;
using Touge.Race;

namespace Touge.Story;

/// <summary>
///     What a chapter asks of the player, from the original's objective code (<see cref="StoryScript.Chapter.Rule"/>, a bit
///     field: 1 reach the goal, 2 positions count, 4 a time limit in <see cref="StoryScript.Chapter.Param"/> s, 8 a special
///     run) and whether there is an opponent. Our reading of the codes, checked against the scenes (FORMATS.md "Story").
/// </summary>
public enum Goal
{
    /// <summary>1: side by side, first to the goal (or the breakaway gap).</summary>
    Race,
    /// <summary>2: the player leads and must not be passed before the goal (Kyoichi at Irohazaka, Daiki's first run).</summary>
    Escape,
    /// <summary>3, 9: the player follows and must pass and hold the lead.</summary>
    Chase,
    /// <summary>6: the player follows and must pass within the time limit (the ghost of Akina).</summary>
    ChaseInTime,
    /// <summary>4: stay with the rival until the time limit runs out (the seminar, death matches).</summary>
    Survive,
    /// <summary>5: alone, reach the goal within the time limit.</summary>
    TimeLimit,
    /// <summary>7: alone, uphill with the tofu: at most <see cref="StoryRules.DeliveryHits"/> wall hits.</summary>
    Delivery,
    /// <summary>8: alone with passengers: a drift score of at least <see cref="StoryRules.ThrillPoints"/>.</summary>
    Thrill,
}

/// <summary>Story rivals that are not in the quick-battle roster (<see cref="Rivals"/>); the car comes from the chapter table.</summary>
public static class StoryRivals
{
    public static readonly Rivals.Rival[] Extra =
    [
        new("kenta", "KENTA NAKAMURA", "AKAGI REDSUNS", "S14Q", new(0.62f, 0.55f, 0.5f)),
        new("couple", "THE S13 COUPLE", "AKAGI REGULARS", "S13", new(0.55f, 0.35f, 0.6f)),
        new("toru", "TORU SUETSUGU", "SEVEN STAR LEAF", "NA6C", new(0.75f, 0.55f, 0.4f)),
        new("atsuro", "ATSURO KAWAI", "SEVEN STAR LEAF", "ER34", new(0.78f, 0.6f, 0.35f)),
        new("daiki", "DAIKI NINOMIYA", "TODO SCHOOL", "EK9", new(0.85f, 0.6f, 0.2f)),
        new("sakai", "HIROYA SAKAI", "TODO SCHOOL", "INTGR", new(0.85f, 0.55f, 0.3f)),
        new("tomo", "TOMOYUKI TACHI", "TODO SCHOOL", "EK9", new(0.92f, 0.6f, 0.25f)),
    ];

    /// <summary>
    ///     Rival <paramref name="id"/> driving the chapter's car <paramref name="car"/> (HCAR id) in chapter <paramref name="chapter"/>:
    ///     the character's style with the chapter's skill (<see cref="StoryRules.RivalSkill"/>; never above the character's own).
    /// </summary>
    public static Rivals.Rival Find(string id, string car, int chapter)
    {
        var r = Extra.FirstOrDefault(x => x.Id == id) ?? Rivals.All.First(x => x.Id == id);
        return r with { Car = car, Style = r.Style with { Skill = MathF.Min(r.Style.Skill, StoryRules.RivalSkill(chapter)) }, Power = StoryRules.RivalPower(chapter) };
    }
}

public static class StoryRules
{
    /// <summary>Delivery: wall hits allowed (the tofu survives three knocks), and how long apart two count as two.</summary>
    public const int DeliveryHits = 3;
    public const float HitGap = 0.5f;
    /// <summary>Thrill: drift points the passengers want to see (the code's 100 × 100, <see cref="Ui.DriftMeter"/> scale).</summary>
    public const float ThrillPoints = 10000;

    /// <summary>
    ///     Rival skill per chapter on the one skill scale (<see cref="Kansei.Physics.RivalPilot.Pace"/>), from two calibrations
    ///     (<c>--story-check calibrate --player-skill k</c>: the highest skill the autopilot as a player of skill k — with his
    ///     mistakes, in the chapter's car, with the game's rubber band — still beats): a beginner (0.2, EASY) for the first chapters
    ///     shading into a NORMAL player (0.55) for the last (weight (chapter / 30)²: part 1 stays a beginner's), less 0.03. Where even 0.1 is too strong for the hero's
    ///     car the rival's car runs detuned (<see cref="RivalPower"/>); the last battle is a strong driver in a detuned car
    ///     (<see cref="Finale"/>). Never above the character's own skill (<see cref="StoryRivals.Find"/>). Not checked with
    ///     human players yet.
    /// </summary>
    public static float RivalSkill(int chapter)
    {
        if (chapter == Finale.Chapter) return Finale.Skill;
        if (!Normal.TryGetValue(chapter, out var n)) return 0.1f + 0.4f * chapter / 30;
        var w = chapter / 30f;
        return Math.Clamp(float.Lerp(Beginner.GetValueOrDefault(chapter, 0.1f), n, w * w) - 0.03f, 0, 1);
    }

    /// <summary>
    ///     Engine torque of the rival's car per chapter (1 = stock): where even 0.1 outruns a beginner his calibrated torque
    ///     less 0.05, shading to stock like the skill; the finale's.
    /// </summary>
    public static float RivalPower(int chapter)
    {
        if (chapter == Finale.Chapter) return Finale.Power;
        var w = chapter / 30f;
        return Power.TryGetValue(chapter, out var p) ? float.Lerp(p - 0.05f, 1, w * w) : 1;
    }

    /// <summary>Calibrated with --player-skill 0.2 (README, balanced cars): chapter → highest rival skill the beginner beats (missing: not even 0.1, see <see cref="Power"/>).</summary>
    private static readonly Dictionary<int, float> Beginner = new()
    {
        [2] = 0.114f, [3] = 0.12f, [6] = 0.15f, [7] = 0.213f, [8] = 0.156f, [9] = 0.283f, [10] = 0.1f, [11] = 0.12f, [12] = 0.08f, [13] = 0.367f,
        [15] = 0.297f, [16] = 0.297f, [17] = 0.184f, [19] = 0.311f, [20] = 0.1f, [22] = 0.17f, [23] = 0.255f, [24] = 0.156f, [25] = 0.142f,
        [26] = 0.213f, [27] = 0.241f, [28] = 0.227f, [29] = 0.986f,
    };

    /// <summary>Calibrated with --player-skill 0.55 (README, balanced cars): chapter → highest rival skill the NORMAL player beats.</summary>
    private static readonly Dictionary<int, float> Normal = new()
    {
        [2] = 0.353f, [3] = 0.494f, [6] = 0.578f, [7] = 0.536f, [8] = 0.536f, [9] = 0.747f, [10] = 0.255f, [11] = 0.423f, [12] = 0.395f,
        [13] = 0.958f, [15] = 0.93f, [16] = 0.677f, [17] = 0.508f, [19] = 0.634f, [20] = 0.395f, [21] = 0.156f, [22] = 0.494f, [23] = 0.831f,
        [24] = 0.395f, [25] = 0.48f, [26] = 0.578f, [27] = 0.536f, [28] = 0.6f, [29] = 0.986f, [30] = 0.55f,
    };

    /// <summary>Where 0.1 is still too strong for the beginner: chapter → torque factor he beats at 0.1 (--player-skill 0.2; 21: Seiji's Evo against Keisuke's FD).</summary>
    private static readonly Dictionary<int, float> Power = new() { [21] = 0.8f };

    /// <summary>
    ///     The last battle (Bunta): a driver of skill 0.6 (clean lines, drifts) in a detuned car, calibrated with STORY_CAL_AT=0.65
    ///     --player-skill 0.55 (torque 0.883 beaten): the NORMAL player wins it, but only just.
    /// </summary>
    public static readonly (int Chapter, float Skill, float Power) Finale = (30, 0.6f, 0.88f);

    /// <summary>
    ///     Time limit of a run alone. The original's seconds (220/210/190) are for its own pace; ours: the player's time on the
    ///     course alone in the chapter's car (<c>--story-check calibrate</c>, AE86: Akina downhill 5'24 a beginner / 4'53 NORMAL,
    ///     uphill 5'37 / 5'09), from the beginner's for the first chapter to NORMAL's for the last, times the original's limit /
    ///     200 s, at least 3 % over it (chapter 18's 190 s: nearly as fast as NORMAL).
    /// </summary>
    public static int Limit(StoryScript.Chapter c) =>
        Of(c.Rule, c.Rival >= 0) == Goal.TimeLimit && Par.TryGetValue((StoryScript.Courses[c.Course], c.Reverse), out var par)
            ? (int)MathF.Round(float.Lerp(par.Beginner, par.Normal, c.Index / 30f) * MathF.Max(1.03f, c.Param / 200f)) : c.Param;

    private static readonly Dictionary<(string, bool), (float Beginner, float Normal)> Par = new() { [("AKINA", false)] = (323.8f, 293.3f), [("AKINA", true)] = (336.6f, 308.9f) };

    public static Goal Of(int rule, bool rival) => (rule, rival) switch
    {
        (7, _) => Goal.Delivery,
        (8, _) => Goal.Thrill,
        (5, _) or (_, false) => Goal.TimeLimit, // chapter 5 lists Kenji's 180SX, but it is a race against the clock up to the summit
        (2, _) => Goal.Escape,
        (3 or 9, _) => Goal.Chase,
        (6, _) => Goal.ChaseInTime,
        (4, _) => Goal.Survive,
        _ => Goal.Race, // 1, and 13 (Myogi in the rain: its parameter is no time)
    };

    public static bool Solo(Goal g) => g is Goal.TimeLimit or Goal.Delivery or Goal.Thrill;

    /// <summary>One line for the chapter select and the HUD.</summary>
    public static string Describe(Goal g, int param) => g switch
    {
        Goal.Race => "WIN THE BATTLE: FIRST TO THE GOAL",
        Goal.Escape => "YOU LEAD: REACH THE GOAL WITHOUT BEING PASSED",
        Goal.Chase => "YOU FOLLOW: PASS AND HOLD THE LEAD",
        Goal.ChaseInTime => $"YOU FOLLOW: PASS WITHIN {Ui.Style.Time(param)[..^4]}",
        Goal.Survive => $"STAY WITH HIM FOR {param} SECONDS",
        Goal.TimeLimit => $"REACH THE GOAL WITHIN {Ui.Style.Time(param)[..^4]}",
        Goal.Delivery => $"DELIVER THE TOFU: NO MORE THAN {DeliveryHits} WALL HITS",
        Goal.Thrill => $"THRILL YOUR PASSENGERS: {ThrillPoints:#,0} DRIFT POINTS",
        _ => "",
    };

    /// <summary>The battle for a chapter with an opponent (null for a run alone).</summary>
    public static BattleSetup? Battle(Goal g, int param, Rivals.Rival rival) => g switch
    {
        Goal.Race => new(rival, BattleRule.Race),
        Goal.Escape => new(rival, BattleRule.LeadChase, 0, new BattleTerms(DrawGap: 0)), // still in front at the goal = escaped
        Goal.Chase => new(rival, BattleRule.LeadChase),
        Goal.ChaseInTime => new(rival, BattleRule.LeadChase, 1, new BattleTerms(TimeLimit: param, TimeLimitWinner: 1)),
        Goal.Survive => new(rival, BattleRule.Race, 1, new BattleTerms(Breakaway: 4, TimeLimit: param, TimeLimitWinner: 0)),
        _ => null,
    };
}

/// <summary>
///     Judge of a run alone (<see cref="Goal.TimeLimit"/>, <see cref="Goal.Delivery"/>, <see cref="Goal.Thrill"/>): fed every
///     tick with the lap timer's state, wall contact and the drift total; <see cref="Outcome"/> once decided (then it stays).
/// </summary>
public sealed class SoloJudge(Goal goal, int param)
{
    public Goal Goal => goal;
    public int Param => param;
    public BattleOutcome Outcome { get; private set; }
    /// <summary>CLEAR, TIME UP, TOO MANY HITS, NOT ENOUGH.</summary>
    public string Reason { get; private set; } = "";
    public int WallHits { get; private set; }
    public float Drift { get; private set; }
    public float Time { get; private set; }
    private float _apart = HitGapReady;
    private const float HitGapReady = 99;

    /// <param name="finished">The timer stopped at the goal; <paramref name="running"/> it runs.</param>
    public void Update(bool running, bool finished, float time, bool wall, float drift, float dt)
    {
        if (Outcome != BattleOutcome.None) return;
        (Time, Drift) = (time, drift);
        if (wall)
        {
            if (_apart >= StoryRules.HitGap && running) WallHits++;
            _apart = 0;
        }
        else _apart += dt;
        switch (goal)
        {
            case Goal.TimeLimit when running && time > param:
                End(BattleOutcome.Lose, "TIME UP");
                break;
            case Goal.Delivery when WallHits > StoryRules.DeliveryHits:
                End(BattleOutcome.Lose, "TOO MANY HITS");
                break;
            default:
                if (finished)
                {
                    var ok = goal switch { Goal.TimeLimit => time <= param, Goal.Thrill => drift >= StoryRules.ThrillPoints, _ => true };
                    End(ok ? BattleOutcome.Win : BattleOutcome.Lose, ok ? "CLEAR" : goal == Goal.Thrill ? "NOT ENOUGH" : "TIME UP");
                }
                break;
        }
    }

    private void End(BattleOutcome o, string reason) => (Outcome, Reason) = (o, reason);
}
