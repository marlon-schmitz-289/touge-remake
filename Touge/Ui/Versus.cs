using System.Numerics;
using Kansei.Graphics;
using Touge.Net;

namespace Touge.Ui;

/// <summary>
///     VERSUS from the main menu, in the style of the other menus (<see cref="Canvas"/>: logo backdrop, red marquee, carbon
///     panels, chrome plates, pulsing frame, original SE): mode (SPLIT SCREEN / ONLINE) → split-screen lobby, or online (host,
///     join by address, games found on the LAN, name) → connecting → online lobby. The lobby: the race on the left (course,
///     route, conditions, rule; split: screen split and player 2's controller), the players on the right (car, colour, ready,
///     ping). The host (split: player 1) chooses the race, everybody their car; START once all are ready. Then loading, and
///     after the race the standings with RETRY/REMATCH, LOBBY, EXIT. The game does what the returned <see cref="Action"/> asks
///     (sessions, loading, racing) and fills <see cref="Net"/>, <see cref="Lan"/>, <see cref="Pads"/>, <see cref="Standings"/>.
/// </summary>
public sealed class Versus(Catalog catalog)
{
    public enum Screen { None, Mode, Online, Connecting, Lobby, Loading, Result, Message }

    /// <summary>
    ///     Split: open the split-screen lobby (<see cref="OpenSplit"/>); Host/Join: open a session (<see cref="JoinAddress"/>/<see cref="JoinLan"/> for the target); Start: start the race (split:
    ///     load it; host: <see cref="NetSession.StartRace"/>); Rematch/ToLobby from the result; Leave: close the session (back to
    ///     ONLINE); Exit: back to the main menu.
    /// </summary>
    public enum Action { None, Split, Host, Join, Start, Rematch, ToLobby, Leave, Exit }

    /// <summary>A local player's choice in the lobby.</summary>
    public sealed class Seat
    {
        public int Car;
        public int Paint;
        public bool Ready;
    }

    /// <summary>One line of the standings: place, who, car, goal time (null: not at the goal), what to show for it, one of ours.</summary>
    public sealed record Standing(int Place, string Name, string Car, float? Time, string Value, bool Local);

    /// <summary>
    ///     What a standings line shows: the goal time; without one the winner of a battle decided early WIN, the others how far
    ///     behind the leader they were, a race's stragglers DNF.
    /// </summary>
    public static string ValueOf(float? time, int place, float along, float leaderAlong, bool decidedEarly) =>
        time is { } t ? Style.Time(t) : decidedEarly && place == 1 ? "WIN" : decidedEarly ? $"{MathF.Max(0, leaderAlong - along):0} m BEHIND" : "DNF";

    /// <summary>
    ///     What <paramref name="e"/> of <paramref name="r"/> shows (result sheet and logs): as <see cref="ValueOf(float?, int, float, float, bool)"/>,
    ///     a breakaway winner with his lead over the next car ("WIN +312 m").
    /// </summary>
    public static string ValueOf(Result r, ResultEntry e)
    {
        float? time = e.Time >= 0 ? e.Time : null;
        var value = ValueOf(time, e.Place, e.Along, r.Entries.Max(x => x.Along), r.Reason is "BREAKAWAY" or "OPPONENTS LEFT");
        if (time == null && e.Place == 1 && r.Reason == "BREAKAWAY" && r.Entries.Length > 1)
            value += $"  +{MathF.Max(0, e.Along - r.Entries.Where(x => x.Id != e.Id).Max(x => x.Along)):0} m";
        return value;
    }

    /// <summary>The finished race for the result screen; <paramref name="Title"/> e.g. YOU WIN!! / PLAYER 1 WINS!!.</summary>
    public sealed record Standings(string Title, bool Won, string Reason, Standing[] Lines);

    public Screen Current { get; private set; }
    public bool Active => Current != Screen.None;
    public Action<string>? Sound { get; set; }
    /// <summary>Cars not won yet (<see cref="Race.Legend.SecretCar"/>): skipped in the lobby, as in every car select.</summary>
    public Func<string, bool>? CarLocked { get; set; }

    // ---- what the lobby edits
    /// <summary>Split screen (two local players) instead of online.</summary>
    public bool Split { get; private set; }
    /// <summary>The online session (null in split screen).</summary>
    public NetSession? Net { get; set; }
    /// <summary>Split screen's race (online: the session's).</summary>
    public RaceConfig SplitConfig { get; set; } = new();
    public RaceConfig Config => Net?.Config ?? SplitConfig;
    /// <summary>Local players: [player 1, player 2] in split screen, [us] online.</summary>
    public Seat[] Seats { get; private set; } = [new()];
    /// <summary>Split screen: left/right instead of top/bottom.</summary>
    public bool Vertical { get; set; }
    /// <summary>Split screen: player 2's controller, index into the connected pads, <see cref="Pads"/> = the keyboard's arrow half.</summary>
    public int P2Device { get; set; }
    /// <summary>Connected pads (the game keeps it current).</summary>
    public int Pads { get; set; }
    /// <summary>Online: player name, address typed for JOIN, games found on the LAN, our own addresses and port (shown when hosting).</summary>
    public string Name { get; set; } = "PLAYER";
    public string Address { get; set; } = "";
    public IReadOnlyList<NetDiscovery.Game> Lan { get; set; } = [];
    public string HostInfo { get; set; } = "";
    /// <summary>UDP port hosted on and joined by default.</summary>
    public int Port { get; set; } = NetSession.DefaultPort;
    /// <summary>JOIN target: a LAN game (null: <see cref="Address"/>).</summary>
    public NetDiscovery.Game? JoinLan { get; private set; }
    public Standings? Result { get; set; }

    private int _mode, _row, _row2, _editing; // _editing: 0 no, 1 address, 2 name, 3 port
    private string _portText = "";
    private const int FixedRows = 4; // HOST, JOIN, NAME, PORT; the LAN games follow
    private string _message = "";
    private Screen _afterMessage;
    private float _t, _clock, _p2Flash = -1;

