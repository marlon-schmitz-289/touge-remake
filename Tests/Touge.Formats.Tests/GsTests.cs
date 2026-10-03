using Touge.Formats;

namespace Touge.Formats.Tests;

public class GsTests
{
    /// <summary>Marks one source byte/nibble at a time; each must land on a distinct output pixel.</summary>
    [Theory]
    [InlineData(64, 32, 128, 64, 8)]  // 8-bit, one full page
    [InlineData(64, 64, 128, 256, 4)] // 4-bit, two pages
    public void Unswizzle_maps_source_units_to_distinct_pixels(int tw, int th, int w, int h, int bits)
    {
        var seen = new HashSet<int>();
        for (var unit = 0; unit < 300; unit++) // sample; full sweep is O(n²)
        {
            var data = new byte[tw * th * 4];
            if (bits == 8) data[unit] = 1;
            else data[unit / 2] = (byte)(1 << (unit % 2 * 4));
            var hit = Array.FindIndex(Gs.UnswizzleIndices(data, tw, th, w, h, bits), b => b != 0);
            Assert.True(hit >= 0 && seen.Add(hit), $"unit {unit} -> {hit}");
        }
    }

    [Fact]
    public void ClutIndex_swaps_bits_3_and_4() =>
        Assert.Equal([0, 1, 16, 8, 31], new[] { 0, 1, 8, 16, 31 }.Select(Gs.ClutIndex));
}
