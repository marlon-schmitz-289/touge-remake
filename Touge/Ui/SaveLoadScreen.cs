using System.Globalization;
using System.Numerics;
using Kansei.Graphics;
using Kansei.Input;

namespace Touge.Ui;

/// <summary>
///     SAVE &amp; LOAD (main menu): the <see cref="SaveSlots.Count"/> profile slots as carbon cards (name, play time, records,
///     what progress it holds, date; the slot in use marked), and the AUTOSAVE switch below. DECIDE on a slot opens its
///     actions (SAVE, LOAD, RENAME, DELETE; an empty slot asks for a name and saves), overwrite/load/delete ask YES/NO.
///     Name entry arcade style: ↑/↓ letter, ←/→ position, or just type; DECIDE OK, BACK cancel. Sounds as everywhere
///     (SYS005, SYS006, BEEP001).
/// </summary>
public sealed class SaveLoadScreen(SaveSlots slots)
{
    public enum Result { None, Saved, Loaded, Exit }

    private static readonly string[] Actions = ["SAVE", "LOAD", "RENAME", "DELETE", "CANCEL"];
    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 -.!";

    public bool Active { get; private set; }
    /// <summary>Selected row: a slot, or <see cref="SaveSlots.Count"/> = AUTOSAVE.</summary>
    public int Row { get; private set; }
    public Action<string>? Sound { get; set; }
    /// <summary>Play time of the current profile so far (the game counts it).</summary>
    public Func<double> PlaySeconds { get; set; } = () => 0;

    private SaveSlots.Meta?[] _meta = new SaveSlots.Meta?[SaveSlots.Count];
    private SaveSlots.State _state = new();
    private int _action = -1, _pending = -1, _cursor;
    private char[]? _name;
    private float _clock, _t, _flash;
    private string _flashText = "";
    private readonly Confirm _confirm = new();

    public void Open()
    {
        (Active, _t, _action, _name, _pending) = (true, 0, -1, null, -1);
        Refresh();
        Row = Math.Max(0, _state.Active);
    }

    public void Close() => Active = false;

    /// <summary>Skips the fade-in (screenshots).</summary>
    public void Settle() => _t = 10;

    /// <summary>Screenshots: the slot's action bar ("actions") or the name entry ("name") open.</summary>
    public void Show(string state)
    {
        if (state == "actions") _action = 1;
        if (state == "name") StartName("KEISUKE");
    }

    private void Refresh()
    {
        for (var i = 0; i < SaveSlots.Count; i++) _meta[i] = slots.Read(i);
        _state = slots.ReadState();
    }

    private void Flash(string text) => (_flashText, _flash) = (text, 2.2f);

    /// <summary>One frame; the name entry also reads typed text and Backspace from <paramref name="input"/>.</summary>
    public Result Update((int X, int Y, bool Ok, bool Back) k, InputSnapshot input, float dt)
    {
        if (!Active) return Result.None;
        (_clock, _t, _flash) = (_clock + dt, _t + dt, MathF.Max(0, _flash - dt));
        if (_confirm.Open)
        {
            if (!_confirm.Update(k, Sound)) return Result.None;
            return Do(Actions[_pending]);
        }
        if (_name != null) return NameEntry(k, input);
        if (_action >= 0)
        {
            if (k.X != 0)
            {
                _action = (_action + k.X + Actions.Length) % Actions.Length;
                Sound?.Invoke("SYS005");
            }
            else if (k.Back)
            {
                Sound?.Invoke("BEEP001");
                _action = -1;
            }
            else if (k.Ok) return Choose(Actions[_action]);
            return Result.None;
        }
        if (k.Y != 0)
        {
            Row = (Row + k.Y + SaveSlots.Count + 1) % (SaveSlots.Count + 1);
            Sound?.Invoke("SYS005");
        }
        else if (Row == SaveSlots.Count && (k.Ok || k.X != 0))
        {
            _state.Autosave = !_state.Autosave;
            slots.WriteState(_state);
            Sound?.Invoke("SYS005");
        }
        else if (k.Ok)
        {
            Sound?.Invoke("SYS006");
            if (_meta[Row] == null) StartName("PLAYER " + (Row + 1));
            else _action = 0;
        }
        else if (k.Back)
        {
            Sound?.Invoke("BEEP001");
            Active = false;
            return Result.Exit;
        }
        return Result.None;
    }

    private Result Choose(string action)
    {
        var name = _meta[Row]?.Name ?? "";
        switch (action)
        {
            case "CANCEL":
                Sound?.Invoke("BEEP001");
                _action = -1;
                return Result.None;
            case "RENAME":
                Sound?.Invoke("SYS006");
                _action = -1;
                StartName(name);
                return Result.None;
            default:
                Sound?.Invoke("SYS006");
                _pending = Array.IndexOf(Actions, action);
                _confirm.Show(action switch
                {
                    "SAVE" => $"OVERWRITE SLOT {Row + 1}?", "LOAD" => $"LOAD SLOT {Row + 1}?", _ => $"DELETE SLOT {Row + 1}?",
                }, action switch
                {
                    "SAVE" => $"\"{name}\" is replaced by your current progress.", "LOAD" => "Progress not saved in a slot is lost.", _ => $"\"{name}\" is gone for good.",
                });
                return Result.None;
        }
    }

