using Kansei.Graphics;
using Kansei.Physics;
using Touge.Race;

namespace Touge.Ui;

/// <summary>AI strength of a free battle: a band of the skill scale (<see cref="FreeBattle.Band"/>), rubber band and mistakes.</summary>
public enum AiLevel { Easy, Normal, Hard, Legend }

/// <summary>The free battle lobby's choice, remembered in <see cref="Settings.FreeBattle"/> (the player's car is <see cref="Settings.Car"/>).</summary>
public sealed class FreeBattleChoice
{
    /// <summary>COURSE.AFS name (_DAY/_NIT/_RIN), with <see cref="Fog"/> over a _DAY/_NIT course: <see cref="Versus.Conditions"/>.</summary>
    public string Course { get; set; } = "AKINA_NIT";
    public bool Reverse { get; set; }
    public bool Fog { get; set; }
    /// <summary><see cref="Rivals.Rival.Id"/>.</summary>
    public string Rival { get; set; } = "keisuke";
    public BattleRule Rule { get; set; } = BattleRule.LeadChase;
    /// <summary>Lead/chase: the player leads off (else the rival).</summary>
    public bool PlayerLeads { get; set; }
    public AiLevel Level { get; set; } = AiLevel.Normal;

    public FreeBattleChoice Copy() => (FreeBattleChoice)MemberwiseClone();
}

/// <summary>
///     VERSUS → VS CPU: the free battle lobby against one of the <see cref="Rivals.All"/>, in the split-screen lobby's style
///     (carbon panels, value rows with ◀ ▶, START): the battle on the left (course, route, conditions as in Time Attack,
///     rule, who leads a lead/chase, AI level), the rival's card (team, car, level stars, a note on how they drive) and the
///     player's car, colour and gearbox on the right. <see cref="Versus"/> hosts it (fade, backdrop, marquee, loading),
///     the game turns START into <see cref="Setup"/>.
/// </summary>
public sealed class FreeBattle(Catalog catalog)
{
    public enum Row { Course, Route, Conditions, Rule, Lead, Level, Rival, Car, Colour, Gearbox, Start }

    /// <summary>PickCar: DECIDE on CAR, the host opens the car select (<see cref="CarPicker"/>) and hands the choice back (<see cref="SetCar"/>).</summary>
    public enum Result { None, Start, Back, PickCar }

    public FreeBattleChoice Choice { get; private set; } = new();
    public int Car { get; private set; }
    /// <summary>Cars not won yet (IMP3 until Story's end or Bunta), skipped as in every car select.</summary>
    public Func<string, bool>? CarLocked { get; set; }
    public int Paint { get; private set; }
    public bool Manual { get; private set; }
    public string CarId => catalog.Cars[Car].Id;
    public Rivals.Rival Rival => Rivals.All[RivalIndex];
    private int RivalIndex => Math.Max(0, Array.FindIndex(Rivals.All, r => r.Id == Choice.Rival));

    private int _row;

    /// <summary>Opens on <paramref name="saved"/> (anything unknown falls back), the cursor where it was last.</summary>
    public void Open(FreeBattleChoice saved, string car, int paint, bool manual)
    {
        Choice = saved.Copy();
        var course = CourseOf(Choice.Course);
        var cond = Versus.Conditions(course);
        if (!Choice.Course.StartsWith(course.Id + "_") || ConditionIndex() < 0 || cond[ConditionIndex()].Fog != Choice.Fog)
            (Choice.Course, Choice.Fog) = ($"{course.Id}_{cond[0].Time}", cond[0].Fog);
        Choice.Rival = Rival.Id;
        if (!Enum.IsDefined(Choice.Level)) Choice.Level = AiLevel.Normal;
        if (!Enum.IsDefined(Choice.Rule)) Choice.Rule = BattleRule.LeadChase;
        Car = Math.Max(0, catalog.Cars.ToList().FindIndex(c => c.Id == car && CarLocked?.Invoke(c.Id) != true));
        (Paint, Manual) = (Math.Clamp(paint, 0, catalog.Cars[Car].Paints.Length - 1), manual);
        _row = Math.Min(_row, Rows.Length - 1);
    }

