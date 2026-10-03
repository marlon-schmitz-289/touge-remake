namespace Kansei.Graphics;

/// <summary>
///     CPU mip chain for sRGB RGBA8 textures. 2×2 box filter in linear light, colour weighted by alpha (transparent
///     texels don't bleed dark fringes into foliage), and per-level alpha scaling so the share of texels passing the
///     alpha test stays as in level 0 — otherwise alpha-tested foliage/fences thin out and vanish with distance.
/// </summary>
public static class Mipmaps
{
    private static readonly float[] ToLinear = Enumerable.Range(0, 256).Select(i => SrgbToLinear(i / 255f)).ToArray();

    public static int LevelCount(int w, int h) => 32 - (int)uint.LeadingZeroCount((uint)Math.Max(w, h));

    /// <summary>Levels 1..n (level 0 is <paramref name="rgba"/> itself). <paramref name="alphaCutoff"/> as in the shader's alpha test.</summary>
    public static List<(int W, int H, byte[] Rgba)> Build(int w, int h, ReadOnlySpan<byte> rgba, float alphaCutoff)
    {
        var levels = new List<(int, int, byte[])>();
        var cut = (byte)Math.Ceiling(alphaCutoff * 255);
        var coverage = Coverage(rgba, cut);
        var cutout = coverage is > 0 and < 1;
        // linear premultiplied colour + alpha of the previous level, unscaled alpha so errors don't accumulate
        var src = new float[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var a = rgba[i * 4 + 3] / 255f;
            for (var c = 0; c < 3; c++) src[i * 4 + c] = ToLinear[rgba[i * 4 + c]] * a;
            src[i * 4 + 3] = a;
        }
        while (w > 1 || h > 1)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var dst = new float[nw * nh * 4];
            for (var y = 0; y < nh; y++)
            for (var x = 0; x < nw; x++)
            {
                int x0 = Math.Min(2 * x, w - 1), x1 = Math.Min(2 * x + 1, w - 1), y0 = Math.Min(2 * y, h - 1), y1 = Math.Min(2 * y + 1, h - 1);
                for (var c = 0; c < 4; c++)
                    dst[(y * nw + x) * 4 + c] = 0.25f * (src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c] + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c]);
            }
            var alpha = new byte[nw * nh];
            for (var i = 0; i < alpha.Length; i++) alpha[i] = (byte)Math.Round(dst[i * 4 + 3] * 255);
            var scale = cutout ? AlphaScale(alpha, coverage, cut) : 1f;
            var px = new byte[nw * nh * 4];
            for (var i = 0; i < nw * nh; i++)
            {
                var a = dst[i * 4 + 3];
                for (var c = 0; c < 3; c++) px[i * 4 + c] = Encode(a > 0 ? dst[i * 4 + c] / a : 0);
                px[i * 4 + 3] = (byte)Math.Clamp(MathF.Round(a * scale * 255), 0, 255);
            }
            levels.Add((nw, nh, px));
            (src, w, h) = (dst, nw, nh);
        }
        return levels;
    }

    /// <summary>Share of texels with alpha ≥ <paramref name="cut"/>.</summary>
    public static float Coverage(ReadOnlySpan<byte> rgba, byte cut)
    {
        var n = 0;
        for (var i = 3; i < rgba.Length; i += 4) if (rgba[i] >= cut) n++;
        return (float)n / (rgba.Length / 4);
    }

    /// <summary>Scale so that the same share of texels as in level 0 ends up ≥ cut: cut / (alpha quantile at that share).</summary>
    private static float AlphaScale(byte[] alpha, float coverage, byte cut)
    {
        Array.Sort(alpha);
        var k = Math.Clamp((int)MathF.Round((1 - coverage) * alpha.Length), 0, alpha.Length - 1);
        return alpha[k] == 0 ? 1 : (cut + 0.5f) / alpha[k];
    }

    private static byte Encode(float linear) => (byte)Math.Clamp(MathF.Round(LinearToSrgb(linear) * 255), 0, 255);

    private static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    private static float LinearToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;
}