    public const float Fade = Menu.Fade;

    // ------------------------------------------------------------ opening

    public void Open()
    {
        (Net, Result) = (null, null);
        Enter(Screen.Mode);
    }

    public void Close() => Current = Screen.None;

    /// <summary>The screen has faded in (loading: the white screen is up, the game may block now).</summary>
    public bool Shown => _t >= Fade;

    /// <summary>Skips the fade-in and entrance (screenshots).</summary>
    public void Settle() => _t = 10;

    /// <summary>--bot guest: ready at once.</summary>
    public void ForceReady()
    {
        Seats[0].Ready = true;
        PushSeat();
    }

    /// <summary>The lobby of a split-screen game: player 1 with the saved car, player 2 with another.</summary>
    public void OpenSplit(string car, int paint)
    {
        Split = true;
        Net = null;
        var c = CarIndex(car);
        Seats = [new Seat { Car = c, Paint = paint, Ready = true }, new Seat { Car = NextCar(c, 1) }];
        P2Device = Pads > 0 ? Pads - 1 : Pads;
        Enter(Screen.Lobby);
    }

    /// <summary>The lobby of <paramref name="net"/> (hosting, or joined).</summary>
    public void OpenLobby(NetSession net, string car, int paint)
    {
        (Split, Net) = (false, net);
        Seats = [new Seat { Car = CarIndex(car), Paint = paint, Ready = net.IsHost }];
        PushSeat();
        Enter(Screen.Lobby);
    }

    public void ShowOnline() => Enter(Screen.Online);
    public void ShowConnecting() => Enter(Screen.Connecting);
    public void ShowLoading() => Enter(Screen.Loading);

    public void ShowResult(Standings s)
    {
        Result = s;
        Enter(Screen.Result);
    }

    /// <summary>A message (connection lost, refused …); DECIDE goes on to <paramref name="then"/>.</summary>
    public void ShowMessage(string text, Screen then)
    {
        (_message, _afterMessage) = (text, then);
        Enter(Screen.Message);
    }

    /// <summary>Back in the lobby after a race: seats not ready again (online: the host's choice stays).</summary>
    public void BackToLobby()
    {
        if (Split) Seats[1].Ready = false;
        else
        {
            Seats[0].Ready = Net?.IsHost == true;
            PushSeat();
        }
        Enter(Screen.Lobby);
    }

    private void Enter(Screen s) => (Current, _t, _row, _row2, _editing) = (s, 0, s == Current ? _row : 0, 0, 0);

    private int CarIndex(string id) => catalog.Cars.ToList().FindIndex(c => c.Id == id) is var i && i >= 0 && CarLocked?.Invoke(id) != true ? i : 0;

    /// <summary>The next car from <paramref name="car"/> in direction <paramref name="d"/> (±1), past locked ones.</summary>
    private int NextCar(int car, int d)
    {
        do car = Wrap(car + d, catalog.Cars.Count);
        while (CarLocked?.Invoke(catalog.Cars[car].Id) == true);
        return car;
    }

    public string CarId(int seat) => catalog.Cars[Seats[seat].Car].Id;

    private void PushSeat() => Net?.SetLocal(CarId(0), (byte)Seats[0].Paint, Seats[0].Ready);

    // ------------------------------------------------------------ race choice

    private Catalog.Course CourseOf(RaceConfig c) => catalog.Courses.FirstOrDefault(x => c.CourseTime.StartsWith(x.Id + "_")) ?? catalog.Courses[0];

    /// <summary>Conditions a course offers: its times of day (rain as WET), fog over the day and night course.</summary>
    public static (string Label, string Time, bool Fog)[] Conditions(Catalog.Course c) =>
    [
        .. c.Times.Contains("DAY") ? new[] { ("DAY", "DAY", false) } : [],
        .. c.Times.Contains("NIT") ? new[] { ("NIGHT", "NIT", false) } : [],
        .. c.Times.Contains("RIN") ? new[] { ("WET", "RIN", false) } : [],
        .. c.Times.Contains("DAY") ? new[] { ("DAY FOG", "DAY", true) } : [],
        .. c.Times.Contains("NIT") ? new[] { ("NIGHT FOG", "NIT", true) } : [],
    ];

    private int ConditionIndex(RaceConfig c)
    {
        var all = Conditions(CourseOf(c));
        var time = c.CourseTime[(c.CourseTime.LastIndexOf('_') + 1)..];
        return Math.Max(0, Array.FindIndex(all, x => x.Time == time && x.Fog == c.Fog));
    }

    private static int Wrap(int i, int n) => n == 0 ? 0 : (i % n + n) % n;

    /// <summary>The race choice moved by <paramref name="d"/> on <paramref name="row"/>.</summary>
    private RaceConfig Change(RaceConfig c, Row row, int d)
    {
        var course = CourseOf(c);
        switch (row)
        {
            case Row.Course:
            {
                var i = Wrap(catalog.Courses.ToList().IndexOf(course) + d, catalog.Courses.Count);
                var next = catalog.Courses[i];
                var cond = Conditions(next);
                var keep = Array.FindIndex(cond, x => x.Label == Conditions(course)[ConditionIndex(c)].Label);
                var (_, time, fog) = cond[Math.Max(0, keep)];
                return c with { CourseTime = $"{next.Id}_{time}", Fog = fog };
            }
            case Row.Route:
                return c with { Reverse = !c.Reverse };
            case Row.Conditions:
            {
                var cond = Conditions(course);
                var (_, time, fog) = cond[Wrap(ConditionIndex(c) + d, cond.Length)];
                return c with { CourseTime = $"{course.Id}_{time}", Fog = fog };
            }
            case Row.Rule:
                return c with { Rule = c.Rule == NetRule.Battle ? NetRule.Race : NetRule.Battle };
        }
        return c;
    }

