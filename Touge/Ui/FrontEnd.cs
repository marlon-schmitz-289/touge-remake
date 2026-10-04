using System.Numerics;
using Kansei.Graphics;

namespace Touge.Ui;

/// <summary>
///     Front end rebuilt after the original (title module 0x1E28F0, main menu 0x1F0950) with vector shapes and our font,
///     no original menu textures: boot notice (save data) → presented-by card → fiction disclaimer → title (PRESS START)
///     → main menu, a 3-row drum of the original's 7 modes. Layout in the original's 512×448 canvas, shown 4:3 in the
///     middle of the screen (the 3D scene behind fills any width). Timings from the code at 60 fps: 30-frame fades,
///     cards 181 frames, title back to the attract cards after 601 idle frames, main menu back to the title after 1801;
///     cursor glow alpha 80 + 175·(1 + sin θ)/2 with θ += 5°/frame (40° after a decision); the drum fades the leaving row
///     out (38/255 per frame), slides 48 px at 9 px per frame and fades the new row in, input locked meanwhile.
///     Sounds by SYSSE name through <see cref="Sound"/>, music per step in <see cref="Music"/>.
/// </summary>
public sealed class FrontEnd
{
    public enum Step { Boot, Logo, Disclaimer, Title, Modes }
    public enum Result { None, TimeAttack, Options, Quit }

    /// <summary>Main menu in the original's drum order (sub_1F0E00, wraps 0 ↔ 6), English labels.</summary>
    public static readonly string[] Modes =
        ["LEGEND OF THE STREETS", "TIME ATTACK", "STORY", "REPLAY & RECORD", "IKETANI'S CAR GUIDE", "SAVE & LOAD", "OPTIONS"];

    /// <summary>What each mode leads to in this build (None: not rebuilt yet, deciding it beeps).</summary>
    private static readonly Result[] ModeResults = [Result.None, Result.TimeAttack, Result.None, Result.None, Result.None, Result.None, Result.Options];

    public const float Fade = 30 / 60f, CardHold = 181 / 60f, BootHold = 2.5f, TitleIdle = 601 / 60f, ModesIdle = 1801 / 60f;
    private const float RollFade = 7 / 60f, RollSlide = 48 / 9f / 60, Roll = 2 * RollFade + RollSlide;

    public bool Active { get; private set; }
    public Step Current { get; private set; }
    /// <summary>Selected mode (stays when the game hands back).</summary>
    public int Index { get; private set; }
    /// <summary>SYSSE sound by name: SYS005 cursor, SYS006 decide, BEEP001 back/blocked, sys002 PRESS START.</summary>
    public Action<string>? Sound { get; set; }
    /// <summary>Boot notice: a save file (settings, best times) was there.</summary>
    public bool SaveFound { get; set; }

    /// <summary>
    ///     BGM.AFS track of the current step, null = silence. The original plays none on title and main menu (only the
    ///     opening movie's own audio); the remake has no movie and plays "1. GAMBLE RUMBLE" there instead.
    /// </summary>
    public string? Music => Active && Current is Step.Title or Step.Modes ? "gam.adx" : null;

    private float _t, _idle, _leave = -1, _roll = -1, _blocked = -1, _theta;
    private int _rollDir;
    private bool _fast;
    private Step _next;
    private Result _result;

    public void Open(Step step)
    {
        Active = true;
        Enter(step);
    }

    /// <summary>Skips the fade-in (screenshots).</summary>
    public void Settle() => _t = 10;

    private void Enter(Step step) =>
        (Current, _t, _idle, _leave, _roll, _blocked, _fast, _result) = (step, 0, 0, -1, -1, -1, false, Result.None);

    private void Leave(Step next, Result result = Result.None) => (_leave, _next, _result) = (0, next, result);

