using System.Numerics;
using Kansei.Graphics;

namespace Touge.Ui;

/// <summary>
///     Now-playing toast of the race music, top right in the HUD's safe frame: a dark slanted plate with an amber edge,
///     "NOW PLAYING" with three bouncing level bars, the title and the artist. Slides in when a song starts or resumes,
///     stays <see cref="Hold"/> s, slides out; on the pause screen it stays (Options → HUD → NOW PLAYING). It goes below the
///     top-right panels (<see cref="Below"/>) and narrows, text shrinking, rather than run into the timing or drift panel.
/// </summary>
public static class NowPlaying
{
    public const float In = 0.35f, Hold = 5, Out = 0.4f;

    /// <summary>Visibility 0..1 at <paramref name="since"/> s after the song started.</summary>
    public static float Visible(float since, bool hold) =>
        hold ? 1 : Style.Ease(since / In) * Style.Ease((In + Hold + Out - since) / Out);

    /// <summary>
    ///     HUD units from the top of the safe frame down to the toast: below the battle panel or the versus position panel
    ///     (<paramref name="versus"/> = its height in units of its own view, 0: none; a top/bottom split view is half as
    ///     high), then below the Story goal panel (<paramref name="story"/> units incl. its gap, 0: none).
    /// </summary>
    public static float Below(bool battle, float versus, bool splitTopBottom, float story) =>
        (battle ? BattleHud.H + 14 : versus <= 0 ? 0 : splitTopBottom ? (versus + 14) / 2 + 14 : versus + 14) + story;

    /// <summary>The toast's box (narrowed to stay clear of <paramref name="clear"/>) at visibility <paramref name="a"/>, and its text scale.</summary>
    public static (Vector2 Min, Vector2 Max, float Text) Box(Overlay o, int width, int height, Jukebox.Song song, float a, float below,
        ReadOnlySpan<(Vector2 Min, Vector2 Max)> clear)
    {
        var g = Style.Safe(width, height);
        var u = g.U;
        var h = 92 * u;
        var top = g.Top + below * u;
        var text = MathF.Max(o.Font!.Measure(song.Title, 28 * u), o.Font.Measure(song.Artist, 17 * u));
        var w = MathF.Max(300 * u, text + 64 * u);
        // panels along the top (timing, drift combo; split views: both views') in the way: narrower if 300 units still fit, else below them
        foreach (var (bMin, bMax) in clear.ToArray().OrderBy(b => b.Min.Y))
        {
            if (bMin.Y >= top + h || bMax.Y <= top || bMax.X <= g.Right - w - 12 * u || bMin.X >= g.Right) continue;
            var room = g.Right - bMax.X - 12 * u;
            if (room >= 300 * u) w = room;
            else top = bMax.Y + 12 * u;
        }
        // slides in from beyond the right edge
        var right = g.Right + (1 - a) * (w + (width - g.Right));
        return (new Vector2(right - w, top), new Vector2(right, top + h), Math.Clamp((w - 64 * u) / MathF.Max(text, 1), 0.4f, 1));
    }

    public static void Draw(Overlay o, int width, int height, Jukebox.Song song, float since, bool hold, float below = 0,
        ReadOnlySpan<(Vector2 Min, Vector2 Max)> clear = default)
    {
        var a = Visible(since, hold);
        if (a <= 0) return;
        var u = Style.Safe(width, height).U;
        var (min, max, k) = Box(o, width, height, song, a, below, clear);
        Style.Slanted(o, min, max, Style.Panel, -0.22f);
        o.Rect(Vector2.Round(new Vector2(max.X - 5 * u, min.Y)), Vector2.Round(max), Style.Amber);
        var x = max.X - 22 * u;
        // level bars left of the label, bouncing with the clock (not the audio)
        var lw = Style.Label(o, "NOW PLAYING", new Vector2(x, min.Y + 26 * u), 15 * u, Style.Amber, 1, 0, 0.3f * u);
        for (var i = 0; i < 3; i++)
        {
            var bh = (4 + 8 * (0.5f + 0.5f * MathF.Sin(since * (7 + 2.3f * i) + i * 1.7f))) * u;
            var bx = x - lw - (12 + (2 - i) * 6) * u;
            o.Rect(Vector2.Round(new Vector2(bx, min.Y + 25 * u - bh)), Vector2.Round(new Vector2(bx + 4 * u, min.Y + 25 * u)), Style.Amber);
        }
        Style.Label(o, song.Title, new Vector2(x, min.Y + 58 * u), 28 * u * k, Style.Text, 1, Style.Slant, 0.3f * u);
        Style.Label(o, song.Artist, new Vector2(x, min.Y + 80 * u), 17 * u * k, Style.Dim, 1);
    }
}
