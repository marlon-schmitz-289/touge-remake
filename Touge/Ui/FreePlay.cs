using Kansei.Graphics;
using Touge.Race;

namespace Touge.Ui;

/// <summary>Free play at the end of a point-to-point course (circuits just go round).</summary>
public enum CourseEndAction { TurnAround, Restart, Stop }

/// <summary>Free play's AI cars: CRUISE = traffic (no racing skill, gentle engine), else an <see cref="AiLevel"/> as in VS CPU.</summary>
public enum Traffic { Cruise, Easy, Normal, Hard, Legend }

/// <summary>The free play lobby's choice, remembered in <see cref="Settings.FreePlay"/> (the car is <see cref="Settings.Car"/>).</summary>
public sealed class FreePlayChoice
{
    /// <summary>COURSE.AFS name (_DAY/_NIT/_RIN), with <see cref="Fog"/> over a _DAY/_NIT course: <see cref="Versus.Conditions"/>.</summary>
    public string Course { get; set; } = "AKINA_DAY";
    public bool Reverse { get; set; }
    public bool Fog { get; set; }
    /// <summary>
    ///     TURN AROUND (default): the car stops past the goal and is turned onto the other direction's line at that end — the
    ///     road never ends, like an arcade's free run; RESTART: back to the start; STOP: it stays there, yours to turn or reset.
    /// </summary>
    public CourseEndAction End { get; set; } = CourseEndAction.TurnAround;
    public bool Timer { get; set; } = true;
    /// <summary>AI cars on the course, 0–<see cref="FreePlay.MaxCars"/>.</summary>
    public int Cars { get; set; }
    public Traffic Traffic { get; set; } = Traffic.Cruise;

    public FreePlayChoice Copy() => (FreePlayChoice)MemberwiseClone();
}

/// <summary>
///     FREE PLAY from the main menu: one lobby in the VS CPU lobby's style (carbon panels, value rows with ◀ ▶, START), hosted
///     by <see cref="Versus"/> (fade, backdrop, marquee, car select, loading): the run on the left (course incl. the circuits,
///     route, time of day and weather, what happens at the course end, timer, AI cars and how they drive), the car, colour and
///     gearbox on the right. The pause menu's CHANGE comes back here; two players: VERSUS, rule FREE RUN.
/// </summary>
public sealed class FreePlay(Catalog catalog)
{
    public const int MaxCars = 3;

    public enum Row { Course, Route, Conditions, End, Timer, Cars, Traffic, Car, Colour, Gearbox, Start }

    /// <summary>PickCar: DECIDE on CAR, the host opens the car select and hands the choice back (<see cref="SetCar"/>).</summary>
    public enum Result { None, Start, Back, PickCar }

    public FreePlayChoice Choice { get; private set; } = new();
    public int Car { get; private set; }
    public int Paint { get; private set; }
    public bool Manual { get; private set; }
    public string CarId => catalog.Cars[Car].Id;
    public Func<string, bool>? CarLocked { get; set; }

    private int _row;

    /// <summary>Opens on <paramref name="saved"/> (anything unknown falls back), the cursor where it was last.</summary>
    public void Open(FreePlayChoice saved, string car, int paint, bool manual)
    {
        Choice = saved.Copy();
        var course = CourseOf(Choice.Course);
        var cond = Versus.Conditions(course);
        if (!Choice.Course.StartsWith(course.Id + "_") || ConditionIndex() < 0)
            (Choice.Course, Choice.Fog) = ($"{course.Id}_{cond[0].Time}", cond[0].Fog);
        if (!Enum.IsDefined(Choice.End)) Choice.End = CourseEndAction.TurnAround;
        if (!Enum.IsDefined(Choice.Traffic)) Choice.Traffic = Traffic.Cruise;
        Choice.Cars = Math.Clamp(Choice.Cars, 0, MaxCars);
        Car = Math.Max(0, catalog.Cars.ToList().FindIndex(c => c.Id == car && CarLocked?.Invoke(c.Id) != true));
        (Paint, Manual) = (Math.Clamp(paint, 0, catalog.Cars[Car].Paints.Length - 1), manual);
    }

