using System.Globalization;
using System.Numerics;
using Touge.Formats;

namespace Touge;

/// <summary>
///     <c>--flicker</c>: measures z-fighting in the rendered image. Every view is rendered <see cref="Spins"/>.Length
///     times with camera and world turned together about the world Y axis (folded into the view matrix): the same
///     picture in exact maths, but every vertex's depth is rounded differently (~|position| × 1e-7, like a camera that
///     turns while driving), x/y move by a few hundredths of a pixel. Pixels whose colour then jumps (any channel ≥
///     <see cref="Threshold"/>) show surfaces with tied depth = z-fight; the rest is edge noise (silhouettes with MSAA).
///     Groups: <c>course</c> = <see cref="CoursePoints"/> points evenly along the driving line from <c>firstPoint</c>
///     (car hidden), <c>car</c> = <see cref="CarAngles"/> orbit angles around the parked car (course hidden),
///     <c>spots</c> = the largest overlaps the detector rates critical (<see cref="Spots"/>), seen from ≤ 10 m.
///     Writes the worst 160×160 crop per group as <c>&lt;prefix&gt;_&lt;group&gt;.png</c> (left: frame, right: flipping
///     pixels in red, ×2) and that whole frame as <c>_full.png</c>.
/// </summary>
public sealed class FlickerProbe
{
    public const int CoursePoints = 8, CarAngles = 8, Threshold = 40, Crop = 160;
    /// <summary>Turns in radians.</summary>
    public static readonly float[] Spins = [0, 0.013f, -0.029f];
    private static readonly string[] Groups = ["course", "car", "spots"];

    /// <param name="Group">0 course (driving line point <paramref name="LinePoint"/>), 1 car (orbit <paramref name="Orbit"/> degrees), 2 spot.</param>
    public readonly record struct View(int Group, int LinePoint, float Orbit, Vector3 Eye, Vector3 Target, float Spin);

    private readonly string _prefix, _label;
    private readonly List<View> _views = [];
    private int _job;
    private readonly List<byte[]> _frames = [];
    private readonly long[] _flips = new long[3], _pixels = new long[3];
    private readonly (int Count, byte[]? Rgba, bool[]? Mask, int X, int Y)[] _worst = new (int, byte[]?, bool[]?, int, int)[3];

    public FlickerProbe(string prefix, string label, Vector3[] line, int firstPoint, Vector3[] spots)
    {
        (_prefix, _label) = (prefix, label);
        for (var i = 0; i < CoursePoints; i++) Add(new View(0, (firstPoint + (int)((i + 0.5f) * line.Length / CoursePoints)) % line.Length, 0, default, default, 0));
        for (var i = 0; i < CarAngles; i++) Add(new View(1, 0, i * 360f / CarAngles, default, default, 0));
        foreach (var s in spots)
        {
            var d = line.MinBy(p => Vector3.DistanceSquared(p, s)) + new Vector3(0, 1.5f, 0) - s;
            Add(new View(2, 0, 0, s + Vector3.Normalize(d) * MathF.Min(d.Length(), 10), s, 0));
        }
    }

    private void Add(View v)
    {
        foreach (var spin in Spins) _views.Add(v with { Spin = spin });
    }

    /// <summary>
    ///     Up to <paramref name="count"/> places to look at: per (batch, batch) group of critical pairs (gap &lt; 1 mm),
    ///     largest overlap first, the centre of its largest pair. <paramref name="batchStart"/>: first triangle per batch.
    /// </summary>
    public static Vector3[] Spots(Vector3[] corners, List<int> batchStart, int count = 8)
    {
        int Batch(int tri)
        {
            var i = batchStart.BinarySearch(tri);
            return i >= 0 ? i : ~i - 1;
        }
        return
        [
            .. ZFight.Find(corners).Where(p => p.Gap < 0.001f).GroupBy(p => (Batch(p.A), Batch(p.B)))
                .OrderByDescending(g => g.Sum(p => p.Area)).Take(count).Select(g => g.MaxBy(p => p.Area).At),
        ];
    }

