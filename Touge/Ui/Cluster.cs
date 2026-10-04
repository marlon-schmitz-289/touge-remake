using System.Numerics;
using Kansei.Graphics;
using static Kansei.Graphics.Overlay;

namespace Touge.Ui;

/// <summary>
///     Instrument cluster of the driven car, bottom right of the HUD, drawn from a per-car <see cref="Gauge"/> spec.
///     The original has no per-car meters (one Sega-Rosso tach in four scales, RACEVIEW.PAC), so each car gets its real
///     dash rebuilt from published layouts: which meters sit where (tach/speedo order, small gauges, boost on turbo cars,
///     R32 console trio, R34 multi-function display, S15 dash-top boost, Evo boost on the brow, FD A-pillar pods),
///     housing shape, face/numeral/needle colours, tick and needle style, night illumination. Dial arc = the original's
///     (252° from 140°, sub_00158B40); the bottom 108° stay free of the needle, so speed/gear windows and sub-dials sit there.
///     Colours and some ranges are from memory and partly uncertain (see the table).
/// </summary>
public static class Cluster
{
    public enum Kind { Tach, Speedo, Fuel, Temp, FuelTemp, Oil, Boost, OilTemp, Volt, Mfd }

    /// <summary>Where a meter sits: in the main housing, in its own pod outside it, or as a sub-dial inside a big dial.</summary>
    public enum Mount { Dash, Pod, Inset }

    /// <summary>Hooded binnacle (corner radius <see cref="Gauge.Corner"/>), Nissan wedge, separate round cowls, or the S2000 LCD.</summary>
    public enum Housing { Hood, Wedge, Cowls, Lcd }

    /// <summary>Thin line ticks; flat block ticks; numbers outside an inner tick ring; wristwatch (chronograph) track.</summary>
    public enum Ticks { Line, Block, Outer, Watch }

    /// <summary>Meter <see cref="K"/> centred at (<see cref="X"/>, <see cref="Y"/>) units from the housing's top-left, face radius <see cref="R"/> (Mfd: half height).</summary>
    public readonly record struct Meter(Kind K, float X, float Y, float R, Mount Mount = Mount.Dash);

    /// <summary>
    ///     Dash of one car: <see cref="TachMax"/>/<see cref="Redline"/> rpm, housing <see cref="Size"/> in units and its
    ///     <see cref="Meters"/>, face/numeral (<see cref="Ink"/>)/needle colours, numeral colour at night (<see cref="Night"/>),
    ///     text on the tach face (<see cref="Badge"/>, red <see cref="BadgeRed"/>), numeral weight/italic/size,
    ///     chrome bezels, accent ring colour (<see cref="Ring"/>, 0 = none), slim needles (<see cref="Slim"/>).
    /// </summary>
    public sealed record Gauge(int TachMax, int Redline, Vector2 Size, Meter[] Meters, uint Face, uint Ink, uint Needle, uint Night,
        Housing Housing = Housing.Hood, float Corner = 30, Ticks Ticks = Ticks.Line, string Badge = "", bool BadgeRed = false,
        float Weight = 0, float Skew = 0, float NumSize = 1, bool Chrome = false, uint Ring = 0, bool Slim = false, int Minor = 0);

    private static readonly uint Black = Rgba(0.035f, 0.037f, 0.042f), RedFace = Rgba(0.30f, 0.035f, 0.04f), WhiteFace = Rgba(0.9f, 0.9f, 0.86f),
        SilverFace = Rgba(0.68f, 0.70f, 0.72f), White = Rgba(0.95f, 0.95f, 0.92f), Ink = Rgba(0.06f, 0.06f, 0.07f),
        Orange = Rgba(1, 0.45f, 0.08f), OrangeRed = Rgba(1, 0.28f, 0.1f), Red = Rgba(0.95f, 0.1f, 0.07f), BatonWhite = Rgba(0.96f, 0.96f, 0.93f),
        LcdAmber = Rgba(1, 0.62f, 0.12f),
        NGreen = Rgba(0.62f, 1, 0.72f), NOrange = Rgba(1, 0.6f, 0.24f), NWhite = Rgba(0.9f, 0.95f, 1), NRed = Rgba(1, 0.36f, 0.3f), NAmber = Rgba(1, 0.72f, 0.3f);

    private static Meter M(Kind k, float x, float y, float r, Mount m = Mount.Dash) => new(k, x, y, r, m);

