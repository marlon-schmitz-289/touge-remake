using System.Numerics;

namespace Kansei.Graphics;

/// <summary>
///     CPU side of the driving effects: tyre smoke puffs, sparks and skid-mark strips in fixed pools (no allocations
///     after construction). The game emits, <see cref="Update"/> runs at the physics tick, the Build* methods write
///     camera-dependent quads (6 <see cref="WorldVertex"/> each) that <see cref="EffectsRenderer"/> draws.
///     Vertex encoding (effect.frag): smoke uv = seed + corner 0..1, colour = albedo + opacity, normal = puff
///     "sphere" normal; skid uv = (metres along, −1..1 across); spark uv.y = −1..1 across, colour = HDR emission.
///     Colours are gamma-space like the course's vertex colours (world.vert linearises them).
/// </summary>
public sealed class Effects
{
    public const int MaxSmoke = 400, MaxSparks = 256, MaxSkids = 4096, SkidTracks = 16; // one per wheel, four cars (versus)
    /// <summary>Skid marks stay this long at full strength, then fade out over <see cref="SkidFade"/> (seconds).</summary>
    public const float SkidHold = 25, SkidFade = 10;
    private const float SkidStep = 0.25f; // metres between strip points

    private struct Particle
    {
        public Vector3 Pos, Vel;
        public float Age, Life, Size, Grow, Opacity, Spin;
        public int Seed;
        public bool Spray; // water spray: falls (gravity, more drag), shorter life, lighter colour
    }

    private struct Segment
    {
        public Vector3 A, B, SideA, SideB, Normal;
        public float Strength, Age, Along;
    }

    private readonly Particle[] _smoke = new Particle[MaxSmoke], _sparks = new Particle[MaxSparks];
    private readonly Segment[] _skids = new Segment[MaxSkids];
    private readonly (Vector3 Pos, Vector3 Side, float Along, bool Active)[] _tracks = new (Vector3, Vector3, float, bool)[SkidTracks];
    private readonly float[] _sortKeys = new float[MaxSmoke];
    private readonly int[] _sortIndex = new int[MaxSmoke];
    private readonly Random _rng = new(7);
    private int _smokeCount, _sparkCount, _skidHead, _skidCount, _seed;

    public int SmokeCount => _smokeCount;
    public int SparkCount => _sparkCount;
    public int SkidCount => _skidCount;

    /// <summary>Smoke puff at <paramref name="pos"/>: grows from <paramref name="size"/> (radius, m), rises, drifts with <paramref name="vel"/>.</summary>
    public void EmitSmoke(Vector3 pos, Vector3 vel, float size, float opacity)
    {
        if (_smokeCount == MaxSmoke) return; // ponytail: full pool drops new puffs; replace the oldest if dense smoke looks capped
        _smoke[_smokeCount++] = new Particle
        {
            Pos = pos, Vel = vel, Life = 2.2f + 1.6f * _rng.NextSingle(), Size = size, Grow = 0.9f + 0.6f * _rng.NextSingle(),
            Opacity = opacity, Spin = (_rng.NextSingle() - 0.5f) * 1.2f, Seed = _seed++ & 255,
        };
    }

    /// <summary>
    ///     Tyre spray in the wet: a mist puff from the smoke pool that is thrown with <paramref name="vel"/>, arcs down
    ///     under gravity and drag, spreads and fades within ~1 s.
    /// </summary>
    public void EmitSpray(Vector3 pos, Vector3 vel, float size, float opacity)
    {
        if (_smokeCount == MaxSmoke) return;
        _smoke[_smokeCount++] = new Particle
        {
            Pos = pos, Vel = vel, Life = 0.7f + 0.5f * _rng.NextSingle(), Size = size, Grow = 0.3f + 0.3f * _rng.NextSingle(),
            Opacity = opacity, Spin = (_rng.NextSingle() - 0.5f) * 2f, Seed = _seed++ & 255, Spray = true,
        };
    }

    public void EmitSpark(Vector3 pos, Vector3 vel)
    {
        if (_sparkCount == MaxSparks) return;
        _sparks[_sparkCount++] = new Particle { Pos = pos, Vel = vel, Life = 0.25f + 0.35f * _rng.NextSingle() };
    }

