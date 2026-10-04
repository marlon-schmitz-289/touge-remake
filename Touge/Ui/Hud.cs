using System.Numerics;
using Kansei.Graphics;
using Kansei.Physics;

namespace Touge.Ui;

/// <summary>
///     Driving HUD (F4), built into an <see cref="Overlay"/> every frame; sizes in units of 1/900 of the target height
///     inside a safe margin, so it scales with resolution and DPI.
///     Top left: run time, best, four sector chips (green/red against the best run) and the split delta (<see cref="LapTimer"/>).
///     Top centre: drift combo with slip-angle bar (<see cref="DriftMeter"/>). Bottom left: course dial with minimap and
///     progress ring (<see cref="MapWidget"/>; N cycles rotating → north up → whole course; CRS_NAVI is the CRS_ROAD line
///     scaled, FORMATS.md). Bottom right: the car's own instrument cluster (<see cref="Cluster"/>). Centre: wrong-way banner, finish banner with sector deltas,
///     reset hint when stuck. Everything sits in <see cref="Style.Safe"/>. State advances per physics tick (<see cref="Tick"/>).
/// </summary>
public sealed class Hud
{
    public enum MapMode { Rotating, NorthUp, Overview }

    private const float TimingW = 340, DriftHalf = 170;

    private readonly Vector3[] _line;
    private readonly LinePilot _pilot;
    private readonly float _start;
    private readonly MapWidget _map;
    private float _progress, _wrongFor, _stuckFor, _wrongA, _driftA, _hintA, _boost = -0.6f;

    public bool Visible = true;
    /// <summary>Night course: cluster illumination on.</summary>
    public bool Night;
    /// <summary>Light switch for the cluster's tell-tales.</summary>
    public Headlights.Mode Lights;
    public MapMode Mode = MapMode.Rotating;
    /// <summary>Speed in mph instead of km/h (Options: UNITS).</summary>
    public bool Mph;
    /// <summary>Options HUD SIZE (0.8..1.3): scales every HUD element.</summary>
    public float Scale = 1;

    /// <summary>Dash row (course dial + cluster) at 100 %, relative to <see cref="Cluster.Box"/> (400×190 px at 1080p → 640×304).</summary>
    public const float DashSize = 1.6f;

    /// <summary>
    ///     Factor on <see cref="Cluster.Box"/> for the dash row of <paramref name="g"/> at HUD size <paramref name="scale"/>; on narrow
    ///     screens (4:3, 5:4) capped so the cluster ends <see cref="CarClear"/> right of the centre, clear of the chase-cam car.
    /// </summary>
    public static float Dash(Style.Grid g, float scale) => MathF.Min(DashSize * Math.Clamp(scale, 0.8f, 1.3f), (g.Units / 2 - CarClear) / Cluster.Box.X);

    /// <summary>Half width (units) kept free around the screen centre for the car.</summary>
    public const float CarClear = 180;
    public LapTimer Timer { get; }
    public DriftMeter Drift { get; } = new();