    // shared meter sets (units from the housing's top-left)
    private static readonly Vector2 Wide = new(500, 232);
    /// <summary>Toyota 80s: fuel/temp combination dial left, tach centre, speedo right.</summary>
    private static readonly Meter[] Ae86 = [M(Kind.FuelTemp, 62, 128, 44), M(Kind.Tach, 222, 118, 104), M(Kind.Speedo, 414, 124, 78)];
    /// <summary>Two small gauges stacked left, tach centre, speedo right.</summary>
    private static Meter[] Stack(Kind top, Kind bottom) =>
        [M(top, 56, 76, 34), M(bottom, 56, 172, 34), M(Kind.Tach, 210, 118, 104), M(Kind.Speedo, 410, 124, 80)];
    /// <summary>Nissan S13 family / R32: speedo left, tach right, two small gauges between.</summary>
    private static Meter[] Twin(Kind top, Kind bottom) =>
        [M(Kind.Speedo, 112, 116, 94), M(top, 240, 66, 28), M(bottom, 240, 166, 28), M(Kind.Tach, 368, 116, 94)];
    private static readonly Vector2 TwinSize = new(480, 228);
    /// <summary>Mazda FD: speedo left, tach centre, two small gauges stacked right.</summary>
    private static Meter[] Fd(Kind top, Kind bottom) =>
        [M(Kind.Speedo, 96, 124, 80), M(Kind.Tach, 290, 118, 104), M(top, 452, 72, 32), M(bottom, 452, 168, 32)];

    /// <summary>Eunos/MX-5: five separate cowls, fuel/temp left, oil right.</summary>
    private static readonly Meter[] Roadster =
        [M(Kind.Fuel, 41, 42, 28), M(Kind.Temp, 41, 138, 28), M(Kind.Speedo, 172, 90, 76), M(Kind.Tach, 351, 90, 76), M(Kind.Oil, 482, 90, 28)];