    internal enum Row { Course, Route, Conditions, Rule, Layout, P2Pad, Car, Colour, Go }

    /// <summary>Player 1's / our rows: the race (host or split), split's screen and pad, our car, then START or READY.</summary>
    internal Row[] Rows => Split ? [Row.Course, Row.Route, Row.Conditions, Row.Rule, Row.Layout, Row.P2Pad, Row.Car, Row.Colour, Row.Go]
        : Net?.IsHost == true ? [Row.Course, Row.Route, Row.Conditions, Row.Rule, Row.Car, Row.Colour, Row.Go]
        : [Row.Car, Row.Colour, Row.Go];

    private static readonly Row[] P2Rows = [Row.Car, Row.Colour, Row.Go];

    /// <summary>Everybody may go: split — player 2 ready; online — the session says so.</summary>
    public bool CanStart => Split ? Seats[1].Ready : Net?.CanStart == true;

    // ------------------------------------------------------------ input

    /// <summary>Text keys of a frame (typing an address or name).</summary>
    public readonly record struct TextKeys(string Typed, bool Backspace, bool Enter, bool Escape);

    /// <summary>
    ///     One frame: <paramref name="k1"/> player 1 (online: every device), <paramref name="k2"/> player 2 in the split-screen
    ///     lobby, <paramref name="text"/> while typing. Returns what the game should do.
    /// </summary>
    public Action Update((int X, int Y, bool Ok, bool Back) k1, (int X, int Y, bool Ok, bool Back) k2, TextKeys text, float dt)
    {
        if (!Active) return Action.None;
        dt = MathF.Min(dt, 1 / 20f);
        (_t, _clock) = (_t + dt, _clock + dt);
        if (_p2Flash >= 0) _p2Flash += dt;
        if (_t < Fade * 0.5f) return Action.None; // a decision that opened this screen does not act on it
        switch (Current)
        {
            case Screen.Mode:
                if (k1.X != 0 || k1.Y != 0)
                {
                    _mode = 1 - _mode;
                    Sound?.Invoke("SYS005");
                }
                else if (k1.Ok)
                {
                    Sound?.Invoke("SYS006");
                    if (_mode == 0) return Action.Split; // the game opens the split lobby (it knows the pads)
                    Enter(Screen.Online);
                }
                else if (k1.Back)
                {
                    Sound?.Invoke("BEEP001");
                    return Action.Exit;
                }
                break;
            case Screen.Online:
                return OnlineInput(k1, text);
            case Screen.Connecting:
                if (k1.Back)
                {
                    Sound?.Invoke("BEEP001");
                    return Action.Leave;
                }
                break;
            case Screen.Lobby:
                return LobbyInput(k1, k2);
            case Screen.Result:
                if (_t < 1) break;
                var buttons = ResultButtons;
                if (k1.X != 0)
                {
                    var n = Math.Clamp(_row + k1.X, 0, buttons.Length - 1);
                    if (n != _row) Sound?.Invoke("SYS005");
                    _row = n;
                }
                else if (k1.Ok)
                {
                    Sound?.Invoke("SYS006");
                    return buttons[_row].Then;
                }
                break;
            case Screen.Message:
                if (k1.Ok || k1.Back)
                {
                    Sound?.Invoke(k1.Ok ? "SYS006" : "BEEP001");
                    Enter(_afterMessage);
                    if (_afterMessage == Screen.None) return Action.Exit;
                }
                break;
        }
        return Action.None;
    }

    /// <summary>ONLINE: HOST, JOIN (address), NAME, PORT, then the LAN games.</summary>
    private Action OnlineInput((int X, int Y, bool Ok, bool Back) k, TextKeys text)
    {
        if (_editing == 3)
        {
            foreach (var ch in text.Typed)
                if (char.IsAsciiDigit(ch) && _portText.Length < 5) _portText += ch;
            if (text.Backspace && _portText.Length > 0) _portText = _portText[..^1];
            if (text.Escape || text.Enter)
            {
                var ok = text.Enter && int.TryParse(_portText, out var port) && port is >= 1024 and <= 65535;
                Sound?.Invoke(ok ? "SYS006" : "BEEP001");
                if (ok) Port = int.Parse(_portText);
                _editing = 0;
            }
            return Action.None;
        }
        if (_editing != 0)
        {
            var value = _editing == 1 ? Address : Name;
            foreach (var ch in text.Typed)
                if (value.Length < (_editing == 1 ? 40 : 16) && (_editing == 1 ? char.IsAsciiLetterOrDigit(ch) || ch is '.' or ':' or '-' : ch >= ' ' && ch < 127))
                    value += _editing == 1 ? ch : char.ToUpperInvariant(ch);
            if (text.Backspace && value.Length > 0) value = value[..^1];
            if (_editing == 1) Address = value;
            else Name = value;
            if (text.Escape || text.Enter)
            {
                var join = _editing == 1 && text.Enter && Address.Trim().Length > 0;
                Sound?.Invoke(text.Enter ? "SYS006" : "BEEP001");
                _editing = 0;
                Name = Name.Trim().Length == 0 ? "PLAYER" : Name.Trim();
                if (join)
                {
                    JoinLan = null;
                    return Action.Join;
                }
            }
            return Action.None;
        }
        var rows = FixedRows + Lan.Count;
        if (k.Y != 0)
        {
            var n = Math.Clamp(_row + k.Y, 0, rows - 1);
            if (n != _row) Sound?.Invoke("SYS005");
            _row = n;
        }
        else if (k.Ok)
        {
            Sound?.Invoke("SYS006");
            switch (_row)
            {
                case 0: return Action.Host;
                case 1:
                case 2:
                case 3:
                    (_editing, _portText) = (_row, "");
                    break;
                default:
                    JoinLan = Lan[_row - FixedRows];
                    return Action.Join;
            }
        }
        else if (k.Back)
        {
            Sound?.Invoke("BEEP001");
            Enter(Screen.Mode);
        }
        return Action.None;
    }

