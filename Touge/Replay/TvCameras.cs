using System.Numerics;
using Touge.Formats;

namespace Touge.Replays;

/// <summary>
///     Trackside TV cameras of the replay: the original's REPCAM table of the course and direction (<see cref="ReplayCameras"/>),
///     each covering a stretch of road; while the car runs through it the camera eye and zoom move from its first keyframe
///     to its second, always looking at the car. Courses without a table get cameras placed along the road (<see cref="Auto"/>).
///     With the course (<paramref name="hull"/>) <see cref="Update"/> directs them: a shot is held at least <see cref="MinHold"/>
///     s, a camera that cannot see the car (terrain between, <see cref="Blind"/> s) is cut away from, and where the table's
///     camera cannot see it a trackside camera ahead of the car takes over (<see cref="Trackside"/>).
/// </summary>
public sealed class TvCameras(ReplayCameras.Cam[] cams, Vector3[] road, bool reverse, CameraHull? hull = null)
{
    /// <summary>Shortest shot (s) before the next cut, unless the car is lost from view; how long (s) it may be hidden.</summary>
    public const float MinHold = 2.5f, Blind = 0.25f;

    private readonly ReplayCameras.Cam[] _cams = cams.Length > 0 ? [.. cams.Select(c => Sane(c, road))] : Auto(road);
    private readonly Vector3[] _road = road;
    private readonly bool _reverse = reverse;
    private int _seg, _index = -1;
    private bool _side;
    private float _held, _blind;
    private Vector3 _look, _lookRate, _sideEye;

    public int Count => _cams.Length;

    /// <summary>
    ///     A camera every 60 road points (~120 m), 9 m beside the road (sides alternating) and 4 m up, 40 points into its
    ///     stretch, fixed, zooming in slightly as the car comes closer.
    /// </summary>
    public static ReplayCameras.Cam[] Auto(Vector3[] road)
    {
        var cams = new List<ReplayCameras.Cam>();
        for (int from = 0, k = 0; from < road.Length - 1; from += 60, k++)
        {
            var at = Math.Min(from + 40, road.Length - 2);
            var dir = Vector3.Normalize((road[at + 1] - road[at]) with { Y = 0 } + new Vector3(0, 0, 1e-6f));
            var side = new Vector3(dir.Z, 0, -dir.X) * (k % 2 == 0 ? 9 : -9);
            var eye = road[at] + side + Vector3.UnitY * 4;
            cams.Add(new ReplayCameras.Cam(1, from, Math.Min(from + 60, road.Length), new(eye, road[from], 28), new(eye, road[at], 20)));
        }
        return [.. cams];
    }

    /// <summary>Road progress of <paramref name="p"/> as a fractional ROAD index (local search from the last one, global when lost).</summary>
    public float Progress(Vector3 p)
    {
        var n = _road.Length;
        int lo = Math.Max(_seg - 12, 0), hi = Math.Min(_seg + 12, n - 2);
        var best = float.MaxValue;
        for (var i = lo; i <= hi; i++)
        {
            var d = Vector3.DistanceSquared(_road[i], p);
            if (d < best) (best, _seg) = (d, i);
        }
        if (best > 30 * 30 || _seg == lo && lo > 0 || _seg == hi && hi < n - 2) // lost, or still running away at the window's edge (a seek)
            for (var i = 0; i < n - 1; i++)
            {
                var d = Vector3.DistanceSquared(_road[i], p);
                if (d < best) (best, _seg) = (d, i);
            }
        var seg = Math.Min(_seg, n - 2);
        var ab = _road[seg + 1] - _road[seg];
        var t = Math.Clamp(Vector3.Dot(p - _road[seg], ab) / MathF.Max(ab.LengthSquared(), 1e-6f), 0, 1);
        var idx = seg + t;
        return _reverse ? n - 1 - idx : idx; // the reverse table counts from the far end
    }

    /// <summary>Camera for the car at <paramref name="car"/>: eye, vertical FOV (degrees) and which camera (cuts when it changes).</summary>
    public (Vector3 Eye, float Fov, int Index) At(Vector3 car)
    {
        var p = Progress(car);
        var index = Pick(p);
        var (eye, fov) = Shot(index, p);
        return (eye, fov, index);
    }

    private int Pick(float p)
    {
        if (p < _cams[0].From) return 0;
        for (var i = 0; i < _cams.Length; i++)
            if (p >= _cams[i].From && p < _cams[i].To) return i;
        return _cams.Length - 1;
    }

    /// <summary>
    ///     Eye and FOV of camera <paramref name="index"/> at progress <paramref name="p"/> (held at its ends outside its stretch).
    /// </summary>
    private (Vector3 Eye, float Fov) Shot(int index, float p)
    {
        var c = _cams[index];
        var t = Math.Clamp((p - c.From) / MathF.Max(c.To - c.From, 1), 0, 1);
        return (Vector3.Lerp(c.A.Eye, c.B.Eye, t), Math.Clamp(float.Lerp(c.A.Fov, c.B.Fov, t), 3, 60));
    }

