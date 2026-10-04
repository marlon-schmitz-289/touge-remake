using System.Numerics;
using Kansei.Physics;
using Touge.Formats;
using Touge.Ui;

namespace Touge;

/// <summary>
///     Goal and closed ends of a point-to-point course in one direction, from the course's own geometry (FORMATS.md
///     "Kursenden"): the goal is the finish arch (<c>gate01</c> forward, <c>gate03</c> reverse), the run-out ends at the
///     "road closed" barricades that <c>gate02</c>/<c>gate00</c> add 13–62 m behind it. The line's valid point count runs on
///     past the arch (Akina 231 m, through the barricades) or stops short of it (Iroha uphill 78 m), so the line is cut at
///     the arch, with the file's run-out points where it is short. The barricades and the road just behind the first
///     spawnable point become walls; the collision itself runs on 15–150 m past the barricades and up to 60 m behind the
///     start. Circuits (MYOUGI0, USUI0) have neither.
/// </summary>
public static class CourseEnd
{
    /// <param name="Line">Driving line ending at the goal (<see cref="LapTimer"/> finishes at its end).</param>
    /// <param name="RunOut">Line from the goal to the end barrier (empty without one).</param>
    /// <param name="Barrier">End barrier, its normal towards the course (null: none).</param>
    public sealed record Ends(Vector3[] Line, Vector3[] RunOut, TriangleGround.WallSegment? Barrier);

    /// <summary>Behind the first spawnable point the start barrier stands this far (m, the car's centre spawns on the point).</summary>
    public const float BehindStart = 5;

    /// <summary>Reads the line with run-out and the gates of <paramref name="courseTime"/>'s PAC, adds the barriers to <paramref name="ground"/>.</summary>
    public static Ends Close(Iso9660 iso, string courseTime, bool reverse, TriangleGround ground)
    {
        var course = courseTime[..courseTime.LastIndexOf('_')];
        var ext = CourseLoader.ReadDrivingLine(iso, course, reverse, runOut: true);
        var valid = ext[..DrivingLine.PointCount(course)];
        if (course.EndsWith('0')) return new Ends(valid, [], null); // circuits: laps, no ends

        var models = iso.OpenAfs("CDVD/DATA/MODEL/COURSE.AFS");
        var pac = models.Read(models.Find(courseTime + ".PAC") ?? throw new FileNotFoundException(courseTime + ".PAC"));
        var entries = Pac.Entries(pac);
        var path = new LinePilot(ext);
        // gate vertices beside the line (|lateral| < 15 m) as distance along it
        float[] Along(string gate) =>
        [
            .. entries.Where(e => e.Type == 3 && e.Name == gate)
                .SelectMany(e => Mesh.Parse(pac.AsSpan(e.Offset, e.Size)).Materials.SelectMany(m => m.Triangles))
                .Select(v => { path.Nearest(v.Position); return path.Track(v.Position); })
                .Where(t => MathF.Abs(t.Lateral) < 15).Select(t => t.Along),
        ];
        var arch = Along(reverse ? "gate03" : "gate01");
        if (arch.Length == 0) return new Ends(valid, [], null);
        var goal = arch.Average();
        var line = Cut(path, ext, 0, goal + LapTimer.Gate);

        TriangleGround.WallSegment? start = null;
        // start: first point with ground under both axles (as LinePilot.Spawn), the barrier behind its tail
        for (var i = 0; i < line.Length - 1; i++)
        {
            var axle = Vector3.Normalize(line[i + 1] - line[i]) * 3;
            if (!OnGround(ground, line[i]) || !OnGround(ground, line[i] - axle) || !OnGround(ground, line[i] + axle)) continue;
            start = Across(ground, line[i] - axle * (BehindStart / 3), Xz(axle), true);
            if (start != null) ground.AddWalls(start.Value);
            break;
        }

        var behind = Along(reverse ? "gate00" : "gate02").Where(s => s > goal + 5).ToArray();
        var end = behind.Length > 0 ? Across(ground, path.PointAt(behind.Max()), Xz(path.PointAt(behind.Max() + 1) - path.PointAt(behind.Max() - 1)), false) : null;
        if (end != null) ground.AddWalls(end.Value);
        static string Width(TriangleGround.WallSegment? w) => w is { } x ? $"{Vector3.Distance(x.A, x.B):F1} m breit" : "keine (Kollision endet)";
        Console.WriteLine($"[Drive] Ziel {goal - new LinePilot(valid).Length:+0;-0} m gegenüber dem Ende der gültigen Linie, " +
                          $"Endsperre {(end != null ? $"{behind.Max() - goal:F1} m hinter dem Ziel, " : "")}{Width(end)}, Sperre hinter dem Start {Width(start)}");
        return new Ends(line, end != null ? Cut(path, ext, goal, behind.Max()) : [], end);
    }

    private static bool OnGround(TriangleGround g, Vector3 p) => g.Raycast(p + Vector3.UnitY * 5, -Vector3.UnitY, 20, out _);

    /// <summary>Points of <paramref name="line"/> from <paramref name="from"/> to <paramref name="to"/> m along it, both ends interpolated.</summary>
    private static Vector3[] Cut(LinePilot path, Vector3[] line, float from, float to)
    {
        var inner = new List<Vector3> { path.PointAt(from) };
        float s = 0;
        for (var i = 1; i < line.Length; i++)
        {
            s += new Vector2(line[i].X - line[i - 1].X, line[i].Z - line[i - 1].Z).Length();
            if (s > from && s < to) inner.Add(line[i]);
        }
        inner.Add(path.PointAt(to));
        return [.. inner];
    }

    /// <summary>
    ///     Wall across the road at <paramref name="c"/> (travel direction <paramref name="dir"/>), from edge wall to edge wall
    ///     (0.5 m into them, at most 30 m each side), facing back against the travel direction (<paramref name="forward"/>:
    ///     along it, behind the start). Null where there is no drivable ground: the collision already ends there.
    /// </summary>
    private static TriangleGround.WallSegment? Across(TriangleGround ground, Vector3 c, Vector2 dir, bool forward)
    {
        if (!ground.Raycast(c + Vector3.UnitY * 5, -Vector3.UnitY, 20, out var hit)) return null;
        var d = Vector2.Normalize(dir);
        var side = new Vector2(-d.Y, d.X);
        float Reach(Vector2 r)
        {
            var best = 30f;
            foreach (var w in ground.Walls)
            {
                Vector2 a = new(w.A.X, w.A.Z), e = new Vector2(w.B.X, w.B.Z) - a, o = new(c.X, c.Z);
                var den = Cross(r, e);
                if (MathF.Abs(den) < 1e-6f) continue;
                float t = Cross(a - o, e) / den, u = Cross(a - o, r) / den;
                if (t > 0 && u is >= 0 and <= 1 && MathF.Abs(Vector3.Lerp(w.A, w.B, u).Y - hit.Point.Y) < 3) best = MathF.Min(best, t + 0.5f);
            }
            return best;
        }
        Vector2 left = side * Reach(side), right = -side * Reach(-side), n = forward ? d : -d;
        return new TriangleGround.WallSegment(hit.Point + new Vector3(left.X, 0, left.Y), hit.Point + new Vector3(right.X, 0, right.Y), new Vector3(n.X, 0, n.Y));
    }

    private static Vector2 Xz(Vector3 v) => new(v.X, v.Z);

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