    private Action LobbyInput((int X, int Y, bool Ok, bool Back) k1, (int X, int Y, bool Ok, bool Back) k2)
    {
        var rows = Rows;
        _row = Math.Min(_row, rows.Length - 1);
        if (k1.Y != 0)
        {
            var n = Math.Clamp(_row + k1.Y, 0, rows.Length - 1);
            if (n != _row) Sound?.Invoke("SYS005");
            _row = n;
        }
        else if (k1.X != 0 && rows[_row] != Row.Go)
        {
            Sound?.Invoke("SYS005");
            Edit(rows[_row], 0, k1.X);
        }
        else if (k1.Ok && rows[_row] == Row.Go)
        {
            if (Split || Net?.IsHost == true)
            {
                if (!CanStart)
                {
                    Sound?.Invoke("BEEP001");
                    _p2Flash = 0;
                }
                else
                {
                    Sound?.Invoke("SYS006");
                    return Action.Start;
                }
            }
            else
            {
                Sound?.Invoke(Seats[0].Ready ? "BEEP001" : "SYS006");
                Seats[0].Ready = !Seats[0].Ready;
                PushSeat();
            }
        }
        else if (k1.Ok)
        {
            Sound?.Invoke("SYS005");
            Edit(rows[_row], 0, 1);
        }
        else if (k1.Back)
        {
            Sound?.Invoke("BEEP001");
            if (Split)
            {
                Enter(Screen.Mode);
                return Action.None;
            }
            return Action.Leave;
        }
        if (!Split) return Action.None;
        // player 2: own cursor over car, colour, ready
        if (k2.Y != 0)
        {
            var n = Math.Clamp(_row2 + k2.Y, 0, P2Rows.Length - 1);
            if (n != _row2) Sound?.Invoke("SYS005");
            _row2 = n;
        }
        else if (k2.X != 0 && P2Rows[_row2] != Row.Go)
        {
            Sound?.Invoke("SYS005");
            Edit(P2Rows[_row2], 1, k2.X);
        }
        else if (k2.Ok)
        {
            if (P2Rows[_row2] == Row.Go)
            {
                Seats[1].Ready = !Seats[1].Ready;
                Sound?.Invoke(Seats[1].Ready ? "SYS006" : "BEEP001");
            }
            else
            {
                Sound?.Invoke("SYS005");
                Edit(P2Rows[_row2], 1, 1);
            }
        }
        else if (k2.Back && Seats[1].Ready)
        {
            Sound?.Invoke("BEEP001");
            Seats[1].Ready = false;
        }
        return Action.None;
    }

    private void Edit(Row row, int seat, int d)
    {
        var s = Seats[seat];
        switch (row)
        {
            case Row.Car:
                (s.Car, s.Paint) = (NextCar(s.Car, d), 0);
                break;
            case Row.Colour:
                s.Paint = Wrap(s.Paint + d, catalog.Cars[s.Car].Paints.Length);
                break;
            case Row.Layout:
                Vertical = !Vertical;
                return;
            case Row.P2Pad:
                P2Device = Wrap(P2Device + d, Pads + 1);
                return;
            default:
                if (Split) SplitConfig = Change(SplitConfig, row, d);
                else Net?.SetConfig(Change(Net.Config, row, d));
                return;
        }
        if (seat == 0 && !Split) PushSeat();
    }

    private (string Label, Action Then)[] ResultButtons =>
        Split ? [("RETRY", Action.Rematch), ("LOBBY", Action.ToLobby), ("EXIT", Action.Exit)]
        : Net?.IsHost == true ? [("REMATCH", Action.Rematch), ("LOBBY", Action.ToLobby), ("LEAVE", Action.Leave)]
        : [("LEAVE", Action.Leave)];

    /// <summary>Menu BGM.AFS track: the course flow's "LIVE IN TOKYO" in the lobby, silence while loading, "JOY" on the result.</summary>
    public string? Music => Current switch
    {
        Screen.None => null, Screen.Loading => null, Screen.Result => "JOY.adx", _ => "TOKYO.adx",
    };

    private bool P2Keyboard => P2Device >= Pads;

    /// <summary>Player 2's controller as shown.</summary>
    public string P2DeviceName => P2Device < Pads ? $"GAMEPAD {P2Device + 1}" : "KEYBOARD - ARROWS";

    // ------------------------------------------------------------ drawing

    private readonly Canvas _c = new();
    private float Theta => _clock * 300 % 360;
    private static readonly uint Grey = Overlay.Rgba(0.72f, 0.73f, 0.75f), Ink = Canvas.Shade(0.08f, 0.08f, 0.09f, 1),
        ReadyGreen = Overlay.Rgba(0.15f, 0.75f, 0.3f), P2Blue = Overlay.Rgba(0.25f, 0.65f, 1);

