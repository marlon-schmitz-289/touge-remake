using System.Numerics;
using System.Runtime.InteropServices;
using Penelope;

namespace Kansei.Graphics;

/// <summary>
///     Overlay vertex: pixel position (top-left origin), <see cref="Sdf"/> = offset from the shape's centre line/point
///     in pixels, <see cref="Radius"/> its half width; overlay.frag covers <c>Radius - |Sdf| + 0.5</c> (1-px AA edge).
///     Colour is sRGB RGBA8 (the swapchain is UNORM with gamma applied by the tonemapper).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct OverlayVertex(Vector2 position, Vector2 sdf, float radius, uint color)
{
    public Vector2 Position = position;
    public Vector2 Sdf = sdf;
    public float Radius = radius;
    public uint Color = color;

    public const int Size = 24;

    public static readonly VertexLayout Layout = VertexLayout.Interleaved(Size,
        new VertexAttribute(0, VertexFormat.Float2, 0),
        new VertexAttribute(1, VertexFormat.Float2, 8),
        new VertexAttribute(2, VertexFormat.Float1, 16),
        new VertexAttribute(3, VertexFormat.UByte4Norm, 20));
}

/// <summary>
///     Screen-space 2D shapes for one frame (HUD), drawn after tonemapping by <see cref="OverlayRenderer"/>. Pixel
///     coordinates, top-left origin; anti-aliased lines and discs from a distance field across thickness quads, filled
///     triangles without AA. <see cref="Clip"/> starts a batch that is cut to a circle (minimaps). Fixed capacity, no
///     allocations: shapes beyond <see cref="MaxVertices"/> are dropped. Text: 5×7 dot-matrix glyphs (digits, % / and
///     a few capitals) as round dots.
/// </summary>
public sealed class Overlay
{
    public const int MaxVertices = 96 * 1024, MaxBatches = 16;
    private readonly OverlayVertex[] _vertices = new OverlayVertex[MaxVertices];
    private readonly (int First, Vector3 Clip)[] _batches = new (int, Vector3)[MaxBatches];
    private int _batchCount;
    public int VertexCount { get; private set; }

    public ReadOnlySpan<OverlayVertex> Vertices => _vertices.AsSpan(0, VertexCount);
    /// <summary>Draw ranges: first vertex and clip circle (centre, radius); each runs to the next one's first vertex.</summary>
    public ReadOnlySpan<(int First, Vector3 Clip)> Batches => _batches.AsSpan(0, _batchCount);

    /// <summary>Empties the overlay for a new frame (no clip).</summary>
    public void Clear()
    {
        VertexCount = 0;
        _batchCount = 1;
        _batches[0] = (0, new Vector3(0, 0, float.MaxValue));
    }

    public Overlay() => Clear();

    /// <summary>Shapes from now on are cut to the circle (<paramref name="center"/>, <paramref name="radius"/>); radius ∞ = no clip.</summary>
    public void Clip(Vector2 center, float radius = float.MaxValue)
    {
        if (_batches[_batchCount - 1].First == VertexCount) _batchCount--; // empty batch: replace it
        if (_batchCount == MaxBatches) return;
        _batches[_batchCount++] = (VertexCount, new Vector3(center, radius));
    }

    /// <summary>sRGB colour, components 0..1.</summary>
    public static uint Rgba(float r, float g, float b, float a = 1) =>
        (uint)(r * 255 + 0.5f) | (uint)(g * 255 + 0.5f) << 8 | (uint)(b * 255 + 0.5f) << 16 | (uint)(a * 255 + 0.5f) << 24;

    private bool Reserve(int n) => VertexCount + n <= MaxVertices;

    private void Put(Vector2 p, Vector2 sdf, float radius, uint color) => _vertices[VertexCount++] = new OverlayVertex(p, sdf, radius, color);

    /// <summary>Filled triangle, no anti-aliasing (outline it with <see cref="Line"/>).</summary>
    public void Triangle(Vector2 a, Vector2 b, Vector2 c, uint color)
    {
        if (!Reserve(3)) return;
        Put(a, Vector2.Zero, 1, color);
        Put(b, Vector2.Zero, 1, color);
        Put(c, Vector2.Zero, 1, color);
    }