    /// <summary>
    ///     A table camera whose eyes are both places by the road: kind 4 does not travel (its second "eye" is a few metres from
    ///     the origin), and a key more than 150 m from the road (SHOMARU has one 3 km off) takes the other key's eye.
    /// </summary>
    private static ReplayCameras.Cam Sane(ReplayCameras.Cam c, Vector3[] road)
    {
        bool Near(Vector3 e) => road.Any(r => Vector3.DistanceSquared(r, e) < 150 * 150);
        var (a, b) = (Near(c.A.Eye), c.Kind != 4 && Near(c.B.Eye));
        return a && !b ? c with { B = c.B with { Eye = c.A.Eye } } : !a && b ? c with { A = c.A with { Eye = c.B.Eye } } : c;
    }

    /// <summary>
    ///     One frame of the TV view on the car at <paramref name="car"/>: eye, aim, vertical FOV (degrees) and whether this frame
    ///     cuts. The table's camera for the car's place once the shot has run <see cref="MinHold"/> s and that camera sees the
    ///     car; earlier only when the shot has lost the car (<see cref="Blind"/> s hidden, or too far) and another one sees it —
    ///     the table's, else a <see cref="Trackside"/> camera ahead. The aim follows the car on a stiff spring (a TV operator).
    /// </summary>
    public (Vector3 Eye, Vector3 Look, float Fov, bool Cut) Update(Vector3 car, float dt, bool snap)
    {
        var p = Progress(car);
        var index = Pick(p);
        var target = car + Vector3.UnitY;
        _held += dt;
        var (eye, fov) = _index < 0 ? default : _side ? (_sideEye, Zoom(_sideEye, target)) : Shot(_index, p);
        _blind = _index >= 0 && Sees(eye, target) ? 0 : _blind + dt;
        var far = _side && Vector3.Distance(eye, target) > 60; // a trackside camera the car has left behind (the table's zoom from afar)
        var table = Shot(index, p);
        var tableSees = Sees(table.Eye, target);
        var lost = _blind > Blind || far;
        var cut = snap || _index < 0 || _held >= MinHold && index != _index && tableSees;
        var side = (cut || lost) && !tableSees ? Trackside(car) : default;
        cut |= lost && (tableSees || side.Sees || far);
        if (cut)
        {
            (_index, _held, _blind, _side) = (index, 0, 0, !tableSees);
            if (tableSees) (eye, fov) = table;
            else (eye, fov) = (_sideEye = side.Eye, Zoom(side.Eye, target));
            (_look, _lookRate) = (target, Vector3.Zero);
        }
        else CameraRig.Spring(ref _look, ref _lookRate, target, 16, dt);
        return (eye, _look, fov, cut);
    }

    /// <summary>Nothing of the course between <paramref name="eye"/> and <paramref name="target"/> (always without a hull).</summary>
    private bool Sees(Vector3 eye, Vector3 target) => hull?.Hit(eye, target) == null;

    /// <summary>Vertical FOV (degrees) that keeps the car about 9 m of the picture high, 10°–50°.</summary>
    private static float Zoom(Vector3 eye, Vector3 target) => Math.Clamp(2 * MathF.Atan(4.5f / MathF.Max(Vector3.Distance(eye, target), 1)) * 180 / MathF.PI, 10, 50);

    /// <summary>
    ///     A camera the car will drive past: the road 35, 22 or 12 m ahead, 6 m to either side and 3.5 m up,
    ///     pulled in towards the road by the course, the first that sees the car and the road it will drive up to the camera
    ///     (every ~8 m, 1 m up: no cut away again a moment later); else (not seeing) 2.5 m over the road 12 m ahead.
    /// </summary>
    public (Vector3 Eye, bool Sees) Trackside(Vector3 car)
    {
        var target = car + Vector3.UnitY;
        var dirSign = _reverse ? -1 : 1;
        int Ahead(float m) // the road point m metres on in the run direction
        {
            var i = _seg;
            for (var d = 0f; d < m && i + dirSign >= 0 && i + dirSign < _road.Length; i += dirSign) d += Vector3.Distance(_road[i], _road[i + dirSign]);
            return i;
        }
        Vector3 fallback = default;
        foreach (var ahead in new[] { 35f, 22f, 12f })
        {
            var j = Ahead(ahead);
            var k = Math.Clamp(j + dirSign, 0, _road.Length - 1);
            var along = (_road[k] - _road[j]) with { Y = 0 };
            along = along.LengthSquared() < 1e-6f ? Vector3.UnitZ : Vector3.Normalize(along);
            var left = new Vector3(along.Z, 0, -along.X);
            var basePoint = _road[j] + Vector3.UnitY * 1.5f;
            fallback = _road[j] + Vector3.UnitY * 2.5f;
            foreach (var side in new[] { 1f, -1f })
            {
                var eye = CameraRig.Clip(basePoint, basePoint + left * (6 * side) + Vector3.UnitY * 2, hull);
                var path = true;
                for (var m = 8f; path && m < ahead; m += 8) path = Sees(eye, _road[Ahead(m)] + Vector3.UnitY);
                if (path && Sees(eye, target)) return (eye, true);
            }
        }
        return (fallback, false);
    }
}