    public void Build(Overlay o, int width, int height)
    {
        o.Clear();
        if (!Active) return;
        var c = _c;
        c.Begin(o, width, height);
        switch (Current)
        {
            case Screen.Mode:
                c.Backdrop(_clock);
                ModeScreen(c);
                c.Marquee("VERSUS", false, _clock);
                break;
            case Screen.Online:
                c.Backdrop(_clock);
                OnlineScreen(c);
                c.Marquee("ONLINE", false, _clock);
                break;
            case Screen.Connecting:
                c.Backdrop(_clock);
                c.Carbon(96, 170, 416, 290);
                var dots = new string('.', 1 + (int)(_clock * 3) % 3);
                c.Lettering("CONNECTING" + dots, 256, 220, 26, Canvas.White, Grey, 0.5f, 0.15f);
                c.Text(JoinLan is { } g ? $"{g.Host}  {g.EndPoint}" : Address, 256, 256, 13, Canvas.White, 0.5f, 0.12f);
                Menu.Hint(c, "BACK: Cancel");
                c.Marquee("ONLINE", false, _clock);
                break;
            case Screen.Lobby:
                c.Backdrop(_clock);
                LobbyScreen(c);
                c.Marquee(Split ? "SPLIT SCREEN" : "LOBBY", false, _clock);
                break;
            case Screen.Loading:
                c.Fill(Canvas.White);
                c.Text("Now Loading...", 476, 428, 15, Overlay.Rgba(0.92f, 0.08f, 0.06f), 1, 0.22f, 0, 0.4f);
                if (!Split && Net != null)
                    c.Text($"Waiting for {Net.Players.Count(p => p.Connected && p.LoadedRace != Net.RaceId)} player(s)", 476, 404, 11,
                        Overlay.Rgba(0.4f, 0.4f, 0.42f), 1, 0.15f);
                break;
            case Screen.Result:
                ResultScreen(c);
                break;
            case Screen.Message:
                c.Backdrop(_clock);
                c.Carbon(76, 170, 436, 300);
                c.Lettering(_message, 256, 228, MathF.Min(30, 330 * c.Kx / o.Font!.Measure(_message, c.Ky)), Overlay.Rgba(1, 0.55f, 0.3f), Canvas.WordRed, 0.5f, 0.15f);
                c.Button(196, 254, 120, 30, "OK", Canvas.ButtonKind.Positive);
                c.Glow(192, 250, 320, 288, Canvas.Pulse(Theta));
                c.Marquee("VERSUS", false, _clock);
                break;
        }
        c.Fade(1 - Math.Clamp(_t / Fade, 0, 1));
    }

    private void ModeScreen(Canvas c)
    {
        string[] titles = ["SPLIT SCREEN", "ONLINE"];
        string[] subs = ["Two players on this screen", "2 to 4 players over the network"];
        for (var i = 0; i < 2; i++)
        {
            var x = 46 + i * 216;
            var sel = i == _mode;
            c.Plate(x, 150, 204, 120, sel ? 1 : 0.62f);
            c.Carbon(x + 14, 164, x + 190, 224, 1, false);
            c.Lettering(titles[i], x + 102, 206, MathF.Min(24, 160 * c.Kx / c.O.Font!.Measure(titles[i], c.Ky)),
                i == 0 ? Overlay.Rgba(1, 0.55f, 0.3f) : Overlay.Rgba(0.45f, 0.6f, 1), i == 0 ? Canvas.WordRed : Canvas.WordBlue, 0.5f, 0.15f, false, true, sel ? 1 : 0.6f);
            c.Fit(subs[i], x + 102, 250, 180, 0.5f, Canvas.Shade(0.08f, 0.08f, 0.09f, 1), 0.1f, 0, 12);
            if (sel) c.Glow(x - 6, 144, x + 210, 276, Canvas.Pulse(Theta));
        }
        c.Text(_mode == 0 ? "Player 1: keyboard, wheel or pad   -   Player 2: a second pad or the arrow keys"
            : "Host a game for your friends, or join one on your network or by address", 256, 316, 12, Canvas.White, 0.5f, 0.12f, 0.08f);
        Menu.Hint(c, "LEFT/RIGHT: Select    DECIDE: OK    BACK: Main menu");
    }

    private void OnlineScreen(Canvas c)
    {
        c.Carbon(56, 76, 456, 384);
        var rows = new List<(string Label, string Value)>
        {
            ("HOST A GAME", ""),
            ("JOIN BY ADDRESS", _editing == 1 ? Address + Cursor : Address.Length > 0 ? Address : "TYPE IP[:PORT]"),
            ("YOUR NAME", _editing == 2 ? Name + Cursor : Name),
            ("UDP PORT", _editing == 3 ? _portText + Cursor : Port.ToString()),
        };
        foreach (var g in Lan) rows.Add((g.Host, $"{CourseName(g.CourseTime)}   {g.Players}/{g.Max}{(g.Phase != Phase.Lobby ? "  RACING" : "")}"));
        c.Rule(66, 446, 228);
        c.Text("GAMES ON YOUR NETWORK", 72, 244, 11, Grey, 0, 0.12f);
        for (var i = 0; i < rows.Count; i++)
        {
            var y = 104 + i * 34 + (i >= FixedRows ? 30 : 0);
            if (y > 340) break;
            var sel = i == _row;
            c.Plate(72, y - 18, 368, 28, sel ? 1 : 0.62f);
            c.Text(rows[i].Label, 86, y + 1, 13, Ink, 0, 0.15f);
            c.Fit(rows[i].Value, 426, y + 1, 220, 1, Ink, 0.08f, 0, 13);
            if (sel) c.Glow(66, y - 24, 446, y + 16, _editing != 0 ? 1 : Canvas.Pulse(Theta));
        }
        if (Lan.Count == 0) c.Text("Searching" + new string('.', 1 + (int)(_clock * 2) % 3), 256, 290, 12, Grey, 0.5f, 0.12f);
        c.Text($"Over the internet the host forwards UDP port {Port} to their computer (README)", 256, 372, 10, Grey, 0.5f, 0.1f);
        Menu.Hint(c, _editing != 0 ? "TYPE    ENTER: OK    ESC: Cancel" : "UP/DOWN: Select    DECIDE: OK    BACK: Return");
    }

    private string Cursor => _clock % 1 < 0.5f ? "_" : " ";

    private string CourseName(string courseTime)
    {
        var course = catalog.Courses.FirstOrDefault(x => courseTime.StartsWith(x.Id + "_"));
        return course == null ? courseTime : $"{course.Name} {Catalog.TimeName(courseTime[(courseTime.LastIndexOf('_') + 1)..])}";
    }

    private string RowLabel(Row r) => r switch
    {
        Row.Course => "COURSE", Row.Route => "ROUTE", Row.Conditions => "CONDITIONS", Row.Rule => "RULE", Row.Layout => "SCREEN",
        Row.P2Pad => "PLAYER 2", Row.Car => "CAR", Row.Colour => "COLOUR", _ => "",
    };

