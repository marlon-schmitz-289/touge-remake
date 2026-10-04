using System.Numerics;
using Kansei.Physics;
using Touge.Formats;

namespace Touge.Ui;

/// <summary>
///     What the menus offer, read once from the ISO: the 11 courses (times of day present in COURSE.AFS, driving line for
///     the preview, which direction is downhill) and the 32 cars (name, drivetrain, power, weight, paint colours from CAR_ENV).
/// </summary>
public sealed class Catalog
{
    /// <param name="Times">"DAY", "NIT", "RIN" as present.</param>
    /// <param name="Line">Forward driving line (XZ).</param>
    /// <param name="ForwardDownhill">Forward (_I) line ends lower than it starts; circuits have neither (<see cref="Circuit"/>).</param>
    public sealed record Course(string Id, string Name, string[] Times, Vector2[] Line, float LengthM, float ClimbM, bool ForwardDownhill, bool Circuit);

    public sealed record Car(string Id, string Name, string Drive, int Ps, int Kg, uint[] Paints);

    // ELF course order (FORMATS.md) regrouped: touge first, the two circuits last
    private static readonly (string Id, string Name)[] CourseNames =
    [
        ("AKINA", "AKINA"), ("AKAGI", "AKAGI"), ("MYOUGI", "MYOGI"), ("USUI", "USUI"), ("IROHA", "IROHAZAKA"), ("HAPPOU", "HAPPOGAHARA"),
        ("MOMIJI", "MOMIJI LINE"), ("SHIONA", "SHIONA"), ("SHOMARU", "SHOMARU"), ("MYOUGI0", "MYOGI CIRCUIT"), ("USUI0", "USUI CIRCUIT"),
    ];

    private static readonly Dictionary<string, string> CarNames = new()
    {
        ["AE86T"] = "Toyota Sprinter Trueno GT-APEX", ["AE86L"] = "Toyota Corolla Levin GT-APEX", ["AE85"] = "Toyota Corolla Levin SR",
        ["MR2"] = "Toyota MR2 GT-S", ["MRS"] = "Toyota MR-S", ["ALTEZ"] = "Toyota Altezza RS200", ["GT-4"] = "Toyota Celica GT-Four",
        ["R32"] = "Nissan Skyline GT-R (R32)", ["R34"] = "Nissan Skyline GT-R (R34)", ["ER34"] = "Nissan Skyline 25GT-T",
        ["S13"] = "Nissan Silvia K's (S13)", ["S14Q"] = "Nissan Silvia Q's (S14)", ["S14"] = "Nissan Silvia K's (S14)", ["S15"] = "Nissan Silvia Spec-R",
        ["ONE80"] = "Nissan 180SX", ["SIL80"] = "Nissan Sileighty", ["EK9"] = "Honda Civic Type R", ["EG6"] = "Honda Civic SiR-II",
        ["INTGR"] = "Honda Integra Type R", ["S2000"] = "Honda S2000", ["EVO3"] = "Mitsubishi Lancer Evolution III",
        ["EVO4"] = "Mitsubishi Lancer Evolution IV", ["EVO7"] = "Mitsubishi Lancer Evolution VII", ["FD3S"] = "Mazda RX-7 Type R (FD3S)",
        ["FD3SA"] = "Mazda RX-7 Type RS (FD3S)", ["FC3S"] = "Mazda Savanna RX-7 (FC3S)", ["NA6C"] = "Mazda Eunos Roadster",
        ["NB8C"] = "Mazda Roadster RS", ["IMP"] = "Subaru Impreza WRX STi (GC8)", ["IMP2"] = "Subaru Impreza WRX STi (GDB)",
        ["IMP3"] = "Subaru Impreza WRX Type R", ["CAPPU"] = "Suzuki Cappuccino",
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
        Cars = [.. CarPaint.Cars.Select((id, i) => CarFor(id, paints[i]))];
    }

    private static Car CarFor(string id, uint[] paints)
    {
        var s = CarSpecs.All[id];
        var drive = s.DriveFront >= 1 ? "FF" : s.DriveFront > 0 ? "4WD" : s.FrontWeight < 0.46f ? "MR" : "FR";
        // peak power from the torque curve's points (P = T·ω)
        var watts = s.TorqueRpm.Select((rpm, i) => s.TorqueNm[i] * rpm * MathF.PI / 30).Max();
        return new Car(id, CarNames.GetValueOrDefault(id, id), drive, (int)MathF.Round(watts / 735.5f), (int)s.Mass, paints);
    }

    public static string TimeName(string t) => t switch { "NIT" => "NIGHT", "RIN" => "RAIN", _ => "DAY" };

    /// <summary>Direction label: circuits normal/reverse, touge downhill/uphill.</summary>
    public static string DirectionName(Course c, bool reverse) =>
        c.Circuit ? reverse ? "REVERSE" : "NORMAL" : c.ForwardDownhill != reverse ? "DOWNHILL" : "UPHILL";

    /// <summary>Paint 0xBBGGRR as an overlay colour.</summary>
    public static uint Swatch(uint bgr) => bgr | 0xFF000000;
}