    private Result Do(string action)
    {
        _action = -1;
        var r = Result.None;
        try
        {
            switch (action)
            {
                case "SAVE":
                    slots.Save(Row, _meta[Row]?.Name ?? $"PLAYER {Row + 1}", PlaySeconds());
                    Flash("SAVED");
                    r = Result.Saved;
                    break;
                case "LOAD":
                    if (slots.Load(Row))
                    {
                        Flash("LOADED");
                        r = Result.Loaded;
                    }
                    break;
                case "DELETE":
                    slots.Delete(Row);
                    Flash("DELETED");
                    break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Sound?.Invoke("BEEP001");
            Flash("FAILED: " + e.Message);
        }
        Refresh();
        return r;
    }

    private void StartName(string name)
    {
        _name = name.ToUpperInvariant().PadRight(SaveSlots.NameLength)[..SaveSlots.NameLength].ToCharArray();
        _cursor = Math.Min(name.TrimEnd().Length, SaveSlots.NameLength - 1);
    }

    private Result NameEntry((int X, int Y, bool Ok, bool Back) k, InputSnapshot input)
    {
        var name = _name!;
        var kb = input.Keyboard;
        foreach (var ch in input.TypedText.ToUpperInvariant())
            if (Letters.Contains(ch) && _cursor < name.Length)
            {
                name[_cursor] = ch;
                _cursor = Math.Min(_cursor + 1, name.Length - 1);
            }
        var typing = input.TypedText.Length > 0;
        if (kb.IsKeyPressed(Key.Backspace))
        {
            if (name[_cursor] == ' ' && _cursor > 0) _cursor--;
            name[_cursor] = ' ';
            return Result.None;
        }
        if (k.Y != 0 && !typing)
        {
            var i = Letters.IndexOf(name[_cursor]);
            name[_cursor] = Letters[(Math.Max(i, 0) - k.Y + Letters.Length) % Letters.Length];
            Sound?.Invoke("SYS005");
        }
        else if (k.X != 0 && !typing)
        {
            _cursor = Math.Clamp(_cursor + k.X, 0, name.Length - 1);
            Sound?.Invoke("SYS005");
        }
        else if (kb.IsKeyPressed(Key.Escape) || k.Back && !kb.IsKeyPressed(Key.Backspace) && !typing)
        {
            Sound?.Invoke("BEEP001");
            _name = null;
        }
        else if (kb.IsKeyPressed(Key.Enter) || k.Ok && !typing)
        {
            var text = new string(name).Trim();
            if (text.Length == 0)
            {
                Sound?.Invoke("BEEP001");
                return Result.None;
            }
            Sound?.Invoke("SYS006");
            _name = null;
            try
            {
                if (_meta[Row] is { } meta) Rename(meta, text);
                else
                {
                    slots.Save(Row, text, PlaySeconds());
                    Flash("SAVED");
                    Refresh();
                    return Result.Saved;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Flash("FAILED: " + e.Message);
            }
            Refresh();
        }
        return Result.None;
    }

    private void Rename(SaveSlots.Meta meta, string name)
    {
        meta.Name = name;
        var path = Path.Combine(slots.SlotDir(Row), "slot.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(meta, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Flash("RENAMED");
    }

    // ---------------------------------------------------------------- drawing

    private float Theta => _clock * 300 % 360;
    private static readonly uint Grey = Overlay.Rgba(0.72f, 0.73f, 0.75f);

    public static string PlayTime(double seconds) => $"{(int)(seconds / 3600)}:{(int)(seconds / 60 % 60):00}:{(int)(seconds % 60):00}";

    public void Build(Overlay o, int width, int height, Canvas c)
    {
        o.Clear();
        if (!Active) return;
        c.Begin(o, width, height);
        c.Backdrop(_clock);
        for (var i = 0; i < SaveSlots.Count; i++) Slot(c, i, 78 + i * 92);
        // autosave switch as an options row: steel tab, chrome value plate
        var ay = 360;
        o.RectGradient(Vector2.Round(c.P(56, ay)), Vector2.Round(c.P(206, ay + 30)), Overlay.Rgba(0.42f, 0.44f, 0.46f), Overlay.Rgba(0.16f, 0.17f, 0.18f));
        c.Text("AUTOSAVE", 66, ay + 21, 14, Canvas.White, 0, 0.18f, 0.06f, 0.2f);
        c.Plate(212, ay, 120, 30, Row == SaveSlots.Count ? 1 : 0.62f);
        c.Text(_state.Autosave ? "ON" : "OFF", 272, ay + 21, 15, Canvas.Shade(0.08f, 0.08f, 0.1f, 1), 0.5f, 0.18f, 0, 0.3f);
        c.Text(_state.Active >= 0 ? $"After every run into slot {_state.Active + 1}" : "Save to a slot first", 342, ay + 20, 10, Grey, 0, 0.1f);
        if (Row == SaveSlots.Count && _action < 0 && _name == null) c.Glow(50, ay - 5, 338, ay + 35, Canvas.Pulse(Theta));
        if (_action >= 0) ActionBar(c);
        if (_name != null) NameBox(c);
        _confirm.Draw(c, Theta);
        if (_flash > 0) c.Text(_flashText, 256, 418, 16, Style.Fade(Canvas.Yellow, MathF.Min(1, _flash * 2)), 0.5f, 0.15f, 0.1f, 0.3f);
        c.Marquee("SAVE & LOAD", true, _clock);
        Menu.Hint(c, _name != null ? "UP/DOWN or TYPE: Letter    LEFT/RIGHT: Position    DECIDE: OK    BACK: Cancel"
            : _action >= 0 ? "LEFT/RIGHT: Select    DECIDE: OK    BACK: Cancel"
            : "UP/DOWN: Select    DECIDE: Slot actions / Switch    BACK: Main menu");
        c.Fade(1 - Math.Clamp(_t / Menu.Fade, 0, 1));
    }

    private void Slot(Canvas c, int i, float y)
    {
        var m = _meta[i];
        var lit = i == Row ? 1 : 0.7f;
        c.Carbon(56, y, 456, y + 80, 1, true);
        c.Plate(66, y + 10, 88, 24, lit);
        c.Text($"SLOT {i + 1}", 110, y + 27, 13, Canvas.Shade(0.08f, 0.08f, 0.1f, 1), 0.5f, 0.18f, 0, 0.3f);
        if (_state.Active == i)
        {
            c.Diamond(72, y + 52, 4);
            c.Text("IN USE", 82, y + 56, 10, Canvas.Yellow, 0, 0.15f);
        }
        if (m == null)
        {
            c.Text("NO DATA", 300, y + 48, 18, Overlay.Rgba(1, 1, 1, 0.4f), 0.5f, 0.15f, 0.06f, 0.3f);
        }
        else
        {
            c.Fit(m.Name, 168, y + 30, 170, 0, Canvas.White, 0.15f, 0.08f, 22);
            c.Text(m.Saved.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), 444, y + 26, 10, Grey, 1, 0.1f);
            c.Text("PLAY TIME", 168, y + 50, 9, Grey, 0, 0.1f);
            c.Text(PlayTime(m.PlaySeconds), 168, y + 68, 14, Canvas.White, 0, 0.15f, 0, 0.3f);
            c.Text("RECORDS", 262, y + 50, 9, Grey, 0, 0.1f);
            c.Text($"{m.Records}", 262, y + 68, 14, Canvas.White, 0, 0.15f, 0, 0.3f);
            var extra = m.Files.Where(f => f != "settings").Select(f => f.ToUpperInvariant()).ToArray();
            c.Text("PROGRESS", 330, y + 50, 9, Grey, 0, 0.1f);
            c.Fit(extra.Length > 0 ? string.Join("  ", extra) : "TIME ATTACK", 330, y + 68, 116, 0, Canvas.White, 0.15f, 0, 12);
        }
        if (i == Row && _action < 0 && _name == null) c.Glow(50, y - 6, 462, y + 86, Canvas.Pulse(Theta));
    }

    private void ActionBar(Canvas c)
    {
        var y = 78 + Row * 92 + 84;
        if (y > 330) y = 78 + Row * 92 - 40;
        c.O.FadeText(0.5f);
        c.Carbon(30, y, 482, y + 38, 1, false);
        for (var i = 0; i < Actions.Length; i++)
            c.Button(40 + i * 88, y + 6, 80, 26, Actions[i], Actions[i] is "LOAD" or "SAVE" ? Canvas.ButtonKind.Positive : Actions[i] == "DELETE" ? Canvas.ButtonKind.Negative : Canvas.ButtonKind.Neutral);
        c.Glow(36 + _action * 88, y + 2, 124 + _action * 88, y + 36, Canvas.Pulse(Theta));
    }

    private void NameBox(Canvas c)
    {
        c.Fill(Overlay.Rgba(0, 0, 0, 0.55f));
        c.O.FadeText(0.12f);
        c.Carbon(96, 170, 416, 290);
        c.Lettering("ENTER NAME", 256, 206, 22, Canvas.White, Grey);
        for (var i = 0; i < SaveSlots.NameLength; i++)
        {
            var x = 126 + i * 26;
            var sel = i == _cursor;
            c.O.Rect(Vector2.Round(c.P(x, 222)), Vector2.Round(c.P(x + 22, 254)), sel ? Overlay.Rgba(0.8f, 0.07f, 0.06f) : Overlay.Rgba(0.12f, 0.12f, 0.13f));
            c.Text(_name![i].ToString(), x + 11, 247, 20, Canvas.White, 0.5f, 0.1f, 0, 0.3f);
            if (sel && _clock % 0.8f < 0.5f)
            {
                c.Arrow(x + 5, 218, x + 17, 218, x + 11, 211);
                c.Arrow(x + 5, 258, x + 17, 258, x + 11, 265);
            }
        }
        c.Text($"SLOT {Row + 1}", 256, 282, 10, Grey, 0.5f, 0.1f);
    }
}
