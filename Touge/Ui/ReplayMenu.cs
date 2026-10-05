using Kansei.Graphics;
using Kansei.Input;
using Touge.Replays;

namespace Touge.Ui;

/// <summary>
///     REPLAY &amp; RECORD (main menu, BGM WORRY as the original): three chrome tabs on the left — REPLAYS (every finished run
///     and battle, newest first), BEST RUNS (the record run per course, direction and assists) and RECORDS (best time per
///     course and route, the former Records screen) — and the list in the carbon panel. ←/→ tabs ↔ list, ↑/↓ move, DECIDE
///     watch, K (pad Y) keeps a replay from pruning, X/DELETE (pad X) delete with a YES/NO question, BACK to the tabs / main menu.
/// </summary>
public sealed class ReplayMenu(Catalog catalog, Settings settings)
{
    public enum Result { None, Play, Exit }

    public static readonly string[] Tabs = ["REPLAYS", "BEST RUNS", "RECORDS"];
    private const int Visible = 7;

    public bool Active { get; private set; }
    public int Tab { get; private set; }
    public bool InList { get; private set; }
    /// <summary>The replay to play after <see cref="Result.Play"/>.</summary>
    public string? Chosen { get; private set; }
    public Action<string>? Sound { get; set; }
    public List<(string Path, ReplayInfo Info)> Items { get; private set; } = [];

    private int _row, _top;
    private float _clock, _t;
    private readonly Confirm _confirm = new();

    public void Open(int tab = 0, bool list = false)
    {
        (Active, Tab, InList, _t) = (true, tab, list, 0);
        Refresh();
    }

    public void Close() => Active = false;

    /// <summary>Skips the fade-in (screenshots).</summary>
    public void Settle() => _t = 10;

    /// <summary>Screenshots: the delete question over the list.</summary>
    public void AskDelete()
    {
        if (Items.Count > 0) _confirm.Show("DELETE THIS REPLAY?");
    }

    private void Refresh()
    {
        Items = Tab switch { 0 => ReplayStore.List(ReplayStore.Root), 1 => ReplayStore.List(ReplayStore.BestDir), _ => [] };
        _row = Math.Clamp(_row, 0, Math.Max(0, Items.Count - 1));
        _top = Math.Clamp(_top, Math.Max(0, _row - Visible + 1), _row);
    }

    public Result Update((int X, int Y, bool Ok, bool Back) k, InputSnapshot input, float dt)
    {
        if (!Active) return Result.None;
        _clock += dt;
        _t += dt;
        if (_confirm.Open)
        {
            if (_confirm.Update(k, Sound))
            {
                ReplayStore.Delete(Items[_row].Path);
                Refresh();
            }
            return Result.None;
        }
        var pad = input.Gamepad;
        var keep = input.Keyboard.IsKeyPressed(Key.K) || pad.IsConnected && pad.IsButtonPressed(GamepadButton.Y);
        var delete = input.Keyboard.IsKeyPressed(Key.Delete) || input.Keyboard.IsKeyPressed(Key.X) || pad.IsConnected && pad.IsButtonPressed(GamepadButton.X);
        if (!InList)
        {
            if (k.Y != 0)
            {
                Tab = (Tab + k.Y + Tabs.Length) % Tabs.Length;
                (_row, _top) = (0, 0);
                Refresh();
                Sound?.Invoke("SYS005");
            }
            else if ((k.Ok || k.X > 0) && Tab < 2)
            {
                Sound?.Invoke(Items.Count > 0 ? "SYS006" : "BEEP001");
                InList = Items.Count > 0;
            }
            else if (k.Back)
            {
                Sound?.Invoke("BEEP001");
                Active = false;
                return Result.Exit;
            }
            return Result.None;
        }
        if (k.Back || k.X < 0)
        {
            Sound?.Invoke("BEEP001");
            InList = false;
        }
        else if (k.Y != 0 && Items.Count > 0)
        {
            _row = (_row + k.Y + Items.Count) % Items.Count;
            _top = Math.Clamp(_top, Math.Max(0, _row - Visible + 1), _row);
            Sound?.Invoke("SYS005");
        }
        else if (k.Ok && Items.Count > 0)
        {
            Sound?.Invoke("SYS006");
            Chosen = Items[_row].Path;
            Active = false;
            return Result.Play;
        }
        else if (keep && Tab == 0 && Items.Count > 0)
        {
            try
            {
                var path = ReplayStore.ToggleKept(Items[_row].Path);
                Items[_row] = (path, Items[_row].Info);
                Sound?.Invoke("SYS006");
            }
            catch (IOException e)
            {
                Console.WriteLine($"[Replay] {Items[_row].Path}: {e.Message}");
                Sound?.Invoke("BEEP001");
            }
        }
        else if (delete && Items.Count > 0)
        {
            Sound?.Invoke("SYS006");
            _confirm.Show("DELETE THIS REPLAY?", Tab == 1 ? "The record time stays, its ghost is gone." : null);
        }
        return Result.None;
    }

    // ---------------------------------------------------------------- drawing

    private float Theta => _clock * 300 % 360;