    /// <param name="start">Start line, m along <paramref name="line"/> (the car spawns behind it); timing and progress run from there.</param>
    public Hud(Vector3[] road, Vector3[] line, LinePilot pilot, float[]? best, float start = 0)
    {
        (_line, _pilot, _start) = (line, pilot, start);
        Timer = new LapTimer(pilot.Length - start, best);
        _map = new MapWidget(road, line);
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
        _progress = Math.Clamp((along - _start) / (_pilot.Length - LapTimer.Gate - _start), 0, 1);
        Timer.Update(along - _start, dt);
        var kmh = car.SpeedKmh;
        Drift.Update(car.SlipAngle, kmh, car.WallContacts > 0, dt);
        _map.Tick(kmh, dt);
        var seg = Math.Min(_pilot.Segment, _line.Length - 2);
        var tangent = Xz(_line[seg + 1] - _line[seg]);
        var v = Xz(car.Velocity);
        var against = kmh > 15 && Vector2.Dot(v, tangent) < -0.3f * v.Length() * tangent.Length();
        _wrongFor = against ? _wrongFor + dt : 0;
        var upright = Vector3.Transform(Vector3.UnitY, car.Orientation).Y > 0.3f;
        // past the goal the line ends (run-out up to the end barrier): far from it is not off the road there
        var stuck = (kmh < 3 && Timer.Phase == LapTimer.State.Running) || (MathF.Abs(lateral) > 14 && along < _pilot.Length - 1) || !upright;
        _stuckFor = stuck ? _stuckFor + dt : 0;
        _wrongA = Style.Approach(_wrongA, _wrongFor > 1 ? 1 : 0, 4, dt);
        _driftA = Style.Approach(_driftA, Drift.Drifting || Drift.Score > 0 || Drift.Last.Age < 1.5f ? 1 : 0, 5, dt);
        _hintA = Style.Approach(_hintA, _stuckFor > 2.5f || _wrongFor > 3 ? 1 : 0, 3, dt);
        // boost gauge: vacuum off throttle, spools up with rpm (no turbo model in the physics, display only)
        var spool = Math.Clamp((car.Rpm - 2500) / 2500, 0, 1);
        var boost = car.Throttle > 0.2f ? -0.3f + 1.1f * spool * car.Throttle : -0.6f;
        _boost = Style.Approach(_boost, boost, boost > _boost ? 1.2f : 3, dt);
    }

    /// <summary>
    ///     HUD for a <paramref name="width"/>×<paramref name="height"/> target into <paramref name="o"/> (cleared first): car drawn
    ///     at <paramref name="carPos"/> heading <paramref name="carForward"/> (interpolated pose), cluster of <paramref name="carName"/>
    ///     (<see cref="Cluster.Cars"/>), <paramref name="time"/> s for pulses.
    /// </summary>
    public void Build(Overlay o, int width, int height, Vector3 carPos, Vector3 carForward, Vehicle car, string carName, float time)
    {
        o.Clear();
        var g = Style.Safe(width, height);
        var u = g.U * Math.Clamp(Scale, 0.8f, 1.3f);
        var k = Dash(g, Scale);
        // course dial bottom left, same height as the cluster box (Cluster.Box.Y), so the two read as one dash row
        var s = g.U * k * Cluster.Box.Y / (2 * MapWidget.Radius);
        _map.Draw(o, new Vector2(g.Left + MapWidget.Radius * s, g.Bottom - MapWidget.Radius * s), s, Mode, Xz(carPos), Xz(carForward),
            _progress, (_pilot.Length - LapTimer.Gate - _start) * (1 - _progress), Timer, Cluster.Cars[carName], Night);
        var gauge = Cluster.Cars[carName];
        Cluster.Draw(o, gauge, new Vector2(g.Right, g.Bottom), k * Cluster.Fit(gauge, g),
            new Cluster.Reading(car.Rpm, car.SpeedKmh, car.Gear, car.AutomaticGearbox, _boost, Night, time, Lights, Mph));
        var timingH = Drift.Total > 0 ? 186 : 150;
        Timing(o, new Vector2(g.Left, g.Top), timingH, u, time);
        // drift combo top centre; when it would crowd the timing panel (4:3, 5:4) it moves below the top row
        var cx = width / 2f;
        var fits = cx - DriftHalf * u > g.Left + (TimingW + 24) * u;
        DriftPanel(o, new Vector2(cx, fits ? g.Top : g.Top + (timingH + 24) * u), u);
        Banners(o, width, height, u, time);
    }