    private string RowValue(Row r, int seat)
    {
        var cfg = Config;
        var course = CourseOf(cfg);
        return r switch
        {
            Row.Course => course.Name, Row.Route => Catalog.DirectionName(course, cfg.Reverse), Row.Conditions => Conditions(course)[ConditionIndex(cfg)].Label,
            Row.Rule => cfg.Rule == NetRule.Battle ? "BATTLE" : "RACE", Row.Layout => Vertical ? "LEFT / RIGHT" : "TOP / BOTTOM",
            Row.P2Pad => P2DeviceName, Row.Car => catalog.Cars[Seats[seat].Car].Name, Row.Colour => $"{Seats[seat].Paint + 1} / {catalog.Cars[Seats[seat].Car].Paints.Length}",
            _ => "",
        };
    }

    private void LobbyScreen(Canvas c)
    {
        var o = c.O;
        // the race (left): rows of the host/player 1, read-only for an online guest
        c.Carbon(16, 72, 250, 404);
        var host = Split || Net?.IsHost == true;
        c.Text("RACE", 30, 94, 12, Grey, 0, 0.15f);
        Row[] info = [Row.Course, Row.Route, Row.Conditions, Row.Rule];
        var rows = Rows;
        var y = 118f;
        foreach (var r in host ? rows : [.. info, .. rows])
        {
            if (r == Row.Car)
            {
                y += 6;
                c.Rule(26, 240, y - 14);
                c.Text(Split ? "PLAYER 1" : "YOUR CAR", 30, y, 12, Grey, 0, 0.15f);
                y += 22;
            }
            if (r == Row.Go)
            {
                var label = host ? "START" : Seats[0].Ready ? "READY!" : "READY?";
                var gy = 368f;
                c.Button(42, gy - 20, 182, 30, label, host ? CanStart ? Canvas.ButtonKind.Positive : Canvas.ButtonKind.Neutral
                    : Seats[0].Ready ? Canvas.ButtonKind.Positive : Canvas.ButtonKind.Negative);
                if (rows[_row] == r) c.Glow(38, gy - 24, 228, gy + 14, Canvas.Pulse(Theta));
                continue;
            }
            var editable = Array.IndexOf(rows, r) >= 0;
            var sel = editable && rows[_row] == r;
            c.Text(RowLabel(r), 30, y, 10, Grey, 0, 0.1f);
            var value = RowValue(r, 0);
            c.Fit(value, sel ? 222 : 236, y + 1, 128, 1, editable ? Canvas.White : Overlay.Rgba(1, 1, 1, 0.75f), 0.12f, 0.06f, 14);
            if (r == Row.Colour) Swatch(c, 116, y - 4, catalog.Cars[Seats[0].Car].Paints[Seats[0].Paint]);
            if (sel)
            {
                c.Arrow(228, y - 9, 228, y + 1, 236, y - 4);
                c.Arrow(94, y - 9, 94, y + 1, 86, y - 4);
                c.Glow(22, y - 18, 244, y + 8, Canvas.Pulse(Theta));
            }
            y += 26;
        }
        if (Config.Rule == NetRule.Battle && !Split && Net != null && Net.Players.Count > 2)
            c.Text("BATTLE needs 2 players: RACE rules", 30, 336, 9.5f, Overlay.Rgba(1, 0.6f, 0.3f), 0, 0.1f);

        // the players (right)
        if (Split) SplitCards(c);
        else OnlineCards(c);
        if (!Split && Net?.IsHost == true && HostInfo.Length > 0) c.Text($"HOSTING  {HostInfo}", 270, 420, 10, Canvas.White, 0, 0.12f, 0.08f);
        Menu.Hint(c, Split ? P2Keyboard ? "P1: WASD + SPACE    P2: ARROWS + ENTER    START when both are ready    BACK: Return"
                : "P1: ARROWS + ENTER    P2: D-PAD + (A)    START when both are ready    BACK: Return"
            : host ? "UP/DOWN: Select    LEFT/RIGHT: Change    START when everyone is ready    BACK: Leave"
            : "UP/DOWN: Select    LEFT/RIGHT: Change    READY: tell the host    BACK: Leave");
    }

    private static void Swatch(Canvas c, float x, float y, uint bgr)
    {
        c.O.Disc(c.P(x, y), 7 * c.S, Overlay.Rgba(0, 0, 0, 0.9f));
        c.O.Disc(c.P(x, y), 5.5f * c.S, Catalog.Swatch(bgr));
    }

    /// <summary>One player card: plate with the name, car, colour, status (and ping online).</summary>
    private void Card(Canvas c, float y, float h, string name, string tag, uint tagColour, int car, int paint, string? status, uint statusColour, string? ping)
    {
        c.Carbon(262, y, 496, y + h, 1, false);
        c.Plate(268, y + 6, 222, 24, 1);
        c.Fit(name, 280, y + 23, 150, 0, Ink, 0.15f, 0, 14);
        c.Fit(tag, 470, y + 23, 120, 1, tagColour, 0.15f, 0, 11);
        var info = catalog.Cars[car];
        c.Fit(info.Name, 276, y + 50, 200, 0, Canvas.White, 0.12f, 0.06f, 13);
        Swatch(c, 283, y + 62, info.Paints[Math.Min(paint, info.Paints.Length - 1)]);
        c.Text($"{info.Drive}  {info.Ps} PS  {info.Kg} kg", 296, y + 66, 10, Grey, 0, 0.1f);
        if (status != null) c.Text(status, 484, y + 66, 13, statusColour, 1, 0.15f, 0.06f, 0.3f);
        if (ping != null) c.Text(ping, 484, y + 50, 10, Grey, 1, 0.1f);
    }

