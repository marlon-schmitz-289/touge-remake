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

        // '1' is 10 dots in the 5×7 font, unknown characters are blanks; clip starts a batch at the current vertex
        o.Clip(new Vector2(50, 50), 40);
        o.Text("1?", Vector2.Zero, 2, 0);
        Assert.Equal(6 + 10 * 6, o.VertexCount);
        Assert.Equal(2, o.Batches.Length);
        Assert.Equal((6, new Vector3(50, 50, 40)), o.Batches[1]);
        Assert.Equal(22, Overlay.TextWidth(2, 2)); // (2 glyphs × 6 dots − trailing gap) × 2 px

        // full: further shapes are dropped instead of overflowing the GPU ring
        for (var i = 0; i < Overlay.MaxVertices; i++) o.Disc(Vector2.Zero, 1, 0);
        Assert.Equal(Overlay.MaxVertices, o.VertexCount);
        o.Clear();
        Assert.Equal((0, 1), (o.VertexCount, o.Batches.Length));
    }
}
