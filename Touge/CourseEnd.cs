using System.Numerics;
using Kansei.Physics;
using Touge.Formats;
using Touge.Ui;

namespace Touge;

/// <summary>
///     Start, goal and closed ends of a point-to-point course in one direction, from the course's own geometry (FORMATS.md
///     "Kursenden"). Each end of the road has one arch: <c>gate03</c> at the forward start, <c>gate01</c> at the forward
///     goal. A race starts at the arch of its end and finishes at the other one, which is where the race in the opposite
///     direction starts – so downhill ends where uphill starts, both between the same two arches. The car spawns
///     <see cref="Lead"/> m behind its start arch, like the original's line point 0 on Akina. The line's own valid points
///     start up to 323 m before the arch (IROHA uphill) or 20 m past it and end 231 m past the goal arch or 78 m short of it, so the line
///     is extended behind its first point and past its run-out points (along CRS_ROAD) and cut to spawn…goal. Walls:
///     across the road behind the spawn and at the end of the run-out: at least where the other direction's "road closed"
///     barricades stand (<c>gate02</c>/<c>gate00</c>, 13–62 m past the goal, not drawn in this direction:
///     <see cref="CourseLoader.RaceGates"/>), on to <see cref="RunOut"/> m where the road goes on without a hairpin.
///     Circuits (MYOUGI0, USUI0) have neither.
/// </summary>
public static class CourseEnd
{
    /// <param name="Line">Driving line from the spawn to the goal (+ <see cref="LapTimer.Gate"/>: the timer finishes at its end).</param>
    /// <param name="Start">Start line, m along <paramref name="Line"/> (0 on circuits).</param>
    /// <param name="RunOut">Line from the goal to the end barrier (empty without one).</param>
    /// <param name="Barrier">End barrier, its normal towards the course (null: none).</param>
    public sealed record Ends(Vector3[] Line, float Start, Vector3[] RunOut, TriangleGround.WallSegment? Barrier);

    /// <summary>The car's centre spawns this far behind its start arch (m), the start barrier stands <see cref="BehindStart"/> m further back.</summary>
    public const float Lead = 5, BehindStart = 5;

    /// <summary>
    ///     Run-out the end barrier gets at least (m past the goal) where the road goes on past the barricades: room to coast
    ///     down from 160 km/h at 0.9 g (<see cref="Drive.Coast"/> brakes harder where the road ends sooner).
    /// </summary>
    public const float RunOut = 115;

    /// <summary>Line extension behind the first point (m), room for the spawn and its barrier where the arch stands behind it.</summary>
    private const float Back = 40;

    /// <summary>
    ///     Driving line of <paramref name="course"/> with run-out, extended <see cref="Back"/> m behind its first point, and
    ///     its start/goal arch and the farthest barricade as distance along it (null without arches: circuits).
    /// </summary>
    public static (Vector3[] Line, float Start, float Goal, float Barricade)? Marks(Iso9660 iso, string course, string time, bool reverse)
    {
        if (course.EndsWith('0')) return null; // circuits: laps, no ends
        var ext = CourseLoader.ReadDrivingLine(iso, course, reverse, runOut: true);
        var data = iso.OpenAfs("CDVD/DATA/COURSE/CRS_DATA.AFS");
        var road = CourseRoad.Read(data.Read(data.Find($"CRS_ROAD_{course}.BIN") ?? throw new FileNotFoundException($"CRS_ROAD_{course}.BIN")));
        ext = [ext[0] + Vector3.Normalize(ext[0] - ext[1]) * Back, .. ext];
        var models = iso.OpenAfs("CDVD/DATA/MODEL/COURSE.AFS");
        var name = $"{course}_{time}.PAC";
        var pac = models.Read(models.Find(name) ?? throw new FileNotFoundException(name));
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
        float[] start = Along(reverse ? "gate01" : "gate03"), goal = Along(reverse ? "gate03" : "gate01");
        if (start.Length == 0 || goal.Length == 0) return null;
        var barricade = Along(reverse ? "gate00" : "gate02").Where(s => s > goal.Average() + 5).DefaultIfEmpty(0).Max();
        return ([.. ext, .. Beyond(road, ext[^1], ext[^1] - ext.Last(v => Vector3.Distance(v, ext[^1]) > 5))], start.Average(), goal.Average(), barricade);
    }

