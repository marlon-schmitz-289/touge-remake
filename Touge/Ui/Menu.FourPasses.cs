using System.Numerics;
using Kansei.Graphics;

namespace Touge.Ui;

/// <summary>
///     FOUR PASSES (<see cref="Touge.FourPasses"/>) in the Time Attack flow: the twelfth course slot → weather (DRY/WET, the
///     original's choice; the passes are fixed, all at night) → maker/car/gearbox → stage 1 … after each stage a STAGE n CLEAR
///     banner and a stage sheet with the running total (NEXT / RETRY / EXIT) → after the fourth FINISH!!/NEW RECORD!! and the
///     final sheet (per-pass times, deltas against the record, total) with the Time Attack buttons. RETRY (also in the pause)
///     starts again from stage 1.
/// </summary>
public sealed partial class Menu
{
    /// <summary>The run's stages from the ELF (set by the game; null: the slot stays locked).</summary>
    public IReadOnlyList<FourPasses.Stage>? FourPassStages { get; set; }
    /// <summary>The four-pass run under way (null: plain Time Attack).</summary>
    public FourPasses? FourPass { get; private set; }

    private const int FourSlot = Slots - 1;
    private bool OnFourSlot => _slot == FourSlot && FourPassStages != null;
    private bool InFourPass => FourPass != null && _slot == FourSlot;

    private string[] Buttons => FreeBattle ? FreeBattleButtons : FourPass is { Done: false } ? FourPasses.StageButtons : ResultButtons;

    /// <summary>Course slot 12 decided: a fresh run, on to the weather (route and time of day are the original's, fixed).</summary>
    private void StartFourPass()
    {
        FourPass = new FourPasses(FourPassStages!);
        (_night, _fog) = (true, false);
        Go(Screen.Weather);
    }

    /// <summary>Weather decided: the record of that weather is the reference of the run.</summary>
    private void FourPassWeather()
    {
        FourPass!.Wet = _wet;
        FourPass.Best = settings.Best.GetValueOrDefault(settings.RunKey(FourPasses.CourseKey(_wet), false));
    }

    /// <summary>Retry / a new car: from stage 1 again through the loading screen (the course may change).</summary>
    private void RestartFourPass()
    {
        FourPass!.Reset();
        FourPassWeather(); // the record may have changed with the last run
        Leave(Screen.Loading, Action.None);
    }

    /// <summary>--menu fourpasses…: a run with the given stage splits finished (screenshots of the sheets).</summary>
    public void ShowFourPass(bool wet, float[][] stageSplits, float[]? best, bool sheet)
    {
        FourPass = new FourPasses(FourPassStages!) { Wet = wet, Best = best };
        (_slot, _night, _wet) = (FourSlot, true, wet);
        for (var i = 0; i < stageSplits.Length; i++)
        {
            if (i > 0) FourPass.Next();
            FourPass.Finish(stageSplits[i], 1200 * (i + 1));
        }
        _run = new Run(FourPass.StageTime(FourPass.Index), stageSplits[^1], new float?[LapTimer.Sectors], null, FourPass.NewRecord, FourPass.Drift);
        _back.Clear();
        Enter(sheet ? Screen.Result : Screen.Finish, false);
    }

    /// <summary>--menu fourpasses: the course grid on the twelfth slot.</summary>
    public void ShowFourPassSlot() => _slot = FourSlot;

    // ---------------------------------------------------------------- drawing

    /// <summary>The monitor: the four stage maps in a 2 × 2 grid with their numbers.</summary>
    private void FourPassMaps(Canvas c)
    {
        for (var i = 0; i < FourPassStages!.Count; i++)
        {
            var s = FourPassStages[i];
            float x = 34 + i % 2 * 100, y = 90 + i / 2 * 100;
            MapLine(c, CourseOf(s), s.Reverse, x + 8, y + 8, x + 92, y + 92);
            c.Text($"{i + 1}", x + 4, y + 16, 14, Canvas.Yellow, 0, 0.15f, 0.08f);
        }
    }

    private Catalog.Course CourseOf(FourPasses.Stage s) => catalog.Courses.First(c => c.Id == s.Course);

