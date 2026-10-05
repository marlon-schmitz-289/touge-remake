using System.Numerics;
using System.Runtime.CompilerServices;

namespace Kansei.Physics;

/// <summary>
///     What the AI knows about a course line, built once per line and shared by every car on it: stations every
///     <see cref="Step"/> m along the line (point, left normal, height, the line's signed curvature, drivable full-grip
///     road either side), the bends (<see cref="Corner"/>) and the places where a pass can be set up (<see cref="Zone"/>).
/// </summary>
public sealed class CourseMap
{
    public const float Step = 2, RoomMax = 6, RoomStep = 0.25f;
    private const float Deg = 180 / MathF.PI;

    /// <summary>
    ///     A bend of the line: from/to (m along), apex (m along, tightest point), tightest radius, heading change (rad),
    ///     +1 left / −1 right.
    /// </summary>
    public sealed record Corner(float From, float To, float Radius, float Angle, int Dir, float Apex = 0)
    {
        public string Kind => Radius < 25 && Angle > 1.6f ? "hairpin" : Radius < 45 ? "tight" : Radius < 90 ? "medium" : "fast";
        public bool Hairpin => Kind == "hairpin";
        /// <summary>Hairpin or tight.</summary>
        public bool Slow => Radius < 45;
    }

    /// <summary>
    ///     A stretch where a chaser can pull alongside: the braking zone before corner <paramref name="Corner"/> (its inside
    ///     is the side to pass on), or a straight (<paramref name="Corner"/> −1, either side).
    /// </summary>
    public readonly record struct Zone(float From, float To, int Corner);

    public Vector3[] Line { get; }
    public float Length { get; }
    /// <summary>Per station: point on the line, unit left (XZ), signed curvature of the line (1/m, + left), free road left/right of the line (m).</summary>
    public Vector3[] Point { get; }
    public Vector3[] Left { get; }
    public float[] Bend { get; }
    public float[] RoomLeft { get; }
    public float[] RoomRight { get; }
    public IReadOnlyList<Corner> Corners { get; }
    public IReadOnlyList<Zone> Zones { get; }
    public int Count => Point.Length;

    private static readonly ConditionalWeakTable<Vector3[], CourseMap> Cache = new();

    /// <summary>The map of <paramref name="line"/> (built on first use, then shared).</summary>
    public static CourseMap Of(Vector3[] line, IGround ground, Func<int, float>? surfaceGrip) =>
        Cache.GetValue(line, l => new CourseMap(l, ground, surfaceGrip));

    /// <summary>
    ///     <paramref name="ground"/> null = no road probing (room = <see cref="RoomMax"/> both sides, for tests on bare lines).
    /// </summary>
    public CourseMap(Vector3[] line, IGround? ground, Func<int, float>? surfaceGrip)
    {
        Line = line;
        var lp = new LinePilot(line);
        Length = lp.Length;
        var n = (int)(Length / Step) + 1;
        (Point, Left, Bend, RoomLeft, RoomRight) = (new Vector3[n], new Vector3[n], new float[n], new float[n], new float[n]);
        for (var i = 0; i < n; i++)
        {
            var s = i * Step;
            Point[i] = lp.PointAt(s);
            Left[i] = lp.LeftAt(s);
        }
        for (var i = 0; i < n; i++) Bend[i] = Menger(Point[Math.Max(i - 3, 0)], Point[i], Point[Math.Min(i + 3, n - 1)]);
        for (var i = 0; i < n; i++)
            (RoomLeft[i], RoomRight[i]) = ground == null ? (RoomMax, RoomMax) : Free(ground, surfaceGrip, Point[i], Left[i]);
        Corners = FindCorners(line);
        Zones = FindZones();
    }

