using System.Text.RegularExpressions;
using System.Numerics;
using Kansei.Graphics;
using Kansei.Physics;

namespace Touge.Ui;

/// <summary>
///     IKETANI'S CAR GUIDE (main menu), after the original's 池谷先輩の車紹介 (TSDATA TIKETEX/TIKEANI, BGM WORRY.adx): Itsuki
///     asks Iketani to teach Takumi about cars, then one scrolling list of all cars (the original's itag name strips; here
///     grouped under maker headers), the car turning in 3D, and per car Iketani's own spoken introduction from the disc
///     (SOUND/IKETANI.AFS INTRO_&lt;car&gt;.ADX, Japanese, 34–66 s, played by the game through <see cref="Voice"/>). The disc has
///     no text for these talks, so the English panel text and the spec sheet are the remake's own (specs from
///     <see cref="CarSpecs"/>, engine data of the real cars). Up/down car, left/right body colour, decide = Iketani talks
///     (decide/back skips), back = main menu. Rebuilt with <see cref="Canvas"/>, no original textures.
/// </summary>
public sealed class CarGuide(Catalog catalog)
{
    public enum Step { Intro, List }
    public enum Action { None, PreviewCar, Exit }

    /// <param name="Code">Engine code.</param>
    /// <param name="Cc">Displacement as written in the spec sheet.</param>
    /// <param name="Layout">Cylinders/valvetrain/induction.</param>
    /// <param name="Driver">Who drives it in Initial D (null: no one the remake names).</param>
    /// <param name="Text">Iketani's few words about it (the panel; the voice on the disc says more).</param>
    public sealed record Entry(string Code, string Cc, string Layout, string? Driver, string Text);

