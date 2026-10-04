using System.Numerics;
using Kansei.Graphics;
using static Kansei.Graphics.Overlay;

namespace Touge.Ui;

/// <summary>
///     Instrument cluster of the driven car, bottom right of the HUD, drawn from a per-car <see cref="Gauge"/> spec.
///     The original has no per-car meters (one Sega-Rosso tach in four scales, RACEVIEW.PAC), so each car gets its real
///     dash rebuilt from published layouts: binnacle family, tach range/red zone, face/numeral/needle colours,
///     night illumination, numeral style, badge, boost gauge on turbo cars. Dial arc = the original's (252° from 140°,
///     sub_00158B40). Speed sits in the speedo's odometer window, the gear in a window at the foot of the tach.
///     Colours and some ranges are from memory and partly uncertain (see the table).
/// </summary>
public static class Cluster
{
    public enum Layout
    {
        /// <summary>Hooded binnacle: big tach in the centre, speedo right, small gauge left (most 80s/90s Japanese cars).</summary>
        Trio,
        /// <summary>Speedo left, tach right, equal size, small gauge between (Nissan S13 family, R32).</summary>
        Twin,
        /// <summary>Big speedo in the centre, smaller tach left (MR-S).</summary>
        SpeedoCentre,
        /// <summary>Separate chrome-ringed pods: speedo, tach, three small gauges (Eunos/MX-5).</summary>
        Roadster,
        /// <summary>Wristwatch-style dials with sub-dials, thin numerals, baton needles (Altezza).</summary>
        Chrono,
        /// <summary>LCD: arc of bar segments as tach, digital speed (S2000).</summary>
        Digital,
    }

    /// <summary>
    ///     Dash of one car: <see cref="TachMax"/>/<see cref="Redline"/> rpm, face/numeral (<see cref="Ink"/>)/needle colours,
    ///     numeral colour at night (<see cref="Night"/>), boost gauge (<see cref="Turbo"/>), text on the tach face
    ///     (<see cref="Badge"/>, red <see cref="BadgeRed"/>), numeral weight/italic, chrome rings, size (<see cref="Scale"/>).
    /// </summary>
    public sealed record Gauge(Layout Layout, int TachMax, int Redline, uint Face, uint Ink, uint Needle, uint Night, bool Turbo,
        string Badge = "", bool BadgeRed = false, float Weight = 0, float Skew = 0, bool Chrome = false, float Scale = 1);

    private static readonly uint Black = Rgba(0.035f, 0.037f, 0.042f), RedFace = Rgba(0.30f, 0.035f, 0.04f), WhiteFace = Rgba(0.9f, 0.9f, 0.86f),
        SilverFace = Rgba(0.68f, 0.70f, 0.72f), White = Rgba(0.95f, 0.95f, 0.92f), Ink = Rgba(0.06f, 0.06f, 0.07f),
        Orange = Rgba(1, 0.45f, 0.08f), OrangeRed = Rgba(1, 0.28f, 0.1f), Red = Rgba(0.95f, 0.1f, 0.07f), Baton = Rgba(0.96f, 0.96f, 0.93f),
        LcdAmber = Rgba(1, 0.62f, 0.12f),
        NGreen = Rgba(0.62f, 1, 0.72f), NOrange = Rgba(1, 0.6f, 0.24f), NWhite = Rgba(0.9f, 0.95f, 1), NRed = Rgba(1, 0.36f, 0.3f);

