using System.Numerics;
using Kansei.Graphics;
using Kansei.Physics;
using Touge.Formats;
using Touge.Ui;

namespace Touge.Tests;

public class UiTests
{
    [Fact]
    public void RecordRun_DeltasAgainstOldBest()
    {
        float[] old = [10, 20, 30, 40];
        var t = new LapTimer(100, old);
        t.Update(0, 0.1f);
        for (var along = 3f; along <= 100; along += 1) t.Update(along, 0.3f); // 0.3 s/m → 25 m sectors in ~7.5 s
        Assert.Equal(LapTimer.State.Finished, t.Phase);
        Assert.True(t.NewRecord);
        Assert.NotSame(old, t.Best);
        for (var i = 0; i < LapTimer.Sectors; i++) Assert.Equal(t.Splits[i] - old[i], t.Delta(i)!.Value, 1e-4f);
        Assert.True(t.Delta(3) < -10);
    }

    [Fact]
    public void EveryCarHasCluster_TachCoversRevLimit()
    {
        foreach (var car in CarPaint.Cars)
        {
            var g = Cluster.Cars[car];
            Assert.True(g.TachMax >= CarSpecs.All[car].RevLimit, car);
            Assert.InRange(g.Redline, g.TachMax / 2, g.TachMax);
        }
    }

    /// <summary>Dials, small gauges and the MFD never cover each other and stay inside the housing.</summary>
    [Fact]
    public void ClusterMeters_DoNotOverlap()
    {
        var problems = new List<string>();
        foreach (var (car, g) in Cluster.Cars)
        {
            var ms = g.Meters.Where(m => m.Mount != Cluster.Mount.Inset).ToArray();
            var (e0, e1) = g.Housing == Cluster.Housing.Cowls ? (Vector2.Zero, Vector2.Zero) : (new Vector2(4, 9), new Vector2(4));
            for (var i = 0; i < ms.Length; i++)
            {
                var (m, h) = (ms[i], Cluster.Half(g, ms[i]));
                var p = new Vector2(m.X, m.Y);
                if (Vector2.Max(p - h, e0) != p - h || Vector2.Min(p + h, g.Size - e1) != p + h) problems.Add($"{car} {m.K} outside housing");
                for (var j = i + 1; j < ms.Length; j++)
                {
                    var (n, hn) = (ms[j], Cluster.Half(g, ms[j]));
                    var d = Vector2.Abs(p - new Vector2(n.X, n.Y));
                    if (m.K != Cluster.Kind.Mfd && n.K != Cluster.Kind.Mfd ? d.Length() < h.X + hn.X + 1 : d.X < h.X + hn.X && d.Y < h.Y + hn.Y)
                        problems.Add($"{car} {m.K}/{n.K} overlap");
                }
            }
        }
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    ///     Drawn cluster (every shape and glyph) fits 400×190 px at 1080p (scaled with height), bottom right inside the safe
    ///     area; no text (numbers, legends, captions, windows) lies on any needle's sweep, tail or hub.
    /// </summary>
    [Fact]
    public void Cluster_FitsCorner_TextClearOfNeedles()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "InitialDRemake.slnx"))) dir = Path.GetDirectoryName(dir)!;
        var font = new SdfFont(File.ReadAllBytes(Path.Combine(dir, "Touge/Assets/Fonts/Rajdhani-Bold.ttf")),
            string.Concat(Enumerable.Range(32, 95).Select(c => (char)c)) + "°");
        var o = new Overlay { Font = font };
        var problems = new List<string>();
        Cluster.Sweeps = [];
        try
        {
            foreach (var (w, h) in new[] { (1280, 720), (1920, 1080), (1024, 768), (3440, 1440) })
            foreach (var (car, g) in Cluster.Cars)
            {
                var grid = Style.Safe(w, h);
                o.Clear();
                Cluster.Sweeps.Clear();
                Cluster.Draw(o, g, new Vector2(grid.Right, grid.Bottom), Cluster.Fit(g, grid), new Cluster.Reading(6000, 123, 3, false, 0.4f, false, 0));
                var pts = o.Vertices.ToArray().Select(v => v.Position).Concat(o.GlyphVertices.ToArray().Select(v => v.Position)).ToArray();
                var (min, max) = (pts.Aggregate(Vector2.Min), pts.Aggregate(Vector2.Max));
                var at = $"{car} {w}x{h}";
                if (max.X - min.X > 400 * h / 1080f + 3 || max.Y - min.Y > 190 * h / 1080f + 3) problems.Add($"{at} size {max - min}");
                if (min.X < w / 2f || min.Y < h / 2f || max.X > grid.Right + 2 || max.Y > grid.Bottom + 2) problems.Add($"{at} outside corner");
                var glyphs = o.GlyphVertices.ToArray();
                for (var q = 0; q < glyphs.Length; q += 6)
                {
                    var quad = glyphs.AsSpan(q, 6).ToArray().Select(v => v.Position).ToArray();
                    var pad = new Vector2(glyphs[q].Scale / 2); // distance-field padding around the ink
                    var (lo, hi) = (quad.Aggregate(Vector2.Min) + pad, quad.Aggregate(Vector2.Max) - pad);
                    for (var i = 0; i <= 4; i++)
                    for (var j = 0; j <= 4; j++)
                    {
                        var p = lo + (hi - lo) * new Vector2(i, j) / 4;
                        if (Cluster.Sweeps.Any(s => Hits(p, s))) problems.Add($"{at} text at {p}");
                    }
                }
            }
        }
        finally { Cluster.Sweeps = null; }
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>Point within a needle's reach: tail/hub disc, or the swept sector (with about the needle's half width as margin).</summary>
    private static bool Hits(Vector2 p, Cluster.Sweep s)
    {
        var d = p - s.C;
        var dist = d.Length();
        var margin = 0.035f * s.Len + 1;
        if (dist <= s.Tail + margin) return true;
        if (dist > s.Len + margin) return false;
        var a = ((MathF.Atan2(d.Y, d.X) - s.From) % MathF.Tau + MathF.Tau) % MathF.Tau;
        var slack = margin / dist;
        return a <= s.Span + slack || a >= MathF.Tau - slack;
    }
}