    /// <summary>The car select's choice.</summary>
    public void SetCar(int car, int paint) => (Car, Paint) = (car, paint);

    /// <summary>Rows in cursor order: the battle (left), the rival and the player's car (right), START; WHO LEADS only for lead/chase.</summary>
    public Row[] Rows => Choice.Rule == BattleRule.LeadChase
        ? [Row.Course, Row.Route, Row.Conditions, Row.Rule, Row.Lead, Row.Level, Row.Rival, Row.Car, Row.Colour, Row.Gearbox, Row.Start]
        : [Row.Course, Row.Route, Row.Conditions, Row.Rule, Row.Level, Row.Rival, Row.Car, Row.Colour, Row.Gearbox, Row.Start];

    public Row Selected => Rows[Math.Min(_row, Rows.Length - 1)];

    private Catalog.Course CourseOf(string courseTime) => catalog.Courses.FirstOrDefault(x => courseTime.StartsWith(x.Id + "_")) ?? catalog.Courses[0];

    private int ConditionIndex()
    {
        var time = Choice.Course[(Choice.Course.LastIndexOf('_') + 1)..];
        return Array.FindIndex(Versus.Conditions(CourseOf(Choice.Course)), x => x.Time == time && x.Fog == Choice.Fog);
    }

    private static int Wrap(int i, int n) => (i % n + n) % n;

    /// <summary>The value on <paramref name="row"/> moved by <paramref name="d"/> (a new course keeps the conditions where it has them).</summary>
    public void Change(Row row, int d)
    {
        var c = Choice;
        var course = CourseOf(c.Course);
        switch (row)
        {
            case Row.Course:
            {
                var label = Versus.Conditions(course)[Math.Max(0, ConditionIndex())].Label;
                var next = catalog.Courses[Wrap(catalog.Courses.ToList().IndexOf(course) + d, catalog.Courses.Count)];
                var cond = Versus.Conditions(next);
                var (_, time, fog) = cond[Math.Max(0, Array.FindIndex(cond, x => x.Label == label))];
                (c.Course, c.Fog) = ($"{next.Id}_{time}", fog);
                break;
            }
            case Row.Route: c.Reverse = !c.Reverse; break;
            case Row.Conditions:
            {
                var cond = Versus.Conditions(course);
                var (_, time, fog) = cond[Wrap(Math.Max(0, ConditionIndex()) + d, cond.Length)];
                (c.Course, c.Fog) = ($"{course.Id}_{time}", fog);
                break;
            }
            case Row.Rule: c.Rule = c.Rule == BattleRule.Race ? BattleRule.LeadChase : BattleRule.Race; break;
            case Row.Lead: c.PlayerLeads = !c.PlayerLeads; break;
            case Row.Level: c.Level = (AiLevel)Wrap((int)c.Level + d, 4); break;
            case Row.Rival: c.Rival = Rivals.All[Wrap(RivalIndex + d, Rivals.All.Length)].Id; break;
            case Row.Colour: Paint = Wrap(Paint + d, catalog.Cars[Car].Paints.Length); break;
            case Row.Gearbox: Manual = !Manual; break;
        }
    }

    /// <summary>One frame of menu keys: UP/DOWN rows, LEFT/RIGHT (or DECIDE) values, DECIDE or LEFT/RIGHT on CAR asks for the car select, on START starts, BACK leaves.</summary>
    public Result Update((int X, int Y, bool Ok, bool Back) k, Action<string>? sound)
    {
        var rows = Rows;
        _row = Math.Min(_row, rows.Length - 1);
        if (k.Y != 0)
        {
            var n = Math.Clamp(_row + k.Y, 0, rows.Length - 1);
            if (n != _row) sound?.Invoke("SYS005");
            _row = n;
        }
        else if ((k.Ok && rows[_row] == Row.Start) || ((k.Ok || k.X != 0) && rows[_row] == Row.Car)) // CAR: only through the car select
        {
            sound?.Invoke("SYS006");
            return rows[_row] == Row.Start ? Result.Start : Result.PickCar;
        }
        else if (k.X != 0 || k.Ok)
        {
            if (rows[_row] == Row.Start) return Result.None;
            sound?.Invoke("SYS005");
            Change(rows[_row], k.X != 0 ? k.X : 1); // RULE: the WHO LEADS row comes and goes below the cursor
        }
        else if (k.Back)
        {
            sound?.Invoke("BEEP001");
            return Result.Back;
        }
        return Result.None;
    }

