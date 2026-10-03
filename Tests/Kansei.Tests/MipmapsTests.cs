using Kansei.Graphics;

namespace Kansei.Tests;

public class MipmapsTests
{
    [Fact]
    public void FoliageKeepsCoverageAndColour_OpaqueAveragesInLinearLight()
    {
        // 4×4 "foliage": one opaque white texel per 2×2 block, rest transparent black (25 % coverage)
        var leaf = new byte[4 * 4 * 4];
        foreach (var (x, y) in new[] { (0, 0), (2, 0), (0, 2), (2, 2) })
            leaf.AsSpan((y * 4 + x) * 4, 4).Fill(255);
        var mips = Mipmaps.Build(4, 4, leaf, 0.3f);

        Assert.Equal(Mipmaps.LevelCount(4, 4) - 1, mips.Count);
        Assert.Equal((1, 1), (mips[^1].W, mips[^1].H));
        var level1 = mips[0].Rgba;
        // plain averaging gives alpha 64 < cutoff 77 everywhere: the leaves would vanish
        Assert.True(Mipmaps.Coverage(level1, 77) > 0);
        Assert.Equal(255, level1[0]); // alpha-weighted: no dark fringe from the transparent black texels

        // opaque black/white checker → 50 % linear grey = sRGB 188, not 128
        byte[] checker = [0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 255];
        var grey = Mipmaps.Build(2, 2, checker, 0.3f)[0].Rgba;
        Assert.Equal([188, 188, 188, 255], grey);
    }
}
