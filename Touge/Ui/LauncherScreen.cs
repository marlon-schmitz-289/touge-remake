using System.Numerics;
using Kansei.Graphics;
using Kansei.Input;

namespace Touge.Ui;

/// <summary>
///     The launcher before any disc is loaded, in the front end's style (drifting logo backdrop, blue system marquee, chrome
///     plates, carbon panel, pulsing frame) but with nothing from the disc: no original sounds or textures yet, so it is
///     silent. DISCS lists what <see cref="DiscScan"/> finds plus BROWSE (in-game <see cref="Browser"/>), SYSTEM FILE DIALOG
///     (<see cref="NativeDialog"/>) and QUIT; a dropped or pasted path (Ctrl/Cmd+V) is checked the same way. Errors stay on
///     screen in red. Keyboard, pad, wheel (<see cref="MenuKeys"/>) and mouse (hover, click, wheel).
/// </summary>
public sealed class LauncherScreen(DiscScan scan)
{
    public enum Result { None, Start, Quit }

    public DiscScan Scan => scan;
    /// <summary>Status line in the panel; red when <c>Error</c>.</summary>
    public (string Text, bool Error)? Message { get; set; }
    /// <summary>A path to check on the next update (drop, dialog, browser, paste).</summary>
    public string? Picked { get; set; }
    /// <summary>The valid disc after <see cref="Result.Start"/>.</summary>
    public Disc? Chosen { get; private set; }
    public bool Browsing => _browser != null;

    private Browser? _browser;
    private Task<string?>? _dialog;
    private readonly QuitPrompt _quit = new();
    private readonly MenuKeys _keys = new();
    private readonly Canvas _c = new();
    private int _sel, _top;
    private float _t;
    private Vector2 _mouse;

    private const int Visible = 6;
    private static readonly string[] Actions = ["BROWSE FOR THE ISO", "SYSTEM FILE DIALOG", "QUIT"];

    public static string PasteKey => OperatingSystem.IsMacOS() ? "CMD+V" : "CTRL+V";

    public void Browse(string? dir) => _browser = new Browser(dir);

    private static bool Paste(InputSnapshot input) => input.Keyboard.IsKeyPressed(Key.V) &&
        (input.Keyboard.IsKeyDown(Key.LeftCtrl) || input.Keyboard.IsKeyDown(Key.RightCtrl) || input.Keyboard.IsKeyDown(Key.LeftGui) || input.Keyboard.IsKeyDown(Key.RightGui));

