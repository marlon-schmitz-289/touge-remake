using System.Numerics;
using Kansei.Graphics;

namespace Touge.Ui;

/// <summary>
///     Painter of the original-style front end: the original's 512×448 canvas (top-left origin) mapped to a centred 4:3
///     frame of the target (backdrops and header bands still fill any width), and its five materials rebuilt with overlay
///     shapes — brushed-chrome plates with screws, carbon panels, the pulsing yellow cursor frame, the grey drifting
///     tiled-logo backdrop (INID_BG), the red/blue marquee header band — plus the text styles (gradient words with outline,
///     racing banners, chamfered action buttons, teal result sheets). No original textures.
/// </summary>
public sealed class Canvas
{
    public static readonly uint White = Overlay.Rgba(1, 1, 1), Black = Overlay.Rgba(0, 0, 0), Yellow = Overlay.Rgba(1, 1, 0),
        HeaderRed = Overlay.Rgba(0.82f, 0.06f, 0.06f), HeaderBlue = Overlay.Rgba(0.06f, 0.06f, 0.94f),
        WordRed = Overlay.Rgba(0.88f, 0.19f, 0.06f), WordBlue = Overlay.Rgba(0.13f, 0.25f, 0.75f), BrushBlue = Overlay.Rgba(0.13f, 0.19f, 0.75f);

    public Overlay O { get; private set; } = null!;
    public int Width { get; private set; }
    public int Height { get; private set; }
    private float _s, _ox, _oy;

    public void Begin(Overlay o, int width, int height)
    {
        (O, Width, Height) = (o, width, height);
        _s = MathF.Min(width / 640f, height / 480f);
        (_ox, _oy) = ((width - 640 * _s) / 2, (height - 480 * _s) / 2);
    }

    /// <summary>Screen pixels per 640×480 pixel (line widths, radii).</summary>
    public float S => _s;
    public float Kx => 1.25f * _s;
    public float Ky => 480f / 448 * _s;
    /// <summary>Canvas x of the screen's left and right edge (beyond 0..512 on wide screens).</summary>
    public float Left => -_ox / Kx;
    public float Right => (Width - _ox) / Kx;

    public Vector2 P(float x, float y) => new(_ox + x * Kx, _oy + y * Ky);

    /// <summary>Canvas point of screen pixel <paramref name="px"/> (mouse hit tests), the inverse of <see cref="P"/>.</summary>
    public Vector2 Unproject(Vector2 px) => Kx > 0 ? new((px.X - _ox) / Kx, (px.Y - _oy) / Ky) : new(-1, -1);

    /// <summary>Cursor/PRESS START pulse of the original, 80..255 as 0..1, θ in degrees.</summary>
    public static float Pulse(float theta) => (80 + 175 * (1 + MathF.Cos(theta * MathF.PI / 180)) / 2) / 255;

    public static uint Shade(float r, float g, float b, float k, float a = 1) => Overlay.Rgba(r * k, g * k, b * k, a);

    public void Fill(uint color) => O.Rect(Vector2.Zero, new Vector2(Width, Height), color);

    /// <summary>Black fade over everything drawn so far, text included (0 = none, 1 = black).</summary>
    public void Fade(float fade, uint color = 0xFF000000)
    {
        if (fade <= 0) return;
        Fill(Style.Fade(color, fade));
        O.FadeText(1 - fade);
    }

    /// <summary>Text with em <paramref name="size"/> canvas pixels, baseline y; optional black outline of <paramref name="outline"/> × size.</summary>
    public float Text(string text, float x, float y, float size, uint color, float align = 0, float skew = 0, float outline = 0, float weight = 0)
    {
        var px = size * Ky;
        if (outline > 0) O.Text(text, P(x, y), px, Style.Fade(Black, (color >> 24) / 255f), align, px * outline + weight * _s, 0, skew);
        return O.Text(text, P(x, y), px, color, align, weight * _s, 0, skew) / Kx;
    }

    /// <summary>Text fitted to <paramref name="w"/> canvas pixels (the width of the original's baked label), at most <paramref name="max"/> em.</summary>
    public void Fit(string text, float x, float y, float w, float align, uint color, float skew = 0, float outline = 0, float max = float.MaxValue)
    {
        var size = MathF.Min(w * Kx / O.Font!.Measure(text, 1), max * Ky) / Ky;
        Text(text, x, y, size, color, align, skew, outline);
    }

