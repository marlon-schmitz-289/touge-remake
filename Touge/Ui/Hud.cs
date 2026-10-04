using System.Numerics;
using Kansei.Graphics;
using Kansei.Physics;

namespace Touge.Ui;

/// <summary>
///     Driving HUD (F4), built into an <see cref="Overlay"/> every frame; sizes in units of 1/900 of the target height
///     inside a safe margin, so it scales with resolution and DPI.
///     Top left: run time, best, four sector chips (green/red against the best run) and the split delta (<see cref="LapTimer"/>).
///     Top centre: drift combo with slip-angle bar (<see cref="DriftMeter"/>). Top right: round minimap from the CRS_ROAD
///     centre line (road band with outline, start bar, checkered goal, car arrow with glow; N cycles rotating → north up →
///     whole course; CRS_NAVI is this same line scaled, FORMATS.md) and progress with sector ticks. Bottom right:
///     tachometer (segmented arc, redline, needle, shift light at the limiter), speed, gear with A/M.
///     Centre: wrong-way banner, finish banner, reset hint when stuck. State advances per physics tick (<see cref="Tick"/>).
/// </summary>
public sealed class Hud
{
    public enum MapMode { Rotating, NorthUp, Overview }

    private const float Margin = 28, MapRadius = 112, ZoomMetres = 220, TachRadius = 118;
    private static readonly uint RoadEdge = Overlay.Rgba(0.05f, 0.06f, 0.08f, 0.95f), Road = Overlay.Rgba(0.93f, 0.94f, 0.96f),
        StartColor = Overlay.Rgba(0.2f, 0.95f, 0.35f), Frame = Overlay.Rgba(1, 1, 1, 0.5f);

    private readonly Vector2[] _road;
    private readonly Vector3[] _line;
    private readonly LinePilot _pilot;
    private readonly Vector2 _start, _startDir, _goal, _goalDir, _centre;
    private readonly float _extent;
    private float _progress, _wrongFor, _stuckFor, _wrongA, _driftA, _hintA;

    public bool Visible = true;
    public MapMode Mode = MapMode.Rotating;
    public LapTimer Timer { get; }
    public DriftMeter Drift { get; } = new();

