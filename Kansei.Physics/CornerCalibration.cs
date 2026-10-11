using System.Globalization;
using System.Numerics;

namespace Kansei.Physics;

/// <summary>
///     Course knowledge of the top drivers: per course line and drivetrain class a factor on the planned grip of each bend
///     (<see cref="CourseMap.Corners"/>), baked offline by <c>--ai-bench calibrate</c> into CornerGrip.csv: two cars per class
///     driven as the good human H, a wall hit in or after the bend −8 %, unused road on its outside up to +2 % per pass, the
///     pass with the fewest hits kept. Lines are matched by their geometry (<see cref="Key"/>), so every mode gets it without
///     plumbing. Re-bake after physics changes (README).
/// </summary>
public static class CornerCalibration
{
    public enum Class { Rwd, Awd, Ff }

    public static Class Of(CarSpec spec) => spec.DriveFront <= 0 ? Class.Rwd : spec.DriveFront >= 1 ? Class.Ff : Class.Awd;

    /// <summary>A course line's identity: point count and its ends (0.1 m).</summary>
    public static string Key(Vector3[] line) =>
        FormattableString.Invariant($"{line.Length}:{line[0].X:F1},{line[0].Z:F1}:{line[^1].X:F1},{line[^1].Z:F1}");

    private static readonly Dictionary<(string, Class), float[]> Table = Load();

    /// <summary>The baked factors for <paramref name="line"/> in <paramref name="spec"/>'s class, null = none.</summary>
    public static float[]? Factors(Vector3[] line, CarSpec spec) => Table.GetValueOrDefault((Key(line), Of(spec)));

    /// <summary>One CSV row: course and direction (for people), class, line key, the factors.</summary>
    public static string Row(string name, Class c, string key, float[] factors) =>
        $"{name};{c};{key};{string.Join(" ", factors.Select(f => f.ToString("F3", CultureInfo.InvariantCulture)))}";

    private static Dictionary<(string, Class), float[]> Load()
    {
        var table = new Dictionary<(string, Class), float[]>();
        using var stream = typeof(CornerCalibration).Assembly.GetManifestResourceStream("Kansei.Physics.CornerGrip.csv");
        if (stream == null) return table;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('#') || line.Split(';') is not [_, var c, var key, var f]) continue;
            table[(key, Enum.Parse<Class>(c))] = [.. f.Split(' ').Select(x => float.Parse(x, CultureInfo.InvariantCulture))];
        }
        return table;
    }
}
