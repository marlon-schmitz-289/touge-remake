using Touge.Formats;

namespace Touge.Tests;

public class CarModelTests
{
    /// <summary>A car is read and baked once per car, paint and livery (when the ISO is at hand); a prefetched one is the same bake.</summary>
    [Fact]
    public void Bakes_are_cached_per_car_paint_and_livery()
    {
        var path = Environment.GetEnvironmentVariable("INITIALD_ISO") ?? "";
        if (!File.Exists(path)) return;
        using var iso = new Iso9660(path);
        var a = CarModel.Bake(iso, "FC3S", 0, Livery.Rival);
        Assert.Same(a, CarModel.Bake(iso, "FC3S", 0, Livery.Rival));
        Assert.NotSame(a, CarModel.Bake(iso, "FC3S", 1, Livery.Rival));
        Assert.NotSame(a, CarModel.Bake(iso, "FC3S", 0, Livery.None));
        Assert.Throws<FileNotFoundException>(() => CarModel.Bake(iso, "NOPE", 0, Livery.Rival));
    }
}
