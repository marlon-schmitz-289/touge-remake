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
}