    /// <summary>One frame of menu input (<see cref="MenuKeys"/>); returns what the game should do once the fade-out is over.</summary>
    public Result Update((int X, int Y, bool Ok, bool Back) k, float dt)
    {
        if (!Active) return Result.None;
        _t += dt;
        _theta = (_theta + dt * 60 * (_fast ? 40 : 5)) % 360;
        if (_blocked >= 0) _blocked += dt;
        if (_leave >= 0)
        {
            if ((_leave += dt) < Fade) return Result.None;
            if (_result == Result.None) Enter(_next);
            else Active = false;
            return _result;
        }
        if (_roll >= 0 && (_roll += dt) >= Roll) _roll = -1;
        _idle = k.X != 0 || k.Y != 0 || k.Ok || k.Back ? 0 : _idle + dt;
        switch (Current)
        {
            case Step.Boot:
                if (k.Ok) Sound?.Invoke("SYS006");
                if (k.Ok || _t >= Fade + BootHold) Leave(Step.Logo);
                break;
            case Step.Logo or Step.Disclaimer:
                if (k.Ok) Leave(Step.Title); // START skips the attract cards
                else if (_t >= Fade + CardHold) Leave(Current == Step.Logo ? Step.Disclaimer : Step.Title);
                break;
            case Step.Title:
                if (k.Ok)
                {
                    Sound?.Invoke("sys002");
                    _fast = true;
                    Leave(Step.Modes);
                }
                else if (k.Back) Leave(Step.Title, Result.Quit);
                else if (_idle >= TitleIdle) Leave(Step.Logo);
                break;
            case Step.Modes:
                if (_roll >= 0) break;
                if (k.Y != 0)
                {
                    Index = (Index + k.Y + Modes.Length) % Modes.Length;
                    (_rollDir, _roll) = (k.Y, 0);
                    Sound?.Invoke("SYS005");
                }
                else if (k.Ok && ModeResults[Index] is var r and not Result.None)
                {
                    Sound?.Invoke("SYS006");
                    _fast = true;
                    Leave(Step.Modes, r);
                }
                else if (k.Ok)
                {
                    Sound?.Invoke("BEEP001");
                    _blocked = 0;
                }
                else if (k.Back)
                {
                    Sound?.Invoke("BEEP001");
                    Leave(Step.Title);
                }
                else if (_idle >= ModesIdle) Leave(Step.Title);
                break;
        }
        return Result.None;
    }

    // ---------------------------------------------------------------- drawing

    private float _s, _ox, _oy;

    /// <summary>Original canvas (512×448, top-left origin) → screen pixels of the centred 4:3 frame.</summary>
    private Vector2 P(float x, float y) => new(_ox + x * 1.25f * _s, _oy + y * (480f / 448) * _s);

    private float Kx => 1.25f * _s;
    private float Ky => 480f / 448 * _s;

    private static readonly uint White = Overlay.Rgba(1, 1, 1), Black = Overlay.Rgba(0, 0, 0), Yellow = Overlay.Rgba(1, 1, 0);

    /// <summary>Pulse of the cursor glow and PRESS START, 80..255 as 0..1.</summary>
    private float Pulse => (80 + 175 * (1 + MathF.Cos(_theta * MathF.PI / 180)) / 2) / 255;

