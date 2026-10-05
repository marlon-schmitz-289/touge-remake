using System.Numerics;
using Kansei.Graphics;
using Touge.Race;

namespace Touge.Ui;

/// <summary>
///     LEGEND OF THE STREETS screens (main menu), rebuilt in the original's course-select style with <see cref="Canvas"/>
///     (the original: KCRSSEL0–2 with rival name strips r_selnm, car cuts r_selc and the beaten cross r_selpeke): course grid
///     with each course's progress → rival ladder (name, team, car, conditions, status WIN / CHALLENGE / NEW / LOCKED, the
///     selected rival's details) → VS card over the rival's 3D car with its theme (MG_BGM.AFS). Deciding the card hands
///     over to the car select (<see cref="Action.Challenge"/>); the game runs the battle and records it in
///     <see cref="Progress"/>. Unlock news (rivals, courses, the secret car) shows on the ladder after a battle.
///     Sounds by SYSSE name (SYS005 move, SYS006 decide, BEEP001 back/locked).
/// </summary>
public sealed class LegendScreen(Catalog catalog, Progress progress)
{
    public enum Step { Course, Rivals, Card }

    /// <summary>PreviewRival: load <see cref="Selected"/>'s course and car for the card (behind the fade); Challenge: on to the car select; Exit: main menu.</summary>
    public enum Action { None, PreviewRival, Challenge, Exit }

    public const float Fade = 30 / 60f, NewsHold = 5;

    /// <summary>The career progress; a new one (a loaded save slot) is taken as it is, without unlock news (nor the last profile's NEW! tags).</summary>
    public Progress Progress
    {
        get => progress;
        set => (progress, _seen, _seenCourses, _seenCar, _fresh, _news) = (value, [], 0, true, [], 99);
    }
    public bool Active { get; private set; }
    public Step Current { get; private set; }
    public Action<string>? Sound { get; set; }
    /// <summary>The rival on the ladder cursor / card.</summary>
    public Legend.Entry Selected => _rivals.Length > 0 ? _rivals[Math.Min(_row, _rivals.Length - 1)] : Legend.Of(_slot).FirstOrDefault() ?? Legend.All[0];
    /// <summary>Card: the 3D rival car is shown (the game turns the camera around it).</summary>
    public bool ShowsCar => Active && Current == Step.Card && _leave < 0;
    /// <summary>Course flow TOKYO as the original's course select, the card the rival's own theme.</summary>
    public string? Music => !Active ? null : Current == Step.Card ? Selected.Theme : "TOKYO.adx";

    private int _slot, _row;
    private float _t, _clock, _leave = -1, _news = 99;
    private Step _next;
    private Action _then;
    private Legend.Entry[] _rivals = [];
    private readonly List<string> _newsLines = [];
    private HashSet<string> _seen = [];
    private int _seenCourses;
    private bool _seenCar = true, _fadeIn;
    /// <summary>Rivals that opened with the last battle (tagged NEW!).</summary>
    private HashSet<string> _fresh = [];

    private const int Slots = 12;

    /// <summary>
    ///     Opens on <paramref name="step"/>; <paramref name="key"/> puts the cursor on that rival (the ladder after a battle:
    ///     on the next one to beat; the card: exactly that rival). Anything unlocked since the ladder was last shown is listed as news.
    /// </summary>
    public void Open(Step step, string? key = null)
    {
        if (Legend.Find(key ?? "") is { } e) _slot = e.Slot;
        (Active, _leave) = (true, -1);
        Enter(step);
        _fadeIn = true;
        if (key != null && step != Step.Course)
        {
            var at = Array.FindIndex(_rivals, r => r.Key == key);
            _row = at >= 0 && step == Step.Rivals && Progress.Beaten(key) ? NextOpen(at) : Math.Max(0, at);
        }
        Snapshot(news: _seen.Count > 0 || _seenCourses > 0 || !_seenCar);
    }

    /// <summary>Skips the fade-in and entrances (screenshots).</summary>
    public void Settle() => _t = MathF.Max(_t, 9);

    private void Enter(Step s)
    {
        (Current, _t, _fadeIn) = (s, 0, false);
        _rivals = [.. Legend.Of(_slot).Where(e => Legend.Visible(e, Progress))];
        if (s == Step.Rivals) _row = NextOpen(-1);
    }

