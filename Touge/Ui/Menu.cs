using System.Numerics;
using Kansei.Graphics;
using Kansei.Input;

namespace Touge.Ui;

/// <summary>
///     Menu screens over the running 3D scene: title (course fly-over behind), course select (list, time of day ×
///     direction, line preview, length/climb/best), car select (list, specs, paint swatches; the game shows the car
///     turning behind), pause (Esc) and settings. Arrows/Enter/Esc or D-pad/A/B (stick, Start too). The game reacts to the
///     returned <see cref="Action"/> and reads the selection (<see cref="CourseTime"/>, <see cref="Reverse"/>, <see cref="CarId"/>, <see cref="Paint"/>).
/// </summary>
public sealed class Menu(Catalog catalog, Settings settings)
{
    public enum Screen { None, Title, Course, Car, Pause, Settings, Loading }
    public enum Action { None, Start, Resume, Restart, Quit, PreviewCar, SettingsChanged }

    private static readonly string[] TitleRows = ["START", "SETTINGS", "QUIT"],
        PauseRows = ["RESUME", "RESTART", "CHANGE COURSE", "CHANGE CAR", "SETTINGS", "QUIT"],
        SettingRows = ["GRAPHICS", "MUSIC", "MUSIC VOLUME", "HUD", "MINIMAP", "CAMERA", "BACK"];

    public Screen Current { get; private set; }
    private Screen _settingsFrom, _courseFrom, _carFrom;
    private int _row, _course, _variant, _car, _paint;
    private float _t; // seconds since the screen opened (entrance animation)
    private readonly MenuKeys _keys = new();

    private Catalog.Course SelectedCourse => catalog.Courses[_course];
    private (string Time, bool Reverse) Variant => Variants(SelectedCourse)[_variant];
    public string CourseTime => $"{SelectedCourse.Id}_{Variant.Time}";
    public bool Reverse => Variant.Reverse;
    public string CarId => catalog.Cars[_car].Id;
    public int Paint => _paint;
    /// <summary>Run info for the pause screen: course name, current/best time.</summary>
    public (string Course, string Car, float? Time, float? Best) Run { get; set; }

    private static List<(string Time, bool Reverse)> Variants(Catalog.Course c) => [.. c.Times.SelectMany(t => new[] { (t, false), (t, true) })];

    /// <summary>Opens <paramref name="s"/>; the course/car lists start at the current game's course, direction, car and paint.</summary>
    public void Open(Screen s, string courseTime, bool reverse, string car, int paint)
    {
        var id = courseTime[..courseTime.LastIndexOf('_')];
        _course = Math.Max(0, catalog.Courses.ToList().FindIndex(c => c.Id == id));
        _variant = Math.Max(0, Variants(SelectedCourse).IndexOf((courseTime[(courseTime.LastIndexOf('_') + 1)..], reverse)));
        _car = Math.Max(0, catalog.Cars.ToList().FindIndex(c => c.Id == car));
        _paint = paint;
        Go(s);
    }

    private void Go(Screen s)
    {
        if (s == Screen.Course && Current != Screen.Car) _courseFrom = Current;
        if (s == Screen.Car) _carFrom = Current;
        if (s == Screen.Settings) _settingsFrom = Current;
        (Current, _row, _t) = (s, 0, 0);
    }

    /// <summary>Shows the screen fully faded in (screenshots).</summary>
    public void Settle() => _t = 10;

    public void Close() => Current = Screen.None;

    public void ShowLoading() => Current = Screen.Loading;

