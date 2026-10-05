using Touge.Formats;
using Touge.Ui;

namespace Touge;

/// <summary>
///     FOUR PASSES (四峠走破, the twelfth course slot of Time Attack, mode byte 8 in the original): four passes back to back
///     with one total time. Stages from the ELF table 0x2A37F0 (4 × 8 bytes, copied to 0x328154 per stage by the mode's
///     module 0x204E50/0x204F10): AKAGI, AKINA, HAPPOGAHARA reversed, IROHAZAKA, all at night; the car stays the same, the
///     weather is the player's choice. A stage's time runs from its start line to its goal as in Time Attack; the total is
///     the sum. The record keeps the run's cumulative sector splits over all stages (4 per stage, <see cref="LapTimer.Sectors"/>),
///     so its last value is the total like every other <see cref="Settings.Best"/> entry and each stage's own splits can be
///     handed to the HUD's timer.
/// </summary>
public sealed class FourPasses(IReadOnlyList<FourPasses.Stage> stages)
{
    /// <param name="Course">Course id (COURSE.AFS).</param>
    /// <param name="Reverse">Direction byte ≠ 0: the course's _O line.</param>
    public sealed record Stage(string Course, bool Reverse);

    /// <summary>Address of the stage table in the ELF.</summary>
    public const int TableAt = 0x2A37F0, Count = 4;

    /// <summary>The result screen's buttons between stages (the original's ACTCHOICE act_next / act_retry / act_exit).</summary>
    public static readonly string[] StageButtons = ["NEXT", "RETRY", "EXIT"];

    public IReadOnlyList<Stage> Stages => stages;
    /// <summary>Stage being driven (0-based); <see cref="Next"/> moves on after a finished one.</summary>
    public int Index { get; private set; }
    public Stage Current => stages[Index];
    /// <summary>Chosen weather WET (rain over the night courses); records are kept per weather as in the original.</summary>
    public bool Wet { get; set; }
    /// <summary>The record when the run started (cumulative splits, last = total), null without one.</summary>
    public float[]? Best { get; set; }
    private readonly List<float> _splits = [];
    private readonly List<float> _drift = [];

    /// <summary>Stages finished so far.</summary>
    public int Finished => _drift.Count;
    public bool Done => Finished == stages.Count;
    /// <summary>Cumulative sector splits of the run so far (4 per finished stage).</summary>
    public float[] Splits => [.. _splits];
    public float Total => _splits.Count > 0 ? _splits[^1] : 0;
    public float Drift => _drift.Sum();
    /// <summary>Time of finished stage <paramref name="i"/>.</summary>
    public float StageTime(int i) => StageEnd(_splits, i) - StageEnd(_splits, i - 1);
    /// <summary>Time of stage <paramref name="i"/> in the record (null without one).</summary>
    public float? BestStageTime(int i) => Best is { } b ? StageEnd(b, i) - StageEnd(b, i - 1) : null;
    /// <summary>Total after stage <paramref name="i"/> minus the record's (null without one or before the stage is done).</summary>
    public float? Delta(int i) => Best != null && i < Finished ? StageEnd(_splits, i) - StageEnd(Best, i) : null;
    /// <summary>The finished run beats the record it started with.</summary>
    public bool NewRecord => Done && (Best == null || Total < Best[^1]);

    private static float StageEnd(IReadOnlyList<float> splits, int i) => i < 0 ? 0 : splits[(i + 1) * LapTimer.Sectors - 1];

    /// <summary>The current stage's own splits in the record (for the HUD's sector deltas), null without one.</summary>
    public float[]? StageBest()
    {
        if (Best == null) return null;
        var start = StageEnd(Best, Index - 1);
        return [.. Best.Skip(Index * LapTimer.Sectors).Take(LapTimer.Sectors).Select(t => t - start)];
    }

    /// <summary>The current stage finished with these cumulative sector splits (its own, from its start line) and drift points.</summary>
    public void Finish(IReadOnlyList<float> stageSplits, float drift)
    {
        if (Finished > Index) return; // once per stage
        var before = Total;
        foreach (var s in stageSplits.Take(LapTimer.Sectors)) _splits.Add(before + s);
        _drift.Add(drift);
    }

    /// <summary>On to the next stage after a finished one; false at the end.</summary>
    public bool Next()
    {
        if (Finished <= Index || Index + 1 >= stages.Count) return false;
        Index++;
        return true;
    }

    /// <summary>From the first stage again (retry, new car).</summary>
    public void Reset()
    {
        Index = 0;
        _splits.Clear();
        _drift.Clear();
    }

    /// <summary>Course key of the record (<see cref="Settings.BestKey"/>/<see cref="Settings.RunKey"/>), one per weather.</summary>
    public static string CourseKey(bool wet) => wet ? "FOURPASS_WET" : "FOURPASS";

    /// <summary>The stage table from the ELF (<see cref="TableAt"/>: chapter, ?, course, direction, weather, night, figure, ? per stage).</summary>
    public static Stage[] Read(ReadOnlySpan<byte> elf)
    {
        var list = new Stage[Count];
        for (var n = 0; n < Count; n++)
        {
            var e = elf.Slice(TableAt - StoryScript.ElfBase + 8 * n, 8);
            if (e[2] >= StoryScript.Courses.Length || e[5] != 1) throw new InvalidDataException($"four-pass table: entry {n} looks wrong ({Convert.ToHexString(e)})");
            list[n] = new Stage(StoryScript.Courses[e[2]], e[3] != 0);
        }
        return list;
    }
}