    /// <summary>
    ///     Per car (HCAR names). Layout/range from the real car (research notes, partly from memory); night colours and the
    ///     Type R red faces / STi white faces are best guesses. Every tach reaches past the physics rev limit (CarSpecs).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Gauge> Cars = new Dictionary<string, Gauge>
    {
        ["AE86T"] = new(Layout.Trio, 9000, 7600, Black, White, Orange, NGreen, false, "TWIN CAM 16"),
        ["AE86L"] = new(Layout.Trio, 9000, 7600, Black, White, Orange, NGreen, false, "GT-APEX"),
        ["AE85"] = new(Layout.Trio, 8000, 6000, Black, White, Orange, NGreen, false, Weight: -0.6f, Scale: 0.92f),
        ["MR2"] = new(Layout.Trio, 8000, 7000, Black, White, OrangeRed, NOrange, true),
        ["MRS"] = new(Layout.SpeedoCentre, 8000, 6800, Black, White, OrangeRed, NOrange, false, Chrome: true),
        ["ALTEZ"] = new(Layout.Chrono, 9000, 7800, Black, White, Baton, NWhite, false, Weight: -0.4f, Chrome: true),
        ["GT-4"] = new(Layout.Trio, 8000, 7000, Black, White, OrangeRed, NOrange, true, "GT-FOUR"),
        ["R32"] = new(Layout.Twin, 9000, 8000, Black, White, OrangeRed, NOrange, true, "GT-R"),
        ["R34"] = new(Layout.Trio, 9000, 8000, Black, White, Red, NWhite, true, "GT-R", Skew: 0.1f),
        ["ER34"] = new(Layout.Trio, 8000, 7000, Black, White, OrangeRed, NOrange, true),
        ["S13"] = new(Layout.Twin, 8000, 7000, Black, White, Orange, NOrange, true),
        ["S14Q"] = new(Layout.Twin, 8000, 7000, Black, White, OrangeRed, NOrange, false),
        ["S14"] = new(Layout.Twin, 8000, 7000, Black, White, OrangeRed, NOrange, true),
        ["S15"] = new(Layout.Trio, 9000, 7500, Black, White, OrangeRed, Rgba(1, 0.72f, 0.3f), true, "SPEC R", Skew: 0.1f),
        ["ONE80"] = new(Layout.Twin, 8000, 7000, Black, White, Orange, NOrange, true),
        ["SIL80"] = new(Layout.Twin, 8000, 7000, Black, White, Orange, NOrange, true),
        ["EK9"] = new(Layout.Trio, 10000, 8400, RedFace, White, Red, NWhite, false, "TYPE R", Skew: 0.08f),
        ["EG6"] = new(Layout.Trio, 10000, 8200, Black, White, OrangeRed, NGreen, false, "VTEC"),
        ["INTGR"] = new(Layout.Trio, 10000, 8400, RedFace, White, Red, NWhite, false, "TYPE R", Skew: 0.08f),
        ["S2000"] = new(Layout.Digital, 10000, 9000, Black, LcdAmber, LcdAmber, LcdAmber, false),
        ["EVO3"] = new(Layout.Trio, 8000, 7000, Black, White, Orange, NGreen, true),
        ["EVO4"] = new(Layout.Trio, 9000, 7500, Black, White, Orange, NGreen, true),
        ["EVO7"] = new(Layout.Trio, 8000, 7000, Black, White, Red, NWhite, true, Skew: 0.1f, Chrome: true),
        ["FD3S"] = new(Layout.Trio, 10000, 8000, Black, White, OrangeRed, NOrange, true, "ROTARY"),
        ["FD3SA"] = new(Layout.Trio, 10000, 8000, Black, White, OrangeRed, NOrange, true, "ROTARY"),
        ["FC3S"] = new(Layout.Trio, 9000, 7000, Black, White, Orange, NOrange, true, "ROTARY TURBO"),
        ["NA6C"] = new(Layout.Roadster, 8000, 7200, Black, White, Orange, NOrange, false, Chrome: true),
        ["NB8C"] = new(Layout.Roadster, 8000, 7000, WhiteFace, Ink, Red, NOrange, false, Chrome: true),
        ["IMP"] = new(Layout.Trio, 9000, 8000, WhiteFace, Ink, Red, NOrange, true, "STi", BadgeRed: true),
        ["IMP2"] = new(Layout.Trio, 9000, 8000, SilverFace, Ink, Red, NRed, true, "STi", BadgeRed: true, Skew: 0.1f),
        ["IMP3"] = new(Layout.Trio, 9000, 8000, WhiteFace, Ink, Red, NOrange, true, "STi", BadgeRed: true),
        ["CAPPU"] = new(Layout.Trio, 11000, 9000, Black, White, Orange, NOrange, true, Scale: 0.82f),
    };