    public Result Update(InputSnapshot input, float dt, (float X, float Y) mouse)
    {
        _t += dt;
        if (_dialog is { IsCompleted: true } dialog)
        {
            _dialog = null;
            if (dialog.IsFaulted) Message = (dialog.Exception!.InnerException!.Message, true);
            else if (dialog.Result is { } file) Picked = file;
        }
        if (Picked is { } picked)
        {
            Picked = null;
            var d = Disc.Check(picked);
            Console.WriteLine($"[Launcher] {picked}: {d.Error ?? "OK " + d.Info}");
            if (d.Ok)
            {
                (Chosen, Message) = (d, null);
                return Result.Start;
            }
            Message = ($"{Path.GetFileName(picked.Trim().Trim('"').TrimEnd('/', '\\'))}: {d.Error}", true);
        }
        var moved = mouse.X != _mouse.X || mouse.Y != _mouse.Y;
        _mouse = new Vector2(mouse.X, mouse.Y);
        var click = input.Mouse.IsButtonPressed(1);
        if (_browser != null)
        {
            switch (_browser.Update(input, dt, _keys, _c.Unproject(_mouse), moved, click))
            {
                case Browser.Outcome.Leave:
                    _browser = null;
                    break;
                case Browser.Outcome.Pick:
                    Picked = _browser.Result;
                    break;
            }
            return Result.None;
        }
        var k = _keys.Read(input, dt);
        if (_quit.Open) return _quit.Update(k, null) ? Result.Quit : Result.None;
        if (Paste(input) && input.Clipboard is { } clip)
        {
            Picked = clip;
            return Result.None;
        }
        var discs = scan.Found;
        var rows = discs.Count + Actions.Length;
        // mouse: hover selects, click decides, wheel scrolls
        var m = _c.Unproject(_mouse);
        var hover = m.X is >= 56 and <= 456 ? (int)MathF.Floor((m.Y - 70) / 44) : -1;
        var overRow = hover >= 0 && hover < Visible && _top + hover < rows && m.Y - 70 - hover * 44 <= 40;
        if (overRow && moved) _sel = _top + hover;
        _sel = Math.Clamp(_sel + k.Y - input.Mouse.ScrollDelta, 0, rows - 1);
        _top = Math.Clamp(_top, Math.Max(0, _sel - Visible + 1), _sel);
        if (k.Back) _quit.Show();
        else if (k.Ok || click && overRow)
        {
            if (_sel < discs.Count) Picked = discs[_sel].Path;
            else if (_sel == discs.Count) _browser = new Browser(null);
            else if (_sel == discs.Count + 1) _dialog ??= NativeDialog.PickIso();
            else _quit.Show();
        }
        return Result.None;
    }

    public void Build(Overlay o, int w, int h, bool loading)
    {
        var c = _c;
        c.Begin(o, w, h);
        var theta = _t * 300;
        c.Backdrop(_t);
        c.Marquee(_browser != null ? "BROWSE" : "SELECT GAME DISC", true, _t);
        if (loading)
        {
            c.Carbon(116, 190, 396, 260);
            c.Lettering("LOADING", 256, 236, 30, Canvas.White, Overlay.Rgba(0.72f, 0.73f, 0.75f));
            return;
        }
        string[] status;
        if (_browser != null)
        {
            _browser.Draw(c, theta);
            status = [_browser.Error ?? "Folders and .iso files. Choose the disc image of Initial D Special Stage (SLPM-65268)."];
        }
        else
        {
            var discs = scan.Found;
            var rows = discs.Count + Actions.Length;
            for (var i = _top; i < Math.Min(rows, _top + Visible); i++)
            {
                var y = 70 + (i - _top) * 44;
                var on = i == _sel;
                c.Plate(56, y, 400, 40, on ? 1 : 0.62f);
                var ink = Canvas.Shade(0.08f, 0.08f, 0.1f, 1);
                if (i < discs.Count)
                {
                    var d = discs[i];
                    c.Text(d.Title, 72, y + 18, 14, ink, 0, 0.15f, 0, 0.3f);
                    c.Text(d.Info, 440, y + 18, 9.5f, Overlay.Rgba(0.05f, 0.3f, 0.1f), 1, 0, 0, 0.3f);
                    c.Fit(d.Path, 72, y + 33, 368, 0, Canvas.Shade(0.25f, 0.25f, 0.28f, 1), 0, 0, 9);
                }
                else c.Text(Actions[i - discs.Count], 256, y + 26, 15, ink, 0.5f, 0.15f, 0, 0.3f);
            }
            if (_top > 0) c.Diamond(468, 74, 4);
            if (_top + Visible < rows) c.Diamond(468, 330, 4);
            var sy = 70 + (_sel - _top) * 44;
            if (!_quit.Open) c.Glow(50, sy - 4, 462, sy + 44, Canvas.Pulse(theta));
            status = [
                !scan.Done ? "Looking for the disc in Downloads, Desktop, Documents, home and mounted drives ..."
                : discs.Count == 0 ? "No disc image found. BROWSE to it, or drop the .iso file onto this window."
                : $"{discs.Count} disc image{(discs.Count == 1 ? "" : "s")} found. Or drop an .iso onto this window.",
                "Only your own copy of Initial D Special Stage (Japan, SLPM-65268) works.",
            ];
        }
        c.Carbon(36, 344, 480, 426, 1, false);
        if (Message is var (text, error)) status = [text, .. status.Take(1)];
        for (var i = 0; i < status.Length; i++)
            c.Fit(status[i], 50, 366 + i * 20, 416, 0, i == 0 && Message is (_, true) ? Overlay.Rgba(1, 0.3f, 0.25f) : Canvas.White, 0.12f, 0, 11.5f);
        Menu.Hint(c, _browser != null
            ? _browser.Typing ? "Type or paste a path    ENTER: Go    TAB: List    ESC: Cancel" : $"UP/DOWN: Select    DECIDE: Open    LEFT: Up    TAB/{PasteKey}: Type/paste path    BACK: Discs"
            : $"UP/DOWN: Select    DECIDE: Start    {PasteKey}: Paste path    BACK: Quit");
        _quit.Draw(c, theta);
    }
}

/// <summary>
///     In-game file browser for the launcher: <see cref="Dir"/> null is the top (home, Downloads, Desktop, volumes/drives), else
///     a folder's subfolders and *.iso files with ".." first; a path field to type or paste into (Tab, Ctrl/Cmd+V).
/// </summary>
public sealed class Browser
{
    public enum Kind { Up, Folder, Volume, Iso }
    public enum Outcome { None, Leave, Pick }
    public sealed record Entry(string Name, string Path, Kind Kind, long Size = 0);

    public string? Dir { get; private set; }
    public List<Entry> Entries { get; private set; } = [];
    public int Selected { get; private set; }
    public string? Error { get; private set; }
    public bool Typing { get; private set; }
    public string Field { get; private set; } = "";
    /// <summary>The .iso after <see cref="Outcome.Pick"/>.</summary>
    public string? Result { get; private set; }
    private int _top;
    private float _caret;
    private const int Visible = 9;