    /// <summary>
    ///     Continues skid strip <paramref name="track"/> (one per wheel) to the contact point <paramref name="pos"/>.
    ///     <paramref name="side"/> = half the mark width across the tread, along the ground; <paramref name="strength"/>
    ///     0..1 is the mark's darkness, 0 ends the strip. A new segment is stored every <see cref="SkidStep"/> m; the
    ///     oldest is overwritten when the ring is full.
    /// </summary>
    public void Skid(int track, Vector3 pos, Vector3 side, Vector3 normal, float strength)
    {
        ref var t = ref _tracks[track];
        if (strength <= 0)
        {
            t.Active = false;
            return;
        }
        if (!t.Active)
        {
            t = (pos, side, 0, true);
            return;
        }
        var d = Vector3.Distance(t.Pos, pos);
        if (d < SkidStep) return;
        if (d > 4 * SkidStep) // teleport/reset: start over
        {
            t = (pos, side, 0, true);
            return;
        }
        _skids[_skidHead] = new Segment { A = t.Pos, B = pos, SideA = t.Side, SideB = side, Normal = normal, Strength = strength, Along = t.Along };
        _skidHead = (_skidHead + 1) % MaxSkids;
        _skidCount = Math.Min(_skidCount + 1, MaxSkids);
        t = (pos, side, t.Along + d, true);
    }

    /// <summary>Ages and moves everything; dead particles are swapped out (pool order is not kept).</summary>
    public void Update(float dt)
    {
        float smokeDrag = MathF.Exp(-1.6f * dt), sprayDrag = MathF.Exp(-2.5f * dt);
        for (var i = 0; i < _smokeCount; i++)
        {
            ref var p = ref _smoke[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                p = _smoke[--_smokeCount];
                i--;
                continue;
            }
            p.Vel = p.Spray
                ? p.Vel * sprayDrag - new Vector3(0, 7f * dt, 0) // drops fall, the mist around them is slowed by the air
                : p.Vel * smokeDrag + new Vector3(0.15f, 0.7f, 0.05f) * dt; // buoyancy + a light breeze
            p.Pos += p.Vel * dt;
        }
        var sparkDrag = MathF.Exp(-2f * dt);
        for (var i = 0; i < _sparkCount; i++)
        {
            ref var p = ref _sparks[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                p = _sparks[--_sparkCount];
                i--;
                continue;
            }
            p.Vel = p.Vel * sparkDrag - new Vector3(0, 9.81f * dt, 0);
            p.Pos += p.Vel * dt;
        }
        for (var i = 0; i < _skidCount; i++) _skids[i].Age += dt;
    }

    /// <summary>Smoke puffs back to front as camera-facing quads; returns the vertex count.</summary>
    public int BuildSmoke(Span<WorldVertex> dst, Vector3 eye, Vector3 right, Vector3 up)
    {
        var n = Math.Min(_smokeCount, dst.Length / 6);
        for (var i = 0; i < n; i++)
        {
            _sortKeys[i] = -Vector3.DistanceSquared(_smoke[i].Pos, eye);
            _sortIndex[i] = i;
        }
        _sortKeys.AsSpan(0, n).Sort(_sortIndex.AsSpan(0, n));
        var m = 0;
        for (var k = 0; k < n; k++)
        {
            ref readonly var p = ref _smoke[_sortIndex[k]];
            var t = p.Age / p.Life;
            var radius = p.Size * (1 + p.Grow * 3.5f * MathF.Sqrt(t)); // fast early growth, then slow spread
            var dist = Vector3.Distance(eye, p.Pos);
            // fades in, out with age, and away near the camera (no full-screen fog walls, less overdraw)
            var alpha = p.Opacity * MathF.Min(p.Age * 8, 1) * (1 - t) * (1 - t) * Math.Clamp((dist - radius) / (2 * radius), 0, 1);
            if (alpha < 0.004f) continue;
            var toEye = (eye - p.Pos) / MathF.Max(dist, 1e-3f);
            // pulled towards the camera by half its radius: big puffs cut less hard into the road
            var centre = p.Pos + toEye * MathF.Min(radius * 0.5f, dist * 0.5f);
            var (s, c) = MathF.SinCos(p.Spin * p.Age + p.Seed);
            var ax = (right * c + up * s) * radius;
            var ay = (up * c - right * s) * radius;
            var colour = p.Spray ? new Vector4(0.8f, 0.82f, 0.85f, alpha) : new Vector4(0.72f, 0.72f, 0.74f, alpha);
            Quad(dst[(m++ * 6)..], centre, ax, ay, p.Seed, colour, toEye);
        }
        return m * 6;
    }