    private void Timing(Overlay o, Vector2 at, float h, float u, float time)
    {
        var t = Timer;
        Style.Slanted(o, at, at + new Vector2(TimingW, h) * u, Style.Panel, 0.22f * 150 / h);
        o.Rect(Vector2.Round(at), Vector2.Round(at + new Vector2(5, h) * u), Style.Amber);
        var x = at.X + 22 * u;
        var right = at.X + 290 * u;
        Style.Label(o, "TIME", new Vector2(x, at.Y + 28 * u), 17 * u, Style.Amber, 0, 0, 0.3f * u);
        var finished = t.Phase == LapTimer.State.Finished;
        var col = t.Phase == LapTimer.State.Ready ? Style.Dim : finished && MathF.Sin(time * 8) > 0 ? Style.Amber : Style.Text;
        Style.Label(o, Style.Time(t.Time), new Vector2(x - 2 * u, at.Y + 80 * u), 54 * u, col, 0, Style.Slant, 0.4f * u);
        if (t.Best != null)
        {
            var w = Style.Label(o, "BEST ", new Vector2(x, at.Y + 108 * u), 17 * u, Style.Dim);
            Style.Label(o, Style.Time(t.Best[^1]), new Vector2(x + w, at.Y + 108 * u), 20 * u, Style.Text);
        }
        // split delta pop-up, fades after 3 s
        if (t.SinceSplit < 3 && t.Sector > 0 && t.Delta(t.Sector - 1) is { } d)
            Style.Label(o, Style.Delta(d), new Vector2(right, at.Y + 108 * u), 24 * u,
                Style.Fade(d <= 0 ? Style.Green : Style.Red, Style.Ease((3 - t.SinceSplit) * 2)), 1, Style.Slant, 0.3f * u);
        for (var i = 0; i < LapTimer.Sectors; i++)
        {
            Vector2 min = Vector2.Round(new Vector2(x + i * 68 * u, at.Y + 118 * u)), max = Vector2.Round(min + new Vector2(62, 22) * u);
            var done = i < t.Sector;
            var delta = t.Delta(i);
            var fill = done ? delta is { } dd ? Style.Fade(dd <= 0 ? Style.Green : Style.Red, 0.85f) : Overlay.Rgba(1, 1, 1, 0.75f) : Style.Faint;
            Style.Slanted(o, min, max, fill, 0.3f);
            if (i == t.Sector && t.Phase == LapTimer.State.Running) o.Rect(new Vector2(min.X, max.Y - 3 * u), new Vector2(max.X - 6 * u, max.Y), Style.Amber);
            var label = done ? delta is { } d2 ? Style.Delta(d2)[..^1] : Style.Time(t.Splits[i])[2..^2] : $"S{i + 1}";
            o.Text(label, new Vector2((min.X + max.X) / 2 - 2 * u, max.Y - 5 * u), 17 * u, done ? Style.Ink : Style.Text, 0.5f, 0.2f * u);
        }
        if (Drift.Total > 0)
        {
            var dw = Style.Label(o, "DRIFT ", new Vector2(x, at.Y + 170 * u), 17 * u, Style.Dim);
            Style.Label(o, Points(Drift.Total), new Vector2(x + dw, at.Y + 170 * u), 22 * u, Style.Amber, 0, Style.Slant);
        }
    }

    private static string Points(float p) => ((int)p).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', ' ');