    /// <summary>Remake text per HCAR id (own English; facts of the real Japanese-market cars).</summary>
    public static readonly IReadOnlyDictionary<string, Entry> Entries = new Dictionary<string, Entry>
    {
        ["AE86T"] = new("4A-GE", "1587 cc", "I4 DOHC 16V", "Takumi Fujiwara",
            "The Hachi-Roku. Light, FR, a high-revving twin-cam from the mid-'80s - on paper everything here outclasses it. But on Akina's downhill, in the right hands, it shows the turbo cars its tail lights. That's your old man's tofu car, Takumi."),
        ["AE86L"] = new("4A-GE", "1587 cc", "I4 DOHC 16V", "Wataru Akiyama",
            "The Levin is the Trueno's twin: same 4A-GE, same chassis, just fixed headlights instead of pop-ups. Wataru Akiyama's two-door Levin even runs a turbo. Proof the 86 is never done being tuned."),
        ["AE85"] = new("3A-U", "1452 cc", "I4 SOHC 8V", "Itsuki Takeuchi",
            "The Hachi-Go. Looks just like an 86, but under the bonnet is a single-cam with 83 PS. Itsuki bought it thinking it was a Hachi-Roku... Still - light and FR, a fine car to learn on."),
        ["MR2"] = new("3S-GTE", "1998 cc", "I4 DOHC turbo", "Kai Kogashiwa",
            "The second MR2, the SW20: a turbo 3S-GTE right behind the seats. Grip like glue, but when the rear lets go it snaps fast. You have to be smooth with this one."),
        ["MRS"] = new("1ZZ-FE", "1794 cc", "I4 DOHC VVT-i", null,
            "The MR-S, Toyota's little open mid-engine roadster. Only 1.8 litres, but it weighs under a ton and turns in like a go-kart. Never underestimate a light car on a tight pass."),
        ["ALTEZ"] = new("3S-GE", "1998 cc", "I4 DOHC VVT-i", null,
            "The Altezza RS200: a compact FR sedan with the high-revving BEAMS 3S-GE and a six-speed. Balanced, comfortable - and it loves to slide."),
        ["GT-4"] = new("3S-GTE", "1998 cc", "I4 DOHC turbo", null,
            "The Celica GT-Four, Toyota's rally special. Turbo 3S-GTE and full-time 4WD, built to win the WRC. In the wet it's a real weapon."),
        ["R32"] = new("RB26DETT", "2568 cc", "I6 DOHC twin-turbo", "Takeshi Nakazato",
            "The R32 GT-R. Twin-turbo RB26 and ATTESA E-TS four-wheel drive - the car that crushed Group A. Nakazato of the Myogi NightKids swears by it. Heavy on the touge, but on the straights nothing catches it."),
        ["R34"] = new("RB26DETT", "2568 cc", "I6 DOHC twin-turbo", "Kozo Hoshino",
            "The R34 GT-R V-spec II, last of the RB26 GT-Rs. Stiffer body, real aero, a six-speed Getrag and a computer screen on the dash. Huge power, huge grip, huge price."),
        ["ER34"] = new("RB25DET", "2498 cc", "I6 DOHC turbo", null,
            "The ER34 25GT Turbo looks like a GT-R, but it's rear-drive with a single-turbo RB25. A big FR sedan - and plenty of guys drift them."),
        ["S13"] = new("SR20DET", "1998 cc", "I4 DOHC turbo", "Koichiro Iketani",
            "My S13 Silvia K's! Turbo SR20, FR, cheap to buy and easy to tune - the street racer's classic. The car's great... the driver's still catching up."),
        ["S14Q"] = new("SR20DE", "1998 cc", "I4 DOHC", null,
            "The S14 Silvia Q's: the same body as the K's but with the non-turbo SR20DE. Less power, but an honest FR to learn throttle control with."),
        ["S14"] = new("SR20DET", "1998 cc", "I4 DOHC turbo", null,
            "The S14 Silvia K's Aero. Bigger and heavier than my S13, but the turbo SR20 got stronger to match - and with the aero kit it looks the business."),
        ["S15"] = new("SR20DET", "1998 cc", "I4 DOHC turbo", null,
            "The S15 Silvia spec-R, the last Silvia. 250 PS, a six-speed and a much stiffer body. Nissan saved the best for last."),
        ["ONE80"] = new("SR20DET", "1998 cc", "I4 DOHC turbo", "Kenji",
            "Kenji's 180SX Type X. Pop-up lights, a hatchback and the same turbo SR20 as the Silvia - it's an S13 underneath. Drift teams everywhere run these."),
        ["SIL80"] = new("SR20DET", "1998 cc", "I4 DOHC turbo", "Mako Sato & Sayuki",
            "The Sileighty: a 180SX wearing a Silvia nose. It started as a cheap fix after front-end crashes and became a legend. Mako and Sayuki of Impact Blue rule Usui with one."),
        ["EK9"] = new("B16B", "1595 cc", "I4 DOHC VTEC", null,
            "The EK9 Civic Type R. A hand-finished B16B that revs past 8000, a stiffened shell and the red Honda badge. It's FF, but through the corners it's ridiculously quick."),
        ["EG6"] = new("B16A", "1595 cc", "I4 DOHC VTEC", "Shingo Shoji",
            "The EG6 Civic SiR II with the B16A VTEC. Shingo Shoji of the NightKids drives one - he's the guy with the duct-tape death match. FF cars are tough in the tight stuff."),
        ["INTGR"] = new("B18C", "1797 cc", "I4 DOHC VTEC", "Smiley Sakai",
            "The DC2 Integra Type R. A 1.8-litre VTEC that screams to 8000 and a chassis Honda tuned like a race car. Some say it's the best-handling FF ever built."),
        ["S2000"] = new("F20C", "1997 cc", "I4 DOHC VTEC", "Toshiya Joshima",
            "The S2000, Honda's 50th-anniversary roadster. 250 PS from two litres without a turbo, and it revs to 9000. Joshima of Purple Shadow drives his like a god."),
        ["EVO3"] = new("4G63", "1997 cc", "I4 DOHC turbo", "Kyoichi Sudo",
            "The Lancer Evolution III: turbo 4G63 and full-time 4WD straight off the rally stages. Kyoichi Sudo, leader of the Emperor, calls the 86 a relic - he trusts the Evo's traction."),
        ["EVO4"] = new("4G63", "1997 cc", "I4 DOHC turbo", "Seiji Iwaki",
            "The Evo IV - a new body and 280 PS. This is the RS, the stripped competition base. Seiji Iwaki of the Emperor drives an Evo IV, all business and four-wheel grip."),
        ["EVO7"] = new("4G63", "1997 cc", "I4 DOHC turbo", null,
            "The Evo VII, on the bigger Lancer Cedia body with an active centre diff. The newest Evo here - there's technology everywhere you look."),
        ["FD3S"] = new("13B-REW", "1308 cc", "twin-rotor, twin-turbo", "Keisuke Takahashi",
            "The FD RX-7 Type R. A 13B rotary with sequential twin turbos, near 50:50 balance and a body like a sculpture. Keisuke Takahashi's yellow FD is the Red Suns' number two."),
        ["FD3SA"] = new("13B-REW", "1308 cc", "twin-rotor, twin-turbo", null,
            "The Spirit R Type A, the final RX-7 from 2002. 280 PS, Recaros, BBS wheels and every last upgrade Mazda had. A proper send-off for the rotary."),
        ["FC3S"] = new("13B-T", "1308 cc", "twin-rotor, turbo", "Ryosuke Takahashi",
            "The FC RX-7 Infini III. A turbo rotary in an '80s chassis - but driven by Ryosuke Takahashi, Akagi's White Comet, it's pure theory made real."),
        ["NA6C"] = new("B6-ZE", "1597 cc", "I4 DOHC", null,
            "The NA Roadster - the Eunos Roadster here in Japan. Light, simple, FR, pop-up lights. It's not about power; it's about the joy of driving."),
        ["NB8C"] = new("BP-ZE", "1839 cc", "I4 DOHC", null,
            "The NB Roadster RS: fixed headlights, a 1.8 and a six-speed. Still light, still a pure FR - fun on any pass."),
        ["IMP"] = new("EJ20", "1994 cc", "flat-4 DOHC turbo", null,
            "The GC8 Impreza WRX STi Version VI: turbo EJ20 boxer and symmetrical 4WD from the WRC. Compact, tough and brutally quick out of a corner."),
        ["IMP2"] = new("EJ20", "1994 cc", "flat-4 DOHC turbo", null,
            "The GDB Impreza WRX STi, the new round-eye model. A six-speed and the DCCD centre diff - stiffer, stronger, even more grip."),
        ["IMP3"] = new("EJ20", "1994 cc", "flat-4 DOHC turbo", "Bunta Fujiwara",
            "The Impreza WRX type R STi Version V, the two-door coupe. Takumi's dad drives one just like it... and somehow he's even scarier in it than in the 86."),
        ["CAPPU"] = new("F6A", "657 cc", "I3 DOHC turbo", null,
            "The Suzuki Cappuccino, a kei-class roadster: 657 cc turbo three-cylinder, 64 PS. It weighs just 700 kg and it's FR. Small - but don't laugh at it."),
    };