    /// <summary>
    ///     The free full-grip road across the line at <paramref name="p"/>: samples every <see cref="RoomStep"/> m out to
    ///     <see cref="RoomMax"/> each side (ground under it, no step over 0.35 m to the next sample, no wall within 0.3 m),
    ///     the run of good samples holding the line point — or, where the line itself runs over a wall or off the road
    ///     (tight hairpins), the nearest run of at least 1 m. Returns its ends as (left, right) of the line: right negative =
    ///     the free road starts left of the line.
    /// </summary>
    private static (float Left, float Right) Free(IGround ground, Func<int, float>? grip, Vector3 p, Vector3 left)
    {
        const int K = (int)(RoomMax / RoomStep);
        Span<float> y = stackalloc float[2 * K + 1];
        Span<WallContact> contacts = stackalloc WallContact[2];
        var y0 = ground.Raycast(p + Vector3.UnitY * 3, -Vector3.UnitY, 6, out var at) ? at.Point.Y : p.Y;
        for (var k = -K; k <= K; k++)
        {
            var q = p + left * (k * RoomStep);
            y[k + K] = ground.Raycast(q with { Y = y0 + 4 }, -Vector3.UnitY, 9, out var hit) && (grip?.Invoke(hit.Surface) ?? 1) >= 0.9f
                       && ground.CollideWalls([hit.Point + Vector3.UnitY * 0.4f], 0.3f, contacts) == 0 ? hit.Point.Y : float.NaN;
        }
        // runs of good samples without a step between neighbours
        int bestLo = 0, bestHi = -1;
        var bestScore = float.MaxValue;
        for (var k = 0; k < y.Length;)
        {
            if (float.IsNaN(y[k])) { k++; continue; }
            var lo = k;
            while (k + 1 < y.Length && !float.IsNaN(y[k + 1]) && MathF.Abs(y[k + 1] - y[k]) <= 0.35f) k++;
            var hi = k++;
            float a = (lo - K) * RoomStep, b = (hi - K) * RoomStep;
            // the run with the line point, else the nearest at least 1 m wide, at the road's height
            var score = a <= 0 && b >= 0 ? -1 : b - a < 1 ? float.MaxValue : MathF.Min(MathF.Abs(a), MathF.Abs(b)) + MathF.Abs(y[lo] - y0);
            if (score < bestScore) (bestScore, bestLo, bestHi) = (score, lo, hi);
        }
        if (bestHi < 0) return (0, 0);
        // a run that reaches the end of the scan counts as the full room
        return ((bestHi - K) * RoomStep + (bestHi == 2 * K ? 0 : -0.05f), (K - bestLo) * RoomStep + (bestLo == 0 ? 0 : -0.05f));
    }

    /// <summary>Station index at or below <paramref name="s"/>.</summary>
    public int Index(float s) => Math.Clamp((int)(s / Step), 0, Count - 1);

    /// <summary>Linear interpolation of a per-station value at <paramref name="s"/> m along.</summary>
    public float At(float[] values, float s)
    {
        var x = Math.Clamp(s / Step, 0, Count - 1);
        var i = Math.Min((int)x, Count - 2);
        return i < 0 ? values[0] : float.Lerp(values[i], values[i + 1], x - i);
    }

    /// <summary>Free road left / right of the line at <paramref name="s"/> (the narrower of the two stations around it).</summary>
    public (float Left, float Right) Room(float s)
    {
        int i = Index(s), j = Math.Min(i + 1, Count - 1);
        return (MathF.Min(RoomLeft[i], RoomLeft[j]), MathF.Min(RoomRight[i], RoomRight[j]));
    }

    /// <summary>The corner whose stretch [From − <paramref name="before"/>, To + <paramref name="after"/>] holds <paramref name="s"/>, −1 if none.</summary>
    public int CornerAt(float s, float before = 0, float after = 0)
    {
        for (var i = 0; i < Corners.Count; i++)
            if (s >= Corners[i].From - before && s <= Corners[i].To + after) return i;
        return -1;
    }

    /// <summary>First corner starting after <paramref name="s"/> (−1 if none).</summary>
    public int NextCorner(float s)
    {
        for (var i = 0; i < Corners.Count; i++)
            if (Corners[i].From > s) return i;
        return -1;
    }

    /// <summary>The zone holding <paramref name="s"/> (a braking zone before a straight that overlaps it), null if none.</summary>
    public Zone? ZoneAt(float s)
    {
        Zone? found = null;
        foreach (var z in Zones)
            if (s >= z.From && s <= z.To && (found == null || z.Corner >= 0)) found = z;
        return found;
    }

