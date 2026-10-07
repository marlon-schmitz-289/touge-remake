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
    public enum Result { None, TimeAttack, Records, Options, Quit, Guide, Legend, Story, Versus, Replay, SaveLoad, FreePlay }

    /// <summary>
    ///     Main menu in the original's drum order (sub_1F0E00, wraps 0 ↔ 6), English labels, plus the remake's VERSUS (split screen
    ///     and online, <see cref="Versus"/>) after TIME ATTACK and FREE PLAY (<see cref="FreePlay"/>) after it; on desktop builds the remake's QUIT GAME
    ///     last (<see cref="QuitPrompt"/>).
    /// </summary>
    public static readonly string[] Modes =
    [
        "LEGEND OF THE STREETS", "TIME ATTACK", "VERSUS", "FREE PLAY", "STORY", "REPLAY & RECORD", "IKETANI'S CAR GUIDE", "SAVE & LOAD", "OPTIONS",
        .. QuitPrompt.Available ? new[] { "QUIT GAME" } : [],
    ];

    /// <summary>What each mode leads to (Quit asks first).</summary>
    private static readonly Result[] ModeResults =
        [Result.Legend, Result.TimeAttack, Result.Versus, Result.FreePlay, Result.Story, Result.Replay, Result.Guide, Result.SaveLoad, Result.Options, Result.Quit];

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

    private float _t, _idle, _leave = -1, _roll = -1, _theta;
    private int _rollDir;
    private bool _fast;
    private Step _next;
    private Result _result;
    private readonly QuitPrompt _quit = new();

    public void Open(Step step)
    {
        Active = true;
        Enter(step);
    }

    /// <summary>QUIT GAME selected with its question open (--menu quit, screenshots).</summary>
    public void AskQuit()
    {
        Index = Modes.Length - 1;
        _quit.Show();
    }

    /// <summary>Skips the fade-in (screenshots).</summary>
    public void Settle() => _t = 10;

    private void Enter(Step step) =>
        (Current, _t, _idle, _leave, _roll, _fast, _result) = (step, 0, 0, -1, -1, false, Result.None);

    private void Leave(Step next, Result result = Result.None) => (_leave, _next, _result) = (0, next, result);

    /// <summary>One frame of menu input (<see cref="MenuKeys"/>); returns what the game should do once the fade-out is over.</summary>
    public Result Update((int X, int Y, bool Ok, bool Back) k, float dt)
    {
        if (!Active) return Result.None;
        dt = MathF.Min(dt, 1 / 20f);
        _t += dt;
        _theta = (_theta + dt * 60 * (_fast ? 40 : 5)) % 360;
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
                else if (k.Back && QuitPrompt.Available) Leave(Step.Title, Result.Quit);
                else if (_idle >= TitleIdle) Leave(Step.Logo);
                break;
            case Step.Modes:
                if (_roll >= 0) break;
                if (_quit.Open)
                {
                    _idle = 0;
                    if (_quit.Update(k, Sound)) Leave(Step.Modes, Result.Quit);
                }
                else if (k.Y != 0)
                {
                    Index = (Index + k.Y + Modes.Length) % Modes.Length;
                    (_rollDir, _roll) = (k.Y, 0);
                    Sound?.Invoke("SYS005");
                }
                else if (k.Ok && ModeResults[Index] == Result.Quit)
                {
                    Sound?.Invoke("SYS006");
                    _quit.Show();
                }
                else if (k.Ok)
                {
                    Sound?.Invoke("SYS006");
                    _fast = true;
                    Leave(Step.Modes, ModeResults[Index]);
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

    private readonly Canvas _c = new();

    public void Build(Overlay o, int width, int height)
    {
        o.Clear();
        if (!Active) return;
        var c = _c;
        c.Begin(o, width, height);
        switch (Current)
        {
            case Step.Boot:
                c.Fill(Canvas.Black);
                BootCard(c);
                break;
            case Step.Logo:
                c.Fill(Overlay.Rgba(0.96f, 0.96f, 0.96f));
                LogoCard(c);
                break;
            case Step.Disclaimer:
                c.Fill(Canvas.Black);
                string[] lines = ["This game is a work of fiction.", "When driving a real car, obey the traffic rules", "and always drive safely."];
                for (var i = 0; i < lines.Length; i++) c.Text(lines[i], 75, 185 + i * 32, 19, Canvas.White);
                break;
            case Step.Title:
                Night(c);
                Logo(c, 1, 0.5f + 0.5f * MathF.Exp(-_t * 1.5f)); // the logo blooms up as the title appears
                c.Fit("PRESS START BUTTON", 256, 306, 194, 0.5f, Style.Fade(Canvas.White, Canvas.Pulse(_theta)), 0.06f, 0.06f);
                Copyright(c);
                break;
            case Step.Modes:
                Night(c);
                Logo(c, 1, 0.35f);
                Drum(c);
                Copyright(c);
                _quit.Draw(c, _theta);
                break;
        }
        // 30-frame black fades in and out of every step
        c.Fade(_leave >= 0 ? Math.Clamp(_leave / Fade, 0, 1) : 1 - Math.Clamp(_t / Fade, 0, 1));
    }

    /// <summary>Title/menu backdrop: the night course behind, pulled towards the photo's deep blue-black.</summary>
    private static void Night(Canvas c) => c.Fill(Overlay.Rgba(0.015f, 0.025f, 0.06f, 0.3f));

    /// <summary>
    ///     The coloured logo where the original's sits (331×182 at (−166, −152) from the centre): heavy italic "INITIAL" in
    ///     place of the kanji and a taller "D", both yellow → red with a thin black and a white outline and red speed lines
    ///     along their feet, "Special Stage" in yellow → gold below. <paramref name="glow"/>: soft white light around the letters
    ///     (the original's photo has the logo glowing in it).
    /// </summary>
    private static void Logo(Canvas c, float a, float glow = 0)
    {
        uint F(uint col) => Style.Fade(col, a);
        const float skew = 0.22f;
        for (var i = 0; i < 3; i++) // speed lines behind the letters' feet
            c.O.Line(c.P(64 + i * 10, 172 + i * 5), c.P(420, 172 + i * 5), (2.2f - i * 0.5f) * c.S, F(Overlay.Rgba(0.9f, 0.1f, 0.05f, 0.9f)));
        Outlined(c, "D", 298, 194, 0, 0, skew, F(Overlay.Rgba(1, 0.9f, 0.25f)), F(Overlay.Rgba(0.92f, 0.12f, 0.04f)), a, 104, glow);
        Outlined(c, "INITIAL", 96, 186, 208, 0, skew, F(Overlay.Rgba(1, 0.9f, 0.25f)), F(Overlay.Rgba(0.92f, 0.12f, 0.04f)), a, 0, glow);
        Outlined(c, "Special Stage", 412, 246, 240, 1, 0.3f, F(Overlay.Rgba(1, 0.97f, 0.6f)), F(Overlay.Rgba(0.85f, 0.55f, 0.08f)), a, 0, glow);
    }

    /// <summary>Logo lettering: white outer and thin black inner outline, vertical gradient fill; fitted to <paramref name="w"/> or cap height <paramref name="h"/>.</summary>
    private static void Outlined(Canvas c, string text, float x, float y, float w, float align, float skew, uint top, uint bottom, float a, float h = 0, float glow = 0)
    {
        var o = c.O;
        var size = h > 0 ? h * c.Ky / o.Font!.CapHeight : w * c.Kx / o.Font!.Measure(text, 1);
        if (glow > 0) o.Text(text, c.P(x, y), size, Style.Fade(Overlay.Rgba(0.9f, 0.94f, 1), a * glow), align, size * 0.04f, size, skew); // soft: as wide as the font's range allows
        o.Text(text, c.P(x, y), size, Style.Fade(Canvas.White, a), align, size * 0.06f, 0, skew);
        o.Text(text, c.P(x, y), size, Style.Fade(Canvas.Black, a), align, size * 0.028f, 0, skew);
        o.Text(text, c.P(x, y), size, top, align, 0, 0, skew, bottom);
    }

    private static void Copyright(Canvas c) =>
        c.Text("(C) Shuichi Shigeno / Kodansha   (C) SEGA ROSSO / SEGA, 2003", 500, 440, 9, Overlay.Rgba(1, 1, 1, 0.85f), 1);

    private void BootCard(Canvas c)
    {
        c.Carbon(50, 130, 462, 350);
        string[] lines = SaveFound
            ? ["Save data found.", "Your settings and best times are loaded."]
            : ["No save data found.", "A new save file is created when you change settings."];
        for (var i = 0; i < lines.Length; i++) c.Text(lines[i], 76, 172 + i * 22, 15, Canvas.White);
        if (_t < 1.2f) c.Text("Now checking...", 165, 415, 13, Overlay.Rgba(1, 1, 1, 0.8f));
    }

    private static void LogoCard(Canvas c)
    {
        var ink = Overlay.Rgba(0.1f, 0.1f, 0.12f);
        c.Fit("Based on", 256, 172, 70, 0.5f, ink, 0.2f);
        c.Fit("INITIAL D SPECIAL STAGE", 256, 215, 300, 0.5f, ink, 0.14f);
        c.Fit("SEGA / SEGA ROSSO, 2003", 256, 246, 170, 0.5f, ink);
        c.Fit("Unofficial remake - all game data is read from your own disc", 256, 330, 300, 0.5f, Overlay.Rgba(0.35f, 0.35f, 0.38f));
    }

    /// <summary>The main-menu drum: plates at y +63/+111/+159 from the centre, selected row full white, the others tinted 48/255.</summary>
    private void Drum(Canvas c)
    {
        var o = c.O;
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
            c.Plate(145, y, 222, 33, lit, a);
            var label = Modes[(Index + rel + 2 * Modes.Length) % Modes.Length];
            var ink = Style.Fade(Canvas.Shade(0.05f, 0.05f, 0.06f, lit), a);
            var size = MathF.Min(21 * c.Ky, 196 * c.Kx / o.Font!.Measure(label, 1));
            var at = c.P(256, y + 16.5f) + new Vector2(0, o.Font.CapHeight * size / 2);
            o.Text(label, at, size, Style.Fade(Canvas.Shade(1, 1, 1, lit), a), 0.5f, MathF.Max(1.5f, size * 0.09f), size * 0.05f, Style.Slant); // white halo
            o.Text(label, at, size, ink, 0.5f, 0, 0, Style.Slant);
        }
        // pulsing glow frame around the selected plate, yellow arrows beside it
        c.Glow(138, 328, 374, 375, Canvas.Pulse(_theta));
        c.Arrow(393, 348, 407, 348, 400, 333); // ▲
        c.Arrow(393, 354, 407, 354, 400, 369); // ▼
    }
}