    public void Build(Overlay o, int width, int height, Canvas c)
    {
        o.Clear();
        if (!Active) return;
        c.Begin(o, width, height);
        c.Backdrop(_clock);
        for (var i = 0; i < Tabs.Length; i++)
        {
            var y = 84 + i * 50;
            c.Plate(18, y, 150, 36, i == Tab ? 1 : 0.62f);
            c.Text(Tabs[i], 93, y + 24, 15, Canvas.Shade(0.08f, 0.08f, 0.1f, 1), 0.5f, 0.18f, 0, 0.3f);
        }
        c.Glow(12, 78 + Tab * 50, 174, 126 + Tab * 50, InList ? 0.45f : Canvas.Pulse(Theta));
        c.Carbon(184, 72, 496, 420);
        if (Tab == 2) Records(c);
        else List(c);
        _confirm.Draw(c, Theta);
        c.Marquee("REPLAY & RECORD", true, _clock);
        Menu.Hint(c, Tab == 2 ? "UP/DOWN: Tab    BACK: Main menu"
            : InList ? $"UP/DOWN: Select    DECIDE: Watch    {(Tab == 0 ? "Y / K: Keep    " : "")}X / DELETE: Delete    BACK: Tabs"
            : "UP/DOWN: Tab    DECIDE / RIGHT: List    BACK: Main menu");
        c.Fade(1 - Math.Clamp(_t / Menu.Fade, 0, 1));
    }

    /// <summary>"AKINA  DOWNHILL  NIGHT FOG" for a replay's course.</summary>
    public string Where(ReplayInfo i)
    {
        var id = i.Course[..Math.Max(0, i.Course.LastIndexOf('_'))];
        var course = catalog.Courses.FirstOrDefault(c => c.Id == id);
        var time = Catalog.TimeName(i.Course[(i.Course.LastIndexOf('_') + 1)..]) + (i.Fog ? " FOG" : "");
        return course == null ? i.Course : $"{course.Name}  {Catalog.DirectionName(course, i.Reverse)}  {time}";
    }

    /// <summary>What kind of run: "LEGEND vs KENJI WIN", "STORY ch.3 THE GHOST OF AKINA", "BATTLE LOSE", "TIME ATTACK".</summary>
    public static string Label(ReplayInfo i)
    {
        var rival = i.Cars.Count > 1 ? $" vs {i.Cars[1].Name}" : "";
        return (i.Mode switch
        {
            "LEGEND" => $"LEGEND{rival} {i.Result}",
            "STORY" => $"STORY ch.{i.Chapter} {i.Title} {i.Result}",
            "BATTLE" => $"BATTLE{rival} {i.Result}",
            _ => i.Mode,
        }).TrimEnd();
    }

    public string CarName(string id) => catalog.Cars.FirstOrDefault(c => c.Id == id)?.Name ?? id;

    private void List(Canvas c)
    {
        if (Items.Count == 0)
        {
            c.Text(Tab == 0 ? "NO REPLAYS YET" : "NO BEST RUNS YET", 340, 200, 18, Canvas.White, 0.5f, 0.15f, 0.06f, 0.3f);
            c.Text(Tab == 0 ? "Every finished run and battle is recorded here." : "A new record keeps its run here (and as your ghost).", 340, 224, 11, Overlay.Rgba(1, 1, 1, 0.7f), 0.5f, 0.12f);
            return;
        }
        for (var i = _top; i < Math.Min(Items.Count, _top + Visible); i++)
        {
            var (path, info) = Items[i];
            var y = 84 + (i - _top) * 47;
            var sel = InList && i == _row;
            c.Rule(196, 484, y + 44);
            if (sel) c.Diamond(200, y + 15, 4);
            c.Fit(Where(info), 210, y + 19, 186, 0, sel ? Canvas.Yellow : Canvas.White, 0.15f, 0.06f, 14);
            var cars = string.Join("  VS  ", info.Cars.Select(x => CarName(x.Car)));
            c.Fit($"{Label(info)}   {cars}", 210, y + 37, 186, 0, Overlay.Rgba(0.75f, 0.78f, 0.8f), 0.12f, 0, 11);
            if (Tab == 0 && ReplayStore.IsKept(path)) c.Text("KEPT", 400, y + 37, 10, Canvas.Yellow, 0, 0.12f);
            c.Text(Style.Time(info.Time), 484, y + 19, 15, Canvas.White, 1, 0.15f, 0, 0.3f);
            c.Text(info.Date.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), 484, y + 37, 10, Overlay.Rgba(0.75f, 0.78f, 0.8f), 1, 0.1f);
        }
        if (InList) c.Glow(190, 80 + (_row - _top) * 47, 490, 126 + (_row - _top) * 47, Canvas.Pulse(Theta));
        if (_top > 0) c.Arrow(472, 78, 486, 78, 479, 70);
        if (_top + Visible < Items.Count) c.Arrow(472, 414, 486, 414, 479, 422);
        c.Text($"{_row + 1} / {Items.Count}", 196, 414, 10, Overlay.Rgba(0.72f, 0.73f, 0.75f), 0, 0.1f);
    }

    /// <summary>Best time per course and route (stock assists), as the former RECORDS screen.</summary>
    private void Records(Canvas c)
    {
        var grey = Overlay.Rgba(0.72f, 0.73f, 0.75f);
        c.Text("COURSE", 196, 94, 10, grey, 0, 0.1f);
        for (var r = 0; r < 2; r++) c.Text("ROUTE / TIME", 300 + r * 98, 94, 10, grey, 0, 0.1f);
        for (var i = 0; i < catalog.Courses.Count; i++)
        {
            var course = catalog.Courses[i];
            var y = 100 + i * 28;
            c.Rule(192, 490, y + 26);
            c.Fit(course.Name, 196, y + 20, 100, 0, Canvas.White, 0.15f, 0.06f, 14);
            for (var r = 0; r < 2; r++)
            {
                var best = settings.Best.GetValueOrDefault(Settings.BestKey(course.Id, r == 1));
                var x = 300 + r * 98;
                c.Fit(Catalog.DirectionName(course, r == 1), x, y + 11, 90, 0, grey, 0, 0, 8);
                c.Text(Style.Time(best?[^1]), x, y + 24, 12, best == null ? Overlay.Rgba(1, 1, 1, 0.35f) : Canvas.White, 0, 0.15f);
            }
        }
    }
}