    /// <summary>What the dials show this frame; <see cref="Boost"/> in bar (−1..1), <see cref="Time"/> in s for blinking.</summary>
    public readonly record struct Reading(float Rpm, float Kmh, int Gear, bool Automatic, float Boost, bool Night, float Time);

    private const float SpeedoMax = 180, A0 = 140 * MathF.PI / 180, Sweep = 252 * MathF.PI / 180;
    private static readonly uint Housing = Rgba(0.045f, 0.046f, 0.05f, 0.94f), HousingRim = Rgba(0.2f, 0.2f, 0.22f, 0.95f),
        Brow = Rgba(0.1f, 0.1f, 0.11f, 0.94f), ChromeLight = Rgba(0.82f, 0.83f, 0.85f), ChromeDark = Rgba(0.28f, 0.29f, 0.31f),
        RedZone = Rgba(0.9f, 0.1f, 0.08f), Window = Rgba(0.01f, 0.01f, 0.012f, 0.96f), Hub = Rgba(0.02f, 0.02f, 0.02f);

    /// <summary>Housing size of a layout in units (before <see cref="Gauge.Scale"/>).</summary>
    public static Vector2 Size(Layout l) => l switch
    {
        Layout.Twin => new Vector2(470, 226),
        Layout.SpeedoCentre => new Vector2(440, 232),
        Layout.Roadster => new Vector2(470, 196),
        Layout.Chrono => new Vector2(450, 226),
        Layout.Digital => new Vector2(470, 210),
        _ => new Vector2(490, 232),
    };

