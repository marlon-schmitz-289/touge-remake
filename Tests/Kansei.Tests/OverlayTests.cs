using System.Numerics;
using Kansei.Graphics;

namespace Kansei.Tests;

public class OverlayTests
{
    [Fact]
    public void Shapes_DistanceField_Text_ClipBatches_Capacity()
    {
        var o = new Overlay();
        // horizontal 4-px line: quad reaches 1 px past the half width (AA fringe) and past the ends by the half width
        o.Line(new Vector2(10, 20), new Vector2(30, 20), 4, Overlay.Rgba(1, 0, 0));
        var v = o.Vertices.ToArray();
        Assert.Equal(6, v.Length);
        Assert.All(v, x => Assert.Equal(2, x.Radius));
        Assert.All(v, x => Assert.Equal(3, MathF.Abs(x.Sdf.X)));
        Assert.All(v, x => Assert.Equal(3, MathF.Abs(x.Position.Y - 20), 1e-4f));
        Assert.Equal(8, v.Min(x => x.Position.X), 1e-4f);
        Assert.Equal(32, v.Max(x => x.Position.X), 1e-4f);
        Assert.Equal(0xFF0000FFu, v[0].Color); // RGBA8 little-endian: r in the low byte

        // clip starts a batch at the current vertex
        o.Clip(new Vector2(50, 50), 40);
        o.Disc(Vector2.Zero, 1, 0);
        Assert.Equal(2, o.Batches.Length);
        Assert.Equal((6, new Vector3(50, 50, 40)), o.Batches[1]);

        // full: further shapes are dropped instead of overflowing the GPU ring
        for (var i = 0; i < Overlay.MaxVertices; i++) o.Disc(Vector2.Zero, 1, 0);
        Assert.Equal(Overlay.MaxVertices, o.VertexCount);
        o.Clear();
        Assert.Equal((0, 1), (o.VertexCount, o.Batches.Length));
    }

    [Fact]
    public void SdfFont_Atlas_Measure_TextLayout()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "InitialDRemake.slnx"))) dir = Path.GetDirectoryName(dir)!;
        var font = new SdfFont(File.ReadAllBytes(Path.Combine(dir, "Touge/Assets/Fonts/Rajdhani-Bold.ttf")), "HIil0123456789 %");
        Assert.InRange(font.CapHeight, 0.5f, 0.8f);

        // 'H' is a filled box: its cell centre column is outside (< 0.5) between the stems, inside on the crossbar
        Assert.True(font.TryGet('H', out var h));
        int X(float u) => (int)(u * font.AtlasWidth);
        int Y(float v) => (int)(v * font.AtlasHeight);
        int x0 = X(h.Uv.X), x1 = X(h.Uv.Z), y0 = Y(h.Uv.Y), y1 = Y(h.Uv.W);
        Assert.Equal(0, font.Atlas[y0 * font.AtlasWidth + x0]); // padding corner: far outside
        var column = Enumerable.Range(y0, y1 - y0).Select(y => font.Atlas[y * font.AtlasWidth + (x0 + x1) / 2]).ToArray();
        Assert.Contains(column, b => b > 160); // crossbar
        Assert.Contains(column, b => b < 60);  // above/below it

        // digits are made tabular (one advance), widths add up, unknown characters take half an em
        Assert.Equal(font.Measure("0", 40), font.Measure("8", 40), 1e-3f);
        Assert.Equal(font.Measure("Hi", 30), font.Measure("H", 30) + font.Measure("i", 30), 1e-3f);
        Assert.Equal(20, font.Measure("\u3042", 40), 1e-3f);

        // layout: right-aligned text ends at the anchor, spaces emit nothing, 6 vertices per drawn glyph
        var o = new Overlay { Font = font };
        var w = o.Text("1 2", new Vector2(100, 50), 40, Overlay.Rgba(1, 1, 1), align: 1);
        Assert.Equal(12, o.GlyphCount);
        Assert.Equal(font.Measure("1 2", 40), w, 1e-3f);
        Assert.True(o.GlyphVertices.ToArray().Max(v => v.Position.X) <= 100 + 40f * SdfFont.Spread / SdfFont.EmPx + 1);
        Assert.True(o.GlyphVertices.ToArray().Min(v => v.Position.X) >= 100 - w - 40f * SdfFont.Spread / SdfFont.EmPx - 1);
        o.Clear();
        Assert.Equal(0, o.GlyphCount);
    }

    /// <summary>Split screen: a HUD built for a view at the origin moves into its half, clip circles with it.</summary>
    [Fact]
    public void Shift_MovesShapesAndClips()
    {
        var o = new Overlay();
        o.Rect(new Vector2(0, 0), new Vector2(10, 10), 0);
        o.Clip(new Vector2(5, 5), 3);
        o.Disc(new Vector2(5, 5), 2, 0);
        o.Shift(new Vector2(100, 360));
        Assert.Equal(new Vector2(100, 360), o.Vertices[0].Position);
        Assert.Equal(new Vector3(105, 365, 3), o.Batches[1].Clip);
        Assert.Equal(float.MaxValue, o.Batches[0].Clip.Z); // no clip stays no clip
    }
}
