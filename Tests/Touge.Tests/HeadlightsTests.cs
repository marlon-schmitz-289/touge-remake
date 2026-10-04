using System.Numerics;
using Kansei.Graphics;

namespace Touge.Tests;

public class HeadlightsTests
{
    /// <summary>L toggles off ↔ low, H low ↔ high (and switches on); pop-ups light only once nearly up; defaults per course time.</summary>
    [Fact]
    public void Switch_PopUp_Defaults()
    {
        Assert.Equal(Headlights.Mode.Off, Headlights.For("AKINA_DAY"));
        Assert.Equal(Headlights.Mode.Low, Headlights.For("AKINA_NIT"));
        Assert.Equal(Headlights.Mode.Low, Headlights.For("AKINA_RIN"));

        var h = new Headlights(Headlights.Mode.Off);
        h.ToggleHigh();
        Assert.Equal(Headlights.Mode.High, h.State);
        h.ToggleHigh();
        Assert.Equal(Headlights.Mode.Low, h.State);
        Assert.Equal(1, h.Lit(false));
        Assert.Equal(0, h.Lit(true)); // pop-ups still down
        h.Tick(Headlights.PopUpSeconds * 0.5f);
        Assert.Equal(0, h.Lit(true));
        h.Tick(Headlights.PopUpSeconds);
        Assert.Equal(1, h.Lit(true), 4);
        h.Toggle();
        Assert.Equal(Headlights.Mode.Off, h.State);
        Assert.Equal(0, h.Lit(false));
        Assert.Equal(1, new Headlights(Headlights.Mode.Low).Open); // a course starts with them up
    }

    /// <summary>Beams sit at the lamp centres and follow the car; by day the lamps glow less; brake/reverse pass through.</summary>
    [Fact]
    public void Apply_PlacesLampsOnTheCar()
    {
        var lamps = new CarModel.Lamps(false, default, default, CarModel.Centres([new(0.6f, 0.3f, 1.9f), new(0.7f, 0.4f, 2f), new(-0.65f, 0.35f, 1.95f)], Vector3.Zero, true),
            CarModel.Centres([], new Vector3(0.6f, 0.4f, -2), false));
        Assert.True(Vector3.Distance(new Vector3(0.65f, 0.35f, 2f), lamps.Head[0]) < 1e-5f);
        Assert.True(Vector3.Distance(new Vector3(-0.65f, 0.35f, 1.95f), lamps.Head[1]) < 1e-5f);
        Assert.Equal(new Vector3(-0.6f, 0.4f, -2), lamps.Tail[1]);

        var l = new SceneLights();
        var body = Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 0, 0); // facing +x
        new Headlights(Headlights.Mode.High).Apply(l, lamps, body, 1, 0.5f, true);
        Assert.Equal(1, l.HighBeam);
        Assert.True(Vector3.Distance(l.HeadlightDirection[0], Vector3.UnitX) < 1e-5f);
        Assert.True(Vector3.Distance(l.HeadlightPosition[0], Vector3.Transform(lamps.Head[0], body)) < 1e-5f);
        Assert.Equal((0.5f, 1f), (l.Brake, l.Reverse));
        var night = l.LampGlow;
        new Headlights(Headlights.Mode.Low).Apply(l, lamps, body, 0, 0, false);
        Assert.True(l.LampGlow < night && l.LampGlow > 0);
        new Headlights(Headlights.Mode.Off).Apply(l, lamps, body, 1, 0, false);
        Assert.Equal((Vector3.Zero, 0f), (l.HeadlightColor, l.LampGlow));
    }

    /// <summary>Local lights show fully at night and not at all by day or in overcast rain (TougeGame's atmospheres).</summary>
    [Fact]
    public void LocalLights_OnlyAtNight()
    {
        Assert.Equal(1, new Atmosphere { SunIntensity = 0.12f, Ambient = new(0.025f, 0.03f, 0.045f) }.LocalLightShare);
        Assert.Equal(0, new Atmosphere { SunIntensity = 0.15f, Ambient = new(0.30f, 0.32f, 0.35f) }.LocalLightShare);
        Assert.Equal(0, new Atmosphere { SunIntensity = 1.5f, Ambient = new(0.22f, 0.25f, 0.30f) }.LocalLightShare);
    }
}