    /// <summary>The opening exchange as the original's (itag_10), own English.</summary>
    public static readonly (string Who, string Line)[] Intro =
    [
        ("ITSUKI", "Iketani-senpai! Teach Takumi about cars, will you? He doesn't know a thing!"),
        ("TAKUMI", "...Yeah. To be honest, I don't really know much about cars."),
        ("IKETANI", "Well, you only just got your licence. Alright - I'll show you what a street racer's car is all about! So... which one do you want to hear about?"),
    ];

    public const float Fade = 30 / 60f;
    /// <summary>Panel text types at most this many characters per second while Iketani talks (slower to match a long voice).</summary>
    public const float TypeRate = 45;

    public bool Active { get; private set; }
    public Step Current { get; private set; }
    /// <summary>Original UI sound by SYSSE name (SYS005 move, SYS006 decide, BEEP001 back).</summary>
    public Action<string>? Sound { get; set; }
    public string Music => "WORRY.adx";
    public string CarId => catalog.Cars[_car].Id;
    public int Paint { get; private set; }
    /// <summary>Car whose INTRO_ voice should play now (null: none); the game starts/stops it when this changes.</summary>
    public string? Voice => Active && _talk >= 0 && _leave < 0 ? CarId : null;
    /// <summary>Length of the playing voice (set by the game; 0 = unknown, the talk then runs until skipped).</summary>
    public float VoiceSeconds { get; set; }
    /// <summary>3D car shown (the list), not the intro's backdrop.</summary>
    public bool ShowsCar => Active && Current == Step.List;
    /// <summary>Camera: 1 = car right of the list, 0 = centred (talking); eased.</summary>
    public float Shift => 1 - Style.Ease(_talk >= 0 ? _talk / 0.4f : 1 - _sinceTalk / 0.4f);