    /// <summary>First rival after <paramref name="after"/> that is open and not beaten yet (else the one at <paramref name="after"/>, else 0).</summary>
    private int NextOpen(int after)
    {
        for (var i = after + 1; i < _rivals.Length; i++)
            if (Legend.Unlocked(_rivals[i], Progress) && !Progress.Beaten(_rivals[i].Key)) return i;
        return Math.Max(0, after);
    }

    /// <summary>Remembers what is open now; with <paramref name="news"/> first lists what opened since the last snapshot.</summary>
    private void Snapshot(bool news)
    {
        var open = Legend.All.Where(e => Legend.Unlocked(e, Progress)).Select(e => e.Key).ToHashSet();
        var courses = Enumerable.Range(0, Legend.CourseIds.Length).Count(s => Legend.CourseOpen(s, Progress));
        var car = !Legend.CarLocked(Legend.SecretCar, Progress);
        if (news)
        {
            _newsLines.Clear();
            _fresh = [.. open.Where(k => !_seen.Contains(k))];
            foreach (var e in Legend.All.Where(e => _fresh.Contains(e.Key)))
                _newsLines.Add($"NEW RIVAL: {e.Rival.Name}  ({CourseName(e.Slot)})");
            if (courses > _seenCourses) _newsLines.Add("NEW COURSES: MYOGI+, USUI+, SHOMARU, MOMIJI LINE, SHIONA");
            if (car && !_seenCar) _newsLines.Add($"NEW CAR: {catalog.Cars.FirstOrDefault(c => c.Id == Legend.SecretCar)?.Name ?? Legend.SecretCar}");
            if (_newsLines.Count > 0) _news = 0;
        }
        (_seen, _seenCourses, _seenCar) = (open, courses, car);
    }

    private string CourseName(int slot) => CourseOf(slot)?.Name ?? Legend.CourseIds[Math.Min(slot, Legend.CourseIds.Length - 1)];

    private Catalog.Course? CourseOf(int slot) => slot < Legend.CourseIds.Length ? catalog.Courses.FirstOrDefault(c => c.Id == Legend.CourseIds[slot]) : null;

    private bool SlotOpen(int slot) => CourseOf(slot) != null && Legend.CourseOpen(slot, Progress);

    private void Leave(Step next, Action then) => (_leave, _next, _then) = (0, next, then);

    public Action Update((int X, int Y, bool Ok, bool Back) k, float dt)
    {
        if (!Active) return Action.None;
        dt = MathF.Min(dt, 1 / 20f);
        _t += dt;
        _clock += dt;
        _news += dt;
        if (_leave >= 0)
        {
            if ((_leave += dt) < Fade) return Action.None;
            _leave = -1;
            if (_then is Action.Exit or Action.Challenge) Active = false;
            else Enter(_next);
            _fadeIn = _then == Action.PreviewRival; // the card fades in once the rival's course is loaded behind the black
            return _then;
        }
        switch (Current)
        {
            case Step.Course:
                if (k.X != 0 || k.Y != 0)
                {
                    do _slot = (_slot + k.X + 3 * k.Y + Slots) % Slots; // the grid's last cell stays empty
                    while (_slot >= Legend.CourseIds.Length);
                    Sound?.Invoke("SYS005");
                }
                else if (k.Ok && !SlotOpen(_slot)) Sound?.Invoke("BEEP001");
                else if (k.Ok)
                {
                    Sound?.Invoke("SYS006");
                    Enter(Step.Rivals);
                }
                else if (k.Back)
                {
                    Sound?.Invoke("BEEP001");
                    Leave(Step.Course, Action.Exit);
                }
                break;
            case Step.Rivals:
                if (k.Y != 0 && _rivals.Length > 0)
                {
                    _row = (_row + k.Y + _rivals.Length) % _rivals.Length;
                    Sound?.Invoke("SYS005");
                }
                else if (k.Ok && !Legend.Unlocked(Selected, Progress)) Sound?.Invoke("BEEP001");
                else if (k.Ok)
                {
                    Sound?.Invoke("SYS006");
                    Leave(Step.Card, Action.PreviewRival);
                }
                else if (k.Back)
                {
                    Sound?.Invoke("BEEP001");
                    Enter(Step.Course);
                }
                break;
            case Step.Card:
                if (k.Ok && _t > 0.3f)
                {
                    Sound?.Invoke("SYS006");
                    Leave(Step.Card, Action.Challenge);
                }
                else if (k.Back)
                {
                    Sound?.Invoke("BEEP001");
                    var row = _row;
                    Enter(Step.Rivals);
                    _row = row;
                }
                break;
        }
        return Action.None;
    }