    /// <summary>Braking zones (the last 60 m before a tight bend, ≥ 5 m of road) and straights (≥ 80 m, ≥ 5.5 m of road).</summary>
    private List<Zone> FindZones()
    {
        var zones = new List<Zone>();
        float Width(float s) { var (l, r) = Room(s); return l + r; }
        bool Wide(float from, float to, float min)
        {
            for (var s = MathF.Max(from, 0); s <= to; s += Step)
                if (Width(s) < min) return false;
            return true;
        }
        for (var c = 0; c < Corners.Count; c++)
            if (Corners[c].Slow && Wide(Corners[c].From - 60, Corners[c].From, 5f))
                zones.Add(new Zone(MathF.Max(Corners[c].From - 90, 0), Corners[c].From, c));
        // straights: between corners (fast bends count as straight), wide enough throughout
        var prevEnd = 0f;
        foreach (var c in Corners.Where(c => c.Kind != "fast").Append(new Corner(Length, Length, 1, 0, 1)))
        {
            float from = prevEnd + 10, to = c.From - 10;
            var start = -1f;
            for (var s = from; s <= to; s += Step)
            {
                var ok = Width(s) >= 5.5f;
                if (ok && start < 0) start = s;
                if ((!ok || s + Step > to) && start >= 0)
                {
                    if (s - start >= 80) zones.Add(new Zone(start, s, -1));
                    start = -1;
                }
            }
            prevEnd = MathF.Max(prevEnd, c.To);
        }
        zones.Sort((a, b) => a.From.CompareTo(b.From));
        return zones;
    }

    /// <summary>Signed Menger curvature through three points (1/m, + = left turn).</summary>
    public static float Menger(Vector3 a3, Vector3 b3, Vector3 c3)
    {
        Vector2 a = new(a3.X, a3.Z), b = new(b3.X, b3.Z), c = new(c3.X, c3.Z);
        var den = Vector2.Distance(a, b) * Vector2.Distance(b, c) * Vector2.Distance(a, c);
        var cross = (b - a).X * (c - a).Y - (b - a).Y * (c - a).X;
        return den < 1e-6f ? 0 : -2 * cross / den; // a left turn (towards +X when heading +Z) has cross < 0
    }

    /// <summary>
    ///     Bends of <paramref name="line"/>: runs of points with radius under 120 m (Menger curvature over ±2 points like
    ///     <see cref="LinePilot"/>), gaps under 12 m merged, at least 20° of heading change.
    /// </summary>
    public static List<Corner> FindCorners(Vector3[] line)
    {
        var n = line.Length;
        var along = new float[n];
        for (var i = 1; i < n; i++) along[i] = along[i - 1] + new Vector2(line[i].X - line[i - 1].X, line[i].Z - line[i - 1].Z).Length();
        var k = new float[n];
        for (var i = 0; i < n; i++) k[i] = Menger(line[Math.Max(i - 2, 0)], line[i], line[Math.Min(i + 2, n - 1)]);
        var corners = new List<Corner>();
        var j = 0;
        while (j < n)
        {
            if (MathF.Abs(k[j]) < 1 / 120f) { j++; continue; }
            var dir = MathF.Sign(k[j]);
            var start = j;
            var end = j;
            while (j < n && (MathF.Sign(k[j]) == dir && MathF.Abs(k[j]) >= 1 / 120f || along[j] - along[end] < 12 && MathF.Sign(k[j]) != -dir))
            {
                if (MathF.Abs(k[j]) >= 1 / 120f && MathF.Sign(k[j]) == dir) end = j;
                j++;
            }
            float angle = 0, maxK = 0, apex = along[start];
            for (var i = start; i <= end; i++)
            {
                var ds = i + 1 < n ? along[i + 1] - along[i] : 0;
                angle += MathF.Abs(k[i]) * ds;
                if (MathF.Abs(k[i]) > maxK) (maxK, apex) = (MathF.Abs(k[i]), along[i]);
            }
            if (angle > 20 / Deg) corners.Add(new Corner(along[start], along[end], 1 / maxK, angle, (int)dir, apex));
            j = end + 1;
        }
        return corners;
    }
}
