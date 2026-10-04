using System.Numerics;
using Kansei.Graphics;

namespace Touge.Ui;

/// <summary>
///     Shared look of HUD and menus: dark translucent panels with a slanted edge, white text, amber accent; text always
///     with a soft dark shadow so it stays readable over bright day and wet scenes. Sizes are in pixels (callers scale by
///     u = target height / 900).
/// </summary>
public static class Style
{
    public static readonly uint Panel = Overlay.Rgba(0.02f, 0.03f, 0.05f, 0.62f), PanelLight = Overlay.Rgba(1, 1, 1, 0.08f),
        Text = Overlay.Rgba(1, 1, 1, 0.96f), Dim = Overlay.Rgba(1, 1, 1, 0.6f), Faint = Overlay.Rgba(1, 1, 1, 0.18f),
        Amber = Overlay.Rgba(1, 0.72f, 0.1f), Red = Overlay.Rgba(1, 0.24f, 0.2f), Green = Overlay.Rgba(0.3f, 1, 0.45f),
        Shadow = Overlay.Rgba(0, 0, 0, 0.75f), Ink = Overlay.Rgba(0.04f, 0.05f, 0.07f, 0.95f);

    /// <summary>Italic slant of headings and numbers (x per y).</summary>
    public const float Slant = 0.14f;

    /// <summary>Margin to the screen edges (units), shared by HUD and menus (~5 % of the height: TV title-safe).</summary>
    public const float SafeMargin = 44;

    /// <summary>Slant of the right-hand detail panels in the menus.</summary>
    public const float PanelSlant = -0.08f;

    /// <summary>
    ///     Layout grid of a <paramref name="width"/>×<paramref name="height"/> target: <see cref="Grid.U"/> = pixels per
    ///     unit (1/900 of the height), safe rectangle <see cref="Grid.Left"/>..<see cref="Grid.Right"/> × <see cref="Grid.Top"/>..<see cref="Grid.Bottom"/>
    ///     inside <see cref="SafeMargin"/>. Wider than 2:1 (21:9) the frame is centred at 2:1, so corner elements stay near the car.
    /// </summary>
    public static Grid Safe(int width, int height)
    {
        var u = height / 900f;
        var w = MathF.Min(width, height * 2f);
        var x0 = (width - w) / 2;
        var m = SafeMargin * u;
        return new Grid(u, x0 + m, x0 + w - m, m, height - m);
    }

    public readonly record struct Grid(float U, float Left, float Right, float Top, float Bottom)
    {
        /// <summary>Width of the safe rectangle in units.</summary>
        public float Units => (Right - Left) / U;
    }

    /// <summary><paramref name="color"/> with its alpha multiplied by <paramref name="a"/>.</summary>
    public static uint Fade(uint color, float a) => color & 0xFFFFFF | (uint)((color >> 24) * Math.Clamp(a, 0, 1) + 0.5f) << 24;

    /// <summary>Text with a soft drop shadow; returns its width. See <see cref="Overlay.Text"/>.</summary>
    public static float Label(Overlay o, ReadOnlySpan<char> s, Vector2 baseline, float size, uint color, float align = 0, float skew = 0, float weight = 0)
    {
        var off = MathF.Max(1, size * 0.04f);
        o.Text(s, baseline + new Vector2(off * 0.6f, off), size, Fade(Shadow, (color >> 24) / 255f), align, weight + off * 0.8f, off * 2, skew);
        return o.Text(s, baseline, size, color, align, weight, 0, skew);
    }

    /// <summary>Panel with the right edge slanted by <paramref name="slant"/> × height (negative: left edge), AA on the slanted edge.</summary>
    public static void Slanted(Overlay o, Vector2 min, Vector2 max, uint color, float slant = 0.25f)
    {
        min = Vector2.Round(min);
        max = Vector2.Round(max);
        var d = (max.Y - min.Y) * MathF.Abs(slant);
        if (slant >= 0)
        {
            o.Quad(min, new Vector2(max.X, min.Y), new Vector2(max.X - d, max.Y), new Vector2(min.X, max.Y), color);
            o.Line(new Vector2(max.X - 0.5f, min.Y), new Vector2(max.X - d - 0.5f, max.Y), 1, color);
        }
        else
        {
            o.Quad(new Vector2(min.X + d, min.Y), new Vector2(max.X, min.Y), max, new Vector2(min.X, max.Y), color);
            o.Line(new Vector2(min.X + d + 0.5f, min.Y), new Vector2(min.X + 0.5f, max.Y), 1, color);
        }
    }

    /// <summary>Key cap (rounded box with a letter) and its action, left end at <paramref name="at"/> (vertical centre); returns the end x.</summary>
    public static float KeyHint(Overlay o, string key, string action, Vector2 at, float u, float alpha = 1)
    {
        var size = 17 * u;
        var w = MathF.Max(o.Font!.Measure(key, size) + 12 * u, 26 * u);
        var h = 24 * u;
        Vector2 min = new(at.X, at.Y - h / 2), max = new(at.X + w, at.Y + h / 2);
        o.Rect(Vector2.Round(min), Vector2.Round(max), Fade(Overlay.Rgba(1, 1, 1, 0.9f), alpha));
        var capY = at.Y + o.Font.CapHeight * size / 2;
        o.Text(key, new Vector2((min.X + max.X) / 2, capY), size, Fade(Ink, alpha), 0.5f, 0.3f * u);
        var tw = Label(o, action, new Vector2(max.X + 8 * u, capY), size, Fade(Text, alpha));
        return max.X + 8 * u + tw + 20 * u;
    }

    /// <summary>Time as m'ss.mmm (or "NO TIME" when none).</summary>
    public static string Time(float? seconds)
    {
        if (seconds is not { } t || !float.IsFinite(t)) return "NO TIME";
        var ms = (int)MathF.Round(t * 1000);
        return $"{ms / 60000}'{ms / 1000 % 60:D2}.{ms % 1000:D3}";
    }

    /// <summary>Signed delta as +s.mmm / −s.mmm.</summary>
    public static string Delta(float d) => (d < 0 ? "-" : "+") + MathF.Abs(d).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Ease in/out (smoothstep) of 0..1.</summary>
    public static float Ease(float t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Moves <paramref name="value"/> towards <paramref name="target"/> at <paramref name="rate"/> per second.</summary>
    public static float Approach(float value, float target, float rate, float dt) =>
        value < target ? MathF.Min(value + rate * dt, target) : MathF.Max(value - rate * dt, target);
}