    // ------------------------------------------------------------ the battle

    /// <summary>
    ///     The skill band of an AI level on the one scale of every mode (<see cref="RivalPilot.Pace"/>): EASY 0–0.2 (a
    ///     beginner can win, also against a stronger car: <see cref="EasyPower"/>), NORMAL 0.35–0.65, HARD 0.65–0.85, LEGEND
    ///     0.88–1 (a good player's pace).
    /// </summary>
    public static (float Lo, float Hi) Band(AiLevel level) => level switch
    {
        AiLevel.Easy => (0f, 0.2f), AiLevel.Hard => (0.65f, 0.85f), AiLevel.Legend => (0.88f, 1f), _ => (0.35f, 0.65f),
    };

    /// <summary>A character's skill (0.15 Itsuki … 1 Bunta) placed within the level's band.</summary>
    public static float SkillAt(AiLevel level, float skill)
    {
        var (lo, hi) = Band(level);
        return float.Lerp(lo, hi, Math.Clamp((skill - 0.15f) / 0.85f, 0, 1));
    }

    /// <summary>Engine torque of the rival's car on EASY (as Legend's first rungs): a beginner's Trueno against an FD3S still has a chance.</summary>
    public const float EasyPower = 0.85f;

    /// <summary>The rival at <paramref name="level"/>: his skill within its band, the rest of his style his own (EASY: <see cref="EasyPower"/>).</summary>
    public static Rivals.Rival Strength(Rivals.Rival r, AiLevel level) =>
        r with { Style = r.Style with { Skill = SkillAt(level, r.Style.Skill) }, Power = level == AiLevel.Easy ? EasyPower : r.Power };

    /// <summary>
    ///     The battle of <paramref name="c"/>: the rival at its strength, the rule, who leads off; the rubber band in full on
    ///     EASY/NORMAL, catch-up only (half) on HARD, off on LEGEND, which also makes half the mistakes.
    /// </summary>
    public static BattleSetup Setup(FreeBattleChoice c)
    {
        var rival = Strength(Rivals.All.FirstOrDefault(r => r.Id == c.Rival) ?? Rivals.All[0], c.Level);
        return new BattleSetup(rival, c.Rule, c.Rule == BattleRule.LeadChase && c.PlayerLeads ? 0 : 1)
        {
            RubberBand = c.Level != AiLevel.Legend,
            BandUp = c.Level == AiLevel.Hard ? 0.015f : 0.03f,
            BandDown = c.Level == AiLevel.Hard ? 0 : 0.04f,
            Mistakes = c.Level == AiLevel.Legend ? 0.5f : 1,
        };
    }

    /// <summary>The rival's theme on the disc (MANGA/MG_BGM.AFS) as Legend plays it on the VS card: his Legend entry with the same car.</summary>
    public static string Theme(Rivals.Rival r) =>
        (Legend.All.FirstOrDefault(e => e.Rival.Id == r.Id && e.Rival.Car == r.Car) ?? Legend.All.FirstOrDefault(e => e.Rival.Id == r.Id))?.Theme ?? "TOKYO.adx";

    public string Music => Theme(Rival);