    /// <summary>The info panel: total length, the passes, the record of the shown weather.</summary>
    private void FourPassStats(Canvas c)
    {
        var length = FourPassStages!.Sum(s => CourseOf(s).LengthM);
        var best = settings.Best.GetValueOrDefault(Settings.BestKey(FourPasses.CourseKey(Current == Screen.Weather && Choices()[_choice] == "WET"), false));
        Stat(c, "LENGTH", FormattableString.Invariant($"{length / 1000:0.0} km"), 274);
        Stat(c, "PASSES", $"{FourPassStages!.Count}", 352);
        Stat(c, "BEST", Style.Time(best?[^1]), 418);
    }

    /// <summary>Line under the course name: the passes in order with their direction, at night.</summary>
    private string FourPassRoute() =>
        string.Join("  >  ", FourPassStages!.Select(s => CourseOf(s) is var c && c.ForwardDownhill != s.Reverse ? c.Name : $"{c.Name} (UP)")) + "   NIGHT";

    /// <summary>Under the telop: which pass of how many, with the total so far.</summary>
    private void FourPassTelop(Canvas c, float shift)
    {
        if (!InFourPass) return;
        var f = FourPass!;
        c.Text($"FOUR PASSES   STAGE {f.Index + 1} / {f.Stages.Count}", 490 + shift, 258, 17, Canvas.Yellow, 1, 0.15f, 0.08f, 0.3f);
        if (f.Finished > 0) c.Text($"TOTAL {Style.Time(f.Total)}", 490 + shift, 280, 13, Canvas.White, 1, 0.12f, 0.08f);
    }

    /// <summary>Finish banner: STAGE n CLEAR with the stage time and the total so far; after the last FINISH!!/NEW RECORD!! with the total.</summary>
    private void FourPassBanner(Canvas c)
    {
        var f = FourPass!;
        var pop = Style.Ease(_t / 0.25f);
        var last = f.Done;
        var text = !last ? $"STAGE {f.Finished} CLEAR" : f.NewRecord ? "NEW RECORD!!" : "FINISH!!";
        c.Lettering(text, 256, 196, MathF.Min(58, 420 * c.Kx / c.O.Font!.Measure(text, c.Ky)) * (1.8f - 0.8f * pop), Overlay.Rgba(1, 0.85f, 0.3f), Overlay.Rgba(1, 0.38f, 0), 0.5f, 0.2f, false, true, pop);
        if (!last) c.Text($"{CourseOf(f.Current).Name}   {Style.Time(f.StageTime(f.Index))}", 256, 226, 15, Style.Fade(Canvas.White, pop), 0.5f, 0.15f, 0.1f, 0.3f);
        else if (f.NewRecord) c.Text("Four passes record updated", 256, 222, 15, Style.Fade(Overlay.Rgba(1, 0.15f, 0.1f), pop), 0.5f, 0.15f, 0.1f, 0.3f);
        c.Text((last ? "" : "TOTAL ") + Style.Time(f.Total), 256, 262, 30, Style.Fade(Canvas.White, pop), 0.5f, 0.15f, 0.06f, 0.5f);
    }