    private void DriftPanel(Overlay o, Vector2 top, float u)
    {
        var a = Style.Ease(_driftA);
        if (a <= 0) return;
        var d = Drift;
        // centred plate, both sides slanted in
        var panel = Style.Fade(Style.Panel, a);
        float hw = DriftHalf * u, bw = hw - 18 * u, h = 100 * u;
        Vector2 tl = Vector2.Round(top - new Vector2(hw, 0)), tr = Vector2.Round(top + new Vector2(hw, 0)),
            br = Vector2.Round(top + new Vector2(bw, h)), bl = Vector2.Round(top + new Vector2(-bw, h));
        o.Quad(tl, tr, br, bl, panel);
        o.Line(tl + new Vector2(0.5f, 0), bl + new Vector2(0.5f, 0), 1, panel);
        o.Line(tr - new Vector2(0.5f, 0), br - new Vector2(0.5f, 0), 1, panel);
        Style.Label(o, "DRIFT", new Vector2(top.X, top.Y + 22 * u), 17 * u, Style.Fade(Style.Dim, a), 0.5f, 0, 0.3f * u);
        var shown = d.Score > 0 || d.Drifting ? d.Score : d.Last.Points;
        var w = o.Font!.Measure(Points(shown), 44 * u);
        Style.Label(o, Points(shown), new Vector2(top.X, top.Y + 64 * u), 44 * u, Style.Fade(Style.Amber, a), 0.5f, Style.Slant, 0.4f * u);
        if (d.Multiplier > 1 && d.Drifting)
            Style.Label(o, $"x{d.Multiplier}", new Vector2(top.X + w / 2 + 10 * u, top.Y + 64 * u), 24 * u, Style.Fade(Style.Text, a), 0, Style.Slant);
        // slip-angle bar from the centre, red beyond 30°
        var half = 110 * u;
        var y = top.Y + 78 * u;
        o.Rect(Vector2.Round(new Vector2(top.X - half, y)), Vector2.Round(new Vector2(top.X + half, y + 7 * u)), Style.Fade(Style.Faint, a));
        var k = Math.Clamp(d.Angle / 45, -1, 1);
        var fillCol = Style.Fade(MathF.Abs(d.Angle) > 30 ? Style.Red : Style.Amber, a);
        o.Rect(Vector2.Round(new Vector2(top.X + MathF.Min(0, k) * half, y)), Vector2.Round(new Vector2(top.X + MathF.Max(0, k) * half, y + 7 * u)), fillCol);
        o.Rect(Vector2.Round(new Vector2(top.X - u, y - 3 * u)), Vector2.Round(new Vector2(top.X + u, y + 10 * u)), Style.Fade(Style.Text, a));
        Style.Label(o, $"{MathF.Abs(d.Angle):0}°", new Vector2(top.X + half + 8 * u, y + 9 * u), 17 * u, Style.Fade(Style.Text, a));
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
            Style.Slanted(o, new Vector2(cx - 260 * u, y - 76 * u), new Vector2(cx + 260 * u, y + 128 * u), Style.Fade(Overlay.Rgba(0.02f, 0.03f, 0.05f, 0.8f), a), 0.12f);
            Style.Label(o, "FINISH", new Vector2(cx, y), 72 * u, Style.Fade(Style.Amber, a), 0.5f, Style.Slant, 0.6f * u);
            Style.Label(o, Style.Time(Timer.Time), new Vector2(cx, y + 46 * u), 36 * u, Style.Fade(Style.Text, a), 0.5f, Style.Slant);
            // sector deltas against the best run this one was compared with
            if (Timer.Delta(0) != null)
                for (var i = 0; i < LapTimer.Sectors; i++)
                {
                    var d = Timer.Delta(i)!.Value;
                    Vector2 min = Vector2.Round(new Vector2(cx - 206 * u + i * 104 * u, y + 62 * u)), max = Vector2.Round(min + new Vector2(96, 26) * u);
                    Style.Slanted(o, min, max, Style.Fade(d <= 0 ? Style.Green : Style.Red, 0.85f * a), 0.3f);
                    o.Text($"S{i + 1} {Style.Delta(d)[..^1]}", new Vector2((min.X + max.X) / 2 - 2 * u, max.Y - 7 * u), 17 * u, Style.Fade(Style.Ink, a), 0.5f, 0.2f * u);
                }
            if (Timer.NewRecord) Style.Label(o, "NEW RECORD", new Vector2(cx - 206 * u, y + 116 * u), 22 * u, Style.Fade(Style.Green, a), 0, Style.Slant, 0.3f * u);
            if (Drift.Total > 0)
                Style.Label(o, $"DRIFT {Points(Drift.Total)}", new Vector2(cx + 206 * u, y + 116 * u), 22 * u, Style.Fade(Style.Amber, a), 1, Style.Slant, 0.3f * u);
        }
        if (_hintA > 0)
        {
            // under the wrong-way banner, clear of the cluster on narrow screens
            var y = height * 0.34f + 74 * u;
            var a = Style.Ease(_hintA);
            const string key = "R", action = "RESET TO ROAD";
            var w = MathF.Max(o.Font!.Measure(key, 17 * u) + 12 * u, 26 * u) + 8 * u + o.Font.Measure(action, 17 * u);
            Style.Slanted(o, new Vector2(cx - w / 2 - 30 * u, y - 22 * u), new Vector2(cx + w / 2 + 30 * u, y + 22 * u), Style.Fade(Style.Panel, a), 0.2f);
            Style.KeyHint(o, key, action, new Vector2(cx - w / 2, y), u, a);
        }
    }
}