    // ---------------------------------------------------------------- drawing

    private readonly Canvas _c = new();
    private float Theta => _clock * 300 % 360;
    private static readonly uint Grey = Overlay.Rgba(0.72f, 0.73f, 0.75f), Gold = Overlay.Rgba(1, 0.8f, 0.2f), Locked = Overlay.Rgba(1, 1, 1, 0.35f);

    public void Build(Overlay o, int width, int height)
    {
        o.Clear();
        if (!Active) return;
        var c = _c;
        c.Begin(o, width, height);
        switch (Current)
        {
            case Step.Course:
                c.Backdrop(_clock);
                CourseScreen(c);
                c.Marquee("LEGEND OF THE STREETS", false, _clock);
                break;
            case Step.Rivals:
                c.Backdrop(_clock);
                RivalScreen(c);
                c.Marquee("SELECT A RIVAL", false, _clock);
                break;
            case Step.Card:
                CardScreen(c);
                break;
        }
        c.Fade(_leave >= 0 ? Math.Clamp(_leave / Fade, 0, 1) : _fadeIn ? 1 - Math.Clamp(_t / Fade, 0, 1) : 0);
    }

    private void CourseScreen(Canvas c)
    {
        var o = c.O;
        var course = CourseOf(_slot);
        var open = SlotOpen(_slot);
        c.Carbon(16, 72, 250, 306);
        if (course != null && open) Menu.MapLine(c, course, false, 34, 90, 232, 288);
        else c.Text("LOCKED", 133, 196, 22, Grey, 0.5f, 0.2f);
        for (var i = 0; i < Legend.CourseIds.Length; i++)
        {
            float x = 266 + i % 3 * 76, y = 74 + i / 3 * 38;
            var on = SlotOpen(i);
            Vector2 min = Vector2.Round(c.P(x, y)), max = Vector2.Round(c.P(x + 70, y + 30));
            var cleared = on && Legend.Cleared(i, Progress);
            o.Rect(min, max, cleared ? Overlay.Rgba(0.85f, 0.65f, 0.15f) : Overlay.Rgba(0.55f, 0.56f, 0.58f));
            o.RectGradient(min + new Vector2(1.5f, 1.5f) * c.S, max - new Vector2(1.5f, 1.5f) * c.S, Overlay.Rgba(0.2f, 0.21f, 0.22f), Overlay.Rgba(0.08f, 0.08f, 0.09f));
            c.Fit(CourseName(i), x + 35, y + 17, 60, 0.5f, on ? Canvas.White : Locked, 0.12f, 0.05f, 12);
            if (!on) continue;
            // one pip per rival: gold = beaten
            var rivals = Legend.Of(i).Where(e => Legend.Visible(e, Progress)).ToArray();
            for (var r = 0; r < rivals.Length; r++)
            {
                var at = c.P(x + 35 + (r - (rivals.Length - 1) / 2f) * 9, y + 24);
                o.Disc(at, 2.6f * c.S, Progress.Beaten(rivals[r].Key) ? Gold : Overlay.Rgba(1, 1, 1, 0.25f));
            }
        }
        float sx = 266 + _slot % 3 * 76, sy = 74 + _slot / 3 * 38;
        c.Glow(sx - 3, sy - 3, sx + 73, sy + 33, Canvas.Pulse(Theta));
        c.Carbon(262, 232, 496, 306, 1, false);
        if (course != null)
        {
            var all = Legend.Of(_slot).Where(e => Legend.Visible(e, Progress)).ToArray();
            Stat(c, "RIVALS", $"{all.Count(e => Progress.Beaten(e.Key))} / {all.Length}", 274);
            Stat(c, "LENGTH", FormattableString.Invariant($"{course.LengthM / 1000:0.0} km"), 352);
            Stat(c, "STATUS", !open ? "LOCKED" : Legend.Cleared(_slot, Progress) ? "CLEARED" : "OPEN", 418);
        }
        var label = CourseName(_slot);
        c.Lettering(label, 256, 372, MathF.Min(54, 380 * c.Kx / c.O.Font!.Measure(label, c.Ky)), Overlay.Rgba(0.35f, 0.45f, 1), Canvas.BrushBlue, 0.5f, 0.12f, true);
        c.Arrow(40, 340, 40, 370, 24, 355);
        c.Arrow(472, 340, 472, 370, 488, 355);
        var sub = !open ? $"Clear {Legend.ExtraUnlock} of the first six courses to race here ({Legend.ClearedMain(Progress)} / {Legend.ExtraUnlock})"
            : $"Beat every rival of the course, one after another";
        c.Text(sub, 256, 404, 12, Canvas.White, 0.5f, 0.15f, 0.08f);
        Menu.Hint(c, "ARROWS: Select course    DECIDE: Rivals    BACK: Main menu");
    }