    private int _car, _line;
    private float _t, _clock, _leave = -1, _talk = -1, _sinceTalk = 10;
    private int[] _order = [];

    /// <summary>Opens on the intro (or straight on the list) with <paramref name="car"/>/<paramref name="paint"/> selected.</summary>
    public void Open(string car, int paint, Step step = Step.Intro)
    {
        // maker order of the original's list, cars in HCAR order within a maker
        _order = [.. Enumerable.Range(0, catalog.Cars.Count).OrderBy(i => Array.IndexOf(Catalog.Makers, catalog.Cars[i].Maker)).ThenBy(i => i)];
        _car = Math.Max(0, catalog.Cars.ToList().FindIndex(c => c.Id == car));
        Paint = Math.Clamp(paint, 0, catalog.Cars[_car].Paints.Length - 1);
        (Active, Current, _t, _line, _leave, _talk, _sinceTalk) = (true, step, 0, 0, -1, -1, 10);
    }

    /// <summary>Iketani starts talking (screenshots).</summary>
    public void Talk(float at = 0) => (_talk, _sinceTalk) = (at, 0);

    /// <summary>Skips the fade-in (screenshots).</summary>
    public void Settle() => _t = MathF.Max(_t, 99);

    public Action Update((int X, int Y, bool Ok, bool Back) k, float dt)
    {
        if (!Active) return Action.None;
        dt = MathF.Min(dt, 1 / 20f);
        _t += dt;
        _clock += dt;
        if (_leave >= 0)
        {
            if ((_leave += dt) < Fade) return Action.None;
            Active = false;
            return Action.Exit;
        }
        if (_talk >= 0)
        {
            _talk += dt;
            if (VoiceSeconds > 0 && _talk >= VoiceSeconds) StopTalk();
        }
        else _sinceTalk += dt;
        switch (Current)
        {
            case Step.Intro:
                if (k.Ok && Typed < Intro[_line].Line.Length) _t = 99; // decide first completes the line
                else if (k.Ok)
                {
                    Sound?.Invoke("SYS006");
                    _t = Fade; // the next line types in, the list switches in place
                    if (++_line < Intro.Length) break;
                    Current = Step.List;
                    return Action.PreviewCar;
                }
                else if (k.Back) Leave();
                break;
            case Step.List:
                if (k.Y != 0)
                {
                    Sound?.Invoke("SYS005");
                    var at = Array.IndexOf(_order, _car);
                    (_car, Paint) = (_order[(at + k.Y + _order.Length) % _order.Length], 0);
                    if (_talk >= 0) StopTalk();
                    return Action.PreviewCar;
                }
                if (k.X != 0)
                {
                    var n = catalog.Cars[_car].Paints.Length;
                    var p = (Paint + k.X + n) % n;
                    if (p == Paint) break;
                    Sound?.Invoke("SYS005");
                    Paint = p;
                    return Action.PreviewCar;
                }
                if (_talk >= 0 && (k.Ok || k.Back))
                {
                    Sound?.Invoke(k.Ok ? "SYS006" : "BEEP001");
                    StopTalk();
                }
                else if (k.Ok)
                {
                    Sound?.Invoke("SYS006");
                    Talk();
                }
                else if (k.Back) Leave();
                break;
        }
        return Action.None;
    }

    /// <summary>Characters of the intro line typed so far.</summary>
    private int Typed => (int)(MathF.Max(0, _t - Fade) * TypeRate * 1.5f);

    private void StopTalk() => (_talk, _sinceTalk, VoiceSeconds) = (-1, 0, 0);

    private void Leave()
    {
        Sound?.Invoke("BEEP001");
        _leave = 0;
    }