    public void Build(Overlay o, int width, int height)
    {
        o.Clear();
        if (!Active) return;
        _s = MathF.Min(width / 640f, height / 480f);
        (_ox, _oy) = ((width - 640 * _s) / 2, (height - 480 * _s) / 2);
        Vector2 full = new(width, height);
        switch (Current)
        {
            case Step.Boot:
                o.Rect(Vector2.Zero, full, Black);
                BootCard(o);
                break;
            case Step.Logo:
                o.Rect(Vector2.Zero, full, Overlay.Rgba(0.96f, 0.96f, 0.96f));
                LogoCard(o);
                break;
            case Step.Disclaimer:
                o.Rect(Vector2.Zero, full, Black);
                string[] lines = ["This game is a work of fiction.", "When driving a real car, obey the traffic rules", "and always drive safely."];
                for (var i = 0; i < lines.Length; i++) o.Text(lines[i], P(75, 185 + i * 32), 19 * Ky, White);
                break;
            case Step.Title:
                Night(o, full);
                Ghost(o, 256, 150, 420, 0.8f + 1.2f * MathF.Exp(-_t * 1.5f)); // the logo blooms up as the title appears
                Logo(o, 1);
                Fit(o, "PRESS START BUTTON", 256, 306, 194, 0.5f, Style.Fade(White, Pulse), 0.06f, true);
                Copyright(o);
                break;
            case Step.Modes:
                Night(o, full);
                Ghost(o, 160, 125, 190, 0.5f);
                Logo(o, 1);
                Drum(o);
                Copyright(o);
                break;
        }
        // 30-frame black fades in and out of every step
        var fade = _leave >= 0 ? Math.Clamp(_leave / Fade, 0, 1) : 1 - Math.Clamp(_t / Fade, 0, 1);
        if (fade <= 0) return;
        o.Rect(Vector2.Zero, full, Style.Fade(Black, fade));
        o.FadeText(1 - fade);
    }

    /// <summary>Text fitted to <paramref name="w"/> canvas pixels (the width of the original's baked label), baseline y; optional black outline.</summary>
    private void Fit(Overlay o, string text, float x, float y, float w, float align, uint color, float skew = 0, bool outline = false)
    {
        var size = w * Kx / o.Font!.Measure(text, 1);
        if (outline) o.Text(text, P(x, y), size, Style.Fade(Black, (color >> 24) / 255f), align, MathF.Max(1, size * 0.06f), 0, skew);
        o.Text(text, P(x, y), size, color, align, 0, 0, skew);
    }

    /// <summary>Title/menu backdrop: the night course behind, pulled towards the photo's deep blue-black.</summary>
    private static void Night(Overlay o, Vector2 full) => o.Rect(Vector2.Zero, full, Overlay.Rgba(0.015f, 0.025f, 0.06f, 0.3f));

    /// <summary>The big soft white "D" logo glowing in the photo, centred at (x, y), <paramref name="w"/> wide.</summary>
    private void Ghost(Overlay o, float x, float y, float w, float a)
    {
        const string text = "INITIAL D";
        var size = w * Kx / o.Font!.Measure(text, 1);
        var at = P(x, y) + new Vector2(0, o.Font.CapHeight * size / 2);
        o.Text(text, at, size, Style.Fade(Overlay.Rgba(0.85f, 0.9f, 1, 0.3f), a), 0.5f, size * 0.04f, size * 0.2f, 0.2f);
        o.Text(text, at, size, Style.Fade(Overlay.Rgba(0.9f, 0.93f, 1, 0.45f), a), 0.5f, 0, size * 0.06f, 0.2f);
    }

    /// <summary>
    ///     The coloured logo where the original's sits (331×182 at (−166, −152) from the centre): heavy italic "INITIAL" in
    ///     place of the kanji and a taller "D", both yellow → red with a thin black and a white outline and red speed lines
    ///     along their feet, "Special Stage" in yellow → gold below.
    /// </summary>
    private void Logo(Overlay o, float a)
    {
        uint F(uint c) => Style.Fade(c, a);
        const float skew = 0.22f;
        for (var i = 0; i < 3; i++) // speed lines behind the letters' feet
            o.Line(P(64 + i * 10, 172 + i * 5), P(420, 172 + i * 5), (2.2f - i * 0.5f) * _s, F(Overlay.Rgba(0.9f, 0.1f, 0.05f, 0.9f)));
        Outlined(o, "D", 298, 194, 0, 0, skew, F(Overlay.Rgba(1, 0.9f, 0.25f)), F(Overlay.Rgba(0.92f, 0.12f, 0.04f)), a, 104);
        Outlined(o, "INITIAL", 96, 186, 208, 0, skew, F(Overlay.Rgba(1, 0.9f, 0.25f)), F(Overlay.Rgba(0.92f, 0.12f, 0.04f)), a);
        Outlined(o, "Special Stage", 412, 246, 240, 1, 0.3f, F(Overlay.Rgba(1, 0.97f, 0.6f)), F(Overlay.Rgba(0.85f, 0.55f, 0.08f)), a);
    }

