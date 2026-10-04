using System.Numerics;
using Kansei.Graphics;

namespace Touge;

/// <summary>
///     HUD (F4): minimap top-right and speed/gear bottom-right, built into an <see cref="Overlay"/> every frame.
///     Minimap: the CRS_ROAD centre line as a road band with outline, start (green) and goal (red) bars across the road
///     at the driving line's first/last valid point, the car as an arrow; N cycles rotating (heading up, default, like
///     the arcade) → north up → whole course. CRS_NAVI is exactly this centre line scaled into the game's map box
///     (FORMATS.md), so ROAD in metres is used directly. Under the map: progress along the driving line in %.
///     Sizes in units of 1/900 of the target height (DPI/resolution independent).
/// </summary>
public sealed class Hud
{
    public enum MapMode { Rotating, NorthUp, Overview }

    private const float MapRadius = 120, ZoomMetres = 220; // panel radius (units), world radius shown when zoomed
    private static readonly uint Panel = Overlay.Rgba(0.02f, 0.03f, 0.05f, 0.5f), Frame = Overlay.Rgba(1, 1, 1, 0.55f),
        RoadEdge = Overlay.Rgba(0.05f, 0.06f, 0.08f, 0.95f), Road = Overlay.Rgba(0.93f, 0.94f, 0.96f),
        StartColor = Overlay.Rgba(0.2f, 0.95f, 0.35f), GoalColor = Overlay.Rgba(1, 0.2f, 0.2f),
        CarColor = Overlay.Rgba(1, 0.72f, 0.1f), Text = Overlay.Rgba(1, 1, 1, 0.95f), Track = Overlay.Rgba(1, 1, 1, 0.22f), Dim = Overlay.Rgba(1, 1, 1, 0.6f);

    private readonly Overlay _overlay = new();
    private readonly Vector2[] _road;
    private readonly Vector2 _start, _startDir, _goal, _goalDir, _centre;
    private readonly float _extent;

    public bool Visible = true;
    public MapMode Mode = MapMode.Rotating;

