using System.Numerics;

namespace Kansei.Physics.Tests;

public class VehicleStateTests
{
    const float Dt = 1f / 120;

    /// <summary>A drive with launch, shifts, a handbrake drift and a wall hit: every bit of state gets exercised.</summary>
    static VehicleInput Script(int tick) => tick switch
    {
        < 300 => new VehicleInput(1, 0, 0),
        < 420 => new VehicleInput(1, 0, 1, Handbrake: tick < 330),
        < 600 => new VehicleInput(0.7f, 0, -0.6f),
        _ => new VehicleInput(0.2f, 0.5f, 0.3f, Shift: tick == 650 ? -1 : 0),
    };

    [Fact]
    public void RestoredStateContinuesBitIdentically()
    {
        var ground = new FlatGround(wallZ: 120);
        var a = new Vehicle(CarSpec.AE86);
        a.Reset(Vector3.Zero, 0.3f);
        for (var t = 0; t < 450; t++) a.Step(Script(t), ground, Dt);
        var state = a.SaveState();
        Assert.Equal(Vehicle.StateBytes, state.Length);

        var b = new Vehicle(CarSpec.AE86) { AutomaticGearbox = false }; // restored too
        b.Reset(new Vector3(50, 0, 50), 2);
        b.LoadState(state);
        for (var t = 450; t < 900; t++)
        {
            a.Step(Script(t), ground, Dt);
            b.Step(Script(t), ground, Dt);
        }
        Assert.Equal(a.SaveState(), b.SaveState());
        Assert.Equal(a.Position, b.Position);
        Assert.True(a.AutomaticGearbox == b.AutomaticGearbox);
    }
}