    /// <summary>Cluster of <paramref name="g"/> with its bottom-right corner at <paramref name="corner"/>, <paramref name="u"/> px per unit.</summary>
    public static void Draw(Overlay o, Gauge g, Vector2 corner, float u, in Reading r)
    {
        u *= g.Scale;
        var size = Size(g.Layout) * u;
        var p0 = corner - size;
        Vector2 P(float x, float y) => p0 + new Vector2(x, y) * u;
        var dark = Luma(g.Face) < 0.4f;
        var look = new Look(g.Face, r.Night && dark ? g.Night : g.Ink, g.Needle, g.Weight * u, g.Skew, g.Chrome, r.Night && dark);
        var tach = new Scale(g.TachMax, 1000, g.TachMax > 9000 ? 2 : 4, 1000, g.Redline, 1000);
        Scale Speedo(float radius) => new(SpeedoMax, 20, 2, radius < 90 ? 40 : 20, float.MaxValue, 1); // small speedos: every 2nd number
        var speedo = Speedo(96);
        var boostOrTemp = g.Turbo ? (Caption: "TURBO", Lo: "-1", Hi: "+1", Value: (r.Boost + 1) / 2) : (Caption: "TEMP", Lo: "C", Hi: "H", Value: 0.45f);
        switch (g.Layout)
        {
            case Layout.Digital:
                Digital(o, g, p0, size, u, r);
                return;
            case Layout.Roadster:
                foreach (var (x, y, rad) in new[] { (36f, 66f, 30f), (36f, 150f, 30f), (154f, 104f, 86f), (340f, 104f, 86f), (442f, 104f, 28f) })
                    Pod(o, P(x, y), (rad + 7) * u, u, g.Chrome);
                Dial(o, P(154, 104), 86 * u, u, look, Speedo(86), r.Kmh, "km/h", 13);
                SpeedWindow(o, P(154, 104 + 50), u, look, r.Kmh);
                Dial(o, P(340, 104), 86 * u, u, look, tach, r.Rpm, "x1000r/min", 18, g.Badge);
                GearWindow(o, P(340, 104 + 56), u, r);
                Mini(o, P(36, 66), 30 * u, u, look, "FUEL", "E", "F", 0.7f);
                Mini(o, P(36, 150), 30 * u, u, look, "TEMP", "C", "H", 0.45f);
                Mini(o, P(442, 104), 28 * u, u, look, "OIL", "0", "8", 0.25f + 0.5f * r.Rpm / g.TachMax);
                return;
        }
        Hood(o, p0, p0 + size, 30 * u);
        switch (g.Layout)
        {
            case Layout.Twin:
                Dial(o, P(114, 112), 96 * u, u, look, speedo, r.Kmh, "km/h", 15);
                SpeedWindow(o, P(114, 112 + 58), u, look, r.Kmh);
                Dial(o, P(356, 112), 96 * u, u, look, tach, r.Rpm, "x1000r/min", 19, g.Badge);
                GearWindow(o, P(356, 112 + 62), u, r);
                Mini(o, P(235, 184), 32 * u, u, look, boostOrTemp.Caption, boostOrTemp.Lo, boostOrTemp.Hi, boostOrTemp.Value);
                break;
            case Layout.SpeedoCentre:
                Dial(o, P(86, 138), 70 * u, u, look, tach, r.Rpm, "x1000r/min", 15, g.Badge);
                GearWindow(o, P(86, 138 + 46), u, r);
                Dial(o, P(234, 116), 102 * u, u, look, speedo, r.Kmh, "km/h", 17);
                SpeedWindow(o, P(234, 116 + 62), u, look, r.Kmh);
                Mini(o, P(388, 140), 38 * u, u, look, "TEMP", "C", "H", 0.45f);
                break;
            case Layout.Chrono:
                ChronoDial(o, P(116, 113), 98 * u, u, look, speedo, r.Kmh, "km/h", ("FUEL", 0.7f));
                SpeedWindow(o, P(116, 113 + 56), u, look, r.Kmh);
                ChronoDial(o, P(334, 113), 98 * u, u, look, tach, r.Rpm, "x1000r/min", ("TEMP", 0.45f));
                GearWindow(o, P(334, 113 + 60), u, r);
                break;
            default:
                Mini(o, P(58, 130), 40 * u, u, look, boostOrTemp.Caption, boostOrTemp.Lo, boostOrTemp.Hi, boostOrTemp.Value);
                Dial(o, P(212, 116), 104 * u, u, look, tach, r.Rpm, "x1000r/min", 20, g.Badge, g.BadgeRed);
                GearWindow(o, P(212, 116 + 66), u, r);
                Dial(o, P(402, 124), 76 * u, u, look, Speedo(76), r.Kmh, "km/h", 13);
                SpeedWindow(o, P(402, 124 + 46), u, look, r.Kmh);
                break;
        }
    }

    private readonly record struct Look(uint Face, uint Ink, uint Needle, float Weight, float Skew, bool Chrome, bool Glow);

    /// <summary>Dial scale: full <see cref="Max"/>, major tick every <see cref="Major"/> with <see cref="Minor"/> steps, number every <see cref="Label"/> (÷<see cref="Divide"/>), red from <see cref="Red"/>.</summary>
    private readonly record struct Scale(float Max, float Major, int Minor, float Label, float Red, float Divide);

    private static float Luma(uint c) => ((c & 0xFF) * 0.3f + (c >> 8 & 0xFF) * 0.59f + (c >> 16 & 0xFF) * 0.11f) / 255;

    private static Vector2 Dir(float a) => new(MathF.Cos(a), MathF.Sin(a));

    private static float Angle(float v, float max) => A0 + Sweep * Math.Clamp(v / max, 0, 1.015f);

    /// <summary>Rounded binnacle with a lighter brow along the top.</summary>
    private static void Hood(Overlay o, Vector2 min, Vector2 max, float r)
    {
        RoundRect(o, min, max, r, HousingRim);
        RoundRect(o, min + new Vector2(2), max - new Vector2(2), r - 2, Brow);
        RoundRect(o, min + new Vector2(4, 9), max - new Vector2(4), r - 4, Housing);
    }

