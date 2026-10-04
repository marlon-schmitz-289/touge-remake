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
///     allocations: shapes beyond <see cref="MaxVertices"/> are dropped. Text: <see cref="SdfFont"/> glyph quads, on top of all shapes.
/// </summary>
public sealed class Overlay
{
    public const int MaxVertices = 96 * 1024, MaxBatches = 16, MaxGlyphVertices = 6 * 4096;
    private readonly OverlayVertex[] _vertices = new OverlayVertex[MaxVertices];
    private readonly GlyphVertex[] _glyphs = new GlyphVertex[MaxGlyphVertices];
    /// <summary>Font of <see cref="Text"/> (no font: text is skipped).</summary>
    public SdfFont? Font { get; set; }
    public int GlyphCount { get; private set; }
    public ReadOnlySpan<GlyphVertex> GlyphVertices => _glyphs.AsSpan(0, GlyphCount);
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
        GlyphCount = 0;
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

    /// <summary>Axis-aligned rectangle with a horizontal colour gradient (<paramref name="left"/> → <paramref name="right"/>).</summary>
    public void RectGradient(Vector2 min, Vector2 max, uint left, uint right)
    {
        if (!Reserve(6)) return;
        Put(min, Vector2.Zero, 1, left);
        Put(new Vector2(max.X, min.Y), Vector2.Zero, 1, right);
        Put(max, Vector2.Zero, 1, right);
        Put(min, Vector2.Zero, 1, left);
        Put(max, Vector2.Zero, 1, right);
        Put(new Vector2(min.X, max.Y), Vector2.Zero, 1, left);
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
    public void Ring(Vector2 center, float radius, float width, uint color, int segments = 64) => Arc(center, radius, width, color, 0, MathF.Tau, segments);

    /// <summary>Arc from angle <paramref name="from"/> to <paramref name="to"/> (radians, clockwise on screen from +x) in <paramref name="segments"/> lines.</summary>
    public void Arc(Vector2 center, float radius, float width, uint color, float from, float to, int segments = 64)
    {
        var prev = center + new Vector2(MathF.Cos(from), MathF.Sin(from)) * radius;
        for (var i = 1; i <= segments; i++)
        {
            var a = from + i * (to - from) / segments;
            var p = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
            Line(prev, p, width, color);
            prev = p;
        }
    }

    /// <summary>Filled quad a-b-c-d (convex, either winding), no anti-aliasing.</summary>
    public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, uint color)
    {
        Triangle(a, b, c, color);
        Triangle(a, c, d, color);
    }

    /// <summary>
    ///     Text in <see cref="Font"/>, <paramref name="baseline"/> = left end of the baseline (shifted by
    ///     −<paramref name="align"/> × width: 0 left, 0.5 centre, 1 right), <paramref name="size"/> = em in pixels.
    ///     <paramref name="weight"/> grows the glyphs by that many pixels (negative: thinner), <paramref name="soft"/>
    ///     widens the edge (shadows/glow; both limited by the font's distance range), <paramref name="skew"/> slants (italic, x per y).
    ///     Glyphs go to <see cref="GlyphVertices"/>, drawn by <see cref="TextRenderer"/> after all shapes. Returns the advance width.
    /// </summary>
    public float Text(ReadOnlySpan<char> text, Vector2 baseline, float size, uint color, float align = 0, float weight = 0, float soft = 0, float skew = 0)
    {
        if (Font is not { } f) return 0;
        var width = f.Measure(text, size);
        var x = baseline.X - width * align;
        var range = size * SdfFont.Spread / SdfFont.EmPx; // distance range in screen pixels
        weight = MathF.Min(weight, range * 0.6f);
        soft = Math.Clamp(soft, 0, MathF.Max(2 * (range - weight) - 1, 0));
        var scale = 2 * range;
        foreach (var c in text)
        {
            if (!f.TryGet(c, out var g)) { x += size * 0.5f; continue; }
            if (g.Plane.Z > g.Plane.X && GlyphCount + 6 <= MaxGlyphVertices)
            {
                float x0 = x + g.Plane.X * size, x1 = x + g.Plane.Z * size, y0 = baseline.Y - g.Plane.W * size, y1 = baseline.Y - g.Plane.Y * size;
                float k0 = (baseline.Y - y0) * skew, k1 = (baseline.Y - y1) * skew;
                GlyphVertex V(float px, float py, float u, float v) => new(new Vector2(px, py), new Vector2(u, v), color, scale, weight, soft);
                var (a, b, cc, d) = (V(x0 + k0, y0, g.Uv.X, g.Uv.Y), V(x1 + k0, y0, g.Uv.Z, g.Uv.Y), V(x1 + k1, y1, g.Uv.Z, g.Uv.W), V(x0 + k1, y1, g.Uv.X, g.Uv.W));
                _glyphs[GlyphCount++] = a;
                _glyphs[GlyphCount++] = b;
                _glyphs[GlyphCount++] = cc;
                _glyphs[GlyphCount++] = a;
                _glyphs[GlyphCount++] = cc;
                _glyphs[GlyphCount++] = d;
            }
            x += g.Advance * size;
        }
        return width;
    }
}

/// <summary>
///     Text vertex (<see cref="Overlay.Text"/>): pixel position, atlas uv, sRGB colour, <see cref="Scale"/> = screen pixels per
///     unit of the distance field around 0.5, <see cref="Weight"/>/<see cref="Soft"/> in pixels (text.frag).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GlyphVertex(Vector2 position, Vector2 uv, uint color, float scale, float weight, float soft)
{
    public Vector2 Position = position;
    public Vector2 Uv = uv;
    public uint Color = color;
    public float Scale = scale;
    public float Weight = weight;
    public float Soft = soft;

    public const int Size = 32;

    public static readonly VertexLayout Layout = VertexLayout.Interleaved(Size,
        new VertexAttribute(0, VertexFormat.Float2, 0),
        new VertexAttribute(1, VertexFormat.Float2, 8),
        new VertexAttribute(2, VertexFormat.UByte4Norm, 16),
        new VertexAttribute(3, VertexFormat.Float3, 20));
}