    /// <summary>
    ///     The stage / final sheet: total, the four passes (time, delta of the total after it against the record), the record
    ///     with the difference (between stages: the pace so far) and the drift points; the same 8 tallied rows as Time Attack.
    /// </summary>
    private void FourPassSheet(Canvas c)
    {
        var f = FourPass!;
        c.Fill(Overlay.Rgba(0, 0, 0, 0.25f));
        float Row(int i) => Style.Ease((_t - (RowFirst + RowStep * i)) / 0.15f);
        void Line(int i, float x0, float x1, float y, string label, string value, uint color, string? extra = null, uint extraColor = 0)
        {
            var a = Row(i);
            c.Rule(x0, x1, y + 6, 1);
            if (a <= 0) return;
            c.Text(label, x0 + 6, y - 2, 9.5f, Style.Fade(Canvas.White, a), 0, 0.2f, 0, 0.2f);
            c.Text(value, x1 - (extra != null ? 52 : 6), y + 2, 17, Style.Fade(color, a), 1, 0.15f, 0, 0.3f);
            if (extra != null) c.Text(extra, x1 - 6, y + 2, 11, Style.Fade(extraColor, a), 1, 0.15f);
        }
        var dim = Overlay.Rgba(1, 1, 1, 0.35f);
        c.Sheet(30, 78, 252, 300, f.Done ? "Result" : $"Stage {f.Finished} / {f.Stages.Count}");
        Line(0, 30, 252, 104, f.Done ? "TOTAL TIME" : "TOTAL SO FAR", Style.Time(f.Total), Canvas.White);
        for (var i = 0; i < f.Stages.Count; i++)
        {
            var done = i < f.Finished;
            var d = f.Delta(i);
            Line(1 + i, 30, 252, 134 + i * 30, $"PASS {i + 1}  {CourseOf(f.Stages[i]).Name}", done ? Style.Time(f.StageTime(i)) : "-", done ? Canvas.White : dim,
                d is { } dd ? Style.Delta(dd) : null, d is <= 0 ? Style.Green : Style.Red);
        }
        c.Sheet(268, 78, 486, 194, "Record");
        c.Text($"FOUR PASSES  {(f.Wet ? "WET" : "DRY")}", 274, 98, 11, Canvas.White, 0, 0.15f, 0, 0.2f);
        var best = f.NewRecord ? f.Total : f.Best?[^1];
        Line(5, 268, 486, 130, "BEST TIME", Style.Time(best), Canvas.White);
        var diff = f.Delta(f.Finished - 1);
        Line(6, 268, 486, 162, f.Done ? "DIFFERENCE" : "PACE", diff is { } p ? Style.Delta(p) : "-", diff is <= 0 ? Style.Green : Canvas.White);
        c.Sheet(268, 220, 486, 262, "Point");
        Line(7, 268, 486, 248, "DRIFT POINTS", $"{(int)f.Drift} pts", Canvas.White);
        if (f.NewRecord && Row(ResultRows - 1) > 0)
        {
            var s = Row(ResultRows - 1);
            c.Lettering("NEW RECORD!!", 377, 300, 26 * (1.6f - 0.6f * s), Overlay.Rgba(1, 0.85f, 0.3f), Overlay.Rgba(1, 0.38f, 0), 0.5f, 0.2f, false, true, s);
        }
        if (!f.Done && Row(ResultRows - 1) > 0)
        {
            var next = f.Stages[f.Finished];
            c.Text($"NEXT  STAGE {f.Finished + 1}   {CourseOf(next).Name}   {Catalog.DirectionName(CourseOf(next), next.Reverse)}", 377, 300, 13, Style.Fade(Canvas.Yellow, Row(ResultRows - 1)), 0.5f, 0.15f, 0.08f);
        }
        ResultChoice(c);
    }

    /// <summary>RECORDS row: FOUR PASSES with its DRY and WET records (<paramref name="x"/>(r) = column of route r).</summary>
    internal static void FourPassRecords(Canvas c, Settings settings, float y, float nameX, float nameW, Func<int, float> x, float size, bool tags)
    {
        c.Fit("FOUR PASSES", nameX, y + 21, nameW, 0, Canvas.White, 0.15f, 0.06f, size + 3);
        for (var r = 0; r < 2; r++)
        {
            var best = settings.Best.GetValueOrDefault(Settings.BestKey(FourPasses.CourseKey(r == 1), false));
            var tag = r == 1 ? "WET" : "DRY";
            if (tags)
            {
                c.O.Rect(Vector2.Round(c.P(x(r), y + 6)), Vector2.Round(c.P(x(r) + 64, y + 16)), Overlay.Rgba(0.95f, 0.95f, 0.96f));
                c.Fit(tag, x(r) + 32, y + 14.5f, 58, 0.5f, Canvas.Shade(0.1f, 0.1f, 0.12f, 1), 0, 0, 9);
                c.Text(Style.Time(best?[^1]), x(r) + 70, y + 19, size, best == null ? Overlay.Rgba(1, 1, 1, 0.35f) : Canvas.White, 0, 0.15f);
            }
            else
            {
                c.Fit(tag, x(r), y + 11, 90, 0, Overlay.Rgba(0.72f, 0.73f, 0.75f), 0, 0, 8);
                c.Text(Style.Time(best?[^1]), x(r), y + 24, size, best == null ? Overlay.Rgba(1, 1, 1, 0.35f) : Canvas.White, 0, 0.15f);
            }
        }
    }
}