    /// <summary>Filled rounded rectangle as a triangle fan (no overlaps, so translucent colours blend once), AA outline.</summary>
    private static void RoundRect(Overlay o, Vector2 min, Vector2 max, float r, uint color)
    {
        const int n = 6;
        Span<Vector2> p = stackalloc Vector2[4 * n];
        Vector2[] corners = [new(max.X - r, min.Y + r), new(max.X - r, max.Y - r), new(min.X + r, max.Y - r), new(min.X + r, min.Y + r)];
        for (var k = 0; k < 4; k++)
        for (var i = 0; i < n; i++)
            p[k * n + i] = corners[k] + Dir((k - 1 + i / (n - 1f)) * MathF.PI / 2) * r;
        var c = (min + max) / 2;
        for (var i = 0; i < p.Length; i++) o.Triangle(c, p[i], p[(i + 1) % p.Length], color);
        for (var i = 0; i < p.Length; i++) o.Line(p[i], p[(i + 1) % p.Length], 1, color);
    }

    /// <summary>Separate round pod (roadster): dark cup with a chrome or black ring.</summary>
    private static void Pod(Overlay o, Vector2 c, float r, float u, bool chrome)
    {
        o.Disc(c, r + 2 * u, Rgba(0, 0, 0, 0.55f));
        o.Disc(c, r, chrome ? ChromeDark : HousingRim);
        o.Disc(c, r - 2 * u, Housing);
    }

    private static void Bezel(Overlay o, Vector2 c, float r, float u, in Look k)
    {
        if (k.Chrome)
        {
            o.Disc(c, r + 5 * u, ChromeDark);
            o.Ring(c, r + 3 * u, 2.5f * u, ChromeLight, 48);
        }
        else o.Disc(c, r + 3 * u, HousingRim);
        o.Disc(c, r, k.Face);
    }

    private static void Numeral(Overlay o, string s, Vector2 centre, float size, in Look k, uint color)
    {
        var at = new Vector2(centre.X, centre.Y + o.Font!.CapHeight * size / 2);
        if (k.Glow) o.Text(s, at, size, Style.Fade(color, 0.45f), 0.5f, k.Weight + size * 0.06f, size * 0.25f, k.Skew);
        o.Text(s, at, size, color, 0.5f, k.Weight, 0, k.Skew);
    }

    /// <summary>Analog dial: bezel, red zone, ticks, numbers, unit, badge, needle with hub.</summary>
    private static void Dial(Overlay o, Vector2 c, float r, float u, in Look k, in Scale s, float value, string unit, float numSize, string badge = "", bool badgeRed = false)
    {
        Bezel(o, c, r, u, k);
        if (s.Red < s.Max) o.Arc(c, r - 7 * u, 6 * u, RedZone, Angle(s.Red, s.Max), Angle(s.Max, s.Max), 24);
        var steps = (int)MathF.Round(s.Max / s.Major) * s.Minor;
        for (var i = 0; i <= steps; i++)
        {
            var v = i * s.Major / s.Minor;
            var d = Dir(Angle(v, s.Max));
            var major = i % s.Minor == 0;
            o.Line(c + d * (r - 3 * u), c + d * (r - (major ? 15 : 9) * u), (major ? 2.6f : 1.2f) * u, v >= s.Red && !k.Glow ? RedZone : k.Ink);
            if (!major || MathF.Round(v) % s.Label != 0) continue;
            // inset by the label's half extent along the radius, so wide numbers clear the ticks at the sides
            var text = $"{v / s.Divide:0}";
            var ext = MathF.Abs(d.X) * o.Font!.Measure(text, numSize * u) / 2 + MathF.Abs(d.Y) * o.Font.CapHeight * numSize * u / 2;
            Numeral(o, text, c + d * (r - 19 * u - ext), numSize * u, k, v >= s.Red ? Red : k.Ink);
        }
        Label(o, unit, new Vector2(c.X, c.Y - r * 0.3f), 0.11f * r, k);
        if (badge != "") Label(o, badge, new Vector2(c.X, c.Y + r * 0.36f), 0.13f * r, k, badgeRed ? Red : null, 0.6f);
        Needle(o, c, Angle(value, s.Max), r - 6 * u, r * 0.2f, 0.07f * r, k.Needle, u);
    }