    public Hud(Vector3[] road, Vector3[] line, LinePilot pilot, float[]? best)
    {
        (_line, _pilot) = (line, pilot);
        Timer = new LapTimer(pilot.Length, best);
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
    ///     Per physics tick: position along the line, timing, drift combo, wrong way (driving against the line for 1 s),
    ///     stuck (standing while timed, far off the line or on the roof) and the fade of each banner.
    /// </summary>
    public void Tick(Vehicle car, float dt)
    {
        var (along, lateral) = _pilot.Track(car.Position);
        _progress = Math.Clamp(along / _pilot.Length, 0, 1);
        Timer.Update(along, dt);
        var kmh = car.SpeedKmh;
        Drift.Update(car.SlipAngle, kmh, car.WallContacts > 0, dt);
        var seg = Math.Min(_pilot.Segment, _line.Length - 2);
        var tangent = Xz(_line[seg + 1] - _line[seg]);
        var v = Xz(car.Velocity);
        var against = kmh > 15 && Vector2.Dot(v, tangent) < -0.3f * v.Length() * tangent.Length();
        _wrongFor = against ? _wrongFor + dt : 0;
        var upright = Vector3.Transform(Vector3.UnitY, car.Orientation).Y > 0.3f;
        var stuck = (kmh < 3 && Timer.Phase == LapTimer.State.Running) || MathF.Abs(lateral) > 14 || !upright;
        _stuckFor = stuck ? _stuckFor + dt : 0;
        _wrongA = Style.Approach(_wrongA, _wrongFor > 1 ? 1 : 0, 4, dt);
        _driftA = Style.Approach(_driftA, Drift.Drifting || Drift.Score > 0 || Drift.Last.Age < 1.5f ? 1 : 0, 5, dt);
        _hintA = Style.Approach(_hintA, _stuckFor > 2.5f || _wrongFor > 3 ? 1 : 0, 3, dt);
    }

    /// <summary>
    ///     HUD for a <paramref name="width"/>×<paramref name="height"/> target into <paramref name="o"/> (cleared first): car drawn
    ///     at <paramref name="carPos"/> heading <paramref name="carForward"/> (interpolated pose), <paramref name="time"/> s for pulses.
    /// </summary>
    public void Build(Overlay o, int width, int height, Vector3 carPos, Vector3 carForward, Vehicle car, float time)
    {
        o.Clear();
        var u = height / 900f;
        var m = Margin * u;
        Map(o, new Vector2(width - m - MapRadius * u, m + MapRadius * u), u, Xz(carPos), Xz(carForward));
        Tach(o, new Vector2(width - m - (TachRadius + 10) * u, height - m - (TachRadius + 10) * u), u, car, time);
        Timing(o, new Vector2(m, m), u, time);
        DriftPanel(o, new Vector2(width / 2f, m), u);
        Banners(o, width, height, u, time);
    }

    private void Timing(Overlay o, Vector2 at, float u, float time)
    {
        var t = Timer;
        Style.Slanted(o, at, at + new Vector2(340, 150) * u, Style.Panel, 0.22f);
        o.Rect(Vector2.Round(at), Vector2.Round(at + new Vector2(5, 150) * u), Style.Amber);
        var x = at.X + 22 * u;
        Style.Label(o, "TIME", new Vector2(x, at.Y + 28 * u), 16 * u, Style.Amber, 0, 0, 0.3f * u);
        Style.Label(o, $"SECTOR {Math.Min(t.Sector + 1, LapTimer.Sectors)}/{LapTimer.Sectors}", new Vector2(at.X + 290 * u, at.Y + 28 * u), 15 * u, Style.Dim, 1);
        var finished = t.Phase == LapTimer.State.Finished;
        var col = t.Phase == LapTimer.State.Ready ? Style.Dim : finished && MathF.Sin(time * 8) > 0 ? Style.Amber : Style.Text;
        Style.Label(o, Style.Time(t.Time), new Vector2(x - 2 * u, at.Y + 80 * u), 54 * u, col, 0, Style.Slant, 0.4f * u);
        var w = Style.Label(o, "BEST ", new Vector2(x, at.Y + 108 * u), 16 * u, Style.Dim);
        Style.Label(o, Style.Time(t.Best?[^1]), new Vector2(x + w, at.Y + 108 * u), 20 * u, Style.Text);
        // split delta pop-up, fades after 3 s
        if (t.SinceSplit < 3 && t.Sector > 0 && t.Delta(t.Sector - 1) is { } d)
            Style.Label(o, Style.Delta(d), new Vector2(at.X + 290 * u, at.Y + 108 * u), 24 * u,
                Style.Fade(d <= 0 ? Style.Green : Style.Red, Style.Ease((3 - t.SinceSplit) * 2)), 1, Style.Slant, 0.3f * u);
        for (var i = 0; i < LapTimer.Sectors; i++)
        {
            Vector2 min = Vector2.Round(new Vector2(x + i * 68 * u, at.Y + 120 * u)), max = Vector2.Round(min + new Vector2(62, 20) * u);
            var done = i < t.Sector;
            var delta = t.Delta(i);
            var fill = done ? delta is { } dd ? Style.Fade(dd <= 0 ? Style.Green : Style.Red, 0.85f) : Overlay.Rgba(1, 1, 1, 0.75f) : Style.Faint;
            Style.Slanted(o, min, max, fill, 0.3f);
            if (i == t.Sector && t.Phase == LapTimer.State.Running) o.Rect(new Vector2(min.X, max.Y - 3 * u), new Vector2(max.X - 6 * u, max.Y), Style.Amber);
            var label = done ? delta is { } d2 ? Style.Delta(d2)[..^1] : Style.Time(t.Splits[i])[2..^2] : $"S{i + 1}";
            o.Text(label, new Vector2((min.X + max.X) / 2 - 2 * u, max.Y - 5 * u), 14 * u, done ? Style.Ink : Style.Dim, 0.5f, 0.2f * u);
        }
        if (Drift.Total > 0)
        {
            var dw = Style.Label(o, "DRIFT ", new Vector2(x, at.Y + 176 * u), 16 * u, Style.Dim);
            Style.Label(o, Points(Drift.Total), new Vector2(x + dw, at.Y + 176 * u), 20 * u, Style.Amber, 0, Style.Slant);
        }
    }

    private static string Points(float p) => ((int)p).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', ' ');

    private void DriftPanel(Overlay o, Vector2 top, float u)
    {
        var a = Style.Ease(_driftA);
        if (a <= 0) return;
        var d = Drift;
        Style.Slanted(o, top + new Vector2(-190, 0) * u, top + new Vector2(190, 100) * u, Style.Fade(Style.Panel, a), 0.18f);
        Style.Label(o, "DRIFT", new Vector2(top.X, top.Y + 22 * u), 15 * u, Style.Fade(Style.Dim, a), 0.5f, 0, 0.3f * u);
        var shown = d.Score > 0 || d.Drifting ? d.Score : d.Last.Points;
        var w = o.Font!.Measure(Points(shown), 44 * u);
        Style.Label(o, Points(shown), new Vector2(top.X, top.Y + 64 * u), 44 * u, Style.Fade(Style.Amber, a), 0.5f, Style.Slant, 0.4f * u);
        if (d.Multiplier > 1 && d.Drifting)
            Style.Label(o, $"x{d.Multiplier}", new Vector2(top.X + w / 2 + 10 * u, top.Y + 64 * u), 24 * u, Style.Fade(Style.Text, a), 0, Style.Slant);
        // slip-angle bar from the centre, red beyond 30°
        var half = 130 * u;
        var y = top.Y + 78 * u;
        o.Rect(Vector2.Round(new Vector2(top.X - half, y)), Vector2.Round(new Vector2(top.X + half, y + 7 * u)), Style.Fade(Style.Faint, a));
        var k = Math.Clamp(d.Angle / 45, -1, 1);
        var fillCol = Style.Fade(MathF.Abs(d.Angle) > 30 ? Style.Red : Style.Amber, a);
        o.Rect(Vector2.Round(new Vector2(top.X + MathF.Min(0, k) * half, y)), Vector2.Round(new Vector2(top.X + MathF.Max(0, k) * half, y + 7 * u)), fillCol);
        o.Rect(Vector2.Round(new Vector2(top.X - u, y - 3 * u)), Vector2.Round(new Vector2(top.X + u, y + 10 * u)), Style.Fade(Style.Text, a));
        Style.Label(o, $"{MathF.Abs(d.Angle):0}°", new Vector2(top.X + half + 8 * u, y + 9 * u), 16 * u, Style.Fade(Style.Text, a));
        // last combo: banked points rise and fade, a wall hit says so
        if (d.Last.Age < 1.5f)
        {
            var la = a * Style.Ease((1.5f - d.Last.Age) * 2);
            var ly = top.Y + 126 * u - d.Last.Age * 16 * u;
            Style.Label(o, d.Last.Points > 0 ? "+" + Points(d.Last.Points) : "WALL - COMBO LOST", new Vector2(top.X, ly), 22 * u,
                Style.Fade(d.Last.Points > 0 ? Style.Green : Style.Red, la), 0.5f, Style.Slant, 0.3f * u);
        }
    }

    private void Banners(Overlay o, int width, int height, float u, float time)
    {
        var cx = width / 2f;
        if (_wrongA > 0)
        {
            var a = Style.Ease(_wrongA) * (0.8f + 0.2f * MathF.Sin(time * 9));
            var y = height * 0.34f;
            Style.Slanted(o, new Vector2(cx - 210 * u, y - 44 * u), new Vector2(cx + 210 * u, y + 30 * u), Style.Fade(Overlay.Rgba(0.75f, 0.06f, 0.05f, 0.85f), a), 0.3f);
            Style.Label(o, "WRONG WAY", new Vector2(cx - 8 * u, y + 12 * u), 52 * u, Style.Fade(Style.Text, a), 0.5f, Style.Slant, 0.5f * u);
        }
        if (Timer.Phase == LapTimer.State.Finished && Timer.SinceSplit < 6)
        {
            var a = Style.Ease(Timer.SinceSplit * 3) * Style.Ease((6 - Timer.SinceSplit) * 2);
            var y = height * 0.3f;
            Style.Label(o, "FINISH", new Vector2(cx, y), 72 * u, Style.Fade(Style.Amber, a), 0.5f, Style.Slant, 0.6f * u);
            Style.Label(o, Style.Time(Timer.Time), new Vector2(cx, y + 48 * u), 36 * u, Style.Fade(Style.Text, a), 0.5f, Style.Slant);
            if (Timer.NewRecord) Style.Label(o, "NEW RECORD", new Vector2(cx, y + 82 * u), 24 * u, Style.Fade(Style.Green, a), 0.5f, Style.Slant, 0.3f * u);
        }
        if (_hintA > 0)
        {
            var y = height - Margin * u - 22 * u;
            var a = Style.Ease(_hintA);
            Style.Slanted(o, new Vector2(cx - 150 * u, y - 22 * u), new Vector2(cx + 150 * u, y + 22 * u), Style.Fade(Style.Panel, a), 0.2f);
            Style.KeyHint(o, "R", "RESET TO ROAD", new Vector2(cx - 112 * u, y), u, a);
        }
    }

    private void Tach(Overlay o, Vector2 c, float u, Vehicle car, float time)
    {
        var spec = car.Spec;
        var r = TachRadius * u;
        var top = MathF.Ceiling((spec.RevLimit + 400) / 1000) * 1000;
        var red = spec.RevLimit - 600;
        const float a0 = 0.75f * MathF.PI, sweep = 1.5f * MathF.PI;
        float Angle(float rpm) => a0 + sweep * Math.Clamp(rpm / top, 0, 1);
        Vector2 Dir(float a) => new(MathF.Cos(a), MathF.Sin(a));
        var shift = car.Rpm > spec.RevLimit - 250 && car.Gear > 0 && MathF.Sin(time * MathF.Tau * 9) > 0;

        o.Disc(c, r + 10 * u, Style.Panel);
        o.Ring(c, r + 10 * u, (shift ? 3 : 1.5f) * u, shift ? Style.Red : Frame, 96);
        // segments every 250 rpm, lit up to the current rpm
        var segR = r - 8 * u;
        for (var rpm = 0f; rpm < top; rpm += 250)
        {
            var lit = rpm + 125 < car.Rpm;
            var col = rpm >= red ? lit ? Style.Red : Overlay.Rgba(1, 0.24f, 0.2f, 0.35f) : lit ? rpm > red - 1500 ? Style.Amber : Style.Text : Style.Faint;
            o.Arc(c, segR, 12 * u, col, Angle(rpm) + 0.012f, Angle(rpm + 250) - 0.012f, 2);
        }
        // major ticks and numbers (×1000 rpm)
        for (var k = 0; k <= top / 1000; k++)
        {
            var d = Dir(Angle(k * 1000));
            o.Line(c + d * (r - 18 * u), c + d * (r - 26 * u), 2 * u, k * 1000 >= red ? Style.Red : Style.Dim);
            var p = c + d * (r - 40 * u);
            var size = 15 * u;
            o.Text(k.ToString(), new Vector2(p.X, p.Y + o.Font!.CapHeight * size / 2), size, k * 1000 >= red ? Style.Red : Style.Dim, 0.5f, 0.2f * u);
        }
        // needle with glow
        var nd = Dir(Angle(car.Rpm));
        o.Line(c + nd * (r * 0.66f), c + nd * (r - 2 * u), 9 * u, Overlay.Rgba(1, 0.3f, 0.15f, 0.25f));
        o.Line(c + nd * (r * 0.66f), c + nd * (r - 2 * u), 3.5f * u, Overlay.Rgba(1, 0.35f, 0.2f));
        // speed
        var kmh = ((int)MathF.Round(car.SpeedKmh)).ToString();
        Style.Label(o, kmh, new Vector2(c.X, c.Y + 18 * u), 66 * u, Style.Text, 0.5f, Style.Slant, 0.5f * u);
        Style.Label(o, "km/h", new Vector2(c.X, c.Y + 40 * u), 15 * u, Style.Dim, 0.5f);
        // gear box in the arc's gap
        var gear = car.Gear < 0 ? "R" : car.Gear == 0 ? "N" : car.Gear.ToString();
        Vector2 gc = new(c.X, c.Y + r * 0.74f), half = new Vector2(26, 22) * u;
        o.Rect(Vector2.Round(gc - half), Vector2.Round(gc + half), shift ? Style.Red : Overlay.Rgba(1, 1, 1, 0.92f));
        o.Text(gear, new Vector2(gc.X, gc.Y + 0.7f * 36 * u / 2), 36 * u, Style.Ink, 0.5f, 0.6f * u, 0, Style.Slant);
        Style.Label(o, car.AutomaticGearbox ? "AT" : "MT", new Vector2(gc.X + half.X + 6 * u, gc.Y + 6 * u), 15 * u, car.AutomaticGearbox ? Style.Dim : Style.Amber);
    }

    private void Map(Overlay o, Vector2 c, float u, Vector2 car, Vector2 heading)
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

        o.Disc(c, r, Style.Panel);
        o.Clip(c, r - 1.5f * u);
        var stride = overview ? 4 : 2; // ROAD points are ~2 m apart
        var reach = ZoomMetres * 1.1f + 2 * stride;
        for (var pass = 0; pass < 2; pass++)
        {
            var col = pass == 0 ? RoadEdge : Road;
            var w = (pass == 0 ? 9 : 5f) * (overview ? 0.6f : 1) * u;
            for (var i = stride; i < _road.Length; i += stride)
            {
                var (a, b) = (_road[i - stride], _road[Math.Min(i, _road.Length - 1)]);
                if (!overview && Vector2.Distance(a, origin) > reach) continue;
                o.Line(ToScreen(a), ToScreen(b), w, col);
            }
        }
        Marker(o, ToScreen(_start), _startDir, f, right, u, false);
        Marker(o, ToScreen(_goal), _goalDir, f, right, u, true);

        // car arrow with a soft glow
        var p = ToScreen(car);
        for (var g = 3; g >= 1; g--) o.Disc(p, (6 + 5 * g) * u, Overlay.Rgba(1, 0.72f, 0.1f, 0.09f));
        var dir = Vector2.Normalize(new Vector2(Vector2.Dot(heading, right), -Vector2.Dot(heading, f)));
        var n = new Vector2(-dir.Y, dir.X);
        Vector2 tip = p + dir * 11 * u, l = p - dir * 7 * u + n * 7.5f * u, rr = p - dir * 7 * u - n * 7.5f * u, notch = p - dir * 3 * u;
        o.Triangle(tip, l, notch, Style.Amber);
        o.Triangle(tip, notch, rr, Style.Amber);
        var edge = 2 * u;
        o.Line(tip, l, edge, RoadEdge);
        o.Line(l, notch, edge, RoadEdge);
        o.Line(notch, rr, edge, RoadEdge);
        o.Line(rr, tip, edge, RoadEdge);

        o.Clip(Vector2.Zero);
        o.Ring(c, r, 2 * u, Frame, 96);
        if (Mode == MapMode.Rotating)
        {
            // north marker on the rim
            var north = new Vector2(-right.Y, f.Y); // screen direction of world −Z: (dot(−Z, right), −dot(−Z, f))
            var at = c + north * r;
            o.Disc(at, 11 * u, RoadEdge);
            o.Text("N", new Vector2(at.X, at.Y + o.Font!.CapHeight * 15 * u / 2), 15 * u, Style.Text, 0.5f, 0.3f * u);
        }

        // progress bar with sector ticks + percent
        var y = c.Y + r + 14 * u;
        Vector2 min = Vector2.Round(new Vector2(c.X - r, y)), max = Vector2.Round(new Vector2(c.X + r, y + 6 * u));
        o.Rect(min - new Vector2(u), max + new Vector2(u), Style.Panel);
        o.Rect(min, max, Style.Faint);
        o.Rect(min, new Vector2(MathF.Round(min.X + (max.X - min.X) * _progress), max.Y), Style.Amber);
        for (var i = 1; i < LapTimer.Sectors; i++)
        {
            var x = MathF.Round(min.X + (max.X - min.X) * i / LapTimer.Sectors);
            o.Rect(new Vector2(x - u, min.Y - 3 * u), new Vector2(x + u, max.Y + 3 * u), Style.Text);
        }
        Style.Label(o, $"{(int)(_progress * 100)}%", new Vector2(c.X, max.Y + 24 * u), 18 * u, Style.Text, 0.5f, 0, 0.2f * u);
    }

    /// <summary>Bar across the road at <paramref name="at"/> (screen), road direction <paramref name="dir"/> (world XZ): green start, checkered goal.</summary>
    private static void Marker(Overlay o, Vector2 at, Vector2 dir, Vector2 f, Vector2 right, float u, bool goal)
    {
        var d = new Vector2(Vector2.Dot(dir, right), -Vector2.Dot(dir, f));
        var across = new Vector2(-d.Y, d.X) * 10 * u;
        o.Line(at - across, at + across, 7 * u, RoadEdge);
        if (!goal)
        {
            o.Line(at - across, at + across, 4 * u, StartColor);
            return;
        }
        // 4 × 2 checkers
        for (var i = 0; i < 4; i++)
        for (var j = 0; j < 2; j++)
        {
            var a = at - across + across * (i / 2f);
            var b = at - across + across * ((i + 1) / 2f);
            var off = d * ((j - 0.5f) * 2.2f * u);
            var white = (i + j) % 2 == 0;
            o.Line(a + off, b + off, 2.2f * u, white ? Road : RoadEdge);
        }
    }
}