    /// <summary>
    ///     Gradient lettering of the original's choice words, titles and banners: <paramref name="top"/> → <paramref name="bottom"/>
    ///     fill, black outline, optional white outer outline and a hard black drop shadow (3D drop).
    /// </summary>
    public void Lettering(string text, float x, float y, float size, uint top, uint bottom, float align = 0.5f, float skew = 0.2f, bool white = false, bool drop = true, float a = 1)
    {
        var px = size * Ky;
        var at = P(x, y);
        if (drop) O.Text(text, at + new Vector2(px * 0.05f, px * 0.06f), px, Style.Fade(Black, a * 0.9f), align, px * 0.07f, 0, skew);
        if (white) O.Text(text, at, px, Style.Fade(White, a), align, px * 0.075f, 0, skew);
        O.Text(text, at, px, Style.Fade(Black, a), align, px * 0.035f, 0, skew);
        O.Text(text, at, px, Style.Fade(top, a), align, 0, 0, skew, Style.Fade(bottom, a));
    }

    /// <summary>Brushed-chrome plate with a dark rim, horizontal streaks and four screws, tinted by <paramref name="lit"/>.</summary>
    public void Plate(float x, float y, float w, float h, float lit, float a = 1)
    {
        uint C(float v, float alpha = 1) => Style.Fade(Shade(v, v, v * 1.02f, lit), a * alpha);
        Vector2 min = Vector2.Round(P(x, y)), max = Vector2.Round(P(x + w, y + h));
        O.Rect(min, max, C(0.32f));
        Vector2 i0 = min + new Vector2(2, 2) * _s, i1 = max - new Vector2(2, 2) * _s;
        var mid = MathF.Round((i0.X + i1.X) / 2);
        O.RectGradient(i0, new Vector2(mid, i1.Y), C(0.62f), C(0.92f));
        O.RectGradient(new Vector2(mid, i0.Y), i1, C(0.92f), C(0.68f));
        var lines = Math.Clamp((int)(h / 3), 4, 12);
        for (var k = 0; k < lines; k++)
        {
            var yy = i0.Y + (i1.Y - i0.Y) * (k + 0.5f) / lines;
            O.Line(new Vector2(i0.X + 1, yy), new Vector2(i1.X - 1, yy), 1, k % 3 == 0 ? Style.Fade(Overlay.Rgba(1, 1, 1, 0.18f), a * lit) : Style.Fade(Overlay.Rgba(0, 0, 0, 0.06f), a));
        }
        O.Line(new Vector2(i0.X, i0.Y), new Vector2(i1.X, i0.Y), 1, C(1, 0.8f));
        O.Line(new Vector2(i0.X, i1.Y), new Vector2(i1.X, i1.Y), 1, C(0.4f));
        var sr = MathF.Min(3.2f, h / 6) * _s;
        var (dx, dy) = (MathF.Min(8, w / 6), MathF.Min(7, h / 4));
        foreach (var (sx, sy) in new[] { (x + dx, y + dy), (x + w - dx, y + dy), (x + dx, y + h - dy), (x + w - dx, y + h - dy) }) Screw(P(sx, sy), sr, lit, a);
    }

    public void Screw(Vector2 c, float r, float lit, float a = 1)
    {
        O.Disc(c, r, Style.Fade(Shade(0.3f, 0.3f, 0.3f, lit), a));
        O.Disc(c, r * 0.75f, Style.Fade(Shade(0.78f, 0.78f, 0.8f, lit), a));
        var w = MathF.Max(1, r * 0.3f);
        O.Line(c - new Vector2(r * 0.5f, 0), c + new Vector2(r * 0.5f, 0), w, Style.Fade(Shade(0.25f, 0.25f, 0.25f, lit), a));
        O.Line(c - new Vector2(0, r * 0.5f), c + new Vector2(0, r * 0.5f), w, Style.Fade(Shade(0.25f, 0.25f, 0.25f, lit), a));
    }

