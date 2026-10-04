using System.Numerics;
using Kansei.Graphics;

namespace Touge.Ui;

/// <summary>
///     Course dial, bottom left of the HUD opposite the cluster: a round pod in the cluster's housing colours (chrome ring on
///     chrome-bezel cars) holding the minimap of the CRS_ROAD centre line. Its bezel carries progress on the cluster's
///     dial arc (252° from 140°): finished sectors green/red like the timing chips, the current one amber, notches at the
///     quarters, green start tick, checkered goal, amber diamond for the car. The free bottom of the arc has a window
///     with the remaining distance. Map: road ahead bright, already driven dimmed, sector bars, checkered goal, start bar
///     until the timer runs, white car arrow; zoom follows speed (170 m at 40 km/h to 320 m at 160 km/h). At night the
///     road and the window light up in the car's <see cref="Cluster.Gauge.Night"/> colour.
/// </summary>
public sealed class MapWidget
{
    /// <summary>Outer radius in units: diameter = the cluster housing's height.</summary>
    public const float Radius = 116;

    private const float A0 = 140 * MathF.PI / 180, Sweep = 252 * MathF.PI / 180;
    // housing colours as in Cluster.cs; track and driven road are mixed opaque, because translucent arc/line segments overlap into beads
    private static readonly uint HousingFill = Overlay.Rgba(0.045f, 0.046f, 0.05f, 0.94f), HousingRim = Overlay.Rgba(0.2f, 0.2f, 0.22f, 0.95f),
        Brow = Overlay.Rgba(0.1f, 0.1f, 0.11f, 0.94f), ChromeLight = Overlay.Rgba(0.82f, 0.83f, 0.85f), Window = Overlay.Rgba(0.01f, 0.01f, 0.012f, 0.96f),
        WindowRim = Overlay.Rgba(0.4f, 0.4f, 0.42f), Track = Overlay.Rgba(0.22f, 0.22f, 0.23f), Done = Overlay.Rgba(0.8f, 0.8f, 0.8f),
        RoadEdge = Overlay.Rgba(0.05f, 0.06f, 0.08f, 0.95f), Road = Overlay.Rgba(0.93f, 0.94f, 0.96f), StartColor = Overlay.Rgba(0.2f, 0.95f, 0.35f);

    private readonly Vector2[] _road;
    private readonly float[] _along;
    private readonly Vector2 _start, _startDir, _goal, _goalDir, _centre;
    private readonly float _extent;
    private readonly int _laps;
    private float _zoom = 170;