    public Action Update(InputSnapshot input, float dt)
    {
        _t += dt;
        if (Current is Screen.None or Screen.Loading) return Action.None;
        var k = _keys.Read(input, dt);
        switch (Current)
        {
            case Screen.Title:
                _row = Wrap(_row + k.Y, TitleRows.Length);
                if (!k.Ok) return Action.None;
                if (_row == 0) Go(Screen.Course);
                else if (_row == 1) Go(Screen.Settings);
                else return Action.Quit;
                return Action.None;
            case Screen.Pause:
                _row = Wrap(_row + k.Y, PauseRows.Length);
                if (k.Back) return Action.Resume;
                if (!k.Ok) return Action.None;
                switch (_row)
                {
                    case 0: return Action.Resume;
                    case 1: return Action.Restart;
                    case 2: Go(Screen.Course); return Action.None;
                    case 3: Go(Screen.Car); return Action.None;
                    case 4: Go(Screen.Settings); return Action.None;
                    default: return Action.Quit;
                }
            case Screen.Course:
                if (k.Y != 0) (_course, _variant) = (Wrap(_course + k.Y, catalog.Courses.Count), 0);
                _variant = Wrap(_variant + k.X, Variants(SelectedCourse).Count);
                if (k.Back) Go(_courseFrom);
                else if (k.Ok) Go(Screen.Car);
                return Action.None;
            case Screen.Car:
                var before = (_car, _paint);
                if (k.Y != 0) (_car, _paint) = (Wrap(_car + k.Y, catalog.Cars.Count), 0);
                _paint = Wrap(_paint + k.X, catalog.Cars[_car].Paints.Length);
                if (k.Back) Go(_carFrom);
                else if (k.Ok) return Action.Start;
                return before != (_car, _paint) ? Action.PreviewCar : Action.None;
            case Screen.Settings:
                _row = Wrap(_row + k.Y, SettingRows.Length);
                if (k.Back || (k.Ok && _row == SettingRows.Length - 1))
                {
                    var from = _settingsFrom;
                    Go(from);
                    _row = from == Screen.Title ? 1 : 4;
                    return Action.None;
                }
                var step = k.X != 0 ? k.X : k.Ok ? 1 : 0;
                if (step == 0) return Action.None;
                switch (_row)
                {
                    case 0: settings.HighQuality = !settings.HighQuality; break;
                    case 1: settings.MusicOn = !settings.MusicOn; break;
                    case 2: settings.MusicVolume = Math.Clamp(MathF.Round(settings.MusicVolume * 10 + step) / 10, 0, 1); break;
                    case 3: settings.HudOn = !settings.HudOn; break;
                    case 4: settings.MapMode = (Hud.MapMode)Wrap((int)settings.MapMode + step, 3); break;
                    case 5: settings.BumperCam = !settings.BumperCam; break;
                }
                return Action.SettingsChanged;
        }
        return Action.None;
    }

    private static int Wrap(int i, int n) => (i % n + n) % n;

    // ---------------------------------------------------------------- drawing

    public void Build(Overlay o, int width, int height, float time)
    {
        o.Clear();
        if (Current == Screen.None) return;
        var u = height / 900f;
        var m = 56 * u;
        if (Current == Screen.Loading)
        {
            o.Rect(Vector2.Zero, new Vector2(width, height), Overlay.Rgba(0.01f, 0.012f, 0.02f, 0.85f));
            Style.Label(o, "LOADING", new Vector2(width / 2f, height / 2f), 64 * u, Style.Text, 0.5f, Style.Slant, 0.5f * u);
            Style.Label(o, $"{SelectedCourse.Name}  {Catalog.TimeName(Variant.Time)}  {Catalog.DirectionName(SelectedCourse, Variant.Reverse)}",
                new Vector2(width / 2f, height / 2f + 44 * u), 22 * u, Style.Amber, 0.5f);
            return;
        }
        // darken the left side for the lists, the whole frame when paused
        o.RectGradient(Vector2.Zero, new Vector2(width * 0.62f, height), Overlay.Rgba(0.01f, 0.012f, 0.02f, 0.82f), Overlay.Rgba(0.01f, 0.012f, 0.02f, 0));
        if (Current is Screen.Pause or Screen.Settings) o.Rect(Vector2.Zero, new Vector2(width, height), Overlay.Rgba(0, 0, 0, 0.35f));
        switch (Current)
        {
            case Screen.Title: Title(o, width, height, u, m, time); break;
            case Screen.Course: CourseScreen(o, width, height, u, m); break;
            case Screen.Car: CarScreen(o, width, height, u, m); break;
            case Screen.Pause: PauseScreen(o, width, height, u, m); break;
            case Screen.Settings: SettingsScreen(o, height, u, m); break;
        }
    }