    /// <summary>Logo lettering: white outer and thin black inner outline, vertical gradient fill; fitted to <paramref name="w"/> or cap height <paramref name="h"/>.</summary>
    private void Outlined(Overlay o, string text, float x, float y, float w, float align, float skew, uint top, uint bottom, float a, float h = 0)
    {
        var size = h > 0 ? h * Ky / o.Font!.CapHeight : w * Kx / o.Font!.Measure(text, 1);
        o.Text(text, P(x, y), size, Style.Fade(White, a), align, size * 0.06f, 0, skew);
        o.Text(text, P(x, y), size, Style.Fade(Black, a), align, size * 0.028f, 0, skew);
        o.Text(text, P(x, y), size, top, align, 0, 0, skew, bottom);
    }

    private void Copyright(Overlay o) =>
        o.Text("(C) Shuichi Shigeno / Kodansha   (C) SEGA ROSSO / SEGA, 2003", P(500, 440), 9 * Ky, Overlay.Rgba(1, 1, 1, 0.85f), 1);

    private void BootCard(Overlay o)
    {
        Carbon(o, 50, 130, 462, 350);
        string[] lines = SaveFound
            ? ["Save data found.", "Your settings and best times are loaded."]
            : ["No save data found.", "A new save file is created when you change settings."];
        for (var i = 0; i < lines.Length; i++) o.Text(lines[i], P(76, 172 + i * 22), 15 * Ky, White);
        if (_t < 1.2f) o.Text("Now checking...", P(165, 415), 13 * Ky, Overlay.Rgba(1, 1, 1, 0.8f));
    }

    private void LogoCard(Overlay o)
    {
        var ink = Overlay.Rgba(0.1f, 0.1f, 0.12f);
        Fit(o, "Based on", 256, 172, 70, 0.5f, ink, 0.2f);
        Fit(o, "INITIAL D SPECIAL STAGE", 256, 215, 300, 0.5f, ink, 0.14f);
        Fit(o, "SEGA / SEGA ROSSO, 2003", 256, 246, 170, 0.5f, ink);
        Fit(o, "Unofficial remake - all game data is read from your own disc", 256, 330, 300, 0.5f, Overlay.Rgba(0.35f, 0.35f, 0.38f));
    }