    /// <summary>Carbon-fibre panel: near-black with a fine diagonal weave, dark-steel frame, screws in the corners.</summary>
    public void Carbon(float x0, float y0, float x1, float y1, float a = 1, bool screws = true)
    {
        Vector2 min = Vector2.Round(P(x0, y0)), max = Vector2.Round(P(x1, y1));
        O.Rect(min, max, Style.Fade(Overlay.Rgba(0.33f, 0.34f, 0.36f), a));
        O.Line(new Vector2(min.X, min.Y + 0.5f), new Vector2(max.X, min.Y + 0.5f), 1, Style.Fade(Overlay.Rgba(0.6f, 0.62f, 0.65f), a));
        Vector2 i0 = min + new Vector2(3, 3) * _s, i1 = max - new Vector2(3, 3) * _s;
        O.Rect(i0, i1, Style.Fade(Overlay.Rgba(0.075f, 0.075f, 0.08f), a));
        // weave: two families of diagonal hairlines (x + y = c and x − y = c), cut to the inner rectangle
        var step = MathF.Max(3, 4 * _s);
        uint c0 = Style.Fade(Overlay.Rgba(0.15f, 0.15f, 0.16f), a), c1 = Style.Fade(Overlay.Rgba(0.02f, 0.02f, 0.025f), a);
        var (w, h) = (i1.X - i0.X, i1.Y - i0.Y);
        for (var c = step / 2; c < w + h; c += step)
        {
            // x + y = c: from (c, 0) to (0, c), clipped
            Vector2 a0 = new(MathF.Min(c, w), c - MathF.Min(c, w)), a1 = new(c - MathF.Min(c, h), MathF.Min(c, h));
            O.Line(i0 + a0, i0 + a1, 1, c0);
            Vector2 b0 = new(w - MathF.Min(c, w), c - MathF.Min(c, w)), b1 = new(w - (c - MathF.Min(c, h)), MathF.Min(c, h));
            O.Line(i0 + b0, i0 + b1, 1, c1);
        }
        if (!screws) return;
        var r = 3.2f * _s;
        var d = 9 * _s;
        foreach (var c in new[] { min + new Vector2(d, d), new Vector2(max.X - d, min.Y + d), new Vector2(min.X + d, max.Y - d), max - new Vector2(d, d) })
            Screw(c, r, 0.75f, a);
    }

    /// <summary>Rounded rectangle outline (lines + quarter arcs).</summary>
    public void RoundRect(Vector2 min, Vector2 max, float r, float width, uint color)
    {
        O.Line(new Vector2(min.X + r, min.Y), new Vector2(max.X - r, min.Y), width, color);
        O.Line(new Vector2(min.X + r, max.Y), new Vector2(max.X - r, max.Y), width, color);
        O.Line(new Vector2(min.X, min.Y + r), new Vector2(min.X, max.Y - r), width, color);
        O.Line(new Vector2(max.X, min.Y + r), new Vector2(max.X, max.Y - r), width, color);
        O.Arc(new Vector2(min.X + r, min.Y + r), r, width, color, MathF.PI, 1.5f * MathF.PI, 4);
        O.Arc(new Vector2(max.X - r, min.Y + r), r, width, color, 1.5f * MathF.PI, 2 * MathF.PI, 4);
        O.Arc(new Vector2(max.X - r, max.Y - r), r, width, color, 0, 0.5f * MathF.PI, 4);
        O.Arc(new Vector2(min.X + r, max.Y - r), r, width, color, 0.5f * MathF.PI, MathF.PI, 4);
    }

    /// <summary>The pulsing yellow cursor frame with its soft glow around the canvas rectangle, <paramref name="pulse"/> from <see cref="Pulse"/>.</summary>
    public void Glow(float x0, float y0, float x1, float y1, float pulse, float a = 1)
    {
        Vector2 min = P(x0, y0), max = P(x1, y1);
        var r = MathF.Min(7 * Kx, (max.Y - min.Y) / 3);
        for (var i = 3; i >= 1; i--) RoundRect(min, max, r, (3 + i * 5) * _s, Style.Fade(Overlay.Rgba(1, 1, 0.2f, 0.08f), pulse * a));
        RoundRect(min, max, r, 4.5f * _s, Style.Fade(Overlay.Rgba(0.4f + 0.6f * pulse, 0.4f + 0.6f * pulse, 0.25f * (1 - pulse)), a));
    }

    /// <summary>Yellow triangle with a dark outline (canvas points).</summary>
    public void Arrow(float ax, float ay, float bx, float by, float cx, float cy, float a = 1)
    {
        Vector2 p = P(ax, ay), q = P(bx, by), r = P(cx, cy);
        O.Triangle(p, q, r, Style.Fade(Yellow, a));
        var w = 1.5f * _s;
        var dark = Style.Fade(Overlay.Rgba(0.15f, 0.1f, 0), a);
        O.Line(p, q, w, dark);
        O.Line(q, r, w, dark);
        O.Line(r, p, w, dark);
    }

