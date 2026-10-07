using Touge.Ui;

namespace Touge.Tests;

public class UpdaterTests
{
    /// <summary>Release tags against the assembly version (4 parts, publish sets 3).</summary>
    [Fact]
    public void Newer_ComparesTagWithAssemblyVersion()
    {
        Assert.True(Updater.Newer("v0.3.0", new Version(0, 2, 0, 0)));
        Assert.True(Updater.Newer("v0.2.1", new Version(0, 2, 0, 0)));
        Assert.True(Updater.Newer("v1.0.0", new Version(0, 9, 12, 0)));
        Assert.False(Updater.Newer("v0.2.0", new Version(0, 2, 0, 0)));
        Assert.False(Updater.Newer("v0.1.9", new Version(0, 2, 0, 0)));
        Assert.False(Updater.Newer("nightly", new Version(0, 2, 0, 0)));
        Assert.Null(Updater.Root); // tests run out of bin/: never a package
    }

    /// <summary>The update row comes first on the drum, keeps the selection on its mode, decides without leaving the menu.</summary>
    [Fact]
    public void FrontEnd_UpdateRow()
    {
        var f = new FrontEnd();
        f.Open(FrontEnd.Step.Modes);
        for (var t = 0f; t < FrontEnd.Fade + 0.1f; t += 1 / 60f) f.Update(default, 1 / 60f);
        Assert.Equal(0, f.Index); // LEGEND
        f.UpdateRow = "UPDATE TO 9.9.9";
        Assert.Equal(1, f.Index); // still LEGEND
        f.Update((0, -1, false, false), 1 / 60f);
        Assert.Equal(0, f.Index);
        Assert.Equal(FrontEnd.Result.Update, f.Update((0, 0, true, false), 1 / 60f));
        Assert.True(f.Active);
        Assert.Equal(FrontEnd.Step.Modes, f.Current);
        f.UpdateRow = "UPDATING 10%"; // label changes: no shift
        Assert.Equal(0, f.Index);
        f.UpdateRow = null;
        Assert.Equal(0, f.Index);
    }
}