    private static void Stat(Canvas c, string label, string value, float x)
    {
        c.Text(label, x, 256, 10, Grey, 0, 0.1f);
        c.Text(value, x, 286, 19, Canvas.White, 0, 0.15f, 0, 0.3f);
    }

    /// <summary>Route/time/weather words of a rival's battle as the menus write them.</summary>
    private string[] Conditions(Legend.Entry e)
    {
        var course = CourseOf(e.Slot);
        if (course == null) return [];
        var (time, wet) = Legend.Conditions(e, course.Times, Progress);
        return [Catalog.DirectionName(course, e.Reverse), time.EndsWith("_NIT") ? "NIGHT" : "DAY", wet ? "WET" : "DRY"];
    }

    private string CarName(Legend.Entry e) => catalog.Cars.FirstOrDefault(c => c.Id == e.Rival.Car)?.Name ?? e.Rival.Car;

    private void RivalScreen(Canvas c)
    {
        var o = c.O;
        // the ladder: one chrome plate per rival, top to bottom in order
        var rowH = _rivals.Length > 4 ? 64 : 76;
        for (var i = 0; i < _rivals.Length; i++)
        {
            var e = _rivals[i];
            var y = 76 + i * rowH;
            var open = Legend.Unlocked(e, Progress);
            var beaten = Progress.Beaten(e.Key);
            var sel = i == _row;
            var a = Style.Ease((_t - 0.06f * i) / 0.2f);
            c.Plate(18 - (1 - a) * 40, y, 270, rowH - 10, sel ? 1 : 0.6f, a);
            // number badge
            Vector2 b0 = Vector2.Round(c.P(28, y + 8)), b1 = Vector2.Round(c.P(52, y + rowH - 18));
            o.Rect(b0, b1, Style.Fade(beaten ? Overlay.Rgba(0.75f, 0.5f, 0.05f) : open ? Overlay.Rgba(0.6f, 0.05f, 0.05f) : Overlay.Rgba(0.2f, 0.2f, 0.22f), a));
            c.Text($"{i + 1}", 40, y + rowH / 2f + 3, 18, Style.Fade(Canvas.White, a), 0.5f, 0.15f, 0.08f);
            var ink = Canvas.Shade(0.08f, 0.08f, 0.1f, 1, a * (open ? 1 : 0.45f));
            c.Fit(open || beaten ? e.Rival.Name : "? ? ?", 62, y + 22, 150, 0, ink, 0.15f, 0, 17);
            c.Fit(open ? $"{e.Rival.Team}   {CarName(e)}" : "Beat the rival above first", 62, y + 38, 160, 0, Canvas.Shade(0.15f, 0.15f, 0.18f, 1, a * 0.85f), 0.1f, 0, 10);
            // status tag on the right
            var (tag, fill) = beaten ? ("WIN", Overlay.Rgba(0.85f, 0.6f, 0.05f)) : !open ? ("LOCKED", Overlay.Rgba(0.25f, 0.25f, 0.27f))
                : _fresh.Contains(e.Key) ? ("NEW!", Overlay.Rgba(0.75f, 0.06f, 0.05f)) : ("CHALLENGE", Overlay.Rgba(0.05f, 0.15f, 0.65f));
            Vector2 t0 = Vector2.Round(c.P(222, y + 10)), t1 = Vector2.Round(c.P(280, y + 28));
            o.Rect(t0, t1, Style.Fade(fill, a));
            c.Fit(tag, 251, y + 24, 52, 0.5f, Style.Fade(Canvas.White, a), 0.15f, 0, 11);
            if (beaten)
            {
                // the original's cross over a beaten rival (r_selpeke), small, beside the tag
                var cx = c.P(266, y + rowH - 22);
                var r = 7 * c.S;
                o.Line(cx - new Vector2(r, r), cx + new Vector2(r, r), 3 * c.S, Style.Fade(Overlay.Rgba(0.85f, 0.08f, 0.06f), a));
                o.Line(cx + new Vector2(-r, r), cx + new Vector2(r, -r), 3 * c.S, Style.Fade(Overlay.Rgba(0.85f, 0.08f, 0.06f), a));
            }
        }
        if (_rivals.Length > 0)
        {
            var y = 76 + _row * rowH;
            c.Glow(13, y - 4, 293, y + rowH - 6, Canvas.Pulse(Theta));
        }
        // the selected rival's sheet
        var s = Selected;
        var known = Legend.Unlocked(s, Progress);
        c.Carbon(300, 72, 496, 420);
        c.Text(CourseName(s.Slot), 312, 96, 11, Grey, 0, 0.12f);
        if (!known)
        {
            c.Text("LOCKED", 398, 240, 26, Grey, 0.5f, 0.2f, 0, 0.4f);
            c.Text("Beat the rival above first", 398, 264, 11, Grey, 0.5f, 0.12f);
        }
        else
        {
            c.Fit(s.Rival.Name, 312, 126, 172, 0, Canvas.White, 0.18f, 0.06f, 22);
            c.Fit(s.Rival.Team, 312, 144, 172, 0, Overlay.Rgba(1, 0.35f, 0.25f), 0.12f, 0.04f, 12);
            c.Rule(308, 488, 156);
            Row(c, 178, "CAR", CarName(s));
            var car = catalog.Cars.FirstOrDefault(x => x.Id == s.Rival.Car);
            if (car != null) Row(c, 200, "SPEC", $"{car.Ps} PS  {car.Kg} kg  {car.Drive}");
            var cond = Conditions(s);
            if (cond.Length == 3)
            {
                Row(c, 222, "ROUTE", cond[0]);
                Row(c, 244, "TIME", cond[1]);
                Row(c, 266, "WEATHER", cond[2] + (Progress.Beaten(s.Key) && cond[2] == "WET" && !s.Wet ? "  (REMATCH)" : ""));
            }
            c.Text("LEVEL", 312, 290, 10, Grey, 0, 0.1f);
            for (var i = 0; i < 5; i++) Star(c, 398 + i * 16, 286, i < Legend.Stars(s));
            c.Rule(308, 488, 302);
            var rec = Progress.Get(s.Key);
            Row(c, 324, "RECORD", $"{rec.Wins} WIN{(rec.Wins == 1 ? "" : "S")}  {rec.Losses} LOSS{(rec.Losses == 1 ? "" : "ES")}");
            Row(c, 346, "BEST GAP", rec.BestGap > 0 ? FormattableString.Invariant($"+{rec.BestGap:0.00} s") : "-");
            if (Progress.Beaten(s.Key)) c.Lettering("CLEARED", 398, 392, 26, Overlay.Rgba(1, 0.85f, 0.3f), Overlay.Rgba(1, 0.38f, 0), 0.5f, 0.2f, false, true);
            else c.Text("RULE: FIRST TO THE GOAL", 398, 392, 11, Canvas.White, 0.5f, 0.12f, 0.06f);
        }
        News(c);
        Menu.Hint(c, "UP/DOWN: Select rival    DECIDE: Battle    BACK: Courses");
    }