    private static void Label(Overlay o, string s, Vector2 centre, float size, in Look k, uint? color = null, float alpha = 0.75f)
    {
        var at = new Vector2(centre.X, centre.Y + o.Font!.CapHeight * size / 2);
        o.Text(s, at, size, Style.Fade(color ?? k.Ink, alpha), 0.5f, k.Weight * 0.5f, 0, k.Skew);
    }

    /// <summary>Tapered needle (thick root, slim tip) over a soft shadow, black hub cap.</summary>
    private static void Needle(Overlay o, Vector2 c, float angle, float len, float tail, float w, uint color, float u)
    {
        var d = Dir(angle);
        var sh = new Vector2(1.5f, 2.5f) * u;
        o.Line(c - d * tail + sh, c + d * len + sh, w * 0.7f, Rgba(0, 0, 0, 0.35f));
        o.Line(c - d * tail, c + d * len * 0.45f, w, color);
        o.Line(c + d * len * 0.4f, c + d * len, w * 0.5f, color);
        o.Disc(c, w * 1.7f, Hub);
        o.Disc(c, w * 0.6f, Rgba(0.25f, 0.25f, 0.27f));
    }

    /// <summary>Small gauge with a short top arc: ends <paramref name="lo"/>/<paramref name="hi"/>, caption below, value 0..1.</summary>
    private static void Mini(Overlay o, Vector2 c, float r, float u, in Look k, string caption, string lo, string hi, float value)
    {
        Bezel(o, c, r, u, k);
        const float a0 = 200 * MathF.PI / 180, sweep = 140 * MathF.PI / 180;
        for (var i = 0; i <= 4; i++)
        {
            var d = Dir(a0 + sweep * i / 4);
            o.Line(c + d * (r - 3 * u), c + d * (r - (i % 2 == 0 ? 11 : 7) * u), (i % 2 == 0 ? 2 : 1.2f) * u, k.Ink);
        }
        var size = MathF.Max(0.26f * r, 9 * u);
        Numeral(o, lo, c + Dir(a0) * (r - 18 * u) + new Vector2(0, 4 * u), size, k, k.Ink);
        Numeral(o, hi, c + Dir(a0 + sweep) * (r - 18 * u) + new Vector2(0, 4 * u), size, k, k.Ink);
        Label(o, caption, new Vector2(c.X, c.Y + r * 0.45f), size * 0.95f, k);
        Needle(o, c, a0 + sweep * Math.Clamp(value, 0, 1), r - 5 * u, r * 0.15f, 0.09f * r, k.Needle, u);
    }