    /// <summary>How each rival drives (the remake's own words, after their <see cref="RivalStyle"/>).</summary>
    private static readonly Dictionary<string, string> Notes = new()
    {
        ["itsuki"] = "Eager rookie. Slow into the bends, hardly ever sideways.",
        ["iketani"] = "Speed Stars leader. Careful and steady, no tricks.",
        ["kenji"] = "Speed Stars regular. Likes to hang the tail out.",
        ["takeshi"] = "GT-R grip driver. Brakes late, powers out hard.",
        ["shingo"] = "Plays dirty. Leans on you and shuts every door.",
        ["mako"] = "Usui specialist. Smooth, flowing slides.",
        ["kai"] = "Irohazaka local. Dives into any gap you leave.",
        ["seiji"] = "Emperor 4WD. All grip and traction, no drama.",
        ["kyoichi"] = "Emperor's leader. Clinical lines, very quick.",
        ["keisuke"] = "Redsuns ace. Big, committed drifts.",
        ["ryosuke"] = "The White Comet. Calculated, rarely errs.",
        ["wataru"] = "Turbo Levin. Hard on the brakes, sideways.",
        ["takumi"] = "The ghost of Akina. Inertia drifts, inhuman pace.",
        ["bunta"] = "The original legend. Do not expect mercy.",
    };

    // ------------------------------------------------------------ drawing

    private static readonly uint Grey = Overlay.Rgba(0.72f, 0.73f, 0.75f), Ink = Canvas.Shade(0.08f, 0.08f, 0.09f, 1);

    private static string RuleName(BattleRule r) => r == BattleRule.Race ? "RACE" : "LEAD / CHASE";

    private string Value(Row r)
    {
        var c = Choice;
        var course = CourseOf(c.Course);
        return r switch
        {
            Row.Course => course.Name, Row.Route => Catalog.DirectionName(course, c.Reverse), Row.Conditions => Versus.Conditions(course)[Math.Max(0, ConditionIndex())].Label,
            Row.Rule => RuleName(c.Rule), Row.Lead => c.PlayerLeads ? "YOU" : "RIVAL", Row.Level => c.Level.ToString().ToUpperInvariant(),
            Row.Rival => Rival.Name, Row.Car => catalog.Cars[Car].Name, Row.Colour => $"{Paint + 1} / {catalog.Cars[Car].Paints.Length}",
            Row.Gearbox => Manual ? "MT" : "AT", _ => "",
        };
    }

    private static string Label(Row r) => r switch
    {
        Row.Course => "COURSE", Row.Route => "ROUTE", Row.Conditions => "CONDITIONS", Row.Rule => "RULE", Row.Lead => "WHO LEADS", Row.Level => "AI LEVEL",
        Row.Car => "CAR", Row.Colour => "COLOUR", Row.Gearbox => "GEARBOX", _ => "",
    };

    /// <summary>A value row between <paramref name="x0"/> and <paramref name="x1"/> with ◀ ▶ and the pulsing frame when selected.</summary>
    private void ValueRow(Canvas c, Row r, float x0, float x1, float y, float pulse)
    {
        var sel = Selected == r;
        c.Text(Label(r), x0 + 14, y, 10, Grey, 0, 0.1f);
        c.Fit(Value(r), sel ? x1 - 28 : x1 - 14, y + 1, x1 - x0 - 106, 1, Canvas.White, 0.12f, 0.06f, 14);
        if (r == Row.Colour) Versus.Swatch(c, x0 + 100, y - 4, catalog.Cars[Car].Paints[Paint]);
        if (!sel) return;
        c.Arrow(x1 - 22, y - 9, x1 - 22, y + 1, x1 - 14, y - 4);
        c.Arrow(x0 + 78, y - 9, x0 + 78, y + 1, x0 + 70, y - 4);
        c.Glow(x0 + 6, y - 18, x1 - 6, y + 8, pulse);
    }