    /// <summary>The main-menu drum: plates at y +63/+111/+159 from the centre, selected row full white, the others tinted 48/255.</summary>
    private void Drum(Overlay o)
    {
        float f1 = 1, p = 1, f3 = 1;
        if (_roll >= 0)
        {
            f1 = Math.Clamp(_roll / RollFade, 0, 1);
            p = Math.Clamp((_roll - RollFade) / RollSlide, 0, 1);
            f3 = Math.Clamp((_roll - RollFade - RollSlide) / RollFade, 0, 1);
        }
        var d = _roll >= 0 ? _rollDir : 0;
        for (var rel = -2; rel <= 2; rel++)
        {
            var a = d != 0 && rel == -2 * d ? 1 - f1 : d != 0 && rel == d ? f3 : Math.Abs(rel) <= 1 ? 1 : 0;
            if (a <= 0) continue;
            var slot = rel + d * (1 - p);
            var lit = 48 / 255f + (1 - 48 / 255f) * MathF.Max(0, 1 - MathF.Abs(slot));
            var y = 224 + 111 + slot * 48;
            Plate(o, 145, y, 222, 33, lit, a);
            var label = Modes[(Index + rel + 2 * Modes.Length) % Modes.Length];
            var ink = Style.Fade(Shade(0.05f, 0.05f, 0.06f, lit), a);
            var size = MathF.Min(21 * Ky, 196 * Kx / o.Font!.Measure(label, 1));
            var at = P(256, y + 16.5f) + new Vector2(0, o.Font.CapHeight * size / 2);
            o.Text(label, at, size, Style.Fade(Shade(1, 1, 1, lit), a), 0.5f, MathF.Max(1.5f, size * 0.09f), size * 0.05f, Style.Slant); // white halo
            o.Text(label, at, size, ink, 0.5f, 0, 0, Style.Slant);
        }
        // pulsing glow frame around the selected plate, yellow arrows beside it
        var pulse = Pulse;
        Vector2 min = P(138, 328), max = P(374, 375);
        for (var i = 3; i >= 1; i--) RoundRect(o, min, max, 7 * Kx, (3 + i * 5) * _s, Style.Fade(Overlay.Rgba(1, 1, 0.2f, 0.08f), pulse));
        RoundRect(o, min, max, 7 * Kx, 4.5f * _s, Overlay.Rgba(0.4f + 0.6f * pulse, 0.4f + 0.6f * pulse, 0.25f * (1 - pulse)));
        Arrow(o, P(393, 348), P(407, 348), P(400, 333)); // ▲
        Arrow(o, P(393, 354), P(407, 354), P(400, 369)); // ▼
        if (_blocked is >= 0 and < 1.6f)
            o.Text("NOT IN THIS BUILD YET", P(256, 425), 13 * Ky, Style.Fade(White, Math.Clamp((1.6f - _blocked) * 3, 0, 1)), 0.5f, 0.5f * _s, 0, Style.Slant);
    }

    private static uint Shade(float r, float g, float b, float k, float a = 1) => Overlay.Rgba(r * k, g * k, b * k, a);

    /// <summary>Brushed-chrome plate with a dark rim, horizontal streaks and four screws, tinted by <paramref name="lit"/>.</summary>
    private void Plate(Overlay o, float x, float y, float w, float h, float lit, float a)
    {
        uint C(float v, float alpha = 1) => Style.Fade(Shade(v, v, v * 1.02f, lit), a * alpha);
        Vector2 min = Vector2.Round(P(x, y)), max = Vector2.Round(P(x + w, y + h));
        o.Rect(min, max, C(0.32f));
        Vector2 i0 = min + new Vector2(2, 2) * _s, i1 = max - new Vector2(2, 2) * _s;
        var mid = MathF.Round((i0.X + i1.X) / 2);
        o.RectGradient(i0, new Vector2(mid, i1.Y), C(0.62f), C(0.92f));
        o.RectGradient(new Vector2(mid, i0.Y), i1, C(0.92f), C(0.68f));
        for (var k = 0; k < 12; k++)
        {
            var yy = i0.Y + (i1.Y - i0.Y) * (k + 0.5f) / 12;
            o.Line(new Vector2(i0.X + 1, yy), new Vector2(i1.X - 1, yy), 1, k % 3 == 0 ? Style.Fade(Overlay.Rgba(1, 1, 1, 0.18f), a * lit) : Style.Fade(Overlay.Rgba(0, 0, 0, 0.06f), a));
        }
        o.Line(new Vector2(i0.X, i0.Y), new Vector2(i1.X, i0.Y), 1, C(1, 0.8f));
        o.Line(new Vector2(i0.X, i1.Y), new Vector2(i1.X, i1.Y), 1, C(0.4f));
        foreach (var (sx, sy) in new[] { (x + 8, y + 7), (x + w - 8, y + 7), (x + 8, y + h - 7), (x + w - 8, y + h - 7) }) Screw(o, P(sx, sy), 3.2f * _s, lit, a);
    }

