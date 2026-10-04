using Kansei.Graphics;
using Kansei.Physics;
using Touge.Formats;
using Touge.Ui;

namespace Touge.Tests;

public class CarGuideTests
{
    /// <summary>
    ///     Intro: decide first completes a typing line, then advances; after the last line the list shows the car. List: up/down
    ///     in maker order (wraps), left/right paint, decide = Iketani talks (Voice = car) until the voice's length, a car change or a
    ///     skip; back leaves after the fade with BEEP001. Music WORRY as the original's guide.
    /// </summary>
    [Fact]
    public void Flow_IntroListTalkAndExit()
    {
        var catalog = new Catalog([],
        [
            new Catalog.Car("AE86T", "TOYOTA", "TRUENO GT-APEX [AE86]", "FR", 130, 940, [1, 2, 3]),
            new Catalog.Car("R32", "NISSAN", "SKYLINE GT-R V-spec II [BNR32]", "4WD", 280, 1430, [1]),
            new Catalog.Car("AE85", "TOYOTA", "LEVIN SR [AE85]", "FR", 83, 925, [1, 2]),
        ]);
        var sounds = new List<string>();
        var actions = new List<CarGuide.Action>();
        var g = new CarGuide(catalog) { Sound = sounds.Add };
        void Run(float seconds, (int, int, bool, bool) k = default)
        {
            actions.Add(g.Update(k, 1 / 60f));
            for (var t = 1 / 60f; t < seconds; t += 1 / 60f) actions.Add(g.Update(default, 1 / 60f));
        }
        (int, int, bool, bool) ok = (0, 0, true, false), back = (0, 0, false, true), down = (0, 1, false, false), right = (1, 0, false, false);

        g.Open("AE86T", 2);
        Assert.Equal("WORRY.adx", g.Music);
        Assert.False(g.ShowsCar);
        Run(CarGuide.Fade + 0.2f, ok); // still typing: completes the line, no sound
        Assert.Empty(sounds);
        Run(0.1f, ok);
        Run(0.1f, ok);
        Run(5, ok);
        Run(5, ok); // past the third line: the list
        Assert.Equal(CarGuide.Step.List, g.Current);
        Assert.True(g.ShowsCar);
        Assert.Contains(CarGuide.Action.PreviewCar, actions);
        Assert.Equal(["SYS006", "SYS006", "SYS006"], sounds);

        Run(0.1f, down); // maker order: TOYOTA AE86T, AE85, then NISSAN R32
        Assert.Equal(("AE85", 0), (g.CarId, g.Paint));
        Run(0.1f, down);
        Run(0.1f, down);
        Assert.Equal("AE86T", g.CarId); // wrapped
        Run(0.1f, right);
        Run(0.1f, right);
        Run(0.1f, right);
        Assert.Equal(0, g.Paint); // three paints: wraps
        Assert.Equal(CarGuide.Action.PreviewCar, g.Update(right, 1 / 60f));
        Assert.Equal(1, g.Paint);
        Run(0.1f, (-1, 0, false, false));

        Assert.Null(g.Voice);
        Run(0.1f, ok);
        Assert.Equal("AE86T", g.Voice);
        g.VoiceSeconds = 1;
        Run(1);
        Assert.Null(g.Voice); // the voice ran out
        Run(0.1f, ok);
        Run(0.1f, down); // another car stops the talk
        Assert.Null(g.Voice);
        Run(0.1f, ok);
        Run(0.1f, back); // back first only skips
        Assert.True(g.Active);
        Assert.Null(g.Voice);

        actions.Clear();
        Run(CarGuide.Fade + 0.1f, back);
        Assert.False(g.Active);
        Assert.Equal(CarGuide.Action.Exit, actions.Last(a => a != CarGuide.Action.None));
        Assert.Equal("BEEP001", sounds[^1]);
    }

    /// <summary>Every car has engine data and a text, and every text fits its panel (Iketani 4 lines, intro 4 lines).</summary>
    [Fact]
    public void Texts_CoverAllCars_AndFit()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "InitialDRemake.slnx"))) dir = Path.GetDirectoryName(dir)!;
        var font = new SdfFont(File.ReadAllBytes(Path.Combine(dir, "Touge/Assets/Fonts/Rajdhani-Bold.ttf")),
            string.Concat(Enumerable.Range(32, 95).Select(c => (char)c)) + "°");
        Assert.Equal(CarPaint.Cars.Order(), CarGuide.Entries.Keys.Order());
        foreach (var (id, e) in CarGuide.Entries)
        {
            Assert.True(CarGuide.Wrap(font, e.Text, 11.5f, 284).Count <= 4, id);
            Assert.All(e.Text, ch => Assert.True(ch is >= ' ' and <= '~', $"{id}: '{ch}' not in the font"));
        }
        foreach (var (_, line) in CarGuide.Intro) Assert.True(CarGuide.Wrap(font, line, 14, 392).Count <= 4, line);
    }

    /// <summary>Spec sheet peaks from the torque curve: AE86 130 PS at 6600 / 149 Nm at 5800, R32 280 PS / 353 Nm.</summary>
    [Fact]
    public void Peaks_FromTorqueCurve()
    {
        Assert.Equal((130, 6600, 149, 5800), CarGuide.Peaks(CarSpecs.All["AE86T"]));
        Assert.Equal((280, 6800, 353, 4400), CarGuide.Peaks(CarSpecs.All["R32"]));
        var sheet = CarGuide.Sheet(new Catalog.Car("CAPPU", "SUZUKI", "Cappuccino [EA11R]", "FR", 64, 700, [1]));
        Assert.Contains(("ENGINE", "F6A  657 cc"), sheet);
        Assert.Contains(("WEIGHT", "700 kg"), sheet);
        Assert.Contains(("PWR / WT", "10.9 kg/PS"), sheet);
    }

    [Fact]
    public void Spot_StraightFlatRoad_AwayFromTheEnds()
    {
        // 2 m points: 300 m straight (but the first/last 150 m are off limits), a 90° bend at 300 m, a 20 % ramp from 600 m, 900 m long
        var line = new System.Numerics.Vector3[451];
        for (var i = 0; i < line.Length; i++)
        {
            float d = i * 2, a = d < 300 ? 0 : MathF.Min((d - 300) / 60, 1) * MathF.PI / 2;
            line[i] = i == 0 ? default : line[i - 1] + new System.Numerics.Vector3(MathF.Sin(a), d >= 600 ? 0.2f : 0, MathF.Cos(a)) * 2;
        }
        var s = CarGuide.Spot(line) * 2;
        Assert.InRange(s, 150, 280); // the flat straight, not the bend or the ramp
        Assert.Equal(1, CarGuide.Spot(new System.Numerics.Vector3[3])); // too short: the middle
    }
}
