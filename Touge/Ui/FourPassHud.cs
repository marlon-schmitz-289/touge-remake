using System.Numerics;
using Kansei.Graphics;

namespace Touge.Ui;

/// <summary>FOUR PASSES in the race HUD, top right (where the battle panel sits): the stage of four with its course and the total so far.</summary>
public static class FourPassHud
{
    /// <summary>Panel size in HUD units (the music toast goes below it).</summary>
    public const float W = 330, H = 112;

    public static void Build(Overlay o, int width, int height, FourPasses f, string course, LapTimer timer)
    {
        var g = Style.Safe(width, height);
        var u = g.U;
        var at = new Vector2(g.Right - W * u, g.Top);
        Style.Slanted(o, at, at + new Vector2(W, H) * u, Style.Panel, -0.22f * 150 / H);
        o.Rect(Vector2.Round(at + new Vector2(W - 5, 0) * u), Vector2.Round(at + new Vector2(W, H) * u), Style.Amber);
        var x0 = at.X + 44 * u;
        var x1 = at.X + (W - 22) * u;
        Style.Label(o, "FOUR PASSES", new Vector2(x0, at.Y + 28 * u), 17 * u, Style.Amber, 0, Style.Slant, 0.3f * u);
        Style.Label(o, $"STAGE {f.Index + 1}/{f.Stages.Count}", new Vector2(x1, at.Y + 28 * u), 19 * u, Style.Text, 1, Style.Slant, 0.3f * u);
        Style.Label(o, course, new Vector2(x1, at.Y + 50 * u), 13 * u, Style.Dim, 1);
        // the total runs on with the stage clock (the finished stages before it)
        var total = (f.Finished > f.Index ? f.Total - f.StageTime(f.Index) : f.Total) + timer.Time;
        var w = Style.Label(o, "TOTAL ", new Vector2(x0, at.Y + 94 * u), 17 * u, Style.Dim);
        Style.Label(o, Style.Time(total), new Vector2(x0 + w, at.Y + 94 * u), 34 * u, timer.Phase == LapTimer.State.Ready ? Style.Dim : Style.Text, 0, Style.Slant, 0.3f * u);
    }
}