    /// <summary>
    ///     Per car (HCAR names). Layout/range from the real car (research notes, partly from memory); night colours and the
    ///     Type R red faces / STi white faces are best guesses. Every tach reaches past the physics rev limit (CarSpecs).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Gauge> Cars = new Dictionary<string, Gauge>
    {
        // Toyota AE8x: boxy 80s hood, upright thin numerals, fine ticks, slim orange needles, greenish night light
        ["AE86T"] = new(9000, 7600, Wide, Ae86, Black, White, Orange, NGreen, Corner: 10, Badge: "TWIN CAM 16", Weight: -0.5f, Slim: true),
        ["AE86L"] = new(9000, 7600, Wide, Ae86, Black, White, Orange, NGreen, Corner: 10, Badge: "GT-APEX", Weight: -0.5f, Slim: true),
        ["AE85"] = new(8000, 6000, new(486, 224),
            [M(Kind.FuelTemp, 60, 124, 42), M(Kind.Tach, 212, 114, 98), M(Kind.Speedo, 398, 120, 76)],
            Black, White, Orange, NGreen, Corner: 10, Weight: -0.4f, NumSize: 0.9f, Slim: true, Minor: 1),
        // Toyota 90s turbo: rounder hood, block ticks, italic numerals, turbo gauge over temp
        ["MR2"] = new(8000, 7000, Wide, Stack(Kind.Boost, Kind.Temp), Black, White, OrangeRed, NOrange, Corner: 44, Ticks: Ticks.Block, Skew: 0.08f),
        // MR-S: big speedo centre, small tach left, fuel/temp right, chrome rings
        ["MRS"] = new(8000, 6800, new(492, 232),
            [M(Kind.Tach, 82, 140, 72), M(Kind.Speedo, 270, 118, 102), M(Kind.FuelTemp, 434, 140, 44)],
            Black, White, OrangeRed, NOrange, Corner: 60, Chrome: true, Weight: -0.3f),
        // Altezza: chronograph dials with sub-dials, thin watch numerals, baton needles
        ["ALTEZ"] = new(9000, 7800, new(500, 228),
            [M(Kind.Speedo, 116, 114, 98), M(Kind.Fuel, 116, 164, 20, Mount.Inset), M(Kind.Tach, 384, 114, 98), M(Kind.Temp, 384, 164, 20, Mount.Inset)],
            Black, White, BatonWhite, NWhite, Corner: 50, Ticks: Ticks.Watch, Weight: -0.6f, Chrome: true),
        // Celica GT-Four: numbers on the outer ring, boost and fuel/temp left
        ["GT-4"] = new(8000, 7000, Wide, Stack(Kind.Boost, Kind.FuelTemp), Black, White, OrangeRed, NOrange, Corner: 24, Ticks: Ticks.Outer,
            Badge: "GT-FOUR", Skew: 0.06f),
        // Skyline R32: Nissan twin, oil pressure/temp between, console trio (boost, oil temp, volts) left
        ["R32"] = new(9000, 8000, TwinSize, [.. Twin(Kind.Oil, Kind.Temp), M(Kind.Boost, -44, 38, 26, Mount.Pod), M(Kind.OilTemp, -44, 114, 26, Mount.Pod),
            M(Kind.Volt, -44, 190, 26, Mount.Pod)], Black, White, OrangeRed, NOrange, Corner: 26, Badge: "GT-R"),
        // Late-90s Nissan: wedge housing, red needles; R34 with the multi-function display on the dash centre
        ["R34"] = new(9000, 8000, new(510, 232), [.. Ae86, M(Kind.Mfd, -96, 132, 64, Mount.Pod)], Black, White, Red, NWhite, Housing.Wedge,
            Ticks: Ticks.Block, Badge: "GT-R", Skew: 0.1f, Ring: Rgba(0.55f, 0.57f, 0.6f)),
        ["ER34"] = new(8000, 7000, new(510, 232), Stack(Kind.Boost, Kind.FuelTemp), Black, White, OrangeRed, NOrange, Housing.Wedge, Ticks: Ticks.Block,
            Skew: 0.1f),
        ["S13"] = new(8000, 7000, TwinSize, Twin(Kind.Boost, Kind.Temp), Black, White, Orange, NOrange, Corner: 20),
        ["S14Q"] = new(8000, 7000, TwinSize, Twin(Kind.Temp, Kind.Fuel), Black, White, OrangeRed, NOrange, Corner: 48, Ticks: Ticks.Block, Skew: 0.06f),
        ["S14"] = new(8000, 7000, TwinSize, Twin(Kind.Boost, Kind.Temp), Black, White, OrangeRed, NOrange, Corner: 48, Ticks: Ticks.Block, Skew: 0.06f),
        // S15: separate round boost gauge on top of the dash centre
        ["S15"] = new(9000, 7500, new(510, 232), [.. Ae86, M(Kind.Boost, 40, -50, 32, Mount.Pod)], Black, White, OrangeRed, NAmber, Housing.Wedge,
            Badge: "SPEC R", Skew: 0.1f, Ring: Rgba(0.75f, 0.2f, 0.1f)),
        ["ONE80"] = new(8000, 7000, TwinSize, Twin(Kind.Boost, Kind.Temp), Black, White, Orange, NOrange, Corner: 20),
        ["SIL80"] = new(8000, 7000, TwinSize, Twin(Kind.Boost, Kind.Temp), Black, White, Orange, NOrange, Corner: 20),
        // Honda: numbers outside the tick ring, Type R red faces, early-90s Civic black with green light
        ["EK9"] = new(10000, 8400, Wide, Stack(Kind.Temp, Kind.Fuel), RedFace, White, Red, NWhite, Corner: 56, Ticks: Ticks.Outer, Badge: "TYPE R",
            BadgeRed: false, Skew: 0.04f),
        ["EG6"] = new(10000, 8200, Wide, Stack(Kind.Temp, Kind.Fuel), Black, White, OrangeRed, NGreen, Corner: 70, Ticks: Ticks.Outer, Badge: "VTEC",
            Weight: -0.4f, Slim: true),
        ["INTGR"] = new(10000, 8400, Wide, Stack(Kind.Temp, Kind.Fuel), RedFace, White, Red, NWhite, Corner: 40, Ticks: Ticks.Outer, Badge: "TYPE R",
            Skew: 0.04f, Chrome: true),
        ["S2000"] = new(10000, 9000, new(470, 210), [], Black, LcdAmber, LcdAmber, LcdAmber, Housing.Lcd),
        // Lancer Evolution III/IV: boost gauge on the brow above the tach
        ["EVO3"] = new(8000, 7000, Wide, [.. Ae86, M(Kind.Boost, 134, -40, 28, Mount.Pod)], Black, White, Orange, NGreen, Corner: 16, Ticks: Ticks.Block),
        ["EVO4"] = new(9000, 7500, Wide, [.. Ae86, M(Kind.Boost, 134, -40, 28, Mount.Pod)], Black, White, Orange, NGreen, Corner: 30, Ticks: Ticks.Block,
            Skew: 0.06f),
        // Evo VII: white rings, red needles, sporty italic
        ["EVO7"] = new(8000, 7000, Wide, Stack(Kind.Temp, Kind.Fuel), Black, White, Red, NWhite, Corner: 44, Skew: 0.12f, Chrome: true, Ring: White),
        // RX-7 FD: speedo left, centre tach, boost/oil right; Project D car: temp/fuel right, aftermarket boost/oil pods up the A-pillar
        ["FD3S"] = new(10000, 8000, Wide, Fd(Kind.Boost, Kind.Oil), Black, White, OrangeRed, NOrange, Corner: 40, Badge: "ROTARY", NumSize: 0.92f),
        ["FD3SA"] = new(10000, 8000, Wide, [.. Fd(Kind.Temp, Kind.Fuel), M(Kind.Boost, 470, -42, 28, Mount.Pod), M(Kind.Oil, 470, -124, 28, Mount.Pod)],
            Black, White, OrangeRed, NOrange, Corner: 40, Badge: "ROTARY", NumSize: 0.92f),
        // RX-7 FC: square column-mounted box, boost and oil pressure left
        ["FC3S"] = new(9000, 7000, Wide, Stack(Kind.Boost, Kind.Oil), Black, White, Orange, NOrange, Corner: 6, Ticks: Ticks.Block, Badge: "ROTARY TURBO",
            Weight: 0.4f),
        // Roadsters: five separate cowls (fuel/temp left, oil right), chrome rings; NB with white faces
        ["NA6C"] = new(8000, 7200, new(524, 180), Roadster, Black, White, Orange, NOrange, Housing.Cowls, Chrome: true, Weight: -0.3f),
        ["NB8C"] = new(8000, 7000, new(524, 180), Roadster, WhiteFace, Ink, Red, NOrange, Housing.Cowls, Chrome: true, Skew: 0.06f),
        // Impreza STi: white faces, pink-red STi badge
        ["IMP"] = new(9000, 8000, Wide, Stack(Kind.Boost, Kind.Temp), WhiteFace, Ink, Red, NOrange, Corner: 36, Badge: "STi", BadgeRed: true),
        ["IMP2"] = new(9000, 8000, Wide, Stack(Kind.Boost, Kind.Temp), SilverFace, Ink, Red, NRed, Corner: 50, Ticks: Ticks.Block, Badge: "STi",
            BadgeRed: true, Skew: 0.1f),
        ["IMP3"] = new(9000, 8000, Wide, Stack(Kind.Boost, Kind.Temp), WhiteFace, Ink, Red, NOrange, Corner: 36, Badge: "STi", BadgeRed: true),
        // Cappuccino: three small black cowls, kei proportions
        ["CAPPU"] = new(11000, 9000, new(436, 210),
            [M(Kind.Speedo, 86, 108, 72), M(Kind.Tach, 261, 108, 80), M(Kind.Boost, 394, 66, 30), M(Kind.Temp, 394, 150, 30)],
            Black, White, Orange, NOrange, Housing.Cowls, NumSize: 0.85f),
    };