    /// <summary>Altezza chronograph: minute-track ring of fine ticks, baton indexes, a sub-dial in the upper half.</summary>
    private static void ChronoDial(Overlay o, Vector2 c, float r, float u, in Look k, in Scale s, float value, string unit, (string Caption, float Value) sub)
    {
        Bezel(o, c, r, u, k);
        o.Ring(c, r - 6 * u, 0.8f * u, Style.Fade(k.Ink, 0.5f), 72);
        for (var i = 0; i < 120; i++) o.Line(c + Dir(i * MathF.Tau / 120) * (r - 4 * u), c + Dir(i * MathF.Tau / 120) * (r - 8 * u), 0.7f * u, Style.Fade(k.Ink, 0.45f));
        var steps = (int)MathF.Round(s.Max / s.Major);
        if (s.Red < s.Max) o.Arc(c, r - 12 * u, 3 * u, RedZone, Angle(s.Red, s.Max), Angle(s.Max, s.Max), 24);
        for (var i = 0; i <= steps; i++)
        {
            var v = i * s.Major;
            var d = Dir(Angle(v, s.Max));
            o.Line(c + d * (r - 10 * u), c + d * (r - 22 * u), 3.2f * u, v >= s.Red ? Red : k.Ink);
            if (MathF.Round(v) % (s.Divide > 1 ? 1000 : 40) == 0)
                Numeral(o, $"{v / s.Divide:0}", c + d * (r - 36 * u), 18 * u, k, v >= s.Red ? Red : k.Ink);
        }
        var sc = c + new Vector2(0, -r * 0.4f);
        var sr = r * 0.22f;
        o.Ring(sc, sr, 1 * u, Style.Fade(k.Ink, 0.7f), 32);
        for (var i = 0; i <= 4; i++) o.Line(sc + Dir(MathF.PI + i * MathF.PI / 4) * sr, sc + Dir(MathF.PI + i * MathF.PI / 4) * (sr - 4 * u), 1 * u, k.Ink);
        Label(o, sub.Caption, sc + new Vector2(0, sr * 0.5f), 0.36f * sr, k);
        var sd = Dir(MathF.PI + MathF.PI * sub.Value);
        o.Line(sc, sc + sd * (sr - 3 * u), 1.6f * u, Red);
        Label(o, unit, new Vector2(c.X, c.Y + r * 0.22f), 0.09f * r, k);
        // baton needle: slim white with a red tip
        var d2 = Dir(Angle(value, s.Max));
        o.Line(c - d2 * r * 0.18f + new Vector2(1.5f, 2.5f) * u, c + d2 * (r - 8 * u) + new Vector2(1.5f, 2.5f) * u, 3 * u, Rgba(0, 0, 0, 0.35f));
        o.Line(c - d2 * r * 0.18f, c + d2 * (r - 22 * u), 4 * u, k.Needle);
        o.Line(c + d2 * (r - 22 * u), c + d2 * (r - 8 * u), 2.2f * u, Red);
        o.Disc(c, 6 * u, ChromeLight);
        o.Disc(c, 3 * u, Hub);
    }

    /// <summary>Odometer-style window with the speed in white drum digits.</summary>
    private static void SpeedWindow(Overlay o, Vector2 c, float u, in Look k, float kmh)
    {
        var half = new Vector2(30, 13) * u;
        o.Rect(Vector2.Round(c - half - new Vector2(u)), Vector2.Round(c + half + new Vector2(u)), Rgba(0.4f, 0.4f, 0.42f));
        o.Rect(Vector2.Round(c - half), Vector2.Round(c + half), Window);
        var size = 24 * u;
        o.Text($"{MathF.Round(kmh):0}", new Vector2(c.X, c.Y + o.Font!.CapHeight * size / 2), size, k.Glow ? k.Ink : White, 0.5f, 0.3f * u);
    }

    /// <summary>Gear window at the foot of the tach, AT/MT in small type beside the digit.</summary>
    private static void GearWindow(Overlay o, Vector2 c, float u, in Reading r)
    {
        var half = new Vector2(26, 17) * u;
        o.Rect(Vector2.Round(c - half - new Vector2(u)), Vector2.Round(c + half + new Vector2(u)), Rgba(0.4f, 0.4f, 0.42f));
        o.Rect(Vector2.Round(c - half), Vector2.Round(c + half), Window);
        var gear = r.Gear < 0 ? "R" : r.Gear == 0 ? "N" : r.Gear.ToString();
        var size = 30 * u;
        o.Text(gear, new Vector2(c.X - 7 * u, c.Y + o.Font!.CapHeight * size / 2), size, Style.Amber, 0.5f, 0.4f * u);
        o.Text(r.Automatic ? "AT" : "MT", new Vector2(c.X + 15 * u, c.Y + 9 * u), 11 * u, Style.Fade(Style.Amber, 0.7f), 0.5f, 0.2f * u);
    }