    private static void Quad(Span<WorldVertex> dst, Vector3 centre, Vector3 ax, Vector3 ay, int seed, Vector4 colour, Vector3 toEye)
    {
        WorldVertex V(float x, float y)
        {
            var normal = Vector3.Normalize(Vector3.Normalize(ax) * x + Vector3.Normalize(ay) * y + toEye * 0.8f);
            return new WorldVertex(centre + ax * x + ay * y, new Vector2(seed + 0.5f + 0.5f * x, 0.5f + 0.5f * y), colour, normal);
        }
        dst[0] = V(-1, -1);
        dst[1] = V(1, -1);
        dst[2] = V(1, 1);
        dst[3] = dst[0];
        dst[4] = dst[2];
        dst[5] = V(-1, 1);
    }

    /// <summary>Sparks as short glowing streaks along their velocity, facing the camera; returns the vertex count.</summary>
    public int BuildSparks(Span<WorldVertex> dst, Vector3 eye)
    {
        var n = Math.Min(_sparkCount, dst.Length / 6);
        for (var i = 0; i < n; i++)
        {
            ref readonly var p = ref _sparks[i];
            var t = p.Age / p.Life;
            var tail = p.Pos - p.Vel * 0.025f;
            var dir = p.Pos - tail;
            var side = Vector3.Cross(dir, eye - p.Pos);
            side = side.LengthSquared() > 1e-8f ? Vector3.Normalize(side) * 0.015f : Vector3.UnitY * 0.015f;
            // white-hot → orange, HDR so the bloom picks it up (gamma values: world.vert raises them to 2.2, ≈ 8 → 5 linear)
            var colour = new Vector4(Vector3.Lerp(new Vector3(2.6f, 2.2f, 1.7f), new Vector3(2.1f, 1.15f, 0.45f), t) * (1 - t * t), 1);
            var o = i * 6;
            dst[o] = new WorldVertex(tail - side, new Vector2(0, -1), colour, Vector3.UnitY);
            dst[o + 1] = new WorldVertex(tail + side, new Vector2(0, 1), colour, Vector3.UnitY);
            dst[o + 2] = new WorldVertex(p.Pos + side, new Vector2(1, 1), colour, Vector3.UnitY);
            dst[o + 3] = dst[o];
            dst[o + 4] = dst[o + 2];
            dst[o + 5] = new WorldVertex(p.Pos - side, new Vector2(1, -1), colour, Vector3.UnitY);
        }
        return n * 6;
    }

    /// <summary>Skid strips lifted 2 cm off the road, fading with age; returns the vertex count.</summary>
    public int BuildSkids(Span<WorldVertex> dst)
    {
        var n = 0;
        for (var i = 0; i < _skidCount && n + 6 <= dst.Length; i++)
        {
            ref readonly var s = ref _skids[i];
            var fade = 1 - Math.Clamp((s.Age - SkidHold) / SkidFade, 0, 1);
            if (fade <= 0) continue;
            var lift = s.Normal * 0.02f;
            var colour = new Vector4(0.03f, 0.028f, 0.026f, 0.9f * s.Strength * fade);
            var along = s.Along + Vector3.Distance(s.A, s.B);
            dst[n] = new WorldVertex(s.A - s.SideA + lift, new Vector2(s.Along, -1), colour, s.Normal);
            dst[n + 1] = new WorldVertex(s.A + s.SideA + lift, new Vector2(s.Along, 1), colour, s.Normal);
            dst[n + 2] = new WorldVertex(s.B + s.SideB + lift, new Vector2(along, 1), colour, s.Normal);
            dst[n + 3] = dst[n];
            dst[n + 4] = dst[n + 2];
            dst[n + 5] = new WorldVertex(s.B - s.SideB + lift, new Vector2(along, -1), colour, s.Normal);
            n += 6;
        }
        return n;
    }
}