    public Hud(Vector3[] road, Vector3[] line)
    {
        _road = Array.ConvertAll(road, Xz);
        (_start, _startDir) = (Xz(line[0]), Vector2.Normalize(Xz(line[1] - line[0])));
        (_goal, _goalDir) = (Xz(line[^1]), Vector2.Normalize(Xz(line[^1] - line[^2])));
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var p in _road) (min, max) = (Vector2.Min(min, p), Vector2.Max(max, p));
        _centre = (min + max) / 2;
        foreach (var p in _road) _extent = MathF.Max(_extent, Vector2.Distance(p, _centre));
    }

    private static Vector2 Xz(Vector3 v) => new(v.X, v.Z);

    public void NextMode() => Mode = (MapMode)(((int)Mode + 1) % 3);

    /// <summary>
    ///     Builds the HUD for a <paramref name="width"/>×<paramref name="height"/> target: car at <paramref name="carPos"/>
    ///     heading <paramref name="carForward"/>, <paramref name="progress"/> 0..1 along the driving line.
    /// </summary>
    public Overlay Build(int width, int height, Vector3 carPos, Vector3 carForward, float kmh, int gear, bool automatic, float progress)
    {
        var o = _overlay;
        o.Clear();
        var u = height / 900f;
        Map(o, new Vector2(width - (MapRadius + 22) * u, (MapRadius + 22) * u), u, Xz(carPos), Xz(carForward), progress);
        Speedo(o, new Vector2(width - 20 * u, height - 20 * u), u, kmh, gear, automatic);
        return o;
    }

    private void Map(Overlay o, Vector2 c, float u, Vector2 car, Vector2 heading, float progress)
    {
        var r = MapRadius * u;
        heading = heading.LengthSquared() > 1e-6f ? Vector2.Normalize(heading) : new Vector2(0, -1);
        // screen up = f, screen right = (-f.y, f.x) on XZ (top-down, not mirrored: X right, −Z up when north up)
        var f = Mode == MapMode.Rotating ? heading : new Vector2(0, -1);
        var right = new Vector2(-f.Y, f.X);
        var overview = Mode == MapMode.Overview;
        var k = overview ? r * 0.9f / _extent : r / ZoomMetres; // pixels per metre
        // zoomed: the view centre sits ahead of the car so more of the road ahead is visible
        var origin = overview ? _centre : car + heading * (ZoomMetres * 0.3f);
        Vector2 ToScreen(Vector2 p)
        {
            var d = p - origin;
            return c + new Vector2(Vector2.Dot(d, right), -Vector2.Dot(d, f)) * k;
        }

        o.Disc(c, r, Panel);
        o.Clip(c, r - 1.5f * u);
        var stride = overview ? 4 : 2; // ROAD points are ~2 m apart
        var reach = ZoomMetres * 1.1f + 2 * stride;
        for (var pass = 0; pass < 2; pass++)
        {
            var col = pass == 0 ? RoadEdge : Road;
            var w = (pass == 0 ? 8 : 4.5f) * (overview ? 0.6f : 1) * u;
            for (var i = stride; i < _road.Length; i += stride)
            {
                var (a, b) = (_road[i - stride], _road[Math.Min(i, _road.Length - 1)]);
                if (!overview && Vector2.Distance(a, origin) > reach) continue;
                o.Line(ToScreen(a), ToScreen(b), w, col);
            }
        }
        Marker(o, ToScreen(_start), _startDir, f, right, u, StartColor);
        Marker(o, ToScreen(_goal), _goalDir, f, right, u, GoalColor);

        // car arrow
        var p = ToScreen(car);
        var dir = Vector2.Normalize(new Vector2(Vector2.Dot(heading, right), -Vector2.Dot(heading, f)));
        var n = new Vector2(-dir.Y, dir.X);
        Vector2 tip = p + dir * 11 * u, l = p - dir * 7 * u + n * 7.5f * u, rr = p - dir * 7 * u - n * 7.5f * u, notch = p - dir * 3 * u;
        o.Triangle(tip, l, notch, CarColor);
        o.Triangle(tip, notch, rr, CarColor);
        var edge = 2 * u;
        o.Line(tip, l, edge, RoadEdge);
        o.Line(l, notch, edge, RoadEdge);
        o.Line(notch, rr, edge, RoadEdge);
        o.Line(rr, tip, edge, RoadEdge);

        o.Clip(Vector2.Zero);
        o.Ring(c, r, 2 * u, Frame);
        if (Mode == MapMode.Rotating)
        {
            // north marker on the rim
            var north = new Vector2(-right.Y, f.Y); // screen direction of world −Z: (dot(−Z, right), −dot(−Z, f))
            var at = c + north * (r - 12 * u);
            o.Disc(at, 10 * u, RoadEdge);
            o.Text("N", at - new Vector2(2.5f, 3.5f) * 2 * u, 2 * u, Text);
        }

        // progress bar + percent
        progress = Math.Clamp(progress, 0, 1);
        var top = c.Y + r + 10 * u;
        Vector2 min = new(MathF.Round(c.X - r), MathF.Round(top)), max = new(MathF.Round(c.X + r), MathF.Round(top + 6 * u));
        o.Rect(min - new Vector2(u), max + new Vector2(u), Panel);
        o.Rect(min, max, Track);
        o.Rect(min, new Vector2(MathF.Round(min.X + (max.X - min.X) * progress), max.Y), CarColor);
        Span<char> s = stackalloc char[8];
        ((int)(progress * 100)).TryFormat(s, out var len);
        s[len++] = '%';
        var dot = 2.6f * u;
        o.Text(s[..len], new Vector2(c.X - Overlay.TextWidth(len, dot) / 2, max.Y + 6 * u), dot, Text);
    }

    /// <summary>Bar across the road at <paramref name="at"/> (screen), road direction <paramref name="dir"/> (world XZ).</summary>
    private static void Marker(Overlay o, Vector2 at, Vector2 dir, Vector2 f, Vector2 right, float u, uint color)
    {
        var d = new Vector2(Vector2.Dot(dir, right), -Vector2.Dot(dir, f));
        var across = new Vector2(-d.Y, d.X) * 9 * u;
        o.Line(at - across, at + across, 6 * u, RoadEdge);
        o.Line(at - across, at + across, 3.5f * u, color);
    }

    /// <summary>Panel with the gear (big digit, A/M under it) and km/h, bottom-right corner at <paramref name="corner"/>.</summary>
    private static void Speedo(Overlay o, Vector2 corner, float u, float kmh, int gear, bool automatic)
    {
        Vector2 max = new(MathF.Round(corner.X), MathF.Round(corner.Y)), min = new(MathF.Round(max.X - 220 * u), MathF.Round(max.Y - 82 * u));
        o.Rect(min, max, Panel);
        var big = 6 * u;
        Span<char> s = stackalloc char[8];
        s[0] = gear < 0 ? 'R' : gear == 0 ? 'N' : (char)('0' + Math.Min(gear, 9));
        o.Text(s[..1], min + new Vector2(14, 12) * u, big, Text);
        o.Text(automatic ? "A" : "M", min + new Vector2(14, 62) * u, 2 * u, Dim);
        o.Line(min + new Vector2(64, 14) * u, min + new Vector2(64, 68) * u, 1.5f * u, Dim);
        ((int)MathF.Round(kmh)).TryFormat(s, out var len);
        o.Text(s[..len], new Vector2(max.X - 14 * u - Overlay.TextWidth(len, big), min.Y + 12 * u), big, Text);
        o.Text("KM/H", new Vector2(max.X - 14 * u - Overlay.TextWidth(4, 2 * u), min.Y + 62 * u), 2 * u, Dim);
    }
}
