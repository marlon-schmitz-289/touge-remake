using System.Numerics;
using Kansei.Graphics;

namespace Touge.Ui;

/// <summary>
///     Now-playing toast of the race music, top right in the HUD's safe frame: a dark slanted plate with an amber edge,
///     "NOW PLAYING" with three bouncing level bars, the title and the artist. Slides in when a song starts or resumes,
///     stays <see cref="Hold"/> s, slides out; on the pause screen it stays.
/// </summary>
public static class NowPlaying
{
    public const float In = 0.35f, Hold = 5, Out = 0.4f;

    /// <summary>Visibility 0..1 at <paramref name="since"/> s after the song started.</summary>
    public static float Visible(float since, bool hold) =>
        hold ? 1 : Style.Ease(since / In) * Style.Ease((In + Hold + Out - since) / Out);

    public static void Draw(Overlay o, int width, int height, Jukebox.Song song, float since, bool hold)
    {
        var a = Visible(since, hold);
        if (a <= 0) return;
        var g = Style.Safe(width, height);
        var u = g.U;
        var w = MathF.Max(300 * u, MathF.Max(o.Font!.Measure(song.Title, 28 * u), o.Font.Measure(song.Artist, 17 * u)) + 64 * u);
        var h = 92 * u;
        // slides in from beyond the right edge
        var right = g.Right + (1 - a) * (w + (width - g.Right));
        Vector2 min = new(right - w, g.Top), max = new(right, g.Top + h);
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
        Style.Label(o, song.Title, new Vector2(x, min.Y + 58 * u), 28 * u, Style.Text, 1, Style.Slant, 0.3f * u);
        Style.Label(o, song.Artist, new Vector2(x, min.Y + 80 * u), 17 * u, Style.Dim, 1);
    }
}