    /// <summary>Line, start and run-out of <paramref name="courseTime"/> in one direction, adds the barriers to <paramref name="ground"/>.</summary>
    public static Ends Close(Iso9660 iso, string courseTime, bool reverse, TriangleGround ground)
    {
        var course = courseTime[..courseTime.LastIndexOf('_')];
        if (Marks(iso, course, courseTime[(courseTime.LastIndexOf('_') + 1)..], reverse) is not var (ext, start, goal, barricade))
            return new Ends(CourseLoader.ReadDrivingLine(iso, course, reverse), 0, [], null);
        var path = new LinePilot(ext);
        bool On(float s) => OnGround(ground, path.PointAt(s));
        // spawn Lead m behind the arch, or as close as there is ground under both axles and room for the body
        // for the biggest car (MYOUGI downhill: the road ends 4 m behind the arch); LinePilot.Spawn checks again with the real one
        var probe = new Vehicle(CarSpec.AE86 with { Length = CarSpecs.All.Values.Max(c => c.Length), Width = CarSpecs.All.Values.Max(c => c.Width) });
        var (probes, contacts) = (new Vector3[4], new WallContact[8]);
        bool Clear(float s)
        {
            if (!On(s - 3) || !On(s + 3) || !ground.Raycast(path.PointAt(s) + Vector3.UnitY * 5, -Vector3.UnitY, 20, out var hit)) return false;
            var d = path.PointAt(s + 1) - path.PointAt(s - 1);
            probe.Reset(hit.Point, MathF.Atan2(d.X, d.Z));
            probe.WallProbes(probes);
            return ground.CollideWalls(probes, Vehicle.ProbeRadius, contacts) == 0;
        }
        var spawn = start - Lead;
        while (spawn < start + 50 && !Clear(spawn)) spawn += 0.5f;
        var line = Cut(path, ext, spawn, goal + LapTimer.Gate);

        // behind the spawn: BehindStart m back, nearer where the ground ends sooner (never on the car: its tail is 2.1 m back)
        TriangleGround.WallSegment? back = null;
        for (var s = spawn - BehindStart; back == null && s < spawn - 2.5f; s += 0.5f) back = Across(ground, path, s, true);
        if (back != null) ground.AddWalls(back.Value);

        // past the goal: the barricades, or RunOut m on while the road goes on without a tight bend (radius < ~25 m: no
        // coasting into a hairpin, SHIONA uphill, SHOMARU downhill); a 2 m margin before its end for the wall's ground
        Vector2 Heading(float s) => Vector2.Normalize(Xz(path.PointAt(s + 1) - path.PointAt(s - 1)));
        var road = goal;
        while (road < MathF.Max(barricade, goal + RunOut) + 2 && road < path.Length && On(road + 1) && Vector2.Dot(Heading(road - 4), Heading(road + 6)) > 0.92f)
            road += 1;
        var endAt = barricade > 0 ? Math.Clamp(goal + RunOut, barricade, MathF.Max(barricade, road - 2)) : 0;
        var end = endAt > 0 ? Across(ground, path, endAt, false) : null;
        if (end != null) ground.AddWalls(end.Value);
        static string Width(TriangleGround.WallSegment? w) => w is { } x ? $"{Vector3.Distance(x.A, x.B):F1} m breit" : "keine (Kollision endet)";
        Console.WriteLine($"[Drive] Start {start - spawn:F1} m vor dem Spawn, Rennen {goal - start:F0} m, " +
                          $"Endsperre {(end != null ? $"{endAt - goal:F1} m hinter dem Ziel (Böcke {barricade - goal:F1} m), " : "")}{Width(end)}, Sperre hinter dem Start {Width(back)}");
        return new Ends(line, start - spawn, end != null ? Cut(path, ext, goal, endAt) : [], end);
    }

    /// <summary>
    ///     CRS_ROAD centre points past <paramref name="p"/> (travel direction <paramref name="dir"/>, over the last 5 m: the file's
    ///     last run-out point can be a 0.2 m step sideways): the modelled road runs on
    ///     past the end of the line's run-out points (USUI uphill 76 m, AKAGI downhill 343 m), where the run-out may need it.
    /// </summary>
    private static IEnumerable<Vector3> Beyond(Vector3[] road, Vector3 p, Vector3 dir)
    {
        var i = 0;
        for (var k = 1; k < road.Length; k++) if (Vector3.DistanceSquared(road[k], p) < Vector3.DistanceSquared(road[i], p)) i = k;
        var step = Vector3.Dot(road[Math.Min(i + 1, road.Length - 1)] - road[Math.Max(i - 1, 0)], dir) > 0 ? 1 : -1;
        var ahead = false;
        for (; i >= 0 && i < road.Length && Vector3.Distance(road[i], p) < RunOut + 50; i += step)
            if (ahead |= Vector3.Dot(road[i] - p, dir) > 0) yield return road[i];
    }

    private static bool OnGround(TriangleGround g, Vector3 p) => g.Raycast(p + Vector3.UnitY * 5, -Vector3.UnitY, 20, out _);

    /// <summary>Points of <paramref name="line"/> from <paramref name="from"/> to <paramref name="to"/> m along it, both ends interpolated.</summary>
    public static Vector3[] Cut(LinePilot path, Vector3[] line, float from, float to)
    {
        var inner = new List<Vector3> { path.PointAt(from) };
        float s = 0;
        for (var i = 1; i < line.Length; i++)
        {
            s += new Vector2(line[i].X - line[i - 1].X, line[i].Z - line[i - 1].Z).Length();
            if (s > from + 0.5f && s < to - 0.5f) inner.Add(line[i]);
        }
        inner.Add(path.PointAt(to));
        return [.. inner];
    }

    /// <summary>
    ///     Wall across the road at <paramref name="s"/> m along <paramref name="path"/>, from edge wall to edge wall (0.5 m
    ///     into them, at most 30 m each side), facing back against the travel direction (<paramref name="forward"/>: along it,
    ///     behind the start). Null where there is no drivable ground: the collision already ends there.
    /// </summary>
    private static TriangleGround.WallSegment? Across(TriangleGround ground, LinePilot path, float s, bool forward)
    {
        var c = path.PointAt(s);
        if (!ground.Raycast(c + Vector3.UnitY * 5, -Vector3.UnitY, 20, out var hit)) return null;
        var d = Vector2.Normalize(Xz(path.PointAt(s + 1) - path.PointAt(s - 1)));
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