    /// <summary>Entrance of element <paramref name="i"/>: 0 → 1 with a small stagger.</summary>
    private float In(int i) => Style.Ease((_t - i * 0.035f) * 5);

    private void Header(Overlay o, string kicker, string title, float u, float m)
    {
        var a = In(0);
        var x = m - (1 - a) * 30 * u;
        Style.Label(o, kicker, new Vector2(x + 4 * u, m + 18 * u), 17 * u, Style.Fade(Style.Amber, a), 0, 0, 0.3f * u);
        o.Quad(new Vector2(x + 10 * u, m + 30 * u), new Vector2(x + 20 * u, m + 30 * u), new Vector2(x + 6 * u, m + 76 * u), new Vector2(x - 4 * u, m + 76 * u), Style.Fade(Style.Amber, a));
        Style.Label(o, title, new Vector2(x + 30 * u, m + 74 * u), 52 * u, Style.Fade(Style.Text, a), 0, Style.Slant, 0.4f * u);
    }

    private static void Footer(Overlay o, float height, float u, float m, params (string Key, string Action)[] hints)
    {
        var x = m;
        foreach (var (key, action) in hints) x = Style.KeyHint(o, key, action, new Vector2(x, height - m + 4 * u), u);
    }

    /// <summary>Vertical list; the selected row gets the amber slanted bar. Returns the y below the list.</summary>
    private float List(Overlay o, IReadOnlyList<string> rows, int selected, Vector2 at, float u, float width = 400, float rowH = 50, float size = 28, int first = 0)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var a = In(i + 1);
            var y = at.Y + i * rowH * u;
            var x = at.X - (1 - a) * 40 * u;
            var sel = i + first == selected;
            if (sel)
            {
                Style.Slanted(o, new Vector2(x - 14 * u, y), new Vector2(x + width * u, y + (rowH - 8) * u), Style.Fade(Style.Amber, a), 0.35f);
                o.Rect(Vector2.Round(new Vector2(x - 22 * u, y)), Vector2.Round(new Vector2(x - 17 * u, y + (rowH - 8) * u)), Style.Fade(Style.Amber, a));
            }
            var baseY = y + (rowH - 8) * u / 2 + o.Font!.CapHeight * size * u / 2;
            if (sel) o.Text(rows[i], new Vector2(x, baseY), size * u, Style.Fade(Style.Ink, a), 0, 0.5f * u, 0, Style.Slant);
            else Style.Label(o, rows[i], new Vector2(x, baseY), size * u, Style.Fade(Style.Dim, a), 0, Style.Slant);
        }
        return at.Y + rows.Count * rowH * u;
    }

    private void Title(Overlay o, int width, int height, float u, float m, float time)
    {
        var a = In(0);
        var x = m + 10 * u - (1 - a) * 60 * u;
        var y = height * 0.44f;
        Style.Label(o, "TOUGE", new Vector2(x, y), 210 * u, Style.Fade(Style.Text, a), 0, 0.2f, 1.2f * u);
        o.Rect(Vector2.Round(new Vector2(x + 8 * u, y + 18 * u)), Vector2.Round(new Vector2(x + 470 * u, y + 24 * u)), Style.Fade(Style.Amber, a));
        Style.Label(o, "SPECIAL STAGE REMAKE", new Vector2(x + 8 * u, y + 58 * u), 24 * u, Style.Fade(Style.Amber, a), 0, Style.Slant, 0.3f * u);
        List(o, TitleRows, _row, new Vector2(m + 24 * u, y + 110 * u), u, 300);
        var blink = 0.55f + 0.45f * MathF.Sin(time * 3);
        Style.Label(o, "PRESS ENTER", new Vector2(width - m, height - m), 20 * u, Style.Fade(Style.Text, a * blink), 1, Style.Slant, 0.3f * u);
        Footer(o, height, u, m, ("UP/DN", "SELECT"), ("ENTER", "OK"));
    }

    private void CourseScreen(Overlay o, int width, int height, float u, float m)
    {
        Header(o, "TOUGE", "SELECT COURSE", u, m);
        List(o, [.. catalog.Courses.Select(c => c.Name)], _course, new Vector2(m + 24 * u, m + 120 * u), u, 360, 52, 27);
        // detail panel on the right
        var c = SelectedCourse;
        var a = In(2);
        Vector2 min = new(MathF.Max(width * 0.5f, m + 480 * u), m + 110 * u), max = new(width - m, height - m - 50 * u);
        min.X += (1 - a) * 40 * u;
        Style.Slanted(o, min, max, Style.Fade(Style.Panel, a), -0.06f);
        var x = min.X + 50 * u;
        Style.Label(o, c.Name, new Vector2(x, min.Y + 56 * u), 44 * u, Style.Fade(Style.Text, a), 0, Style.Slant, 0.4f * u);
        // time-of-day and direction chips
        var cx = x;
        foreach (var t in new[] { "DAY", "NIT", "RIN" })
            cx = Chip(o, Catalog.TimeName(t), new Vector2(cx, min.Y + 84 * u), u, c.Times.Contains(t), t == Variant.Time, a);
        cx += 18 * u;
        foreach (var rev in new[] { false, true })
            cx = Chip(o, Catalog.DirectionName(c, rev), new Vector2(cx, min.Y + 84 * u), u, true, rev == Variant.Reverse, a);
        Style.Label(o, "< >  CHANGE", new Vector2(cx + 10 * u, min.Y + 104 * u), 15 * u, Style.Fade(Style.Dim, a));
        // stats
        var best = settings.Best.GetValueOrDefault(Settings.BestKey(c.Id, Variant.Reverse));
        var sy = min.Y + 160 * u;
        Stat(o, "LENGTH", FormattableString.Invariant($"{c.LengthM / 1000:0.0} km"), new Vector2(x, sy), u, a);
        Stat(o, "ELEVATION", $"{c.ClimbM:0} m", new Vector2(x + 170 * u, sy), u, a);
        Stat(o, "BEST", Style.Time(best?[^1]), new Vector2(x + 340 * u, sy), u, a);
        // line preview, north up, fitted into the rest of the panel
        LinePreview(o, c, Variant.Reverse, new Vector2(x, sy + 30 * u), new Vector2(max.X - 40 * u, max.Y - 30 * u), u, a);
        Footer(o, height, u, m, ("UP/DN", "COURSE"), ("< >", "TIME / DIRECTION"), ("ENTER", "OK"), ("ESC", "BACK"));
    }

    private float Chip(Overlay o, string text, Vector2 at, float u, bool available, bool selected, float a)
    {
        var size = 16 * u;
        var w = o.Font!.Measure(text, size) + 22 * u;
        Vector2 min = Vector2.Round(at), max = Vector2.Round(at + new Vector2(w, 28 * u));
        if (selected) Style.Slanted(o, min, max, Style.Fade(Style.Amber, a), 0.3f);
        else Style.Slanted(o, min, max, Style.Fade(available ? Style.PanelLight : Overlay.Rgba(1, 1, 1, 0.03f), a), 0.3f);
        var col = selected ? Style.Ink : available ? Style.Text : Overlay.Rgba(1, 1, 1, 0.25f);
        o.Text(text, new Vector2(min.X + 9 * u, max.Y - 9 * u), size, Style.Fade(col, a), 0, 0.3f * u);
        return max.X + 8 * u;
    }

    private static void Stat(Overlay o, string label, string value, Vector2 at, float u, float a)
    {
        Style.Label(o, label, at, 14 * u, Style.Fade(Style.Dim, a), 0, 0, 0.2f * u);
        Style.Label(o, value, at + new Vector2(0, 30 * u), 28 * u, Style.Fade(Style.Text, a), 0, Style.Slant, 0.3f * u);
    }

    private static void LinePreview(Overlay o, Catalog.Course c, bool reverse, Vector2 min, Vector2 max, float u, float a)
    {
        var line = c.Line;
        Vector2 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (var p in line) (lo, hi) = (Vector2.Min(lo, p), Vector2.Max(hi, p));
        min.Y += 30 * u;
        var size = max - min;
        var k = MathF.Min(size.X / (hi.X - lo.X), size.Y / (hi.Y - lo.Y)) * 0.92f;
        var off = (min + max) / 2 - (lo + hi) / 2 * k;
        Vector2 S(Vector2 p) => off + p * k;
        for (var pass = 0; pass < 2; pass++)
        for (var i = 1; i < line.Length; i++)
            o.Line(S(line[i - 1]), S(line[i]), (pass == 0 ? 9 : 4.5f) * u, Style.Fade(pass == 0 ? Overlay.Rgba(0, 0, 0, 0.8f) : Overlay.Rgba(0.93f, 0.94f, 0.96f), a));
        var (start, goal) = reverse ? (line[^1], line[0]) : (line[0], line[^1]);
        if (!c.Circuit)
        {
            o.Disc(S(goal), 9 * u, Style.Fade(Style.Red, a));
            Style.Label(o, "GOAL", S(goal) + new Vector2(14 * u, 6 * u), 15 * u, Style.Fade(Style.Text, a));
        }
        o.Disc(S(start), 9 * u, Style.Fade(Overlay.Rgba(0.2f, 0.95f, 0.35f), a));
        Style.Label(o, "START", S(start) + new Vector2(14 * u, 6 * u), 15 * u, Style.Fade(Style.Text, a));
    }

    private void CarScreen(Overlay o, int width, int height, float u, float m)
    {
        Header(o, "TOUGE", "SELECT CAR", u, m);
        // scrolling list: 11 rows around the selection
        const int visible = 11;
        var first = Math.Clamp(_car - visible / 2, 0, catalog.Cars.Count - visible);
        var rows = catalog.Cars.Skip(first).Take(visible).Select(c => c.Name.ToUpperInvariant()).ToList();
        List(o, rows, _car, new Vector2(m + 24 * u, m + 120 * u), u, 430, 48, 22, first);
        if (first > 0) Style.Label(o, "...", new Vector2(m + 24 * u, m + 112 * u), 20 * u, Style.Dim);
        if (first + visible < catalog.Cars.Count) Style.Label(o, "...", new Vector2(m + 24 * u, m + 120 * u + visible * 48 * u), 20 * u, Style.Dim);

        var car = catalog.Cars[_car];
        var a = In(2);
        Vector2 min = new(width - m - 560 * u + (1 - a) * 40 * u, height - m - 300 * u), max = new(width - m, height - m - 50 * u);
        Style.Slanted(o, min, max, Style.Fade(Style.Panel, a), -0.12f);
        var x = min.X + 50 * u;
        Style.Label(o, $"{_car + 1:D2} / {catalog.Cars.Count}   {car.Id}", new Vector2(x, min.Y + 30 * u), 15 * u, Style.Fade(Style.Amber, a), 0, 0, 0.2f * u);
        var nameSize = MathF.Min(36 * u, 460 * u / MathF.Max(o.Font!.Measure(car.Name, 1), 1));
        Style.Label(o, car.Name, new Vector2(x, min.Y + 70 * u), nameSize, Style.Fade(Style.Text, a), 0, Style.Slant, 0.3f * u);
        var sy = min.Y + 110 * u;
        Stat(o, "DRIVE", car.Drive, new Vector2(x, sy), u, a);
        Stat(o, "POWER", $"{car.Ps} PS", new Vector2(x + 110 * u, sy), u, a);
        Stat(o, "WEIGHT", $"{car.Kg} kg", new Vector2(x + 240 * u, sy), u, a);
        Stat(o, "KG/PS", FormattableString.Invariant($"{(float)car.Kg / car.Ps:0.0}"), new Vector2(x + 380 * u, sy), u, a);
        // power bar against the strongest car
        var maxPs = catalog.Cars.Max(c => c.Ps);
        var by = sy + 44 * u;
        o.Rect(Vector2.Round(new Vector2(x, by)), Vector2.Round(new Vector2(x + 440 * u, by + 5 * u)), Style.Fade(Style.Faint, a));
        o.Rect(Vector2.Round(new Vector2(x, by)), Vector2.Round(new Vector2(x + 440 * u * car.Ps / maxPs, by + 5 * u)), Style.Fade(Style.Amber, a));
        // paint swatches
        Style.Label(o, "PAINT", new Vector2(x, by + 44 * u), 14 * u, Style.Fade(Style.Dim, a), 0, 0, 0.2f * u);
        for (var i = 0; i < car.Paints.Length; i++)
        {
            var c = new Vector2(x + 80 * u + i * 40 * u, by + 39 * u);
            if (i == _paint) o.Disc(c, 16 * u, Style.Fade(Style.Amber, a));
            o.Disc(c, 12 * u, Style.Fade(Overlay.Rgba(0, 0, 0, 0.9f), a));
            o.Disc(c, 10.5f * u, Style.Fade(Catalog.Swatch(car.Paints[i]), a));
        }
        Footer(o, height, u, m, ("UP/DN", "CAR"), ("< >", "PAINT"), ("ENTER", "DRIVE"), ("ESC", "BACK"));
    }

    private void PauseScreen(Overlay o, int width, int height, float u, float m)
    {
        Header(o, "TOUGE", "PAUSE", u, m);
        List(o, PauseRows, _row, new Vector2(m + 24 * u, m + 130 * u), u, 340);
        var a = In(2);
        var (course, car, time, best) = Run;
        Vector2 min = new(width - m - 460 * u + (1 - a) * 40 * u, m + 110 * u), max = new(width - m, m + 330 * u);
        Style.Slanted(o, min, max, Style.Fade(Style.Panel, a), -0.1f);
        var x = min.X + 50 * u;
        Style.Label(o, course, new Vector2(x, min.Y + 44 * u), 26 * u, Style.Fade(Style.Text, a), 0, Style.Slant, 0.3f * u);
        Style.Label(o, car, new Vector2(x, min.Y + 74 * u), 18 * u, Style.Fade(Style.Amber, a));
        Stat(o, "TIME", Style.Time(time), new Vector2(x, min.Y + 130 * u), u, a);
        Stat(o, "BEST", Style.Time(best), new Vector2(x + 190 * u, min.Y + 130 * u), u, a);
        Footer(o, height, u, m, ("UP/DN", "SELECT"), ("ENTER", "OK"), ("ESC", "RESUME"));
    }

    private void SettingsScreen(Overlay o, int height, float u, float m)
    {
        Header(o, "TOUGE", "SETTINGS", u, m);
        var at = new Vector2(m + 24 * u, m + 130 * u);
        List(o, SettingRows, _row, at, u, 620);
        string[] values =
        [
            settings.HighQuality ? "HIGH" : "LOW", settings.MusicOn ? "ON" : "OFF", "", settings.HudOn ? "ON" : "OFF",
            settings.MapMode switch { Hud.MapMode.NorthUp => "NORTH UP", Hud.MapMode.Overview => "WHOLE COURSE", _ => "ROTATING" },
            settings.BumperCam ? "BUMPER" : "CHASE", "",
        ];
        for (var i = 0; i < values.Length; i++)
        {
            var a = In(i + 1);
            var y = at.Y + i * 50 * u + 21 * u;
            var sel = i == _row;
            var col = Style.Fade(sel ? Style.Ink : Style.Text, a);
            var right = at.X + 560 * u - (1 - a) * 40 * u;
            if (i == 2)
            {
                // volume: 10 slanted segments
                for (var s = 0; s < 10; s++)
                {
                    var on = s < MathF.Round(settings.MusicVolume * 10);
                    var sx = right - 200 * u + s * 20 * u;
                    Style.Slanted(o, new Vector2(sx, y - 10 * u), new Vector2(sx + 15 * u, y + 10 * u),
                        Style.Fade(on ? sel ? Style.Ink : Style.Amber : sel ? Overlay.Rgba(0, 0, 0, 0.25f) : Style.Faint, a), 0.3f);
                }
                continue;
            }
            if (values[i] == "") continue;
            var size = 22 * u;
            var baseY = y + o.Font!.CapHeight * size / 2;
            if (sel) o.Text($"<  {values[i]}  >", new Vector2(right, baseY), size, col, 1, 0.4f * u, 0, Style.Slant);
            else Style.Label(o, values[i], new Vector2(right, baseY), size, col, 1, Style.Slant);
        }
        Style.Label(o, "Saved to " + Settings.FilePath, new Vector2(m, height - m - 40 * u), 14 * u, Style.Faint);
        Footer(o, height, u, m, ("UP/DN", "SELECT"), ("< >", "CHANGE"), ("ESC", "BACK"));
    }

    /// <summary>Menu navigation from keyboard (arrows with key repeat, Enter/Space, Esc/Backspace) and pad (D-pad, stick edges, A/Start, B).</summary>
    private sealed class MenuKeys
    {
        private Vector2 _stick;

        public (int X, int Y, bool Ok, bool Back) Read(InputSnapshot input, float dt)
        {
            var k = input.Keyboard;
            var pad = input.Gamepad;
            int x = 0, y = 0;
            if (k.IsKeyRepeating(Key.Up, dt) || k.IsKeyRepeating(Key.W, dt)) y--;
            if (k.IsKeyRepeating(Key.Down, dt) || k.IsKeyRepeating(Key.S, dt)) y++;
            if (k.IsKeyRepeating(Key.Left, dt) || k.IsKeyRepeating(Key.A, dt)) x--;
            if (k.IsKeyRepeating(Key.Right, dt) || k.IsKeyRepeating(Key.D, dt)) x++;
            var ok = k.IsKeyPressed(Key.Enter) || k.IsKeyPressed(Key.Space);
            var back = k.IsKeyPressed(Key.Escape) || k.IsKeyPressed(Key.Backspace);
            if (pad.IsConnected)
            {
                if (pad.IsButtonPressed(GamepadButton.DpadUp)) y--;
                if (pad.IsButtonPressed(GamepadButton.DpadDown)) y++;
                if (pad.IsButtonPressed(GamepadButton.DpadLeft)) x--;
                if (pad.IsButtonPressed(GamepadButton.DpadRight)) x++;
                var s = new Vector2(pad.GetAxis(GamepadAxis.LeftX), pad.GetAxis(GamepadAxis.LeftY));
                if (MathF.Abs(s.Y) > 0.6f && MathF.Abs(_stick.Y) <= 0.6f) y += MathF.Sign(s.Y);
                if (MathF.Abs(s.X) > 0.6f && MathF.Abs(_stick.X) <= 0.6f) x += MathF.Sign(s.X);
                _stick = s;
                ok |= pad.IsButtonPressed(GamepadButton.A) || pad.IsButtonPressed(GamepadButton.Start);
                back |= pad.IsButtonPressed(GamepadButton.B);
            }
            return (Math.Sign(x), Math.Sign(y), ok, back);
        }
    }
}
