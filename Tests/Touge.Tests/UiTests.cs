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

    /// <summary>Dials, small gauges and pods never cover each other; dash meters stay inside the housing, pods outside it.</summary>
    [Fact]
    public void ClusterMeters_DoNotOverlap()
    {
        var problems = new List<string>();
        foreach (var (car, g) in Cluster.Cars)
        {
            var ms = g.Meters.Where(m => m.Mount != Cluster.Mount.Inset).ToArray();
            for (var i = 0; i < ms.Length; i++)
            {
                var (m, r) = (ms[i], Cluster.Outer(g, ms[i]));
                var half = m.K == Cluster.Kind.Mfd ? new System.Numerics.Vector2(1.25f * m.R + 4, m.R + 4) : new System.Numerics.Vector2(r);
                var lo = new System.Numerics.Vector2(m.X, m.Y) - half;
                var hi = new System.Numerics.Vector2(m.X, m.Y) + half;
                var cowls = g.Housing == Cluster.Housing.Cowls;
                if (m.Mount == Cluster.Mount.Dash && (lo.X < (cowls ? 0 : 4) || lo.Y < (cowls ? 0 : 9) || hi.X > g.Size.X - (cowls ? 0 : 4) || hi.Y > g.Size.Y - (cowls ? 0 : 4)))
                    problems.Add($"{car} {m.K} outside housing");
                if (m.Mount == Cluster.Mount.Pod && hi.X > 0 && lo.X < g.Size.X && hi.Y > 0 && lo.Y < g.Size.Y)
                    problems.Add($"{car} {m.K} pod over housing");
                for (var j = i + 1; j < ms.Length; j++)
                    if (m.K != Cluster.Kind.Mfd && ms[j].K != Cluster.Kind.Mfd &&
                        System.Numerics.Vector2.Distance(new(m.X, m.Y), new(ms[j].X, ms[j].Y)) < r + Cluster.Outer(g, ms[j]) + 1)
                        problems.Add($"{car} {m.K}/{ms[j].K} overlap");
            }
        }
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
