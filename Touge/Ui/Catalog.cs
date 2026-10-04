using System.Numerics;
using Kansei.Physics;
using Touge.Formats;

namespace Touge.Ui;

/// <summary>
///     What the menus offer, read once from the ISO: the 11 courses (times of day present in COURSE.AFS, driving line for
///     the preview, which direction is downhill) and the 32 cars (maker, name, drivetrain, power, weight, paint colours from CAR_ENV).
/// </summary>
public sealed class Catalog
{
    /// <param name="Times">"DAY", "NIT", "RIN" as present.</param>
    /// <param name="Line">Forward driving line (XZ).</param>
    /// <param name="ForwardDownhill">Forward (_I) line ends lower than it starts; circuits have neither (<see cref="Circuit"/>).</param>
    public sealed record Course(string Id, string Name, string[] Times, Vector2[] Line, float LengthM, float ClimbM, bool ForwardDownhill, bool Circuit)
    {
        /// <summary>
        ///     The forward line turns clockwise seen from above: shoelace sum over (X, Z); Z points south in the game's
        ///     right-handed, y-up frame, so a positive sum is clockwise on a north-up map.
        /// </summary>
        public bool ForwardClockwise
        {
            get
            {
                float sum = 0;
                for (var i = 0; i < Line.Length; i++)
                {
                    var (a, b) = (Line[i], Line[(i + 1) % Line.Length]);
                    sum += a.X * b.Y - b.X * a.Y;
                }
                return sum > 0;
            }
        }
    }

    /// <param name="Name">Name as the original's car lists write it (CAR_NAME/T_TRIAL), chassis code in brackets.</param>
    public sealed record Car(string Id, string Maker, string Name, string Drive, int Ps, int Kg, uint[] Paints);

    /// <summary>Makers in the original's maker select order (T_MKSEL).</summary>
    public static readonly string[] Makers = ["TOYOTA", "NISSAN", "HONDA", "MITSUBISHI", "MAZDA", "SUBARU", "SUZUKI"];

    /// <summary>
    ///     The original's course select grid order (3 × 4, KCRSSEL0 h_selnam00; the ELF course table with Shomaru moved):
    ///     MYOUGI0/USUI0 are the circuits named plain 妙義/碓氷, MYOUGI/USUI the touge 真・妙義/真・碓氷 (MYOGI+/USUI+ in the
    ///     translation). The twelfth slot, 四峠走破 (four-pass run), has no course of its own and stays locked.
    /// </summary>
    private static readonly (string Id, string Name)[] CourseNames =
    [
        ("MYOUGI0", "MYOGI"), ("USUI0", "USUI"), ("AKAGI", "AKAGI"), ("AKINA", "AKINA"), ("HAPPOU", "HAPPOGAHARA"), ("IROHA", "IROHAZAKA"),
        ("MYOUGI", "MYOGI+"), ("USUI", "USUI+"), ("SHOMARU", "SHOMARU"), ("MOMIJI", "MOMIJI LINE"), ("SHIONA", "SHIONA"),
    ];

    private static readonly Dictionary<string, string> CarNames = new()
    {
        ["AE86T"] = "TRUENO GT-APEX [AE86]", ["AE86L"] = "LEVIN GT-APEX [AE86]", ["AE85"] = "LEVIN SR [AE85]", ["MR2"] = "MR2 G-Limited [SW20]",
        ["MRS"] = "MR-S S EDITION [ZZW30]", ["ALTEZ"] = "ALTEZZA RS-200 [SXE10]", ["GT-4"] = "CELICA GT-FOUR [ST205]",
        ["R32"] = "SKYLINE GT-R V-spec II [BNR32]", ["R34"] = "SKYLINE GT-R V-spec II [BNR34]", ["ER34"] = "SKYLINE 25GT TURBO [ER34]",
        ["S13"] = "SILVIA K's [S13]", ["S14Q"] = "SILVIA Q's [S14]", ["S14"] = "SILVIA K's AERO [S14]", ["S15"] = "SILVIA spec-R [S15]",
        ["ONE80"] = "180SX TYPE X [RPS13]", ["SIL80"] = "SILEIGHTY [RPS13]", ["EK9"] = "CIVIC TYPE R [EK9]", ["EG6"] = "CIVIC SiR II [EG6]",
        ["INTGR"] = "INTEGRA TYPE R [DC2]", ["S2000"] = "S2000 [AP1]", ["EVO3"] = "LANCER Evo. III GSR [CE9A]", ["EVO4"] = "LANCER Evo. IV RS [CN9A]",
        ["EVO7"] = "LANCER Evo. VII GSR [CT9A]", ["FD3S"] = "RX-7 Type R [FD3S]", ["FD3SA"] = "RX-7 SPIRIT R Type A [FD3S]", ["FC3S"] = "RX-7 Infini III [FC3S]",
        ["NA6C"] = "ROADSTER S Special [NA6CE]", ["NB8C"] = "ROADSTER RS [NB8C]", ["IMP"] = "IMPREZA WRX STi Version VI [GC8]",
        ["IMP2"] = "IMPREZA WRX STi [GDB]", ["IMP3"] = "IMPREZA WRX type R STi Version V [GC8]", ["CAPPU"] = "Cappuccino [EA11R]",
    };