    private static void Screw(Overlay o, Vector2 c, float r, float lit, float a)
    {
        o.Disc(c, r, Style.Fade(Shade(0.3f, 0.3f, 0.3f, lit), a));
        o.Disc(c, r * 0.75f, Style.Fade(Shade(0.78f, 0.78f, 0.8f, lit), a));
        var w = MathF.Max(1, r * 0.3f);
        o.Line(c - new Vector2(r * 0.5f, 0), c + new Vector2(r * 0.5f, 0), w, Style.Fade(Shade(0.25f, 0.25f, 0.25f, lit), a));
        o.Line(c - new Vector2(0, r * 0.5f), c + new Vector2(0, r * 0.5f), w, Style.Fade(Shade(0.25f, 0.25f, 0.25f, lit), a));
    }

    /// <summary>Carbon-fibre panel: near-black weave (alternating diagonal shades), dark-steel frame, screws in the corners.</summary>
    private void Carbon(Overlay o, float x0, float y0, float x1, float y1)
    {
        Vector2 min = Vector2.Round(P(x0, y0)), max = Vector2.Round(P(x1, y1));
        o.Rect(min, max, Overlay.Rgba(0.33f, 0.34f, 0.36f));
        Vector2 i0 = min + new Vector2(3, 3) * _s, i1 = max - new Vector2(3, 3) * _s;
        o.Rect(i0, i1, Overlay.Rgba(0.07f, 0.07f, 0.075f));
        var cell = MathF.Max(4, 6 * _s);
        uint c0 = Overlay.Rgba(0.11f, 0.11f, 0.115f), c1 = Overlay.Rgba(0.06f, 0.06f, 0.065f);
        var row = 0;
        for (var y = i0.Y; y < i1.Y; y += cell, row++)
        for (var (x, col) = (i0.X, 0); x < i1.X; x += cell, col++)
        {
            Vector2 a = new(x, y), b = Vector2.Min(new Vector2(x + cell, y + cell), i1);
            if ((row + col) % 2 == 0) o.RectGradient(a, b, c0, c1);
            else o.RectGradient(a, b, c1, c0);
        }
        foreach (var c in new[] { min + new Vector2(10, 10) * _s, new Vector2(max.X - 10 * _s, min.Y + 10 * _s), new Vector2(min.X + 10 * _s, max.Y - 10 * _s), max - new Vector2(10, 10) * _s })
            Screw(o, c, 3.5f * _s, 0.8f, 1);
    }

    /// <summary>Rounded rectangle outline (lines + quarter arcs).</summary>
    private static void RoundRect(Overlay o, Vector2 min, Vector2 max, float r, float width, uint color)
    {
        o.Line(new Vector2(min.X + r, min.Y), new Vector2(max.X - r, min.Y), width, color);
        o.Line(new Vector2(min.X + r, max.Y), new Vector2(max.X - r, max.Y), width, color);
        o.Line(new Vector2(min.X, min.Y + r), new Vector2(min.X, max.Y - r), width, color);
        o.Line(new Vector2(max.X, min.Y + r), new Vector2(max.X, max.Y - r), width, color);
        o.Arc(new Vector2(min.X + r, min.Y + r), r, width, color, MathF.PI, 1.5f * MathF.PI, 4);
        o.Arc(new Vector2(max.X - r, min.Y + r), r, width, color, 1.5f * MathF.PI, 2 * MathF.PI, 4);
        o.Arc(new Vector2(max.X - r, max.Y - r), r, width, color, 0, 0.5f * MathF.PI, 4);
        o.Arc(new Vector2(min.X + r, max.Y - r), r, width, color, 0.5f * MathF.PI, MathF.PI, 4);
    }

    /// <summary>Yellow triangle with a dark outline.</summary>
    private void Arrow(Overlay o, Vector2 a, Vector2 b, Vector2 c)
    {
        o.Triangle(a, b, c, Yellow);
        var w = 1.5f * _s;
        var dark = Overlay.Rgba(0.15f, 0.1f, 0);
        o.Line(a, b, w, dark);
        o.Line(b, c, w, dark);
        o.Line(c, a, w, dark);
    }
}
