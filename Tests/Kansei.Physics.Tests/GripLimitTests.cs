using Xunit.Abstractions;

namespace Kansei.Physics.Tests;

public class GripLimitTests(ITestOutputHelper log)
{
    /// <summary>Every car's skid-pad limit lies in a plausible arcade band; the AE86 near its design value (~1.45 g).</summary>
    [Fact]
    public void Limits_are_plausible()
    {
        foreach (var (name, spec) in CarSpecs.All)
        {
            var g = GripLimit.Of(spec) / 9.81f;
            log.WriteLine($"{name,-6} {g:F2} g (100 m: {GripLimit.At(spec, 100) / 9.81f:F2} g), drift up to {GripLimit.DriftSpeed(spec) * 3.6f:F0} km/h");
            Assert.InRange(g, 1.0f, 1.8f);
        }
        Assert.InRange(GripLimit.Of(CarSpec.AE86) / 9.81f, 1.3f, 1.6f);
    }
}