    public Browser(string? dir)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");
        Open(dir ?? (Directory.Exists(downloads) ? downloads : home));
    }

    /// <summary>The top level: home folders and every volume/drive.</summary>
    public static List<Entry> Top()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<Entry> list = [new("HOME", home, Kind.Folder)];
        foreach (var (name, sub) in new[] { ("DOWNLOADS", "Downloads"), ("DESKTOP", "Desktop"), ("DOCUMENTS", "Documents") })
            if (Directory.Exists(Path.Combine(home, sub))) list.Add(new(name, Path.Combine(home, sub), Kind.Folder));
        if (!OperatingSystem.IsWindows()) list.Add(new("/", "/", Kind.Volume));
        list.AddRange(DiscScan.Volumes().Select(v => new Entry(v.Name, v.Path, Kind.Volume)));
        return list;
    }

    /// <summary>A folder's entries: ".." (parent, or the top), visible subfolders, then *.iso files, each sorted by name.</summary>
    public static List<Entry> List(string dir)
    {
        var info = new DirectoryInfo(dir);
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        List<Entry> list = [new("..", info.Parent?.FullName ?? "", Kind.Up)];
        list.AddRange(info.EnumerateDirectories("*", opts).Where(d => !d.Name.StartsWith('.')).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new Entry(d.Name, d.FullName, Kind.Folder)));
        list.AddRange(info.EnumerateFiles("*", opts).Where(f => f.Extension.Equals(".iso", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => new Entry(f.Name, f.FullName, Kind.Iso, f.LinkTarget != null && f.ResolveLinkTarget(true) is FileInfo { Exists: true } t ? t.Length : f.Length)));
        return list;
    }

    /// <summary>Shows <paramref name="dir"/> ("" or null: the top); on failure stays where it was with <see cref="Error"/>.</summary>
    public bool Open(string? dir)
    {
        try
        {
            var full = string.IsNullOrEmpty(dir) ? null : Path.GetFullPath(dir);
            var entries = full == null ? Top() : List(full);
            var from = Dir;
            (Dir, Entries, Error, Field) = (full, entries, null, full ?? "");
            Selected = Math.Max(0, Entries.FindIndex(e => e.Path == from && e.Kind != Kind.Up)); // up: the folder we came from
            _top = Math.Max(0, Selected - Visible / 2);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            Error = $"CANNOT OPEN {dir}: {e.Message}";
            return false;
        }
    }

    /// <summary>A typed/pasted path: a folder opens, anything else is picked (the launcher checks it).</summary>
    public Outcome Submit(string text)
    {
        text = Disc.Resolve(text, Dir);
        if (Directory.Exists(text) || text == "")
        {
            Typing = !Open(text);
            return Outcome.None;
        }
        (Result, Typing) = (text, false);
        return Outcome.Pick;
    }

    /// <summary>Decides on the selected entry: folders/volumes open, ".." goes up, an .iso is picked.</summary>
    public Outcome Activate()
    {
        if (Entries.Count == 0) return Outcome.None;
        var e = Entries[Selected];
        if (e.Kind != Kind.Iso)
        {
            Open(e.Path);
            return Outcome.None;
        }
        Result = e.Path;
        return Outcome.Pick;
    }

    public Outcome Update(InputSnapshot input, float dt, MenuKeys keys, Vector2 mouse, bool moved, bool click)
    {
        _caret += dt;
        var kb = input.Keyboard;
        var paste = kb.IsKeyPressed(Key.V) && (kb.IsKeyDown(Key.LeftCtrl) || kb.IsKeyDown(Key.RightCtrl) || kb.IsKeyDown(Key.LeftGui) || kb.IsKeyDown(Key.RightGui));
        if (paste && input.Clipboard is { } clip)
        {
            (Typing, Field) = (true, (Typing ? Field : "") + clip.Trim());
            return Outcome.None;
        }
        if (click && mouse.Y is >= 64 and <= 88 && mouse.X is >= 36 and <= 480) Typing = true;
        if (Typing)
        {
            if (kb.IsKeyPressed(Key.Escape) || kb.IsKeyPressed(Key.Tab)) (Typing, Field) = (false, Dir ?? "");
            else if (kb.IsKeyPressed(Key.Enter)) return Submit(Field);
            else if (kb.IsKeyRepeating(Key.Backspace, dt) && Field.Length > 0) Field = Field[..^1];
            else if (input.TypedText.Length > 0) Field += input.TypedText;
            return Outcome.None;
        }
        if (kb.IsKeyPressed(Key.Tab))
        {
            (Typing, _caret) = (true, 0);
            return Outcome.None;
        }
        var k = keys.Read(input, dt);
        if (k.Back) return Outcome.Leave;
        var row = (int)MathF.Floor((mouse.Y - 96) / 27);
        var over = mouse.X is >= 36 and <= 480 && row >= 0 && row < Visible && _top + row < Entries.Count;
        if (over && moved) Selected = _top + row;
        if (Entries.Count > 0) Selected = Math.Clamp(Selected + k.Y - input.Mouse.ScrollDelta, 0, Entries.Count - 1);
        _top = Math.Clamp(_top, Math.Max(0, Selected - Visible + 1), Selected);
        if (k.X < 0 && Dir != null) Open(Path.GetDirectoryName(Dir.TrimEnd(Path.DirectorySeparatorChar)) ?? "");
        else if (k.X > 0 && Entries.Count > 0 && Entries[Selected].Kind is Kind.Folder or Kind.Volume) Activate();
        else if (k.Ok || click && over) return Activate();
        return Outcome.None;
    }

    public void Draw(Canvas c, float theta)
    {
        // path field: dark-steel tab + chrome-edged black field, caret while typing
        Vector2 t0 = Vector2.Round(c.P(36, 64)), t1 = Vector2.Round(c.P(96, 88));
        c.O.Rect(t0, t1, Overlay.Rgba(0.5f, 0.51f, 0.53f));
        c.O.RectGradient(t0 + new Vector2(1.5f, 1.5f) * c.S, t1 - new Vector2(1.5f, 1.5f) * c.S, Overlay.Rgba(0.24f, 0.25f, 0.26f), Overlay.Rgba(0.1f, 0.1f, 0.11f));
        c.Text("PATH", 66, 81, 12, Canvas.White, 0.5f, 0.12f);
        Vector2 f0 = Vector2.Round(c.P(100, 64)), f1 = Vector2.Round(c.P(480, 88));
        c.O.Rect(f0, f1, Typing ? Overlay.Rgba(1, 1, 0.2f) : Overlay.Rgba(0.6f, 0.62f, 0.65f));
        c.O.Rect(f0 + new Vector2(1.5f, 1.5f) * c.S, f1 - new Vector2(1.5f, 1.5f) * c.S, Overlay.Rgba(0.04f, 0.04f, 0.05f));
        var field = Typing ? Field + ((int)(_caret * 2) % 2 == 0 ? "_" : " ") : Dir ?? "COMPUTER";
        // long paths: the end stays visible
        var max = 372 * c.Kx / c.O.Font!.Measure("M", 11 * c.Ky) * 1.6f;
        if (field.Length > max) field = "..." + field[^(int)max..];
        c.Text(field, 108, 81, 11, Typing ? Canvas.White : Overlay.Rgba(0.8f, 0.82f, 0.85f), 0, 0);
        for (var i = _top; i < Math.Min(Entries.Count, _top + Visible); i++)
        {
            var e = Entries[i];
            var y = 96 + (i - _top) * 27;
            Vector2 min = Vector2.Round(c.P(36, y)), max2 = Vector2.Round(c.P(480, y + 24));
            c.O.Rect(min, max2, Overlay.Rgba(0.5f, 0.51f, 0.53f, 0.9f));
            c.O.RectGradient(min + new Vector2(1.5f, 1.5f) * c.S, max2 - new Vector2(1.5f, 1.5f) * c.S, Overlay.Rgba(0.24f, 0.25f, 0.26f, 0.95f), Overlay.Rgba(0.1f, 0.1f, 0.11f, 0.95f));
            var (tag, color) = e.Kind switch
            {
                Kind.Up => (Dir == null ? "" : "UP", Overlay.Rgba(0.72f, 0.73f, 0.75f)),
                Kind.Folder => ("FOLDER", Canvas.White),
                Kind.Volume => ("DRIVE", Overlay.Rgba(0.6f, 0.8f, 1)),
                _ => (FormattableString.Invariant($"{e.Size / 1e9:0.00} GB"), Overlay.Rgba(1, 0.85f, 0.2f)),
            };
            var name = e.Kind == Kind.Up ? Path.GetDirectoryName(Dir?.TrimEnd(Path.DirectorySeparatorChar) ?? "") is { } parent ? ".. " + parent : ".. COMPUTER" : e.Name;
            c.Fit(name, 48, y + 17, 350, 0, color, 0.08f, 0, 12.5f);
            c.Text(tag, 470, y + 17, 9.5f, Style.Fade(color, 0.7f), 1, 0.1f);
        }
        if (_top > 0) c.Diamond(492, 100, 4);
        if (_top + Visible < Entries.Count) c.Diamond(492, 332, 4);
        if (Entries.Count > 0 && !Typing)
        {
            var gy = 96 + (Selected - _top) * 27;
            c.Glow(32, gy - 3, 484, gy + 27, Canvas.Pulse(theta));
        }
    }
}