    /// <summary>What the dials show this frame; <see cref="Boost"/> in bar (−1..1), <see cref="Time"/> in s for blinking.</summary>
    public readonly record struct Reading(float Rpm, float Kmh, int Gear, bool Automatic, float Boost, bool Night, float Time);

    private const float SpeedoMax = 180, A0 = 140 * MathF.PI / 180, Sweep = 252 * MathF.PI / 180;
    private static readonly uint HousingFill = Rgba(0.045f, 0.046f, 0.05f, 0.94f), HousingRim = Rgba(0.2f, 0.2f, 0.22f, 0.95f),
        Brow = Rgba(0.1f, 0.1f, 0.11f, 0.94f), ChromeLight = Rgba(0.82f, 0.83f, 0.85f), ChromeDark = Rgba(0.28f, 0.29f, 0.31f),
        RedZone = Rgba(0.9f, 0.1f, 0.08f), Window = Rgba(0.01f, 0.01f, 0.012f, 0.96f), Hub = Rgba(0.02f, 0.02f, 0.02f);

    /// <summary>Outer radius (units) a meter occupies: face + bezel, + the cowl around it.</summary>
    public static float Outer(Gauge g, Meter m) => m.R + (g.Chrome ? 5 : 3) + (m.Mount == Mount.Pod || g.Housing == Housing.Cowls ? 8 : 0);