    private void SplitCards(Canvas c)
    {
        Card(c, 72, 78, "PLAYER 1", P2Keyboard ? "WASD / WHEEL / PADS" : "KEYBOARD / WHEEL", Overlay.Rgba(0.3f, 0.3f, 0.32f), Seats[0].Car, Seats[0].Paint, "READY", ReadyGreen, null);
        // player 2's card carries its own rows and cursor (blue)
        var y = 162f;
        c.Carbon(262, y, 496, y + 210, 1, false);
        c.Plate(268, y + 6, 222, 24, 1);
        c.Text("PLAYER 2", 280, y + 23, 14, Ink, 0, 0.15f);
        c.Fit(P2DeviceName, 470, y + 23, 110, 1, Overlay.Rgba(0.1f, 0.3f, 0.75f), 0.15f, 0, 11);
        var s = Seats[1];
        for (var i = 0; i < P2Rows.Length; i++)
        {
            var ry = y + 60 + i * 34;
            var r = P2Rows[i];
            var sel = i == _row2;
            if (r == Row.Go)
            {
                c.Button(300, ry - 18, 160, 28, s.Ready ? "READY!" : "READY?", s.Ready ? Canvas.ButtonKind.Positive : Canvas.ButtonKind.Negative);
                if (sel) P2Frame(c, 296, ry - 22, 464, ry + 14);
                continue;
            }
            c.Text(RowLabel(r), 276, ry, 10, Grey, 0, 0.1f);
            c.Fit(RowValue(r, 1), 482, ry + 1, 150, 1, Canvas.White, 0.12f, 0.06f, 14);
            if (r == Row.Colour) Swatch(c, 360, ry - 4, catalog.Cars[s.Car].Paints[s.Paint]);
            if (sel) P2Frame(c, 268, ry - 18, 490, ry + 8);
        }
        var info = catalog.Cars[s.Car];
        c.Text($"{info.Drive}  {info.Ps} PS  {info.Kg} kg", 276, y + 196, 10, Grey, 0, 0.1f);
        string[] help = P2Keyboard ? ["ARROWS drive   R-CTRL handbrake", "R-SHIFT / R-ALT gears   BACKSPACE reset   ENTER camera"]
            : ["STICK steer   TRIGGERS throttle/brake   A handbrake", "BUMPERS gears   Y reset   BACK camera   START pause"];
        for (var i = 0; i < help.Length; i++) c.Fit(help[i], 379, y + 160 + i * 14, 210, 0.5f, Grey, 0.08f, 0, 10);
        if (_p2Flash is >= 0 and < 1.5f && !s.Ready)
            c.Text("PLAYER 2: PRESS DECIDE ON READY", 379, 392, 11, Style.Fade(Overlay.Rgba(1, 0.6f, 0.3f), 1.5f - _p2Flash), 0.5f, 0.12f, 0.08f);
    }

    /// <summary>Player 2's cursor: the pulsing frame in blue.</summary>
    private void P2Frame(Canvas c, float x0, float y0, float x1, float y1)
    {
        var pulse = Canvas.Pulse(Theta + 180);
        Vector2 min = c.P(x0, y0), max = c.P(x1, y1);
        var r = MathF.Min(7 * c.Kx, (max.Y - min.Y) / 3);
        c.RoundRect(min, max, r, 9 * c.S, Style.Fade(P2Blue, 0.15f * pulse));
        c.RoundRect(min, max, r, 4 * c.S, Style.Fade(P2Blue, 0.5f + 0.5f * pulse));
    }

    private void OnlineCards(Canvas c)
    {
        var net = Net!;
        var players = net.Players.Where(p => p.Connected).ToArray();
        for (var i = 0; i < NetSession.MaxPlayers; i++)
        {
            var y = 72 + i * 84f;
            if (i >= players.Length)
            {
                c.Carbon(262, y, 496, y + 78, 0.5f, false);
                c.Text("OPEN", 379, y + 46, 14, Overlay.Rgba(1, 1, 1, 0.3f), 0.5f, 0.15f);
                continue;
            }
            var p = players[i];
            var car = p.IsLocal ? Seats[0].Car : CarIndex(p.Car);
            var paint = p.IsLocal ? Seats[0].Paint : p.Paint;
            var ready = p.IsLocal ? Seats[0].Ready : p.Ready;
            var tag = p.Id == 0 ? "HOST" : p.IsLocal ? "YOU" : $"P{p.Id + 1}";
            var ping = p.IsLocal ? net.IsHost ? null : $"{p.PingMs} ms" : p.Id == 0 ? null : $"{p.PingMs} ms";
            Card(c, y, 78, p.IsLocal ? $"{p.Name} (YOU)" : p.Name, tag, p.Id == 0 ? Overlay.Rgba(0.75f, 0.45f, 0) : Overlay.Rgba(0.3f, 0.3f, 0.32f), car, paint,
                p.Id == 0 ? null : ready ? "READY" : "NOT READY", ready ? ReadyGreen : Overlay.Rgba(1, 1, 1, 0.45f), ping);
        }
    }

