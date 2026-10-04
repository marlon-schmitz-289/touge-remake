using System.Buffers.Binary;
using System.Numerics;

namespace Kansei.Graphics;

/// <summary>
///     TrueType font → single-channel signed distance field atlas (R8), built at load time: glyph outlines (simple and
///     composite <c>glyf</c>, quadratic curves flattened), per texel the exact distance to the outline, inside by
///     non-zero winding. 0.5 = edge, ±<see cref="Spread"/> atlas pixels map to 1/0. Only the <c>cmap</c> format 4
///     subtable is read and no kerning (fine for the HUD's ASCII). Metrics are in em units (1 = font size), y up from the baseline.
/// </summary>
public sealed class SdfFont
{
    public const int EmPx = 96, Spread = 10; // atlas pixels per em, distance range in atlas pixels (also the glyph padding)

    public readonly record struct Glyph(float Advance, Vector4 Plane, Vector4 Uv); // Plane: x0, y0 (bottom), x1, y1 (top) in em

    private readonly Dictionary<char, Glyph> _glyphs = [];
    public int AtlasWidth { get; }
    public int AtlasHeight { get; }
    public byte[] Atlas { get; }
    /// <summary>Height of capitals/digits above the baseline (em), from 'H'.</summary>
    public float CapHeight { get; }
    public float Ascender { get; }
    public float Descender { get; }

    public SdfFont(byte[] ttf, string chars)
    {
        var t = new Ttf(ttf);
        Ascender = t.Ascender;
        Descender = t.Descender;
        var s = (float)EmPx / t.UnitsPerEm;
        var cells = new List<(char C, float Advance, List<(Vector2 A, Vector2 B)> Seg, int X0, int Y0, int W, int H)>();
        foreach (var c in chars.Distinct())
        {
            if (t.GlyphIndex(c) is not { } gi) continue;
            var seg = new List<(Vector2, Vector2)>();
            t.Outline(gi, Matrix3x2.CreateScale(s), seg, 0);
            int x0 = 0, y0 = 0, w = 0, h = 0;
            if (seg.Count > 0)
            {
                Vector2 min = new(float.MaxValue), max = new(float.MinValue);
                foreach (var (a, b) in seg) (min, max) = (Vector2.Min(min, Vector2.Min(a, b)), Vector2.Max(max, Vector2.Max(a, b)));
                (x0, y0) = ((int)MathF.Floor(min.X) - Spread, (int)MathF.Floor(min.Y) - Spread);
                (w, h) = ((int)MathF.Ceiling(max.X) + Spread - x0, (int)MathF.Ceiling(max.Y) + Spread - y0);
            }
            cells.Add((c, t.Advance(gi) / (float)t.UnitsPerEm, seg, x0, y0, w, h));
        }

        // shelf packing, tallest first, 1 px gap
        const int width = 1024;
        var place = new (int X, int Y)[cells.Count];
        var order = Enumerable.Range(0, cells.Count).OrderByDescending(i => cells[i].H).ToArray();
        int px = 0, py = 0, shelf = 0;
        foreach (var i in order)
        {
            if (px + cells[i].W > width) (px, py, shelf) = (0, py + shelf + 1, 0);
            place[i] = (px, py);
            px += cells[i].W + 1;
            shelf = Math.Max(shelf, cells[i].H);
        }
        (AtlasWidth, AtlasHeight) = (width, (py + shelf + 4) & ~3);
        Atlas = new byte[AtlasWidth * AtlasHeight];
        Parallel.For(0, cells.Count, i => Rasterize(cells[i].Seg, cells[i].X0, cells[i].Y0, cells[i].W, cells[i].H, place[i].X, place[i].Y));
        for (var i = 0; i < cells.Count; i++)
        {
            var (c, adv, _, x0, y0, w, h) = cells[i];
            var (ax, ay) = place[i];
            _glyphs[c] = new Glyph(adv, new Vector4(x0, y0, x0 + w, y0 + h) / EmPx,
                new Vector4(ax, ay, ax + w, ay + h) / new Vector4(AtlasWidth, AtlasHeight, AtlasWidth, AtlasHeight));
        }
        // tabular digits (timers and speed must not jitter): widest advance for all, glyphs centred in it
        var digit = 0f;
        for (var c = '0'; c <= '9'; c++) digit = _glyphs.TryGetValue(c, out var g) ? MathF.Max(digit, g.Advance) : digit;
        for (var c = '0'; c <= '9'; c++)
            if (_glyphs.TryGetValue(c, out var g))
            {
                var shift = (digit - g.Advance) / 2;
                _glyphs[c] = g with { Advance = digit, Plane = g.Plane + new Vector4(shift, 0, shift, 0) };
            }
        CapHeight = _glyphs.TryGetValue('H', out var hg) ? hg.Plane.W - (float)Spread / EmPx : 0.7f;
    }