    private static void Row(Canvas c, float y, string label, string value)
    {
        c.Text(label, 312, y, 10, Grey, 0, 0.1f);
        c.Fit(value, 484, y, 120, 1, Canvas.White, 0.12f, 0, 13);
    }

    /// <summary>A five-point star (filled gold or dim) centred at canvas (x, y).</summary>
    internal static void Star(Canvas c, float x, float y, bool on)
    {
        var col = on ? Gold : Overlay.Rgba(1, 1, 1, 0.2f);
        var ctr = c.P(x, y);
        Span<Vector2> p = stackalloc Vector2[10];
        for (var i = 0; i < 10; i++)
        {
            var ang = -MathF.PI / 2 + i * MathF.PI / 5;
            var r = (i % 2 == 0 ? 7 : 3f) * c.S;
            p[i] = ctr + new Vector2(MathF.Cos(ang) * r * 1.25f, MathF.Sin(ang) * r * 480 / 448);
        }
        for (var i = 0; i < 10; i++) c.O.Triangle(ctr, p[i], p[(i + 1) % 10], col);
    }

    /// <summary>What a battle unlocked: a red band over the bottom, a few seconds after the ladder opens.</summary>
    private void News(Canvas c)
    {
        if (_news > NewsHold || _newsLines.Count == 0) return;
        var a = Style.Ease(_news / 0.3f) * Style.Ease((NewsHold - _news) / 0.4f);
        var h = 14 + 16 * _newsLines.Count;
        float y1 = 424, y0 = y1 - h;
        c.O.Rect(new Vector2(0, MathF.Round(c.P(0, y0).Y)), new Vector2(c.Width, MathF.Round(c.P(0, y1).Y)), Style.Fade(Overlay.Rgba(0.7f, 0.04f, 0.03f, 0.92f), a));
        for (var i = 0; i < _newsLines.Count; i++) c.Text(_newsLines[i], 256, y0 + 20 + 16 * i, 13, Style.Fade(Canvas.White, a), 0.5f, 0.15f, 0.08f, 0.3f);
    }