    private void ResultScreen(Canvas c)
    {
        var r = Result!;
        c.Fill(Overlay.Rgba(0, 0, 0, 0.35f));
        var pop = Style.Ease(_t / 0.3f);
        c.Lettering(r.Title, 256, 112, 46 * (1.6f - 0.6f * pop), r.Won ? Overlay.Rgba(1, 0.85f, 0.3f) : Overlay.Rgba(0.55f, 0.65f, 1),
            r.Won ? Overlay.Rgba(1, 0.38f, 0) : Canvas.WordBlue, 0.5f, 0.2f, false, true, pop);
        c.Sheet(30, 150, 482, 160 + 34 * Math.Max(2, r.Lines.Length) + 20, "Result");
        c.Text(r.Reason, 476, 140, 11, Canvas.White, 1, 0.15f, 0.06f);
        float? best = r.Lines.Where(l => l.Time != null).Select(l => l.Time).Min();
        for (var i = 0; i < r.Lines.Length; i++)
        {
            var l = r.Lines[i];
            var a = Style.Ease((_t - Menu.RowFirst - Menu.RowStep * i) / 0.15f);
            var y = 186 + i * 34f;
            c.Rule(30, 482, y + 10, 1);
            if (a <= 0) continue;
            var ink = Style.Fade(l.Local ? Canvas.Yellow : Canvas.White, a);
            c.Lettering(l.Place switch { 1 => "1ST", 2 => "2ND", 3 => "3RD", _ => $"{l.Place}TH" }, 44, y + 2, 20, Style.Fade(l.Place == 1 ? Overlay.Rgba(1, 0.85f, 0.3f) : Canvas.White, a),
                Style.Fade(l.Place == 1 ? Overlay.Rgba(1, 0.38f, 0) : Grey, a), 0, 0.2f, false, true, a);
            c.Fit(l.Name, 110, y + 1, 120, 0, ink, 0.15f, 0.06f, 15);
            c.Fit(l.Car, 238, y + 1, 110, 0, Style.Fade(Grey, a), 0.1f, 0, 11);
            c.Text(l.Value, 470, y + 1, 15, ink, 1, 0.15f, 0.06f, 0.3f);
            if (l.Time is { } tt && best is { } b && tt > b) c.Text(Style.Delta(tt - b), 376, y + 1, 11, Style.Fade(Style.Red, a), 1, 0.1f);
        }
        var buttons = ResultButtons;
        var bAlpha = Style.Ease((_t - 1) / 0.2f);
        if (bAlpha <= 0) return;
        var x0 = 256 - buttons.Length * 59;
        for (var i = 0; i < buttons.Length; i++)
            c.Button(x0 + i * 118 + 4, 392, 110, 30, buttons[i].Label, i == 0 && buttons.Length > 1 ? Canvas.ButtonKind.Positive : i == buttons.Length - 1 ? Canvas.ButtonKind.Negative : Canvas.ButtonKind.Neutral, bAlpha);
        var x = x0 + _row * 118 + 4;
        c.Glow(x - 4, 388, x + 114, 426, Canvas.Pulse(Theta), bAlpha);
        if (!Split && Net?.IsHost == false) c.Text("Waiting for the host: rematch or lobby", 256, 372, 11, Grey, 0.5f, 0.12f);
    }
}

/// <summary>
///     Versus HUD of one view (top right, opposite the timing panel, where the battle HUD sits): our place as 1ST/2ND… and every
///     player with the gap along the course and, online, the ping; a red marker on the lap gauge comes from <see cref="Hud.Rival"/>.
/// </summary>
public static class VersusHud
{
    public const float W = 300;

    /// <summary>Panel height (HUD units) for <paramref name="players"/> lines.</summary>
    public static float Height(int players) => 74 + 28 * players;

    /// <summary>The verdict over the view once the race is decided: WIN!! / LOSE / place, popping in, <paramref name="t"/> s since.</summary>
    public static void Verdict(Overlay o, int width, int height, string text, bool good, float t)
    {
        var g = Style.Safe(width, height);
        var u = g.U;
        var a = Style.Ease(t * 4);
        var cx = width / 2f;
        var y = height * 0.3f;
        var size = 96 * u * (1.4f - 0.4f * Style.Ease(t * 5));
        Style.Slanted(o, new Vector2(cx - 300 * u, y - 100 * u), new Vector2(cx + 300 * u, y + 30 * u),
            Style.Fade(good ? Overlay.Rgba(0.6f, 0.3f, 0, 0.75f) : Overlay.Rgba(0.04f, 0.1f, 0.4f, 0.75f), a), 0.15f);
        Style.Label(o, text, new Vector2(cx, y), size, Style.Fade(good ? Style.Amber : Overlay.Rgba(0.6f, 0.75f, 1), a), 0.5f, Style.Slant, 0.6f * u);
    }

    /// <param name="players">Name, metres along, gone, ping (null: local), this view's car.</param>
    public static void Build(Overlay o, int width, int height, IReadOnlyList<(string Name, float Along, bool Gone, int? Ping, bool Me)> players)
    {
        var g = Style.Safe(width, height);
        var u = g.U;
        var order = players.Where(p => !p.Gone).OrderByDescending(p => p.Along).Concat(players.Where(p => p.Gone)).ToArray();
        var me = Array.FindIndex(order, p => p.Me);
        var h = Height(order.Length);
        var at = new Vector2(g.Right - W * u, g.Top);
        Style.Slanted(o, at, at + new Vector2(W, h) * u, Style.Panel, -0.22f * 150 / h);
        o.Rect(Vector2.Round(at + new Vector2(W - 5, 0) * u), Vector2.Round(at + new Vector2(W, h) * u), Style.Red);
        var x0 = at.X + 40 * u;
        var x1 = at.X + (W - 22) * u;
        var place = me + 1;
        Style.Label(o, place switch { 1 => "1ST", 2 => "2ND", 3 => "3RD", _ => $"{place}TH" }, new Vector2(x0 - 4 * u, at.Y + 56 * u), 46 * u, place == 1 ? Style.Amber : Style.Text, 0, Style.Slant, 0.4f * u);
        Style.Label(o, $"/ {order.Count(p => !p.Gone)}", new Vector2(x0 + 92 * u, at.Y + 56 * u), 22 * u, Style.Dim, 0, Style.Slant);
        var mine = order[Math.Max(0, me)].Along;
        for (var i = 0; i < order.Length; i++)
        {
            var p = order[i];
            var y = at.Y + (92 + 28 * i) * u;
            var col = p.Me ? Style.Amber : p.Gone ? Style.Faint : Style.Text;
            Style.Label(o, $"{i + 1}  {p.Name}", new Vector2(x0, y), 17 * u, col, 0, Style.Slant);
            var right = p.Gone ? "LEFT" : p.Me ? "" : $"{(p.Along >= mine ? "+" : "-")}{MathF.Abs(p.Along - mine):0} m";
            if (p.Ping is { } ping && !p.Gone) right += $"  {ping} ms";
            Style.Label(o, right, new Vector2(x1, y), 15 * u, p.Gone ? Style.Faint : Style.Dim, 1);
        }
    }
}