    /// <summary>Axis-aligned filled rectangle (no AA; use whole pixels for crisp edges).</summary>
    public void Rect(Vector2 min, Vector2 max, uint color)
    {
        Triangle(min, new Vector2(max.X, min.Y), max, color);
        Triangle(min, max, new Vector2(min.X, max.Y), color);
    }

    /// <summary>Anti-aliased line of <paramref name="width"/> pixels with round-ish ends (extended by half the width).</summary>
    public void Line(Vector2 a, Vector2 b, float width, uint color)
    {
        if (!Reserve(6)) return;
        var d = b - a;
        var len = d.Length();
        if (len < 1e-4f) return;
        d /= len;
        var n = new Vector2(-d.Y, d.X);
        var h = width / 2;
        var e = h + 1; // quad half size incl. the AA fringe
        Vector2 p0 = a - d * h + n * e, p1 = a - d * h - n * e, p2 = b + d * h + n * e, p3 = b + d * h - n * e;
        Vector2 s0 = new(e, 0), s1 = new(-e, 0);
        Put(p0, s0, h, color);
        Put(p1, s1, h, color);
        Put(p2, s0, h, color);
        Put(p2, s0, h, color);
        Put(p1, s1, h, color);
        Put(p3, s1, h, color);
    }

    /// <summary>Anti-aliased filled disc.</summary>
    public void Disc(Vector2 center, float radius, uint color)
    {
        if (!Reserve(6)) return;
        var e = radius + 1;
        Vector2 a = new(-e, -e), b = new(e, -e), c = new(e, e), d = new(-e, e);
        Put(center + a, a, radius, color);
        Put(center + b, b, radius, color);
        Put(center + c, c, radius, color);
        Put(center + a, a, radius, color);
        Put(center + c, c, radius, color);
        Put(center + d, d, radius, color);
    }

    /// <summary>Circle outline from <paramref name="segments"/> lines.</summary>
    public void Ring(Vector2 center, float radius, float width, uint color, int segments = 64)
    {
        var prev = center + new Vector2(radius, 0);
        for (var i = 1; i <= segments; i++)
        {
            var a = i * MathF.Tau / segments;
            var p = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
            Line(prev, p, width, color);
            prev = p;
        }
    }

    /// <summary>Width in pixels of <paramref name="text"/> at dot pitch <paramref name="dot"/> (6 dots per glyph incl. gap).</summary>
    public static float TextWidth(int chars, float dot) => chars > 0 ? (chars * 6 - 1) * dot : 0;

    /// <summary>
    ///     Dot-matrix text, top-left at <paramref name="pos"/>, <paramref name="dot"/> pixels per dot (glyph 5×7 dots).
    ///     Unknown characters draw as blanks.
    /// </summary>
    public void Text(ReadOnlySpan<char> text, Vector2 pos, float dot, uint color)
    {
        var r = dot * 0.42f;
        for (var c = 0; c < text.Length; c++)
        {
            var g = Glyph(text[c]);
            for (var row = 0; row < 7; row++)
            for (var col = 0; col < 5; col++)
                if ((g[row] & (0x10 >> col)) != 0)
                    Disc(pos + new Vector2((c * 6 + col + 0.5f) * dot, (row + 0.5f) * dot), r, color);
        }
    }

    private static ReadOnlySpan<byte> Font =>
    [
        0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E, // 0
        0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E, // 1
        0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F, // 2
        0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E, // 3
        0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02, // 4
        0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E, // 5
        0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E, // 6
        0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08, // 7
        0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E, // 8
        0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C, // 9
        0x18, 0x19, 0x02, 0x04, 0x08, 0x13, 0x03, // %
        0x01, 0x01, 0x02, 0x04, 0x08, 0x10, 0x10, // /
        0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11, // A
        0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11, // H
        0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11, // K
        0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11, // M
        0x11, 0x11, 0x19, 0x15, 0x13, 0x11, 0x11, // N
        0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11, // R
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // blank
    ];

    private static ReadOnlySpan<byte> Glyph(char c)
    {
        var i = c switch
        {
            >= '0' and <= '9' => c - '0', '%' => 10, '/' => 11, 'A' => 12, 'H' => 13, 'K' => 14, 'M' => 15, 'N' => 16, 'R' => 17,
            _ => 18,
        };
        return Font.Slice(i * 7, 7);
    }
}
