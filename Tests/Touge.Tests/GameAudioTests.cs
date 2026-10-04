namespace Touge.Tests;

public class GameAudioTests
{
    [Fact]
    public void EngineZones_EqualPowerAndContinuousPitch()
    {
        float[] centres = [.113f, .160f, .377f, .727f], natives = [.094f, .137f, .314f, .671f]; // AE86_U
        Span<float> w = stackalloc float[4], prev = stackalloc float[4];
        float prevZone = 0, prevMix = 0;
        for (var x = 0.05f; x <= 1.05f; x += 1e-4f)
        {
            var zone = GameAudio.ZoneWeights(centres, x, w);
            float power = 0, mix = 0;
            var audible = 0;
            for (var k = 0; k < 4; k++)
            {
                power += w[k] * w[k];
                mix += w[k] * w[k] * MathF.Log2(GameAudio.LayerRate(x, natives[k])); // power-weighted pitch of the mix
                if (w[k] > 0) audible++;
            }
            Assert.Equal(1, power, 1e-4f);
            Assert.True(audible <= 2);
            if (x > 0.05f)
            {
                for (var k = 0; k < 4; k++) Assert.True(MathF.Abs(w[k] - prev[k]) < 0.01f, $"weight {k} jumps at x {x}");
                Assert.True(zone >= prevZone);
                Assert.True(MathF.Abs(mix - prevMix) * 12 < 0.05f, $"pitch jumps at x {x}"); // < 0.05 semitones per 0.01 % rpm
            }
            // every layer inside its resampling range plays exactly at rpm / native
            for (var k = 0; k < 4; k++)
                if (x / natives[k] is > GameAudio.MinRate and < GameAudio.MaxRate)
                    Assert.Equal(x / natives[k], GameAudio.LayerRate(x, natives[k]), 1e-5f);
            w.CopyTo(prev);
            (prevZone, prevMix) = (zone, mix);
        }
    }
}