    /// <summary>Cluster of <paramref name="g"/> with its housing's bottom-right corner at <paramref name="corner"/>, <paramref name="u"/> px per unit.</summary>
    public static void Draw(Overlay o, Gauge g, Vector2 corner, float u, in Reading r)
    {
        var size = g.Size * u;
        var p0 = corner - size;
        var dark = Luma(g.Face) < 0.4f;
        var k = new Look(g.Face, r.Night && dark ? g.Night : g.Ink, g.Needle, g.Weight * u, g.Skew, g.Chrome, r.Night && dark, g.Ticks, g.Ring, g.Slim);
        switch (g.Housing)
        {
            case Housing.Lcd:
                Digital(o, g, p0, size, u, r);
                return;
            case Housing.Hood: Hood(o, p0, p0 + size, g.Corner * u); break;
            case Housing.Wedge: Wedge(o, p0, p0 + size, u); break;
        }
        foreach (var m in g.Meters)
        {
            var c = p0 + new Vector2(m.X, m.Y) * u;
            if (m.Mount == Mount.Pod || (g.Housing == Housing.Cowls && m.Mount == Mount.Dash)) Cowl(o, c, (Outer(g, m) - 1) * u, u, g.Chrome);
        }
        // chronograph: the sub-dials take the bottom of each dial, so speed and gear sit in a column between the dials
        var watch = g.Ticks == Ticks.Watch;
        var column = p0 + new Vector2(size.X / 2, 0);
        foreach (var m in g.Meters)
        {
            var c = p0 + new Vector2(m.X, m.Y) * u;
            var rad = m.R * u;
            switch (m.K)
            {
                case Kind.Tach:
                    var tach = new Scale(g.TachMax, 1000, g.Minor > 0 ? g.Minor : g.TachMax > 9000 ? 2 : 4, 1000, g.Redline, 1000);
                    Dial(o, c, rad, u, k, tach, r.Rpm, "x1000r/min", 20 * g.NumSize * MathF.Min(1, m.R / 96), g.Badge, g.BadgeRed);
                    GearWindow(o, watch ? column + new Vector2(0, 150 * u) : c + new Vector2(0, 0.63f * rad), u, r);
                    break;
                case Kind.Speedo:
                    var speedo = new Scale(SpeedoMax, 20, g.Minor > 0 ? g.Minor : 2, 20, float.MaxValue, 1);
                    Dial(o, c, rad, u, k, speedo, r.Kmh, "km/h", 15 * g.NumSize * MathF.Min(1, m.R / 90));
                    SpeedWindow(o, watch ? column + new Vector2(0, 92 * u) : c + new Vector2(0, 0.62f * rad), watch ? 70 * u : rad, u, k, r.Kmh);
                    break;
                case Kind.Mfd:
                    Mfd(o, c, rad, u, r, g.TachMax);
                    break;
                default:
                    Mini(o, c, rad, u, k, m.K, m.Mount == Mount.Inset, r, g.TachMax);
                    break;
            }
        }
    }

    private readonly record struct Look(uint Face, uint Ink, uint Needle, float Weight, float Skew, bool Chrome, bool Glow, Ticks Ticks, uint Ring, bool Slim);

    /// <summary>Dial scale: full <see cref="Max"/>, major tick every <see cref="Major"/> with <see cref="Minor"/> steps, number every <see cref="Label"/> (÷<see cref="Divide"/>), red from <see cref="Red"/>.</summary>
    private readonly record struct Scale(float Max, float Major, int Minor, float Label, float Red, float Divide);

    private static float Luma(uint c) => ((c & 0xFF) * 0.3f + (c >> 8 & 0xFF) * 0.59f + (c >> 16 & 0xFF) * 0.11f) / 255;

    private static Vector2 Dir(float a) => new(MathF.Cos(a), MathF.Sin(a));

    private static float Angle(float v, float max) => A0 + Sweep * Math.Clamp(v / max, 0, 1.015f);

    /// <summary>Rounded binnacle with a lighter brow along the top.</summary>
    private static void Hood(Overlay o, Vector2 min, Vector2 max, float r)
    {
        RoundRect(o, min, max, r, HousingRim);
        RoundRect(o, min + new Vector2(2), max - new Vector2(2), MathF.Max(r - 2, 0), Brow);
        RoundRect(o, min + new Vector2(4, 9), max - new Vector2(4), MathF.Max(r - 4, 0), HousingFill);
    }

    /// <summary>Late-90s Nissan housing: top narrower than the bottom, chamfered lower corners.</summary>
    private static void Wedge(Overlay o, Vector2 min, Vector2 max, float u)
    {
        Vector2[] Shape(float e, float top) =>
        [
            new(min.X + 34 * u + e, min.Y + top), new(max.X - 34 * u - e, min.Y + top), new(max.X - e, min.Y + 70 * u),
            new(max.X - e, max.Y - 22 * u), new(max.X - 22 * u - e, max.Y - e), new(min.X + 22 * u + e, max.Y - e),
            new(min.X + e, max.Y - 22 * u), new(min.X + e, min.Y + 70 * u),
        ];
        Fan(o, Shape(0, 0), HousingRim);
        Fan(o, Shape(2 * u, 2 * u), Brow);
        Fan(o, Shape(4 * u, 9 * u), HousingFill);
    }

    /// <summary>Filled convex polygon as a triangle fan (no overlaps, so translucent colours blend once), AA outline.</summary>
    private static void Fan(Overlay o, ReadOnlySpan<Vector2> p, uint color)
    {
        var c = Vector2.Zero;
        foreach (var v in p) c += v;
        c /= p.Length;
        for (var i = 0; i < p.Length; i++) o.Triangle(c, p[i], p[(i + 1) % p.Length], color);
        for (var i = 0; i < p.Length; i++) o.Line(p[i], p[(i + 1) % p.Length], 1, color);
    }