    public MapWidget(Vector3[] road, Vector3[] line)
    {
        _road = Array.ConvertAll(road, Xz);
        var l = Array.ConvertAll(line, Xz);
        // circuits (USUI0, MYOUGI0): the line runs the road loop twice
        float lineLen = 0, roadLen = 0;
        for (var i = 1; i < l.Length; i++) lineLen += Vector2.Distance(l[i - 1], l[i]);
        for (var i = 1; i < _road.Length; i++) roadLen += Vector2.Distance(_road[i - 1], _road[i]);
        _laps = Math.Clamp((int)MathF.Round(lineLen / roadLen), 1, LapTimer.Sectors);
        _along = Along(_road, l, _laps);
        (_start, _startDir) = (l[0], Vector2.Normalize(l[1] - l[0]));
        (_goal, _goalDir) = (l[^1], Vector2.Normalize(l[^1] - l[^2]));
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var p in _road) (min, max) = (Vector2.Min(min, p), Vector2.Max(max, p));
        _centre = (min + max) / 2;
        foreach (var p in _road) _extent = MathF.Max(_extent, Vector2.Distance(p, _centre));
    }

    private static Vector2 Xz(Vector3 v) => new(v.X, v.Z);

    /// <summary>
    ///     Fraction (0..1) of the first of <paramref name="laps"/> laps along <paramref name="line"/> for each <paramref name="road"/>
    ///     point: that of its nearest line vertex in that lap. Works for either road direction; hairpin legs are far enough
    ///     apart that the nearest vertex is on the same leg.
    /// </summary>
    public static float[] Along(Vector2[] road, Vector2[] line, int laps = 1)
    {
        var cum = new float[line.Length];
        for (var j = 1; j < line.Length; j++) cum[j] = cum[j - 1] + Vector2.Distance(line[j - 1], line[j]);
        var along = new float[road.Length];
        for (var i = 0; i < road.Length; i++)
        {
            // ponytail: brute force O(road × line), ~ms once per course load; windowed search if it ever shows up
            var (best, at) = (float.MaxValue, 0);
            var lap = cum[^1] / laps;
            for (var j = 0; j < line.Length && cum[j] <= lap * 1.02f; j++)
            {
                var d = Vector2.DistanceSquared(road[i], line[j]);
                if (d < best) (best, at) = (d, j);
            }
            along[i] = MathF.Min(cum[at] / lap, 1);
        }
        return along;
    }

    /// <summary>Per physics tick: map zoom eases towards the speed's (half width of the map in metres).</summary>
    public void Tick(float kmh, float dt) =>
        _zoom = Style.Approach(_zoom, 170 + 150 * Math.Clamp((kmh - 40) / 120, 0, 1), 60, dt);

    /// <summary>"2.48 km" / "640 m" split into number and unit.</summary>
    private static (string Value, string Unit) Distance(float metres) =>
        metres >= 1000 ? ((metres / 1000).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), "km") : ($"{MathF.Round(metres):0}", "m");

    /// <summary>
    ///     Pod centred at <paramref name="c"/>, <paramref name="s"/> px per unit; car at <paramref name="car"/> heading
    ///     <paramref name="heading"/> (XZ), <paramref name="progress"/> 0..1 along the line, <paramref name="remaining"/> metres to go.
    /// </summary>
    public void Draw(Overlay o, Vector2 c, float s, Hud.MapMode mode, Vector2 car, Vector2 heading, float progress, float remaining,
        LapTimer timer, Cluster.Gauge gauge, bool night)
    {
        var r = Radius * s;
        o.Disc(c, r + 2 * s, Overlay.Rgba(0, 0, 0, 0.55f));
        if (gauge.Chrome) o.Ring(c, r + s, 2.5f * s, ChromeLight, 96);
        o.Disc(c, r, HousingRim);
        o.Disc(c, r - 2 * s, Brow);
        o.Disc(c, r - 5 * s, HousingFill);

        Map(o, c, s, mode, car, heading, progress, timer, night ? gauge.Night : Road);
        Ring(o, c, s, progress, timer);

        // remaining distance in the free bottom of the arc, like the cluster's speed window
        var wc = c + new Vector2(0, (Radius - 30) * s);
        var half = new Vector2(42, 15) * s;
        o.Rect(Vector2.Round(wc - half - new Vector2(s)), Vector2.Round(wc + half + new Vector2(s)), WindowRim);
        o.Rect(Vector2.Round(wc - half), Vector2.Round(wc + half), Window);
        var (value, unit) = Distance(remaining);
        float big = 24 * s, small = 12 * s, gap = 3 * s;
        var w = o.Font!.Measure(value, big) + gap + o.Font.Measure(unit, small);
        var y = wc.Y + o.Font.CapHeight * big / 2;
        var x = wc.X - w / 2;
        x += o.Text(value, new Vector2(x, y), big, night ? gauge.Night : Style.Text, 0, 0.3f * s, 0, Style.Slant) + gap;
        o.Text(unit, new Vector2(x, y), small, night ? Style.Fade(gauge.Night, 0.6f) : Style.Dim, 0, 0.2f * s);
    }

    private static Vector2 Dir(float t) => new(MathF.Cos(A0 + Sweep * t), MathF.Sin(A0 + Sweep * t));

    /// <summary>Progress on the bezel: track, sector-coloured fill up to the car, quarter notches, start tick, goal checkers, car diamond.</summary>
    private static void Ring(Overlay o, Vector2 c, float s, float progress, LapTimer timer)
    {
        var rr = (Radius - 10) * s;
        var w = 7 * s;
        o.Arc(c, rr, w, Track, A0, A0 + Sweep, 96);
        for (var i = 0; i < LapTimer.Sectors; i++)
        {
            float t0 = (float)i / LapTimer.Sectors, t1 = MathF.Min((i + 1f) / LapTimer.Sectors, progress);
            if (t1 <= t0) break;
            var col = i >= timer.Sector ? Style.Amber : timer.Delta(i) is { } d ? d <= 0 ? Style.Green : Style.Red : Done;
            o.Arc(c, rr, w, col, A0 + Sweep * t0, A0 + Sweep * t1, Math.Max(2, (int)((t1 - t0) * 96)));
        }
        var reach = w / 2 + 3 * s;
        for (var i = 1; i < LapTimer.Sectors; i++)
        {
            var d = Dir((float)i / LapTimer.Sectors);
            o.Line(c + d * (rr - reach), c + d * (rr + reach), 2 * s, Style.Ink);
        }
        var d0 = Dir(0);
        o.Line(c + d0 * (rr - reach), c + d0 * (rr + reach), 3 * s, StartColor);
        // goal: 4 × 2 checkers across the ring end, 12 × 8 units
        var radial = Dir(1);
        var tangent = new Vector2(-radial.Y, radial.X);
        Vector2 g0 = c + radial * (rr - 7 * s), g1 = c + radial * (rr + 7 * s), gt = tangent * 5 * s;
        o.Quad(g0 - gt, g1 - gt, g1 + gt, g0 + gt, Style.Ink);
        for (var i = 0; i < 4; i++)
        for (var j = 0; j < 2; j++)
        {
            Vector2 a = c + radial * (rr + (i - 2) * 3 * s) + tangent * (j * 4 - 2) * s, b = radial * 3 * s, t = tangent * 4 * s;
            o.Quad(a, a + b, a + b + t, a + t, (i + j) % 2 == 0 ? Road : RoadEdge);
        }
        // car: amber diamond riding on the ring
        var p = c + Dir(progress) * rr;
        Vector2 rd = Dir(progress) * 5 * s, td = new Vector2(-rd.Y, rd.X);
        o.Quad(p + rd, p + td, p - rd, p - td, Style.Amber);
        o.Line(p + rd, p + td, s, Style.Ink);
        o.Line(p + td, p - rd, s, Style.Ink);
        o.Line(p - rd, p - td, s, Style.Ink);
        o.Line(p - td, p + rd, s, Style.Ink);
    }

    private void Map(Overlay o, Vector2 c, float s, Hud.MapMode mode, Vector2 car, Vector2 heading, float progress, LapTimer timer, uint road)
    {
        var rc = (Radius - 18) * s;
        heading = heading.LengthSquared() > 1e-6f ? Vector2.Normalize(heading) : new Vector2(0, -1);
        // screen up = f, screen right = (-f.y, f.x) on XZ (top-down, not mirrored: X right, −Z up when north up)
        var f = mode == Hud.MapMode.Rotating ? heading : new Vector2(0, -1);
        var right = new Vector2(-f.Y, f.X);
        var overview = mode == Hud.MapMode.Overview;
        var k = overview ? rc * 0.9f / _extent : rc / _zoom; // pixels per metre
        // zoomed: the view centre sits ahead of the car so more of the road ahead is visible
        var origin = overview ? _centre : car + heading * (_zoom * 0.35f);
        Vector2 ToScreen(Vector2 p)
        {
            var d = p - origin;
            return c + new Vector2(Vector2.Dot(d, right), -Vector2.Dot(d, f)) * k;
        }

        o.Clip(c, rc);
        const int stride = 2; // ROAD points are ~2 m apart
        var reach = _zoom * 1.1f + 2 * stride;
        var driven = Mix(road, HousingFill, 0.35f);
        var laps = progress * _laps;
        var lapDone = laps >= _laps ? 1 : laps - MathF.Floor(laps); // the map shows the current lap's road as driven
        var scale = (overview ? 0.6f : 1) * s;
        for (var pass = 0; pass < 2; pass++)
            for (var i = stride; i < _road.Length; i += stride)
            {
                var (a, b) = (_road[i - stride], _road[Math.Min(i, _road.Length - 1)]);
                if (!overview && Vector2.Distance(a, origin) > reach) continue;
                var col = pass == 0 ? RoadEdge : _along[i] <= lapDone ? driven : road;
                o.Line(ToScreen(a), ToScreen(b), (pass == 0 ? 7 : 4) * scale, col);
            }
        // sector bars across the road where the along fraction crosses a quarter of the run
        for (var i = stride; i < _road.Length; i += stride)
        {
            var (a, b) = (_along[i - stride], _along[Math.Min(i, _road.Length - 1)]);
            var per = LapTimer.Sectors / _laps;
            var q = MathF.Floor(MathF.Max(a, b) * per);
            if (q < 1 || q >= per || MathF.Min(a, b) * per >= q) continue;
            var p = _road[i];
            if (!overview && Vector2.Distance(p, origin) > reach) continue;
            Marker(o, ToScreen(p), _road[i] - _road[i - stride], f, right, s, 0, scale / s);
        }
        if (timer.Phase == LapTimer.State.Ready) Marker(o, ToScreen(_start), _startDir, f, right, s, 1, 1);
        Marker(o, ToScreen(_goal), _goalDir, f, right, s, 2, overview ? 1.5f : 1);

        // car arrow: white with ink outline and an amber glow, distinct from a lit road at night
        var p0 = ToScreen(car);
        for (var g = 3; g >= 1; g--) o.Disc(p0, (5 + 4 * g) * s, Overlay.Rgba(1, 0.72f, 0.1f, 0.1f));
        var dir = Vector2.Normalize(new Vector2(Vector2.Dot(heading, right), -Vector2.Dot(heading, f)));
        var n = new Vector2(-dir.Y, dir.X);
        Vector2 tip = p0 + dir * 10 * s, l = p0 - dir * 6 * s + n * 7 * s, rr = p0 - dir * 6 * s - n * 7 * s, notch = p0 - dir * 2.5f * s;
        o.Triangle(tip, l, notch, Style.Text);
        o.Triangle(tip, notch, rr, Style.Text);
        var edge = 2 * s;
        o.Line(tip, l, edge, Style.Ink);
        o.Line(l, notch, edge, Style.Ink);
        o.Line(notch, rr, edge, Style.Ink);
        o.Line(rr, tip, edge, Style.Ink);
        o.Clip(Vector2.Zero);
    }

    /// <summary><paramref name="a"/> blended over <paramref name="b"/> with weight <paramref name="t"/>, opaque.</summary>
    private static uint Mix(uint a, uint b, float t)
    {
        float C(uint x, int sh) => (x >> sh & 0xFF) / 255f;
        return Overlay.Rgba(C(a, 0) * t + C(b, 0) * (1 - t), C(a, 8) * t + C(b, 8) * (1 - t), C(a, 16) * t + C(b, 16) * (1 - t));
    }

    /// <summary>Bar across the road at <paramref name="at"/> (screen), road direction <paramref name="dir"/> (world XZ): 0 sector, 1 green start, 2 checkered goal.</summary>
    private static void Marker(Overlay o, Vector2 at, Vector2 dir, Vector2 f, Vector2 right, float s, int kind, float scale)
    {
        s *= scale;
        var d = Vector2.Normalize(new Vector2(Vector2.Dot(dir, right), -Vector2.Dot(dir, f)));
        var across = new Vector2(-d.Y, d.X) * 8 * s;
        if (kind == 0)
        {
            o.Line(at - across * 0.75f, at + across * 0.75f, 2 * s, Style.Dim);
            return;
        }
        o.Line(at - across, at + across, 6 * s, RoadEdge);
        if (kind == 1)
        {
            o.Line(at - across, at + across, 3.5f * s, StartColor);
            return;
        }
        // 4 × 2 checkers
        for (var i = 0; i < 4; i++)
        for (var j = 0; j < 2; j++)
        {
            var a = at - across + across * (i / 2f);
            var b = at - across + across * ((i + 1) / 2f);
            var off = d * ((j - 0.5f) * 2 * s);
            o.Line(a + off, b + off, 2 * s, (i + j) % 2 == 0 ? Road : RoadEdge);
        }
    }
}