    /// <summary>
    ///     Driving-line point for the turntable: the straightest, flattest road (±20 m) at least 150 m from either end, so
    ///     no start/goal arch, barricade or gantry stands near the car and the camera looks along open road.
    /// </summary>
    public static int Spot(Vector3[] line)
    {
        var s = new float[line.Length];
        for (var i = 1; i < line.Length; i++) s[i] = s[i - 1] + Vector3.Distance(line[i - 1], line[i]);
        var (best, bestScore) = (line.Length / 2, float.MaxValue);
        for (int i = 0, a = 0, b = 0; i < line.Length; i++)
        {
            while (s[a] < s[i] - 20) a++;
            while (b < line.Length - 1 && s[b + 1] <= s[i] + 20) b++;
            if (s[i] < 150 || s[^1] - s[i] < 150 || a == i || b == i) continue;
            Vector3 u = Vector3.Normalize(line[i] - line[a]), v = Vector3.Normalize(line[b] - line[i]);
            var score = 1 - Vector3.Dot(u, v) + MathF.Abs(u.Y) + MathF.Abs(v.Y);
            if (score < bestScore) (best, bestScore) = (i, score);
        }
        return best;
    }

    /// <summary>Peak power (PS) and torque (Nm) with their rpm from the car's torque curve (P = T·ω).</summary>
    public static (int Ps, int PsRpm, int Nm, int NmRpm) Peaks(CarSpec s)
    {
        int p = 0, t = 0;
        for (var i = 1; i < s.TorqueRpm.Length; i++)
        {
            if (s.TorqueNm[i] * s.TorqueRpm[i] > s.TorqueNm[p] * s.TorqueRpm[p]) p = i;
            if (s.TorqueNm[i] > s.TorqueNm[t]) t = i;
        }
        static int R(float v, float to) => (int)(MathF.Round(v / to) * to);
        return (R(s.TorqueNm[p] * s.TorqueRpm[p] * MathF.PI / 30 / 735.5f, 1), R(s.TorqueRpm[p], 100), R(s.TorqueNm[t], 1), R(s.TorqueRpm[t], 100));
    }

    /// <summary>Spec sheet rows (label, value) of a car, two columns of four.</summary>
    public static (string Label, string Value)[] Sheet(Catalog.Car car)
    {
        var s = CarSpecs.Real[car.Id];
        var e = Entries[car.Id];
        var (ps, psRpm, nm, nmRpm) = Peaks(s);
        return
        [
            ("ENGINE", $"{e.Code}  {e.Cc}"), ("POWER", $"{ps} PS / {psRpm} rpm"), ("TORQUE", $"{nm} Nm / {nmRpm} rpm"), ("WEIGHT", $"{s.Mass:0} kg"),
            ("LAYOUT", e.Layout), ("DRIVE", car.Drive), ("GEARBOX", $"{s.Gears.Length}-speed MT"),
            ("PWR / WT", FormattableString.Invariant($"{s.Mass / ps:0.0} kg/PS")),
        ];
    }