    private static void RoundRect(Overlay o, Vector2 min, Vector2 max, float r, uint color)
    {
        const int n = 6;
        Span<Vector2> p = stackalloc Vector2[4 * n];
        Vector2[] corners = [new(max.X - r, min.Y + r), new(max.X - r, max.Y - r), new(min.X + r, max.Y - r), new(min.X + r, min.Y + r)];
        for (var k = 0; k < 4; k++)
        for (var i = 0; i < n; i++)
            p[k * n + i] = corners[k] + Dir((k - 1 + i / (n - 1f)) * MathF.PI / 2) * r;
        Fan(o, p, color);
    }

    /// <summary>Separate round cowl: dark cup with a chrome or black ring.</summary>
    private static void Cowl(Overlay o, Vector2 c, float r, float u, bool chrome)
    {
        o.Disc(c, r + 2 * u, Rgba(0, 0, 0, 0.55f));
        o.Disc(c, r, chrome ? ChromeDark : HousingRim);
        o.Disc(c, r - 2 * u, HousingFill);
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

    /// <summary>Half extent of a centred label along direction <paramref name="d"/> (box approximation).</summary>
    private static float Extent(Overlay o, string text, float size, Vector2 d) =>
        MathF.Abs(d.X) * o.Font!.Measure(text, size) / 2 + MathF.Abs(d.Y) * o.Font.CapHeight * size / 2;

    /// <summary>Analog dial: bezel, red zone, ticks, numbers (inside or outside the ticks), unit, badge, needle with hub.</summary>
    private static void Dial(Overlay o, Vector2 c, float r, float u, in Look k, in Scale s, float value, string unit, float numSize, string badge = "", bool badgeRed = false)
    {
        Bezel(o, c, r, u, k);
        if (k.Ring != 0) o.Ring(c, r - 1.5f * u, 2 * u, k.Ring, 64);
        var watch = k.Ticks == Ticks.Watch;
        var outer = k.Ticks == Ticks.Outer;
        // tick band: at the rim, or an inner ring with the numbers outside it
        var t0 = outer ? r - 30 * u : r - 3 * u;
        var redR = outer ? t0 - 3 * u : watch ? r - 12 * u : r - 6 * u;
        if (s.Red < s.Max) o.Arc(c, redR, (watch ? 3 : 5) * u, RedZone, Angle(s.Red, s.Max), Angle(s.Max, s.Max), 24);
        if (watch)
        {
            o.Ring(c, r - 6 * u, 0.8f * u, Style.Fade(k.Ink, 0.5f), 72);
            for (var i = 0; i < 120; i++)
                o.Line(c + Dir(i * MathF.Tau / 120) * (r - 4 * u), c + Dir(i * MathF.Tau / 120) * (r - 8 * u), 0.7f * u, Style.Fade(k.Ink, 0.45f));
        }
        var steps = (int)MathF.Round(s.Max / s.Major) * s.Minor;
        var size = numSize * u;
        for (var i = 0; i <= steps; i++)
        {
            var v = i * s.Major / s.Minor;
            var d = Dir(Angle(v, s.Max));
            var major = i % s.Minor == 0;
            var tickCol = v >= s.Red && !k.Glow ? RedZone : k.Ink;
            if (watch)
            {
                if (major) o.Line(c + d * (r - 10 * u), c + d * (r - 22 * u), 3.2f * u, v >= s.Red ? Red : k.Ink);
            }
            else if (k.Ticks == Ticks.Block)
            {
                if (major) o.Line(c + d * t0, c + d * (t0 - 10 * u), 5 * u, tickCol);
                else o.Disc(c + d * (t0 - 3 * u), 1.4f * u, tickCol);
            }
            else o.Line(c + d * t0, c + d * (t0 - (major ? 12 : 7) * u), (major ? 2.6f : 1.2f) * (k.Slim ? 0.8f : 1) * u, tickCol);
            if (!major || MathF.Round(v) % s.Label != 0) continue;
            var text = $"{v / s.Divide:0}";
            var sz = text.Length > 2 && s.Divide == 1 ? size * 0.88f : text.Length > 1 && s.Divide > 1 ? size * 0.82f : size;
            var ext = Extent(o, text, sz, d);
            // outside: between rim and tick ring; inside: clear of the ticks and the red band
            var at = outer ? (r - 4 * u + t0) / 2 : watch ? r - 30 * u - ext : t0 - 19 * u - ext;
            Numeral(o, text, c + d * at, sz, k, v >= s.Red ? Red : k.Ink);
        }
        Label(o, unit, new Vector2(c.X, c.Y - r * 0.3f), MathF.Max(0.11f * r, 8 * u), k);
        if (badge != "") Label(o, badge, new Vector2(c.X, c.Y + r * 0.34f), 0.12f * r, k, badgeRed ? Red : null, 0.6f);
        var a = Angle(value, s.Max);
        if (watch) Baton(o, c, a, r, u, k);
        else if (k.Slim) Needle(o, c, a, r - (outer ? 26 : 6) * u, r * 0.12f, 0.035f * r, 0.035f * r, k.Needle, u, 0.09f * r);
        else Needle(o, c, a, r - (outer ? 26 : 6) * u, r * 0.2f, 0.07f * r, 0.035f * r, k.Needle, u, 0.12f * r);
    }

    private static void Label(Overlay o, string s, Vector2 centre, float size, in Look k, uint? color = null, float alpha = 0.75f)
    {
        var at = new Vector2(centre.X, centre.Y + o.Font!.CapHeight * size / 2);
        o.Text(s, at, size, Style.Fade(color ?? k.Ink, alpha), 0.5f, k.Weight * 0.5f, 0, k.Skew);
    }

    /// <summary>Needle from width <paramref name="w0"/> at the root to <paramref name="w1"/> at the tip, over a soft shadow, black hub cap.</summary>
    private static void Needle(Overlay o, Vector2 c, float angle, float len, float tail, float w0, float w1, uint color, float u, float hub)
    {
        var d = Dir(angle);
        var sh = new Vector2(1.5f, 2.5f) * u;
        o.Line(c - d * tail + sh, c + d * len + sh, w1 * 1.4f, Rgba(0, 0, 0, 0.35f));
        o.Line(c - d * tail, c + d * len * 0.45f, w0, color);
        o.Line(c + d * len * 0.4f, c + d * len, w1, color);
        o.Disc(c, hub, Hub);
        o.Disc(c, hub * 0.35f, Rgba(0.25f, 0.25f, 0.27f));
    }

    /// <summary>Altezza baton: slim white hand with a red tip, chrome hub.</summary>
    private static void Baton(Overlay o, Vector2 c, float a, float r, float u, in Look k)
    {
        var d = Dir(a);
        var sh = new Vector2(1.5f, 2.5f) * u;
        o.Line(c - d * r * 0.18f + sh, c + d * (r - 8 * u) + sh, 3 * u, Rgba(0, 0, 0, 0.35f));
        o.Line(c - d * r * 0.18f, c + d * (r - 22 * u), 4 * u, k.Needle);
        o.Line(c + d * (r - 22 * u), c + d * (r - 8 * u), 2.2f * u, Red);
        o.Disc(c, 6 * u, ChromeLight);
        o.Disc(c, 3 * u, Hub);
    }

    /// <summary>
    ///     Small gauge: arc across the top (ends lo/hi, caption below), fuel/temp combination with two half-arcs, or a
    ///     thin-ringed chronograph sub-dial when <paramref name="inset"/>.
    /// </summary>
    private static void Mini(Overlay o, Vector2 c, float r, float u, in Look k, Kind kind, bool inset, in Reading rd, int tachMax)
    {
        var (caption, lo, hi, value) = kind switch
        {
            Kind.Fuel => ("FUEL", "E", "F", 0.7f),
            Kind.Temp => ("TEMP", "C", "H", 0.45f),
            Kind.Oil => ("OIL", "0", "8", 0.25f + 0.5f * rd.Rpm / tachMax),
            Kind.Boost => ("BOOST", "-1", "+1", (rd.Boost + 1) / 2),
            Kind.OilTemp => ("OIL °C", "50", "150", 0.42f + 0.1f * rd.Rpm / tachMax),
            Kind.Volt => ("VOLT", "8", "16", 0.7f),
            _ => ("FUEL", "E", "F", 0.7f),
        };
        if (inset)
        {
            o.Disc(c, r, k.Face);
            o.Ring(c, r, 1 * u, Style.Fade(k.Ink, 0.7f), 32);
            for (var i = 0; i <= 4; i++)
                o.Line(c + Dir(MathF.PI + i * MathF.PI / 4) * r, c + Dir(MathF.PI + i * MathF.PI / 4) * (r - 4 * u), 1 * u, k.Ink);
            Label(o, caption, c + new Vector2(0, r * 0.45f), 0.36f * r, k);
            o.Line(c, c + Dir(MathF.PI + MathF.PI * value) * (r - 3 * u), 1.6f * u, Red);
            o.Disc(c, 2.2f * u, ChromeLight);
            return;
        }
        Bezel(o, c, r, u, k);
        var size = MathF.Max(0.3f * r, 10 * u);
        if (kind == Kind.FuelTemp)
        {
            // fuel on an upper arc, water temperature on a lower one, one needle each from the centre
            Arc(o, c, r, u, k, 215, 110, size, "E", "F", 0.7f, "FUEL", -1);
            Arc(o, c, r, u, k, 145, -110, size, "C", "H", 0.45f, "TEMP", 1);
            o.Disc(c, 0.1f * r, Hub);
            return;
        }
        Arc(o, c, r, u, k, 200, 140, size, lo, hi, value, caption, 1);
    }

    /// <summary>Scale arc from <paramref name="from"/>° over <paramref name="sweep"/>° with end letters, caption above (−1) or below (+1) the hub, and its needle.</summary>
    private static void Arc(Overlay o, Vector2 c, float r, float u, in Look k, float from, float sweep, float size, string lo, string hi, float value, string caption, int side)
    {
        float a0 = from * MathF.PI / 180, sw = sweep * MathF.PI / 180;
        for (var i = 0; i <= 4; i++)
        {
            var d = Dir(a0 + sw * i / 4);
            o.Line(c + d * (r - 3 * u), c + d * (r - (i % 2 == 0 ? 10 : 6) * u), (i % 2 == 0 ? 2 : 1.2f) * u, i == 4 && caption is "TEMP" or "OIL °C" ? RedZone : k.Ink);
        }
        Numeral(o, lo, c + Dir(a0) * (r - 10 * u - size * 0.7f), size, k, k.Ink);
        Numeral(o, hi, c + Dir(a0 + sw) * (r - 10 * u - size * 0.7f), size, k, k.Ink);
        Label(o, caption, new Vector2(c.X, c.Y + side * r * 0.42f), size * 0.85f, k);
        Needle(o, c, a0 + sw * Math.Clamp(value, 0, 1), r - 5 * u, r * 0.12f, 0.08f * r, 0.05f * r, k.Needle, u, 0.13f * r);
    }

    /// <summary>Odometer-style window with the speed in drum digits, sized to the dial.</summary>
    private static void SpeedWindow(Overlay o, Vector2 c, float dial, float u, in Look k, float kmh)
    {
        var half = new Vector2(MathF.Min(30 * u, 0.36f * dial), 13 * u);
        o.Rect(Vector2.Round(c - half - new Vector2(u)), Vector2.Round(c + half + new Vector2(u)), Rgba(0.4f, 0.4f, 0.42f));
        o.Rect(Vector2.Round(c - half), Vector2.Round(c + half), Window);
        var size = MathF.Min(24 * u, half.X * 0.8f);
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

    /// <summary>R34 multi-function display: dark LCD panel (half height <paramref name="h"/>) with boost bar, oil and water temperatures.</summary>
    private static void Mfd(Overlay o, Vector2 c, float h, float u, in Reading r, int tachMax)
    {
        var half = new Vector2(1.25f * h, h);
        RoundRect(o, c - half - new Vector2(4 * u), c + half + new Vector2(4 * u), 10 * u, HousingRim);
        RoundRect(o, c - half, c + half, 6 * u, Rgba(0.02f, 0.035f, 0.05f, 0.97f));
        var ink = Rgba(0.55f, 0.85f, 1);
        var x0 = c.X - half.X + 12 * u;
        var x1 = c.X + half.X - 12 * u;
        var y = c.Y - half.Y + 24 * u;
        o.Text("BOOST", new Vector2(x0, y), 14 * u, Style.Fade(ink, 0.75f), 0, 0.2f * u);
        o.Text($"{r.Boost:+0.0;-0.0}", new Vector2(x1, y), 18 * u, ink, 1, 0.3f * u);
        var bar0 = new Vector2(x0, y + 8 * u);
        for (var i = 0; i < 16; i++)
        {
            var x = bar0.X + i * (x1 - x0) / 16;
            o.Rect(Vector2.Round(new Vector2(x, bar0.Y)), Vector2.Round(new Vector2(x + (x1 - x0) / 16 - 2 * u, bar0.Y + 12 * u)),
                i < (r.Boost + 1) / 2 * 16 ? ink : Style.Fade(ink, 0.15f));
        }
        y += 46 * u;
        o.Text("OIL", new Vector2(x0, y), 14 * u, Style.Fade(ink, 0.75f), 0, 0.2f * u);
        o.Text($"{90 + 12 * r.Rpm / tachMax:0}°C", new Vector2(x1, y), 18 * u, ink, 1, 0.3f * u);
        y += 30 * u;
        o.Text("WATER", new Vector2(x0, y), 14 * u, Style.Fade(ink, 0.75f), 0, 0.2f * u);
        o.Text("84°C", new Vector2(x1, y), 18 * u, ink, 1, 0.3f * u);
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