    public void Draw(Canvas c, float clock)
    {
        var pulse = Canvas.Pulse(clock * 300 % 360);
        // the battle (left)
        c.Carbon(16, 72, 250, 404);
        c.Text("BATTLE", 30, 94, 12, Grey, 0, 0.15f);
        var y = 118f;
        foreach (var r in Rows.TakeWhile(r => r != Row.Rival))
        {
            ValueRow(c, r, 16, 250, y, pulse);
            y += 28;
        }
        c.Rule(26, 240, y - 6);
        string[] rule = Choice.Rule == BattleRule.Race
            ? ["Side by side from the line.", "First to the goal wins, or pull", $"{Battle.DefaultBreakaway(BattleRule.Race):0} s away to end it early."]
            : [Choice.PlayerLeads ? "You lead, the rival chases." : "The rival leads, you chase.", "The chaser wins by passing and", "holding the lead; the leader by", $"pulling {Battle.DefaultBreakaway(BattleRule.LeadChase):0} s away or a gap at the goal."];
        for (var i = 0; i < rule.Length; i++) c.Fit(rule[i], 30, y + 16 + i * 15, 206, 0, Grey, 0.1f, 0, 11);

        // the rival (right, top)
        var rival = Strength(Rival, Choice.Level);
        c.Carbon(262, 72, 496, 252, 1, false);
        c.Plate(268, 78, 222, 24, 1);
        c.Text("RIVAL", 280, 95, 13, Ink, 0, 0.15f);
        c.Fit($"{RivalIndex + 1} / {Rivals.All.Length}", 476, 95, 80, 1, Ink, 0.12f, 0, 11);
        var selRival = Selected == Row.Rival;
        c.Fit(rival.Name, 379, 128, 170, 0.5f, Overlay.Rgba(0.45f, 0.6f, 1), 0.15f, 0.08f, 20);
        if (selRival)
        {
            c.Arrow(478, 116, 478, 128, 488, 122);
            c.Arrow(280, 116, 280, 128, 270, 122);
            c.Glow(266, 106, 492, 136, pulse);
        }
        c.Text(rival.Team, 379, 150, 12, Overlay.Rgba(1, 0.35f, 0.25f), 0.5f, 0.12f, 0.06f);
        var car = catalog.Cars.FirstOrDefault(x => x.Id == rival.Car);
        c.Rule(272, 486, 160);
        c.Text("CAR", 276, 180, 10, Grey, 0, 0.1f);
        c.Fit(car?.Name ?? rival.Car, 482, 181, 160, 1, Canvas.White, 0.12f, 0.06f, 14);
        if (car != null) c.Text($"{car.Drive}  {car.Ps} PS  {car.Kg} kg", 482, 198, 10, Grey, 1, 0.1f);
        c.Text("LEVEL", 276, 218, 10, Grey, 0, 0.1f);
        for (var i = 0; i < 5; i++) LegendScreen.Star(c, 410 + i * 18, 214, i < Legend.Stars(rival.Style.Skill));
        c.Fit(Notes.GetValueOrDefault(rival.Id, ""), 379, 240, 214, 0.5f, Canvas.White, 0.1f, 0.05f, 11);

        // the player's car (right, bottom) and START
        c.Carbon(262, 260, 496, 404, 1, false);
        c.Plate(268, 266, 222, 24, 1);
        c.Text("YOU", 280, 283, 13, Ink, 0, 0.15f);
        var mine = catalog.Cars[Car];
        c.Fit($"{mine.Drive}  {mine.Ps} PS  {mine.Kg} kg", 476, 283, 130, 1, Ink, 0.12f, 0, 11);
        y = 306;
        foreach (var r in (ReadOnlySpan<Row>)[Row.Car, Row.Colour, Row.Gearbox])
        {
            ValueRow(c, r, 262, 496, y, pulse);
            y += 24;
        }
        c.Button(298, 368, 162, 28, "START", Canvas.ButtonKind.Positive);
        if (Selected == Row.Start) c.Glow(294, 364, 464, 400, pulse);
        Menu.Hint(c, Selected == Row.Car ? "UP/DOWN: Select    DECIDE: Car select    BACK: Return"
            : "UP/DOWN: Select    LEFT/RIGHT: Change    START: Battle    BACK: Return");
    }
}