    /// <summary>
    ///     S2000-style LCD: arc of bar segments (200 rpm each) across the top, red from the redline and flashing at the limit,
    ///     big speed digits over unlit "888" segments, gear box, fuel and temperature bar graphs at the sides.
    /// </summary>
    private static void Digital(Overlay o, Gauge g, Vector2 p0, Vector2 size, float u, in Reading r)
    {
        var min = p0;
        var max = p0 + size;
        Hood(o, min, max, 18 * u);
        var lcd = Rgba(0.02f, 0.018f, 0.012f, 0.97f);
        RoundRect(o, min + new Vector2(16, 22) * u, max - new Vector2(16, 16) * u, 10 * u, lcd);
        var unlit = Style.Fade(g.Ink, 0.1f);
        var c = new Vector2((min.X + max.X) / 2, min.Y + 388 * u);
        const float from = -120 * MathF.PI / 180, to = -60 * MathF.PI / 180;
        var segs = g.TachMax / 200;
        var flash = r.Rpm >= g.TachMax - 1100 && MathF.Sin(r.Time * MathF.Tau * 7) > 0;
        for (var i = 0; i < segs; i++)
        {
            var a = from + (to - from) * (i + 0.5f) / segs;
            var d = Dir(a);
            var rpm = i * 200f;
            var red = rpm >= g.Redline;
            var lit = rpm + 100 < r.Rpm && !(red && flash);
            var len = (24 + 18f * i / segs) * u;
            o.Line(c + d * (284 * u), c + d * (284 * u + len), 5.2f * u, lit ? red ? Red : g.Ink : unlit);
        }
        for (var k = 0; k <= g.TachMax / 1000; k++)
        {
            var d = Dir(from + (to - from) * k * 1000 / g.TachMax);
            var p = c + d * (284 * u + (52 + 18f * k / (g.TachMax / 1000)) * u);
            var s = 14 * u;
            o.Text($"{k}", new Vector2(p.X, p.Y + o.Font!.CapHeight * s / 2), s, Style.Fade(k * 1000 >= g.Redline ? Red : g.Ink, 0.85f), 0.5f, 0.2f * u);
        }
        // speed: digits over unlit 888
        var sp = new Vector2(c.X - 20 * u, max.Y - 28 * u);
        var big = 56 * u;
        o.Text("888", sp, big, unlit, 1, 0.5f * u, 0, 0.08f);
        o.Text($"{MathF.Round(r.Kmh):0}", sp, big, g.Ink, 1, 0.5f * u, 0, 0.08f);
        o.Text("km/h", sp + new Vector2(8 * u, 0), 14 * u, Style.Fade(g.Ink, 0.8f), 0, 0.2f * u);
        // gear box
        var gc = new Vector2(c.X + 70 * u, max.Y - 48 * u);
        var half = new Vector2(24, 22) * u;
        o.Rect(Vector2.Round(gc - half), Vector2.Round(gc + half), Style.Fade(g.Ink, 0.16f));
        var gear = r.Gear < 0 ? "R" : r.Gear == 0 ? "N" : r.Gear.ToString();
        o.Text(gear, new Vector2(gc.X, gc.Y + o.Font!.CapHeight * 34 * u / 2), 34 * u, g.Ink, 0.5f, 0.5f * u, 0, 0.08f);
        o.Text(r.Automatic ? "AT" : "MT", new Vector2(gc.X + half.X + 6 * u, gc.Y + half.Y), 11 * u, Style.Fade(g.Ink, 0.7f), 0, 0.2f * u);
        // fuel (left) and temp (right) bar graphs
        Bars(o, new Vector2(min.X + 34 * u, max.Y - 32 * u), u, g.Ink, unlit, 0.7f, "F");
        Bars(o, new Vector2(max.X - 34 * u - 8 * 9 * u, max.Y - 32 * u), u, g.Ink, unlit, 0.5f, "T");
    }

    private static void Bars(Overlay o, Vector2 at, float u, uint ink, uint unlit, float value, string label)
    {
        for (var i = 0; i < 8; i++)
        {
            var x = at.X + i * 9 * u;
            var h = (8 + i * 2.5f) * u;
            o.Rect(Vector2.Round(new Vector2(x, at.Y - h)), Vector2.Round(new Vector2(x + 6 * u, at.Y)), i < value * 8 ? ink : unlit);
        }
        o.Text(label, new Vector2(at.X - 4 * u, at.Y), 12 * u, Style.Fade(ink, 0.8f), 1, 0.2f * u);
    }
}
