using System.Numerics;
using Touge.Formats;

namespace Touge.Replays;

/// <summary>
///     Trackside TV cameras of the replay: the original's REPCAM table of the course and direction (<see cref="ReplayCameras"/>),
///     each covering a stretch of road; while the car runs through it the camera eye and zoom move from its first keyframe
///     to its second, always looking at the car. Courses without a table get cameras placed along the road (<see cref="Auto"/>).
/// </summary>
public sealed class TvCameras
{
    private readonly ReplayCameras.Cam[] _cams;
    private readonly Vector3[] _road;
    private readonly bool _reverse;
    private int _seg;

    public TvCameras(ReplayCameras.Cam[] cams, Vector3[] road, bool reverse)
    {
        _road = road;
        _reverse = reverse;
        _cams = cams.Length > 0 ? cams : Auto(road);
    }

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
        var index = _cams.Length - 1;
        for (var i = 0; i < _cams.Length; i++)
            if (p >= _cams[i].From && p < _cams[i].To)
            {
                index = i;
                break;
            }
        if (p < _cams[0].From) index = 0;
        var c = _cams[index];
        var t = Math.Clamp((p - c.From) / MathF.Max(c.To - c.From, 1), 0, 1);
        return (Vector3.Lerp(c.A.Eye, c.B.Eye, t), Math.Clamp(float.Lerp(c.A.Fov, c.B.Fov, t), 3, 60), index);
    }
}