    /// <summary>
    ///     The VS card before the car select: black bands wipe in over the rival's turning car, VS in red, the name in big
    ///     blue lettering, team and car, the course with its conditions and the rival's level below.
    /// </summary>
    private void CardScreen(Canvas c)
    {
        var o = c.O;
        var e = Selected;
        var wipe = Style.Ease(_t / 0.35f);
        float L = c.Left, R = c.Right;
        // top band: VS + name
        var x0 = R - (R - L) * wipe;
        o.Rect(Vector2.Round(c.P(x0, 64)), Vector2.Round(c.P(R, 178)), Overlay.Rgba(0, 0, 0, 0.82f));
        o.Rect(Vector2.Round(c.P(x0, 178)), Vector2.Round(c.P(R, 181)), Overlay.Rgba(0.8f, 0.07f, 0.06f));
        var shift = (1 - wipe) * 400;
        c.Lettering("VS", 40 + shift, 130, 64, Overlay.Rgba(1, 0.35f, 0.3f), Overlay.Rgba(0.75f, 0, 0), 0.5f, 0.2f, true);
        c.Lettering(e.Rival.Name, 90 + shift, 128, MathF.Min(44, 400 * c.Kx / o.Font!.Measure(e.Rival.Name, c.Ky)), Overlay.Rgba(0.35f, 0.45f, 1), Canvas.BrushBlue, 0, 0.12f, true);
        c.Text(e.Rival.Team, 92 + shift, 158, 15, Overlay.Rgba(1, 0.35f, 0.25f), 0, 0.15f, 0.08f, 0.3f);
        // bottom band: car, course and conditions, level
        var b = Style.Ease((_t - 0.15f) / 0.35f);
        var x1 = L + (R - L) * b;
        o.Rect(Vector2.Round(c.P(L, 318)), Vector2.Round(c.P(x1, 424)), Overlay.Rgba(0, 0, 0, 0.82f));
        o.Rect(Vector2.Round(c.P(L, 315)), Vector2.Round(c.P(x1, 318)), Overlay.Rgba(0.8f, 0.07f, 0.06f));
        if (b > 0.5f)
        {
            var fa = Style.Ease((b - 0.5f) * 2);
            c.Text("CAR", 24, 340, 10, Style.Fade(Grey, fa), 0, 0.1f);
            c.Fit(CarName(e), 24, 362, 280, 0, Style.Fade(Canvas.White, fa), 0.15f, 0.06f, 20);
            var cond = Conditions(e);
            c.Text("COURSE", 24, 384, 10, Style.Fade(Grey, fa), 0, 0.1f);
            c.Text($"{CourseName(e.Slot)}    {string.Join("  /  ", cond)}", 24, 404, 14, Style.Fade(Canvas.White, fa), 0, 0.15f, 0.06f);
            c.Text("LEVEL", 340, 340, 10, Style.Fade(Grey, fa), 0, 0.1f);
            for (var i = 0; i < 5; i++) Star(c, 348 + i * 18, 358, i < Legend.Stars(e));
            var rec = Progress.Get(e.Key);
            c.Text(Progress.Beaten(e.Key) ? $"REMATCH   {rec.Wins} W  {rec.Losses} L" : rec.Losses > 0 ? $"{rec.Losses} LOSS{(rec.Losses == 1 ? "" : "ES")} SO FAR" : "FIRST BATTLE",
                340, 388, 12, Style.Fade(Progress.Beaten(e.Key) ? Gold : Canvas.White, fa), 0, 0.15f, 0.06f);
            c.Text("FIRST TO THE GOAL WINS", 340, 406, 11, Style.Fade(Grey, fa), 0, 0.12f);
        }
        Menu.Hint(c, "DECIDE: Choose your car    BACK: Rivals");
    }
}