    /// <summary>
    ///     Greedy word wrap of <paramref name="text"/> for <see cref="Canvas.Text"/> at <paramref name="size"/> canvas px into
    ///     <paramref name="width"/> canvas px (canvas x and y scale differently: 1.25 vs 480/448 per 640×480 px).
    /// </summary>
    public static List<string> Wrap(SdfFont font, string text, float size, float width)
    {
        if (font.Map != null) text = font.Map(text).ToString(); // whole, before the lines are cut (draw them raw)
        var lines = new List<string>();
        var line = "";
        // a word: up to a space; Japanese has none, it breaks between any two characters
        foreach (var word in Regex.Split(text, @" |(?<=[\u3000-\u9FFF\uFF00-\uFFEF])|(?=[\u3000-\u9FFF\uFF00-\uFFEF])").Where(w => w != ""))
        {
            var cjk = word[0] >= '\u3000' || line.Length > 0 && line[^1] >= '\u3000';
            var next = line.Length == 0 ? word : line + (cjk ? "" : " ") + word;
            if (line.Length > 0 && font.Measure(next, size * 480f / 448) > width * 1.25f)
            {
                lines.Add(line);
                next = word;
            }
            line = next;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    // ---------------------------------------------------------------- drawing

    private readonly Canvas _c = new();
    private float Theta => _clock * 300 % 360;
    private static readonly uint Grey = Overlay.Rgba(0.72f, 0.73f, 0.75f), Ink = Overlay.Rgba(0.1f, 0.1f, 0.12f);
    private const float Row = 17, ListTop = 96, ListRows = 18;

    public void Build(Overlay o, int width, int height)
    {
        o.Clear();
        if (!Active) return;
        var c = _c;
        c.Begin(o, width, height);
        if (Current == Step.Intro)
        {
            c.Backdrop(_clock);
            IntroScreen(c);
        }
        else ListScreen(c);
        c.Marquee("IKETANI'S CAR GUIDE", false, _clock);
        c.Fade(_leave >= 0 ? Math.Clamp(_leave / Fade, 0, 1) : 1 - Math.Clamp(_t / Fade, 0, 1));
    }

    /// <summary>Red slanted title banner as the original's 池谷先輩の車紹介 strip, white lettering with a black rim.</summary>
    private static void Banner(Canvas c, float y, float a)
    {
        const float sk = 0.25f;
        Vector2 Q(float x, float yy) => c.P(x - (yy - y) * sk, yy);
        c.O.Quad(Q(70, y - 30), Q(452, y - 30), Q(452, y + 12), Q(70, y + 12), Style.Fade(Canvas.Black, a));
        c.O.Quad(Q(74, y - 27), Q(448, y - 27), Q(448, y + 9), Q(74, y + 9), Style.Fade(Canvas.HeaderRed, a));
        c.Lettering("IKETANI'S CAR GUIDE", 256, y, 30, Canvas.White, Overlay.Rgba(0.85f, 0.87f, 0.9f), 0.5f, 0.2f, false, true, a);
    }

    private void IntroScreen(Canvas c)
    {
        Banner(c, 170, Style.Ease(_t / 0.4f));
        var (who, line) = Intro[Math.Min(_line, Intro.Length - 1)];
        c.Carbon(40, 270, 472, 384);
        c.Plate(52, 256, 110, 26, 1);
        c.Text(who, 107, 275, 14, Ink, 0.5f, 0.15f);
        var shown = Typed;
        Paragraph(c, line, 60, 312, 14, 392, 20, shown);
        if (shown >= line.Length) c.Arrow(448, 364, 462, 364, 455, 375, Canvas.Pulse(Theta)); // ▼ more
        Hint(c, _line < Intro.Length - 1 ? "DECIDE: Next    BACK: Main menu" : "DECIDE: Choose a car    BACK: Main menu");
    }

    /// <summary>Wrapped text from (x, y), <paramref name="shown"/> characters of it (typewriter).</summary>
    private static void Paragraph(Canvas c, string text, float x, float y, float size, float width, float step, int shown = int.MaxValue)
    {
        foreach (var l in Wrap(c.O.Font!, text, size, width))
        {
            if (shown <= 0) break;
            c.Text(l.Length <= shown ? l : l[..shown], x, y, size, Canvas.White, 0, 0.1f, raw: true);
            shown -= l.Length + (l[^1] >= '\u3000' ? 0 : 1); // the space the wrap took (Japanese has none)
            y += step;
        }
    }

    private void ListScreen(Canvas c)
    {
        var car = catalog.Cars[_car];
        var e = Entries[car.Id];
        // the car list slides out to the left while Iketani talks
        var dx = -200 * (1 - Shift);
        if (dx > -199) CarList(c, dx);
        // Iketani's panel: what he says about the car (types in while the voice runs)
        c.Carbon(190, 70, 500, 158);
        c.Plate(198, 58, 92, 22, 1);
        c.Text("IKETANI", 244, 74, 13, Ink, 0.5f, 0.15f);
        // while the voice runs the text types along with it (done at ~85 % of the talk), never faster than TypeRate
        var rate = VoiceSeconds > 0 ? MathF.Min(TypeRate, e.Text.Length / (0.85f * VoiceSeconds)) : TypeRate;
        var shown = _talk >= 0 ? (int)(_talk * rate) : int.MaxValue;
        Paragraph(c, e.Text, 202, 96, 11.5f, 284, 14.5f, shown);
        if (_talk >= 0)
        {
            // voice progress along the panel's foot
            var p = VoiceSeconds > 0 ? Math.Clamp(_talk / VoiceSeconds, 0, 1) : 0;
            c.O.Rect(Vector2.Round(c.P(200, 151)), Vector2.Round(c.P(490, 153)), Overlay.Rgba(1, 1, 1, 0.15f));
            c.O.Rect(Vector2.Round(c.P(200, 151)), Vector2.Round(c.P(200 + 290 * p, 153)), Canvas.HeaderRed);
            c.Text("TALKING...", 298, 67, 10, Style.Fade(Overlay.Rgba(1, 0.3f, 0.25f), Canvas.Pulse(Theta)), 0, 0.15f, 0.12f);
        }
        SpecPanel(c, car, e);
        Hint(c, _talk >= 0 ? "DECIDE / BACK: Skip    UP/DOWN: Car    LEFT/RIGHT: Body colour"
            : "UP/DOWN: Car    LEFT/RIGHT: Body colour    DECIDE: Iketani talks    BACK: Main menu");
    }

    private void CarList(Canvas c, float dx)
    {
        c.Carbon(12 + dx, 70, 180 + dx, 424);
        c.Plate(20 + dx, 58, 76, 22, 1);
        c.Text("CARS", 58 + dx, 74, 13, Ink, 0.5f, 0.15f);
        // rows: maker headers + cars; the window follows the selection
        var rows = new List<(string Text, int Car)>();
        foreach (var maker in Catalog.Makers)
        {
            rows.Add((maker, -1));
            rows.AddRange(_order.Where(i => catalog.Cars[i].Maker == maker).Select(i => (catalog.Cars[i].Name, i)));
        }
        var sel = rows.FindIndex(r => r.Car == _car);
        var first = (int)Math.Clamp(sel - ListRows / 2, 0, Math.Max(0, rows.Count - ListRows));
        for (var i = 0; i < ListRows && first + i < rows.Count; i++)
        {
            var (text, idx) = rows[first + i];
            var y = ListTop + i * Row;
            if (idx < 0)
            {
                c.Text(text, 22 + dx, y + 1, 9, Grey, 0, 0.12f);
                c.O.Line(c.P(22 + dx + c.O.Font!.Measure(text, 9 * c.Ky) / c.Kx + 4, y - 3), c.P(170 + dx, y - 3), 1, Overlay.Rgba(0.4f, 0.41f, 0.43f));
                continue;
            }
            var on = idx == _car;
            if (on) c.Diamond(28 + dx, y - 4, 4);
            c.Fit(text, 36 + dx, y, 136, 0, on ? Canvas.Yellow : Canvas.White, 0.12f, 0.06f, 11.5f);
        }
        var sy = ListTop + (sel - first) * Row;
        if (_talk < 0) c.Glow(16 + dx, sy - 14, 176 + dx, sy + 5, Canvas.Pulse(Theta));
        if (first > 0) c.Arrow(150 + dx, 77, 162 + dx, 77, 156 + dx, 70);
        if (first + ListRows < rows.Count) c.Arrow(88 + dx, 412, 100 + dx, 412, 94 + dx, 419);
    }

    private void SpecPanel(Canvas c, Catalog.Car car, Entry e)
    {
        c.Carbon(190, 300, 500, 424);
        var at = Array.IndexOf(_order, _car) + 1;
        c.Text($"{car.Maker}   {at} / {_order.Length}", 204, 318, 10, Grey, 0, 0.12f);
        c.Fit(car.Name, 204, 340, 284, 0, Canvas.White, 0.15f, 0.07f, 20);
        if (e.Driver != null) c.Text($"IN INITIAL D:  {e.Driver}", 204, 355, 10, Overlay.Rgba(1, 0.85f, 0.3f), 0, 0.12f);
        // body colours, the yellow diamond over the shown one
        for (var i = 0; i < car.Paints.Length; i++)
        {
            var x = 474 - (car.Paints.Length - 1 - i) * 16;
            if (i == Paint) c.Diamond(x, 309, 3.5f);
            c.O.Disc(c.P(x, 320), 6 * c.S, Overlay.Rgba(0, 0, 0, 0.9f));
            c.O.Disc(c.P(x, 320), 4.8f * c.S, Catalog.Swatch(car.Paints[i]));
        }
        var rows = Sheet(car);
        for (var i = 0; i < rows.Length; i++)
        {
            float x = 204 + i / 4 * 150, y = 374 + i % 4 * 14;
            c.Text(rows[i].Label, x, y, 8.5f, Grey, 0, 0.1f);
            c.Fit(rows[i].Value, x + 48, y, 96, 0, Canvas.White, 0.12f, 0, 11);
        }
    }

    private static void Hint(Canvas c, string text) => Menu.Hint(c, text);
}