    /// <summary>Yellow diamond selector (◆) centred at canvas (x, y).</summary>
    public void Diamond(float x, float y, float r, float a = 1)
    {
        Vector2 c = P(x, y), dx = new(r * Kx, 0), dy = new(0, r * Ky);
        O.Quad(c - dy, c + dx, c + dy, c - dx, Style.Fade(Yellow, a));
    }

    /// <summary>
    ///     Grey tiled-logo backdrop (INID_BG): clear colour 90/90/90, small semi-transparent logos 213 px apart in rows 128 px
    ///     apart, odd rows shifted half a column, drifting down-right at 0.5 px per frame (30 px/s) without end.
    /// </summary>
    public void Backdrop(float t)
    {
        Fill(Overlay.Rgba(90 / 255f, 90 / 255f, 90 / 255f));
        var d = t * 30;
        var (ox, oy) = (d % 213, d % 256);
        for (var row = -2; row <= 3; row++)
        {
            var y = 32 + row * 128 + oy;
            if (y < -40 || y > 488) continue;
            var x0 = (row & 1) == 0 ? 64 : 170.5f;
            for (var x = x0 + ox - 213 * MathF.Ceiling((x0 + ox - Left + 70) / 213); x < Right + 70; x += 213) MiniLogo(x, y);
        }
    }

    /// <summary>
    ///     A 128×64 logo tile centred at (x, y), half transparent, from shapes only (text would lie above every panel): a
    ///     slanted red → orange bar for the kanji, a dark italic "D" stroke and a gold line for "Special Stage".
    /// </summary>
    private void MiniLogo(float x, float y)
    {
        const float a = 0.4f, sk = 0.3f; // slant: x per y
        Vector2 Q(float px, float py) => P(px - (py - y) * sk, py);
        O.Quad(Q(x - 50, y - 10), Q(x + 10, y - 10), Q(x + 10, y + 8), Q(x - 50, y + 8), Style.Fade(Overlay.Rgba(0.92f, 0.3f, 0.08f), a));
        O.Quad(Q(x - 50, y - 1), Q(x + 10, y - 1), Q(x + 10, y + 8), Q(x - 50, y + 8), Style.Fade(Overlay.Rgba(1, 0.62f, 0.15f), a * 0.8f));
        var ink = Style.Fade(Overlay.Rgba(0.12f, 0.07f, 0.05f), a + 0.1f);
        var w = 5 * _s;
        var prev = Q(x + 20, y - 18);
        O.Line(prev, Q(x + 20, y + 12), w, ink);
        for (var i = 1; i <= 10; i++) // the D's bowl: half an ellipse, slanted with the rest
        {
            var t = MathF.PI * (i / 10f - 0.5f);
            var p = Q(x + 20 + 22 * MathF.Cos(t), y - 3 + 15 * MathF.Sin(t));
            O.Line(prev, p, w, ink);
            prev = p;
        }
        O.Quad(Q(x - 38, y + 14), Q(x + 40, y + 14), Q(x + 40, y + 17), Q(x - 38, y + 17), Style.Fade(Overlay.Rgba(1, 0.85f, 0.35f), a));
    }

    /// <summary>
    ///     Header marquee band at the top (64 of 448 px), red for game-flow steps, blue for system screens: heavy italic
    ///     white text with a hard black drop shadow, repeated and scrolling right to left (1 px per frame).
    /// </summary>
    public void Marquee(string text, bool blue, float t, float a = 1)
    {
        var band = blue ? HeaderBlue : HeaderRed;
        Vector2 min = new(0, P(0, 0).Y), max = new(Width, P(0, 58).Y);
        if (min.Y > 0) min.Y = 0; // letterboxed: band from the screen top
        O.Rect(min, Vector2.Round(max), Style.Fade(band, a));
        O.Rect(new Vector2(0, MathF.Round(max.Y)), new Vector2(Width, MathF.Round(max.Y + 2 * _s)), Style.Fade(Black, 0.5f * a));
        const float size = 30;
        var w = O.Font!.Measure(text, size * Ky) / Kx + 90;
        var x = 512 - t * 60 % w;
        while (x > Left) x -= w;
        for (; x < Right; x += w)
        {
            Text(text, x + 2.5f, 42 + 3, size, Style.Fade(Black, a), 0, 0.2f, 0, 1.5f);
            Text(text, x, 42, size, Style.Fade(White, a), 0, 0.2f, 0, 1.2f);
        }
    }

    public enum ButtonKind { Positive, Negative, Neutral }