    /// <summary>Distance field of one glyph into its atlas cell (atlas rows top-down, glyph y up).</summary>
    private void Rasterize(List<(Vector2 A, Vector2 B)> seg, int x0, int y0, int w, int h, int ax, int ay)
    {
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var p = new Vector2(x0 + x + 0.5f, y0 + h - y - 0.5f);
            var best = float.MaxValue;
            var winding = 0;
            foreach (var (a, b) in seg)
            {
                var ab = b - a;
                var k = Math.Clamp(Vector2.Dot(p - a, ab) / MathF.Max(ab.LengthSquared(), 1e-12f), 0, 1);
                best = MathF.Min(best, Vector2.DistanceSquared(p, a + ab * k));
                if ((a.Y <= p.Y) != (b.Y <= p.Y) && a.X + (p.Y - a.Y) / ab.Y * ab.X > p.X) winding += a.Y < b.Y ? 1 : -1;
            }
            var d = MathF.Sqrt(best) * (winding != 0 ? 1 : -1);
            Atlas[(ay + y) * AtlasWidth + ax + x] = (byte)Math.Clamp(MathF.Round((0.5f + d / (2 * Spread)) * 255), 0, 255);
        }
    }

    public bool TryGet(char c, out Glyph g) => _glyphs.TryGetValue(c, out g);

    /// <summary>Advance width of <paramref name="text"/> in pixels at font size <paramref name="size"/> (unknown characters: half an em).</summary>
    public float Measure(ReadOnlySpan<char> text, float size)
    {
        var w = 0f;
        foreach (var c in text) w += _glyphs.TryGetValue(c, out var g) ? g.Advance : 0.5f;
        return w * size;
    }

    /// <summary>Minimal big-endian TrueType reader: head, hhea, maxp, hmtx, loca, glyf, cmap format 4.</summary>
    private sealed class Ttf
    {
        private readonly byte[] _d;
        private readonly int _glyf, _loca, _hmtx, _cmap4, _metrics;
        private readonly bool _longLoca;
        public readonly int UnitsPerEm;
        public readonly float Ascender, Descender;

        public Ttf(byte[] d)
        {
            _d = d;
            var tables = new Dictionary<string, int>();
            for (int i = 0, n = U16(4); i < n; i++)
                tables[System.Text.Encoding.ASCII.GetString(d, 12 + 16 * i, 4)] = (int)U32(12 + 16 * i + 8);
            int Table(string tag) => tables.TryGetValue(tag, out var o) ? o : throw new InvalidDataException($"TTF: {tag} fehlt");
            var head = Table("head");
            UnitsPerEm = U16(head + 18);
            _longLoca = S16(head + 50) != 0;
            var hhea = Table("hhea");
            (Ascender, Descender) = (S16(hhea + 4) / (float)UnitsPerEm, S16(hhea + 6) / (float)UnitsPerEm);
            _metrics = U16(hhea + 34);
            (_glyf, _loca, _hmtx) = (Table("glyf"), Table("loca"), Table("hmtx"));
            var cmap = Table("cmap");
            for (int i = 0, n = U16(cmap + 2); i < n; i++)
            {
                var sub = cmap + (int)U32(cmap + 4 + 8 * i + 4);
                if (U16(sub) == 4) { _cmap4 = sub; break; }
            }
            if (_cmap4 == 0) throw new InvalidDataException("TTF: keine cmap Format 4");
        }

        private int U16(int o) => BinaryPrimitives.ReadUInt16BigEndian(_d.AsSpan(o));
        private short S16(int o) => BinaryPrimitives.ReadInt16BigEndian(_d.AsSpan(o));
        private uint U32(int o) => BinaryPrimitives.ReadUInt32BigEndian(_d.AsSpan(o));

        public int? GlyphIndex(char c)
        {
            var segs = U16(_cmap4 + 6) / 2;
            int ends = _cmap4 + 14, starts = ends + 2 * segs + 2, deltas = starts + 2 * segs, ranges = deltas + 2 * segs;
            for (var i = 0; i < segs; i++)
            {
                if (c > U16(ends + 2 * i)) continue;
                var start = U16(starts + 2 * i);
                if (c < start) return null;
                var ro = U16(ranges + 2 * i);
                var g = ro == 0 ? c : U16(ranges + 2 * i + ro + 2 * (c - start));
                g = g == 0 ? 0 : (g + S16(deltas + 2 * i)) & 0xFFFF;
                return g == 0 ? null : g;
            }
            return null;
        }

        public int Advance(int g) => U16(_hmtx + 4 * Math.Min(g, _metrics - 1));

        private (int Start, int End) Range(int g) => _longLoca
            ? ((int)U32(_loca + 4 * g), (int)U32(_loca + 4 * g + 4))
            : (U16(_loca + 2 * g) * 2, U16(_loca + 2 * g + 2) * 2);

        /// <summary>Glyph <paramref name="g"/> as line segments (curves in 6 steps), transformed by <paramref name="m"/>.</summary>
        public void Outline(int g, Matrix3x2 m, List<(Vector2, Vector2)> seg, int depth)
        {
            var (start, end) = Range(g);
            if (end <= start || depth > 4) return;
            var o = _glyf + start;
            int contours = S16(o);
            if (contours < 0)
            {
                // composite: components with x/y offsets (ARGS_ARE_XY_VALUES assumed) and optional scale
                var p = o + 10;
                int flags;
                do
                {
                    flags = U16(p);
                    var child = U16(p + 2);
                    p += 4;
                    float dx, dy;
                    if ((flags & 1) != 0) (dx, dy, p) = (S16(p), S16(p + 2), p + 4);
                    else (dx, dy, p) = ((sbyte)_d[p], (sbyte)_d[p + 1], p + 2);
                    var t = Matrix3x2.Identity;
                    float F2(int at) => S16(at) / 16384f;
                    if ((flags & 8) != 0) (t, p) = (Matrix3x2.CreateScale(F2(p)), p + 2);
                    else if ((flags & 0x40) != 0) (t, p) = (Matrix3x2.CreateScale(F2(p), F2(p + 2)), p + 4);
                    else if ((flags & 0x80) != 0) (t, p) = (new Matrix3x2(F2(p), F2(p + 2), F2(p + 4), F2(p + 6), 0, 0), p + 8);
                    t.Translation = new Vector2(dx, dy);
                    Outline(child, t * m, seg, depth + 1);
                } while ((flags & 0x20) != 0);
                return;
            }
            var endPts = new int[contours];
            for (var i = 0; i < contours; i++) endPts[i] = U16(o + 10 + 2 * i);
            var count = contours == 0 ? 0 : endPts[^1] + 1;
            var q = o + 10 + 2 * contours;
            q += 2 + U16(q); // instructions
            var fl = new byte[count];
            for (var i = 0; i < count;)
            {
                var f = _d[q++];
                fl[i++] = f;
                if ((f & 8) != 0)
                    for (int r = _d[q++]; r > 0; r--) fl[i++] = f;
            }
            var pts = new Vector2[count];
            int v = 0;
            for (var i = 0; i < count; i++)
            {
                if ((fl[i] & 2) != 0) v += (fl[i] & 16) != 0 ? _d[q++] : -_d[q++];
                else if ((fl[i] & 16) == 0) { v += S16(q); q += 2; }
                pts[i].X = v;
            }
            v = 0;
            for (var i = 0; i < count; i++)
            {
                if ((fl[i] & 4) != 0) v += (fl[i] & 32) != 0 ? _d[q++] : -_d[q++];
                else if ((fl[i] & 32) == 0) { v += S16(q); q += 2; }
                pts[i].Y = v;
            }
            for (int c = 0, first = 0; c < contours; first = endPts[c++] + 1)
            {
                var n = endPts[c] - first + 1;
                if (n < 2) continue;
                bool On(int i) => (fl[first + (i % n + n) % n] & 1) != 0;
                Vector2 P(int i) => pts[first + (i % n + n) % n];
                // start on an on-curve point (or the midpoint of two off-curve ones)
                var s0 = 0;
                while (s0 < n && !On(s0)) s0++;
                var startPt = s0 < n ? P(s0) : (P(0) + P(1)) / 2;
                var cur = startPt;
                for (var k = 1; k <= n; k++)
                {
                    var i = s0 + k;
                    if (On(i)) { Add(cur, P(i)); cur = P(i); continue; }
                    var next = On(i + 1) ? P(i + 1) : (P(i) + P(i + 1)) / 2;
                    for (var st = 1; st <= 6; st++)
                    {
                        var t = st / 6f;
                        var b = (1 - t) * (1 - t) * cur + 2 * (1 - t) * t * P(i) + t * t * next;
                        Add(cur, b);
                        cur = b;
                    }
                    if (On(i + 1)) k++;
                }
                if (cur != startPt) Add(cur, startPt);
            }
            return;

            void Add(Vector2 a, Vector2 b) => seg.Add((Vector2.Transform(a, m), Vector2.Transform(b, m)));
        }
    }
}
