using System.Numerics;
using Kansei.Graphics;
using Touge.Formats;

namespace Touge.Ui;

/// <summary>
///     OPTIONS in the original's two-level style: a section list of chrome plates (OPSL: GAME SETTING, SCREEN, SOUND,
///     CONTROLLER …) and per section a page of rows (OPGM: dark-steel label tab, chrome value plate with engraved values,
///     the active one lit; help text in the carbon panel below). Values change live (<see cref="Result.Changed"/>: the
///     game applies and saves the settings).
///     <para>
///         Extension point: <see cref="Pages"/> is a plain list — add a page (<c>Pages.Add(new Page("TITLE", "Caption") { Rows = { … } })</c>),
///         or rows to an existing one (<c>Find("HUD")!.Rows.Add(Row.Choice(…))</c>); a page with its own screen (key remapping,
///         music list) sets <see cref="Page.Input"/> and <see cref="Page.Draw"/> instead of rows.
///     </para>
/// </summary>
public sealed class Options
{
    public enum Result { None, Changed, Leave }

    /// <summary>One option: <see cref="Values"/> (null = 0..10 slider), the selected index, its setter and the help lines for the panel.</summary>
    public sealed record Row(string Label, Func<string[]>? Values, Func<int> Get, Action<int> Set, Func<string[]> Help)
    {
        /// <summary>A choice among fixed or live (<paramref name="values"/> re-read every frame) values.</summary>
        public static Row Choice(string label, Func<string[]> values, Func<int> get, Action<int> set, params string[] help) =>
            new(label, values, get, set, () => help);

        public static Row Choice(string label, string[] values, Func<int> get, Action<int> set, params string[] help) =>
            Choice(label, () => values, get, set, help);

        public static Row Toggle(string label, Func<bool> get, Action<bool> set, params string[] help) =>
            Choice(label, ["ON", "OFF"], () => get() ? 0 : 1, i => set(i == 0), help);

        /// <summary>0..1 in tenths, drawn as 10 blocks.</summary>
        public static Row Slider(string label, Func<float> get, Action<float> set, params string[] help) =>
            new(label, null, () => (int)MathF.Round(get() * 10), i => set(i / 10f), () => help);
    }

    /// <summary>A section: its rows, or a screen of its own (<see cref="Input"/> + <see cref="Draw"/>; Leave returns to the list).</summary>
    public sealed class Page(string title, string caption)
    {
        public string Title => title;
        public string Caption => caption;
        public List<Row> Rows { get; init; } = [];
        public Func<(int X, int Y, bool Ok, bool Back), Action<string>?, Result>? Input { get; init; }
        public Action<Canvas, float>? Draw { get; init; }
    }

    public List<Page> Pages { get; }
    /// <summary>Window sizes to offer (the game fills in the display's modes); the current size is always one of them.</summary>
    public Func<IReadOnlyList<(int W, int H)>> Resolutions { get; set; } = () => [(1280, 720), (1600, 900), (1920, 1080), (2560, 1440)];

    /// <summary>The open page, null = the section list.</summary>
    public Page? Current { get; private set; }
    public int Section { get; private set; }
    public int Selected { get; private set; }
    private int _top;
    private bool _padHelp;

    /// <summary>Visible rows of a page; more scroll.</summary>
    public const int VisibleRows = 9;

    public Options(Settings s) => Pages = Defaults(s);

    /// <summary>Header text: OPTIONS on the list, the page title inside a page.</summary>
    public string Title => Current?.Title ?? "OPTIONS";

    public Page? Find(string title) => Pages.Find(p => p.Title == title);

    /// <summary>Back to the section list (entering OPTIONS from the main menu).</summary>
    public void Open() => (Current, Selected, _top) = (null, 0, 0);