    /// <summary>ACTCHOICE button: bevelled rectangle with a chamfered corner, blue (go on), red (back/exit) or dark checker, white italic label.</summary>
    public void Button(float x, float y, float w, float h, string label, ButtonKind kind, float a = 1)
    {
        var (fill, check) = kind switch
        {
            ButtonKind.Positive => (Overlay.Rgba(0, 0.09f, 0.63f), Overlay.Rgba(0.05f, 0.18f, 0.8f)),
            ButtonKind.Negative => (Overlay.Rgba(0.5f, 0.06f, 0.06f), Overlay.Rgba(0.65f, 0.12f, 0.1f)),
            _ => (Overlay.Rgba(0.12f, 0.13f, 0.12f), Overlay.Rgba(0.2f, 0.21f, 0.2f)),
        };
        var c = 6f; // chamfer, canvas px
        Vector2 a0 = P(x, y), a1 = P(x + w - c, y), a2 = P(x + w, y + c), a3 = P(x + w, y + h), a4 = P(x + c, y + h), a5 = P(x, y + h - c);
        var rim = Style.Fade(Overlay.Rgba(0.85f, 0.86f, 0.88f), a);
        O.Quad(a0, a1, a3, a4, rim);
        O.Triangle(a1, a2, a3, rim);
        O.Triangle(a0, a4, a5, rim);
        Vector2 b0 = P(x + 1.5f, y + 1.5f), b1 = P(x + w - c, y + 1.5f), b2 = P(x + w - 1.5f, y + c), b3 = P(x + w - 1.5f, y + h - 1.5f), b4 = P(x + c, y + h - 1.5f), b5 = P(x + 1.5f, y + h - c);
        O.Quad(b0, b1, b3, b4, Style.Fade(fill, a));
        O.Triangle(b1, b2, b3, Style.Fade(fill, a));
        O.Triangle(b0, b4, b5, Style.Fade(fill, a));
        // checker weave: small squares in the lighter shade
        for (var yy = y + 3; yy < y + h - 4; yy += 4)
        for (var xx = x + 5 + ((int)((yy - y) / 4) & 1) * 2; xx < x + w - 6; xx += 4)
            O.Rect(Vector2.Round(P(xx, yy)), Vector2.Round(P(xx + 2, yy + 2)), Style.Fade(check, a * 0.6f));
        Fit(label, x + w / 2, y + h / 2 + 5, w - 18, 0.5f, Style.Fade(White, a), 0.18f, 0.06f, 14);
    }

    /// <summary>Result sheet: dark teal-black glass with thin cyan-grey separators, a chrome-on-dark tab label on top.</summary>
    public void Sheet(float x0, float y0, float x1, float y1, string tab, float a = 1)
    {
        Vector2 min = Vector2.Round(P(x0, y0)), max = Vector2.Round(P(x1, y1));
        var edge = Style.Fade(Overlay.Rgba(0.2f, 0.4f, 0.42f), a);
        O.Rect(min, max, Style.Fade(Overlay.Rgba(0.05f, 0.1f, 0.1f, 0.92f), a));
        O.Line(min, new Vector2(max.X, min.Y), 1.5f * _s, edge);
        O.Line(new Vector2(min.X, max.Y), max, 1.5f * _s, edge);
        O.Line(min, new Vector2(min.X, max.Y), 1.5f * _s, edge);
        O.Line(new Vector2(max.X, min.Y), max, 1.5f * _s, edge);
        // tab: dark steel with a light top edge, label in white italic
        Vector2 t0 = P(x0, y0 - 16), t1 = P(x0 + 70, y0);
        O.RectGradient(Vector2.Round(t0), Vector2.Round(t1), Style.Fade(Overlay.Rgba(0.42f, 0.44f, 0.46f), a), Style.Fade(Overlay.Rgba(0.16f, 0.17f, 0.18f), a));
        O.Line(Vector2.Round(t0), new Vector2(MathF.Round(t1.X), MathF.Round(t0.Y)), 1, Style.Fade(Overlay.Rgba(0.85f, 0.87f, 0.9f), a));
        Text(tab, x0 + 6, y0 - 3, 13, Style.Fade(White, a), 0, 0.2f, 0.08f);
    }

    /// <summary>A thin cyan-grey separator line across the sheet at canvas y.</summary>
    public void Rule(float x0, float x1, float y, float a = 1) =>
        O.Line(P(x0, y), P(x1, y), MathF.Max(1, 0.8f * _s), Style.Fade(Overlay.Rgba(0.25f, 0.42f, 0.44f, 0.8f), a));
}