    public IReadOnlyList<Course> Courses { get; }
    public IReadOnlyList<Car> Cars { get; }

    public Catalog(Iso9660 iso)
    {
        var models = iso.OpenAfs("CDVD/DATA/MODEL/COURSE.AFS");
        var data = iso.OpenAfs("CDVD/DATA/COURSE/CRS_DATA.AFS");
        Courses =
        [
            .. CourseNames.Select(c =>
            {
                string[] times = [.. new[] { "DAY", "NIT", "RIN" }.Where(t => models.Find($"{c.Id}_{t}.PAC") != null)];
                var drv = data.Find($"CRS_DRV_{c.Id}_I.BIN") ?? throw new FileNotFoundException($"CRS_DRV_{c.Id}_I.BIN");
                var line = DrivingLine.Read(data.Read(drv), DrivingLine.PointCount(c.Id));
                float length = 0, lo = float.MaxValue, hi = float.MinValue;
                for (var i = 0; i < line.Length; i++)
                {
                    if (i > 0) length += Vector2.Distance(new(line[i].X, line[i].Z), new(line[i - 1].X, line[i - 1].Z));
                    (lo, hi) = (MathF.Min(lo, line[i].Y), MathF.Max(hi, line[i].Y));
                }
                return new Course(c.Id, c.Name, times, [.. line.Select(p => new Vector2(p.X, p.Z))], length, hi - lo,
                    line[0].Y > line[^1].Y, c.Id.EndsWith('0'));
            }),
        ];
        var paints = CarPaint.Parse(iso.ReadFile("CDVD/DATA/BINARY/CAR_ENV.BIN"));
        Cars = [.. CarPaint.Cars.Select((id, i) => CarFor(id, MakerOf(i), paints[i]))];
    }

    /// <summary>A catalog from given lists (tests).</summary>
    public Catalog(IReadOnlyList<Course> courses, IReadOnlyList<Car> cars) => (Courses, Cars) = (courses, cars);

    /// <summary>Maker of HCAR car <paramref name="index"/>: <see cref="CarPaint.Cars"/> is grouped by maker in <see cref="Makers"/> order.</summary>
    private static string MakerOf(int index) => Makers[index switch { < 7 => 0, < 16 => 1, < 20 => 2, < 23 => 3, < 28 => 4, < 31 => 5, _ => 6 }];

    private static Car CarFor(string id, string maker, uint[] paints)
    {
        var s = CarSpecs.All[id];
        var drive = s.DriveFront >= 1 ? "FF" : s.DriveFront > 0 ? "4WD" : s.FrontWeight < 0.46f ? "MR" : "FR";
        // peak power from the torque curve's points (P = T·ω)
        var watts = s.TorqueRpm.Select((rpm, i) => s.TorqueNm[i] * rpm * MathF.PI / 30).Max();
        return new Car(id, maker, CarNames.GetValueOrDefault(id, id), drive, (int)MathF.Round(watts / 735.5f), (int)s.Mass, paints);
    }

    public static string TimeName(string t) => t switch { "NIT" => "NIGHT", "RIN" => "WET", _ => "DAY" };

    /// <summary>Direction as the original's route words: circuits clockwise/counter-clockwise (winding of the line), touge downhill/uphill.</summary>
    public static string DirectionName(Course c, bool reverse) =>
        c.Circuit ? c.ForwardClockwise != reverse ? "CLOCKWISE" : "COUNTER-CLOCKWISE" : c.ForwardDownhill != reverse ? "DOWNHILL" : "UPHILL";

    /// <summary>Paint 0xBBGGRR as an overlay colour.</summary>
    public static uint Swatch(uint bgr) => bgr | 0xFF000000;
}