    /// <summary>The next view to render, false when done.</summary>
    public bool Next(out View view)
    {
        view = _job < _views.Count ? _views[_job] : default;
        return _job++ < _views.Count;
    }

    /// <summary>Frame of the last view from <see cref="Next"/>; after its last spin the view is compared.</summary>
    public void Add(int w, int h, byte[] rgba)
    {
        _frames.Add(rgba);
        if (_frames.Count < Spins.Length) return;
        var group = _views[_job - 1].Group;
        var mask = new bool[w * h];
        var flips = 0;
        for (var i = 0; i < mask.Length; i++)
        {
            var d = 0;
            for (var f = 1; f < _frames.Count; f++)
            for (var c = 0; c < 3; c++)
                d = Math.Max(d, Math.Abs(_frames[f][i * 4 + c] - _frames[0][i * 4 + c]));
            if (d < Threshold) continue;
            mask[i] = true;
            flips++;
        }
        _flips[group] += flips;
        _pixels[group] += mask.Length;
        var (count, x, y) = Densest(mask, w, h);
        if (count > _worst[group].Count) _worst[group] = (count, _frames[0], mask, x, y);
        _frames.Clear();
    }

    /// <summary>Prints the flip shares and writes the worst crops.</summary>
    public void Finish(int w)
    {
        for (var g = 0; g < Groups.Length; g++)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[Flicker] {_label} {Groups[g]}: {_flips[g]} von {_pixels[g]} Pixeln flackern ({100.0 * _flips[g] / Math.Max(_pixels[g], 1):F4} %), dichtester Ausschnitt {_worst[g].Count} Pixel"));
            if (_worst[g].Rgba is not { } rgba) continue;
            var mask = _worst[g].Mask!;
            Png.Write($"{_prefix}_{Groups[g]}.png", Crop * 4, Crop * 2, CropImage(rgba, mask, w, _worst[g].X, _worst[g].Y));
            var full = (byte[])rgba.Clone();
            for (var i = 0; i < mask.Length; i++)
                if (mask[i]) (full[i * 4], full[i * 4 + 1], full[i * 4 + 2]) = ((byte)255, (byte)0, (byte)0);
            Png.Write($"{_prefix}_{Groups[g]}_full.png", w, full.Length / 4 / w, full);
        }
    }

    /// <summary>Top-left of the <see cref="Crop"/>² window with the most flipping pixels (stride 16) and their count.</summary>
    private static (int Count, int X, int Y) Densest(bool[] mask, int w, int h)
    {
        var sum = new int[(w + 1) * (h + 1)];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            sum[(y + 1) * (w + 1) + x + 1] = (mask[y * w + x] ? 1 : 0) + sum[y * (w + 1) + x + 1] + sum[(y + 1) * (w + 1) + x] - sum[y * (w + 1) + x];
        (int, int, int) best = (0, 0, 0);
        for (var y = 0; y + Crop <= h; y += 16)
        for (var x = 0; x + Crop <= w; x += 16)
        {
            var n = sum[(y + Crop) * (w + 1) + x + Crop] - sum[y * (w + 1) + x + Crop] - sum[(y + Crop) * (w + 1) + x] + sum[y * (w + 1) + x];
            if (n > best.Item1) best = (n, x, y);
        }
        return best;
    }

    private static byte[] CropImage(byte[] rgba, bool[] mask, int w, int x0, int y0)
    {
        var outW = Crop * 4;
        var img = new byte[outW * Crop * 2 * 4];
        for (var y = 0; y < Crop * 2; y++)
        for (var x = 0; x < outW; x++)
        {
            var right = x >= Crop * 2;
            var src = (y0 + y / 2) * w + x0 + x % (Crop * 2) / 2;
            var o = (y * outW + x) * 4;
            if (right && mask[src]) (img[o], img[o + 1], img[o + 2]) = ((byte)255, (byte)0, (byte)0);
            else for (var c = 0; c < 3; c++) img[o + c] = (byte)(rgba[src * 4 + c] / (right ? 2 : 1));
            img[o + 3] = 255;
        }
        return img;
    }
}