    /// <summary>Opens a page directly (<c>--menu options:sound</c>); false if there is none of that title.</summary>
    public bool OpenPage(string title)
    {
        var i = Pages.FindIndex(p => p.Title.Replace(" ", "").Equals(title.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        (Section, Current, Selected, _top) = (i, Pages[i], 0, 0);
        return true;
    }

    private static int Wrap(int i, int n) => n == 0 ? 0 : (i % n + n) % n;

    public Result Update((int X, int Y, bool Ok, bool Back) k, Action<string>? sound)
    {
        if (Current == null)
        {
            if (k.Y != 0)
            {
                Section = Wrap(Section + k.Y, Pages.Count);
                sound?.Invoke("SYS005");
            }
            else if (k.Back) return Result.Leave;
            else if (k.Ok && Pages.Count > 0)
            {
                sound?.Invoke("SYS006");
                (Current, Selected, _top) = (Pages[Section], 0, 0);
            }
            return Result.None;
        }
        if (Current.Input is { } input)
        {
            if (input(k, sound) != Result.Leave) return Result.None;
            sound?.Invoke("BEEP001");
            Current = null;
            return Result.None;
        }
        var rows = Current.Rows;
        if (k.Back)
        {
            sound?.Invoke("BEEP001");
            Current = null;
        }
        else if (k.Y != 0 && rows.Count > 0)
        {
            Selected = Wrap(Selected + k.Y, rows.Count);
            _top = Math.Clamp(_top, Math.Max(0, Selected - VisibleRows + 1), Selected);
            sound?.Invoke("SYS005");
        }
        else if ((k.X != 0 || k.Ok) && rows.Count > 0 && Step(rows[Selected], k.X != 0 ? k.X : 1))
        {
            sound?.Invoke("SYS005");
            return Result.Changed;
        }
        return Result.None;
    }

    /// <summary>Steps a row's value by <paramref name="step"/> (choices wrap, sliders stop at the ends); false if nothing changed.</summary>
    public static bool Step(Row row, int step)
    {
        var cur = row.Get();
        int next;
        if (row.Values is { } values)
        {
            var n = values().Length;
            if (n < 2) return false;
            next = Wrap(Math.Clamp(cur, 0, n - 1) + step, n);
        }
        else next = Math.Clamp(cur + step, 0, 10);
        if (next == cur) return false;
        row.Set(next);
        return true;
    }

    // ---------------------------------------------------------------- default pages

    private List<Page> Defaults(Settings s)
    {
        static string Pct(int p) => $"{p} %";
        string[] Sizes() => [.. ResolutionList(s).Select(r => $"{r.W} x {r.H}")];
        var displays = OperatingSystem.IsMacOS() ? new[] { "WINDOW", "BORDERLESS" } : ["WINDOW", "BORDERLESS", "FULLSCREEN"];
        string[] presets() => s.QualityPreset == Settings.Preset.Custom ? ["LOW", "MEDIUM", "HIGH", "ULTRA", "CUSTOM"] : ["LOW", "MEDIUM", "HIGH", "ULTRA"];
        int[] fovs = [.. Enumerable.Range(0, (Settings.FovMax - Settings.FovMin) / 5 + 1).Select(i => Settings.FovMin + 5 * i)];
        return
        [
            new Page("GAME SETTING", "Units, transmission, driving assists, camera and car stickers.")
            {
                Rows =
                {
                    Row.Choice("UNITS", ["KM/H", "MPH"], () => s.Mph ? 1 : 0, i => s.Mph = i == 1, "Speed on the car's instrument cluster."),
                    Row.Choice("TRANSMISSION", ["AT", "MT"], () => s.Manual ? 1 : 0, i => s.Manual = i == 1,
                        "Gearbox preselected in the car select.", "AT: automatic.  MT: shift yourself (SHIFT/CTRL, bumpers)."),
                    Row.Choice("STEER ASSIST", ["OFF", "LOW", "FULL"], () => s.SteerAssist, i => s.SteerAssist = i,
                        "Counter-steer into a slide by itself.", "OFF: you catch every slide.  Applies from the next run."),
                    Row.Choice("DRIFT ASSIST", ["LOW", "NORMAL", "HIGH"], () => s.DriftAssist, i => s.DriftAssist = i,
                        "How strongly slides are held steady (no spins).", "LOW: livelier, easier to spin.  Applies from the next run."),
                    Row.Choice("CAMERA", ["CHASE", "BUMPER"], () => s.BumperCam ? 1 : 0, i => s.BumperCam = i == 1,
                        "Camera at the start of a run (C switches while driving)."),
                    Row.Choice("FIELD OF VIEW", [.. fovs.Select(f => $"{f}°")], () => Array.IndexOf(fovs, s.Fov), i => s.Fov = fovs[i],
                        "Chase camera view angle at standstill; it widens with speed."),
                    Row.Slider("CAMERA SHAKE", () => s.CameraShake, v => s.CameraShake = v, "Camera shake on wall hits."),
                    Row.Choice("STICKERS", ["ANIME", "STOCK", "NONE"], () => s.Livery switch { Livery.Rival => 0, Livery.Stock => 1, _ => 2 },
                        i => s.Livery = i switch { 0 => Livery.Rival, 1 => Livery.Stock, _ => Livery.None },
                        "ANIME: the character's car with its stickers.", "STOCK: the game's stock car.  NONE: no stickers or plates."),
                },
            },
            new Page("HUD", "Times, drift meter, course dial and instruments.")
            {
                Rows =
                {
                    Row.Toggle("HUD", () => s.HudOn, v => s.HudOn = v, "Times, drift meter, course dial and the car's own gauges (F4)."),
                    Row.Choice("NAVI MAP", ["ROTATING", "NORTH UP", "WHOLE"], () => (int)s.MapMode, i => s.MapMode = (Hud.MapMode)i,
                        "Course dial: turns with the car, north up,", "or shows the whole course (N)."),
                },
            },
            new Page("SCREEN", "Window, resolution, frame pacing and render scale.")
            {
                Rows =
                {
                    Row.Choice("DISPLAY", displays, () => Math.Min((int)s.Display, displays.Length - 1), i => s.Display = (Settings.DisplayMode)i,
                        "WINDOW: resizable window.  BORDERLESS: fills the desktop (F11).",
                        OperatingSystem.IsMacOS() ? "macOS has no exclusive fullscreen." : "FULLSCREEN: exclusive, at the resolution below."),
                    Row.Choice("RESOLUTION", Sizes, () => ResolutionList(s).ToList().IndexOf((s.Width, s.Height)), i => (s.Width, s.Height) = ResolutionList(s)[i],
                        "Window size (WINDOW) or display mode (FULLSCREEN).", "BORDERLESS always uses the desktop size."),
                    Row.Toggle("VSYNC", () => s.VSync, v => s.VSync = v, "Wait for the display: no tearing.", "OFF: lowest latency, may tear."),
                    Row.Choice("FRAME CAP", [.. Settings.FrameCaps.Select(f => f == 0 ? "OFF" : $"{f} FPS")], () => Array.IndexOf(Settings.FrameCaps, s.FrameCap),
                        i => s.FrameCap = Settings.FrameCaps[i], "Frames per second at most (saves power on laptops)."),
                    Row.Choice("RENDER SCALE", [.. Settings.RenderScales.Select(Pct)], () => Array.IndexOf(Settings.RenderScales, s.RenderScale),
                        i => s.RenderScale = Settings.RenderScales[i], "3D resolution in percent of the window; menus and HUD stay sharp.",
                        "Below 100 % for slower machines, above for supersampling."),
                },
            },
            new Page("GRAPHICS", "Quality preset and the single effects.")
            {
                Rows =
                {
                    Row.Choice("QUALITY", presets, () => (int)s.QualityPreset, i => s.QualityPreset = (Settings.Preset)i,
                        "LOW: no effects.  MEDIUM: sun shadows and bloom.", "HIGH: + 4x MSAA and ambient occlusion.  ULTRA: + rain reflections."),
                    Row.Toggle("ANTI-ALIASING", () => s.Msaa, v => s.Msaa = v, "4x MSAA: smooth edges and foliage (F2 switches all)."),
                    Row.Toggle("SUN SHADOWS", () => s.Shadows, v => s.Shadows = v, "Cascaded shadows of car, trees and buildings."),
                    Row.Toggle("AMBIENT OCCLUSION", () => s.Ao, v => s.Ao = v, "Soft contact shading in corners and under the car."),
                    Row.Toggle("BLOOM", () => s.Bloom, v => s.Bloom = v, "Glow around the sun, lamps and bright lights."),
                    Row.Toggle("RAIN REFLECTIONS", () => s.Ssr, v => s.Ssr = v, "Screen-space reflections on the wet road (WET)."),
                },
            },
            new Page("SOUND", "Volume of music, effects, engine and menus.")
            {
                Rows =
                {
                    Row.Slider("MASTER", () => s.MasterVolume, v => s.MasterVolume = v, "Overall volume."),
                    Row.Toggle("MUSIC", () => s.MusicOn, v => s.MusicOn = v, "Menu music and the Eurobeat in the race (F3)."),
                    Row.Slider("MUSIC VOLUME", () => s.MusicVolume, v => s.MusicVolume = v, "Volume of the music."),
                    Row.Slider("SE VOLUME", () => s.SoundVolume, v => s.SoundVolume = v, "Tyres, walls, wind and engine in the race."),
                    Row.Slider("ENGINE", () => s.EngineVolume, v => s.EngineVolume = v, "Engine on top of the SE volume."),
                    Row.Slider("MENU SE", () => s.MenuVolume, v => s.MenuVolume = v, "Cursor, decide and countdown sounds."),
                },
            },
            new Page("CONTROLLER", "Keyboard and pad layout.")
            {
                Rows =
                {
                    new Row("SHOW", () => ["KEYBOARD", "PAD"], () => _padHelp ? 1 : 0, i => _padHelp = i == 1, () => _padHelp ? PadHelp : KeyboardHelp),
                },
            },
        ];
    }

    /// <summary>Offered window sizes plus the current one, smallest first.</summary>
    private List<(int W, int H)> ResolutionList(Settings s)
    {
        var list = Resolutions().Where(r => r.W > 0 && r.H > 0).ToList();
        if (!list.Contains((s.Width, s.Height))) list.Add((s.Width, s.Height));
        list.Sort();
        return list;
    }

    private static readonly string[] KeyboardHelp =
        ["W/S or UP/DOWN throttle and brake, A/D or LEFT/RIGHT steer, SPACE handbrake,", "SHIFT/CTRL gear up/down (MT), T AT/MT, R back to the road, C camera,", "L lights, H high beam, F2 graphics, F3 music, F4 HUD, N map, ESC pause."];

    private static readonly string[] PadHelp =
        ["Left stick steer, right/left trigger throttle and brake, A handbrake,", "bumpers gear up/down (MT), Y back to the road, START pause.", "D-pad up/down lights/high beam. Menus: D-pad or stick, A decide, B back."];

    // ---------------------------------------------------------------- drawing

    private static readonly uint Engraved = Overlay.Rgba(0.05f, 0.3f, 0.1f), Dim = Overlay.Rgba(0.35f, 0.42f, 0.38f, 0.55f);

    public void Draw(Canvas c, float theta)
    {
        if (Current == null) SectionList(c, theta);
        else if (Current.Draw is { } draw) draw(c, theta);
        else PageRows(c, Current, theta);
    }

    /// <summary>OPSL: centred chrome plates with the section names, the chosen one in the pulsing frame, its caption below.</summary>
    private void SectionList(Canvas c, float theta)
    {
        var step = MathF.Min(38, 264f / Math.Max(1, Pages.Count));
        for (var i = 0; i < Pages.Count; i++)
        {
            var y = 76 + i * step;
            c.Plate(136, y, 240, step - 8, i == Section ? 1 : 0.62f);
            c.Fit(Pages[i].Title, 256, y + (step - 8) / 2 + 6, 200, 0.5f, Canvas.Shade(0.12f, 0.12f, 0.14f, 1), 0.12f, 0, 17);
        }
        if (Pages.Count > 0)
        {
            var sy = 76 + Section * step;
            c.Glow(130, sy - 6, 382, sy + step - 2, Canvas.Pulse(theta));
        }
        c.Carbon(36, 344, 480, 426, 1, false);
        if (Pages.Count > 0) c.Text(Pages[Section].Caption, 50, 366, 11.5f, Canvas.White, 0, 0.12f);
        Menu.Hint(c, "UP/DOWN: Select    DECIDE: Open    BACK: Main menu");
    }

    /// <summary>OPGM: label tab + chrome value plate per row (≤ 3 values engraved side by side, more as ◀ value ▶, sliders as 10 blocks).</summary>
    private void PageRows(Canvas c, Page page, float theta)
    {
        var rows = page.Rows;
        for (var i = _top; i < Math.Min(rows.Count, _top + VisibleRows); i++)
        {
            var row = rows[i];
            var y = 70 + (i - _top) * 30;
            Vector2 min = Vector2.Round(c.P(36, y)), max = Vector2.Round(c.P(196, y + 26));
            c.O.Rect(min, max, Overlay.Rgba(0.5f, 0.51f, 0.53f));
            c.O.RectGradient(min + new Vector2(1.5f, 1.5f) * c.S, max - new Vector2(1.5f, 1.5f) * c.S, Overlay.Rgba(0.24f, 0.25f, 0.26f), Overlay.Rgba(0.1f, 0.1f, 0.11f));
            c.Fit(row.Label, 116, y + 18, 146, 0.5f, Canvas.White, 0.1f, 0, 13);
            c.Plate(204, y, 276, 26, 1);
            var sel = row.Get();
            if (row.Values == null)
            {
                for (var s = 0; s < 10; s++)
                {
                    float sx = 232 + s * 22;
                    c.O.Rect(Vector2.Round(c.P(sx, y + 7)), Vector2.Round(c.P(sx + 16, y + 19)), s < sel ? Overlay.Rgba(0.15f, 0.42f, 0.2f) : Overlay.Rgba(0.45f, 0.47f, 0.46f, 0.6f));
                }
                continue;
            }
            var values = row.Values();
            if (values.Length <= 3)
                for (var j = 0; j < values.Length; j++)
                {
                    var on = j == sel;
                    c.Text(values[j], 204 + 276 * (j + 0.5f) / values.Length, y + 18, 13, on ? Engraved : Dim, 0.5f, 0, 0, on ? 0.6f : 0);
                }
            else
            {
                c.Text(values[Math.Clamp(sel, 0, values.Length - 1)], 342, y + 18, 13, Engraved, 0.5f, 0, 0, 0.6f);
                if (i == Selected)
                {
                    c.Arrow(236, y + 6, 236, y + 20, 226, y + 13);
                    c.Arrow(448, y + 6, 448, y + 20, 458, y + 13);
                }
            }
        }
        if (rows.Count > 0)
        {
            var gy = 70 + (Selected - _top) * 30;
            c.Glow(200, gy - 4, 484, gy + 30, Canvas.Pulse(theta));
        }
        // more rows above/below: small diamonds on the right edge
        if (_top > 0) c.Diamond(492, 74, 4);
        if (_top + VisibleRows < rows.Count) c.Diamond(492, 334, 4);
        c.Carbon(36, 344, 480, 426, 1, false);
        var help = rows.Count > 0 ? rows[Selected].Help() : [page.Caption];
        for (var i = 0; i < help.Length; i++) c.Text(help[i], 50, 366 + i * 20, 11.5f, Canvas.White, 0, 0.12f);
        Menu.Hint(c, "UP/DOWN: Select    LEFT/RIGHT: Change    BACK: Options");
    }
}