    public void SetCar(int car, int paint) => (Car, Paint) = (car, paint);

    /// <summary>Rows in cursor order; the AI's driving only with AI cars, the course end only on a point-to-point course.</summary>
    public Row[] Rows =>
    [
        Row.Course, Row.Route, Row.Conditions, .. CourseOf(Choice.Course).Circuit ? Array.Empty<Row>() : [Row.End], Row.Timer, Row.Cars,
        .. Choice.Cars > 0 ? new[] { Row.Traffic } : [], Row.Car, Row.Colour, Row.Gearbox, Row.Start,
    ];

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
            case Row.End: c.End = (CourseEndAction)Wrap((int)c.End + d, 3); break;
            case Row.Timer: c.Timer = !c.Timer; break;
            case Row.Cars: c.Cars = Wrap(c.Cars + d, MaxCars + 1); break;
            case Row.Traffic: c.Traffic = (Traffic)Wrap((int)c.Traffic + d, 5); break;
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
        else if ((k.Ok && rows[_row] == Row.Start) || ((k.Ok || k.X != 0) && rows[_row] == Row.Car))
        {
            sound?.Invoke("SYS006");
            return rows[_row] == Row.Start ? Result.Start : Result.PickCar;
        }
        else if ((k.X != 0 || k.Ok) && rows[_row] != Row.Start)
        {
            sound?.Invoke("SYS005");
            var row = rows[_row];
            Change(row, k.X != 0 ? k.X : 1);
            _row = Math.Max(0, Array.IndexOf(Rows, row)); // rows come and go (circuits, AI cars): stay on this one
        }
        else if (k.Back)
        {
            sound?.Invoke("BEEP001");
            return Result.Back;
        }
        return Result.None;
    }

    /// <summary>The AI cars of a run: the first <see cref="FreePlayChoice.Cars"/> rivals from one picked by the course, each at the traffic's strength.</summary>
    public static Rivals.Rival[] Field(FreePlayChoice c)
    {
        var all = Rivals.All;
        var first = c.Course.Sum(ch => ch) % all.Length; // stable across runs (string hashes are not)
        return [.. Enumerable.Range(0, Math.Clamp(c.Cars, 0, MaxCars)).Select(i => Strength(all[(first + i * 5) % all.Length], c.Traffic))];
    }

    /// <summary>CRUISE: no racing skill and 75 % engine torque (traffic you catch up with); the levels as a free battle's.</summary>
    public static Rivals.Rival Strength(Rivals.Rival r, Traffic t) =>
        t == Traffic.Cruise ? r with { Style = r.Style with { Skill = 0 }, Power = 0.75f } : FreeBattle.Strength(r, (AiLevel)(t - 1));

    // ------------------------------------------------------------ drawing

    private static readonly uint Grey = Overlay.Rgba(0.72f, 0.73f, 0.75f), Ink = Canvas.Shade(0.08f, 0.08f, 0.09f, 1);

    public static string EndName(CourseEndAction e) => e switch { CourseEndAction.Restart => "RESTART", CourseEndAction.Stop => "STOP", _ => "TURN AROUND" };

    private string Value(Row r)
    {
        var c = Choice;
        var course = CourseOf(c.Course);
        return r switch
        {
            Row.Course => course.Name, Row.Route => Catalog.DirectionName(course, c.Reverse), Row.Conditions => Versus.Conditions(course)[Math.Max(0, ConditionIndex())].Label,
            Row.End => EndName(c.End), Row.Timer => c.Timer ? "ON" : "OFF", Row.Cars => c.Cars == 0 ? "NONE" : c.Cars.ToString(),
            Row.Traffic => c.Traffic.ToString().ToUpperInvariant(), Row.Car => catalog.Cars[Car].Name,
            Row.Colour => $"{Paint + 1} / {catalog.Cars[Car].Paints.Length}", Row.Gearbox => Manual ? "MT" : "AT", _ => "",
        };
    }

    private static string Label(Row r) => r switch
    {
        Row.Course => "COURSE", Row.Route => "ROUTE", Row.Conditions => "CONDITIONS", Row.End => "AT COURSE END", Row.Timer => "TIMER",
        Row.Cars => "AI CARS", Row.Traffic => "AI DRIVING", Row.Car => "CAR", Row.Colour => "COLOUR", Row.Gearbox => "GEARBOX", _ => "",
    };

    private string Note(Row r) => r switch
    {
        Row.End => Choice.End switch
        {
            CourseEndAction.Restart => "Past the goal you stop, then go back to the start.",
            CourseEndAction.Stop => "Past the goal you stop and drive on yourself (R: road, B: turn).",
            _ => "Past the goal you stop and turn around: on down the other way.",
        },
        Row.Timer => "Run time and sector splits against your best (no records).",
        Row.Cars => Choice.Cars == 0 ? "The course to yourself." : "AI cars set off ahead of you and go round.",
        Row.Traffic => Choice.Traffic == Traffic.Cruise ? "Cruising traffic, easy to catch." : "Rivals at the VS CPU level of this name.",
        _ => "No timer pressure, no finish: drive as long as you like.",
    };

    private void ValueRow(Canvas c, Row r, float x0, float x1, float y, float pulse)
    {
        var sel = Selected == r;
        c.Text(Label(r), x0 + 14, y, 10, Grey, 0, 0.1f);
        c.Fit(Value(r), sel ? x1 - 28 : x1 - 14, y + 1, x1 - x0 - 120, 1, Canvas.White, 0.12f, 0.06f, 14);
        if (r == Row.Colour) Versus.Swatch(c, x0 + 100, y - 4, catalog.Cars[Car].Paints[Paint]);
        if (!sel) return;
        c.Arrow(x1 - 22, y - 9, x1 - 22, y + 1, x1 - 14, y - 4);
        c.Arrow(x0 + 106, y - 9, x0 + 106, y + 1, x0 + 98, y - 4);
        c.Glow(x0 + 6, y - 18, x1 - 6, y + 8, pulse);
    }

    public void Draw(Canvas c, float clock)
    {
        var pulse = Canvas.Pulse(clock * 300 % 360);
        var rows = Rows;
        // the run (left)
        c.Carbon(16, 72, 250, 404);
        c.Text("FREE RUN", 30, 94, 12, Grey, 0, 0.15f);
        var y = 118f;
        foreach (var r in rows.TakeWhile(r => r != Row.Car))
        {
            ValueRow(c, r, 16, 250, y, pulse);
            y += 28;
        }
        c.Rule(26, 240, y - 6);
        c.Fit(Note(Selected), 30, y + 14, 206, 0, Grey, 0.1f, 0, 11);
        c.Fit("Two players: VERSUS, rule FREE RUN.", 30, 394, 206, 0, Grey, 0.1f, 0, 10);

        // the course map (right, top): start green, goal red, as on the course select
        var course = CourseOf(Choice.Course);
        c.Carbon(262, 72, 496, 252, 1, false);
        Menu.MapLine(c, course, Choice.Reverse, 280, 84, 478, 214);
        c.Text(FormattableString.Invariant($"{course.LengthM / 1000:0.0} km   {course.ClimbM:0} m"), 379, 240, 11, Grey, 0.5f, 0.1f);

        // the car (right, bottom) and START
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
        Menu.Hint(c, Selected == Row.Car ? "UP/DOWN: Select    DECIDE: Car select    BACK: Main menu"
            : "UP/DOWN: Select    LEFT/RIGHT: Change    START: Drive    BACK: Main menu");
    }
}
