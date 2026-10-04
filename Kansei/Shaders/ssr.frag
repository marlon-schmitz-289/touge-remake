#version 450

// Half-resolution screen-space reflections on wet ground (PostProcess). Only where the scene marked a reflection
// weight (gbuffer g, world.frag: Fresnel × gloss). Everything is measured in the full-res fp32 depth (half-res or
// fp16 depth lets thin posts blink in and out and the road hit itself in noisy patches far away).
// Mirror plane: the road's own normal from the depth (smallest of the left/right and up/down differences, as ao.frag),
// levelled towards +y for puddles (flat water) — a flat-ground mirror on a 6 % slope tilts every reflection by ~7°
// and detaches it from the object. The ray is marched in 20 growing steps (0.2 m … ~70 m); where it first goes behind
// the depth, bisection finds the crossing and the hit only counts if the ray is really near the surface there
// (a ray passing behind the car or a post is no hit, the march goes on). The hit is smeared vertically by gbuffer b
// (rough wet asphalt → long streak, puddle → sharp) with taps ≤ 3 px apart (no ghost copies of bright lamps),
// premultiplied by a confidence that fades at the screen edges and with distance. No hit (sky, off screen) = 0:
// world.frag's own sky reflection stays.

layout(set = 0, binding = 0) uniform sampler2D uScene;
layout(set = 0, binding = 1) uniform sampler2D uDepth; // resolved scene depth: reversed-Z, view distance = near / depth
layout(set = 0, binding = 2) uniform sampler2D uGbuf;

// uniform buffer slice (PostProcess.Upload): 144 bytes > Vulkan's guaranteed 128 of push constants
layout(set = 0, binding = 3, std140) uniform Push {
    mat4 uViewProj;    // view rotation (no translation) × projection
    mat4 uInvViewProj;
    vec4 uParams;      // x = near plane, y = clip y per uv y (+1 Vulkan/GL, -1 Metal), zw = 1 / full-res size
} pc;

layout(location = 0) out vec4 FragColor;

vec2 toUv(vec4 clip) { return vec2(clip.x, clip.y * pc.uParams.y) / clip.w * 0.5 + 0.5; }
float sceneDist(vec2 uv) { return pc.uParams.x / max(textureLod(uDepth, uv, 0.0).r, 1e-7); }

// camera-relative world position of full-res texel t
vec3 posAt(ivec2 t)
{
    t = clamp(t, ivec2(0), textureSize(uDepth, 0) - 1); // neighbours at the screen border
    vec2 uv = (vec2(t) + 0.5) * pc.uParams.zw;
    vec4 h = pc.uInvViewProj * vec4(uv.x * 2.0 - 1.0, (uv.y * 2.0 - 1.0) * pc.uParams.y, max(texelFetch(uDepth, t, 0).r, 1e-7), 1.0);
    return h.xyz / h.w;
}

void main()
{
    ivec2 t = ivec2(gl_FragCoord.xy) * 2; // the full-res texel this half-res pixel stands for
    vec4 g = texelFetch(uGbuf, t, 0);
    if (g.g < 0.01)
    {
        FragColor = vec4(0.0);
        return;
    }
    vec3 p = posAt(t);
    vec3 l = posAt(t - ivec2(1, 0)), r = posAt(t + ivec2(1, 0)), u = posAt(t - ivec2(0, 1)), d = posAt(t + ivec2(0, 1));
    vec3 dx = dot(r - p, r - p) < dot(p - l, p - l) ? r - p : p - l;
    vec3 dy = dot(d - p, d - p) < dot(p - u, p - u) ? d - p : p - u;
    vec3 n = normalize(cross(dx, dy));
    if (n.y < 0.0) n = -n;
    n = normalize(mix(vec3(0.0, 1.0, 0.0), n.y > 0.8 ? n : vec3(0.0, 1.0, 0.0), g.b)); // edges/odd normals: level
    vec3 dir = reflect(normalize(p), n);
    p += n * 0.02;

    float t0 = 0.0, step = 0.2, hit = -1.0;
    bool wasBehind = false;
    for (int i = 0; i < 20 && hit < 0.0; i++)
    {
        float tn = t0 + step;
        vec4 clip = pc.uViewProj * vec4(p + dir * tn, 1.0);
        if (clip.w <= pc.uParams.x) break;
        vec2 q = toUv(clip);
        if (any(lessThan(q, vec2(0.0))) || any(greaterThan(q, vec2(1.0)))) break;
        bool behind = clip.w > sceneDist(q);
        if (behind && !wasBehind)
        {
            // bisection between t0 (in front) and tn (behind), then: is the ray at the surface or passing behind it?
            float a = t0, b = tn;
            for (int k = 0; k < 6; k++)
            {
                float m = 0.5 * (a + b);
                vec4 cm = pc.uViewProj * vec4(p + dir * m, 1.0);
                if (cm.w > sceneDist(toUv(cm))) b = m; else a = m;
            }
            vec4 cb = pc.uViewProj * vec4(p + dir * b, 1.0);
            if (cb.w - sceneDist(toUv(cb)) < 0.15 + 2.0 * (b - a)) hit = b;
        }
        wasBehind = behind;
        t0 = tn;
        step *= 1.25;
    }
    if (hit < 0.0)
    {
        FragColor = vec4(0.0);
        return;
    }
    vec2 hitUv = toUv(pc.uViewProj * vec4(p + dir * hit, 1.0));
    vec2 edge = smoothstep(vec2(0.0), vec2(0.08), hitUv) * smoothstep(vec2(0.0), vec2(0.08), 1.0 - hitUv);
    float conf = edge.x * edge.y * (1.0 - smoothstep(40.0, 80.0, hit));
    // vertical streak of ±0.024 uv on rough asphalt: enough taps that they stay ≤ ~3 full-res px apart
    float reach = g.b * 0.024;
    int taps = clamp(int(reach / (1.5 * pc.uParams.w)), 0, 16);
    vec3 c = textureLod(uScene, hitUv, 0.0).rgb;
    float wsum = 1.0;
    for (int k = 1; k <= taps; k++)
    {
        float x = float(k) / float(taps + 1);
        float w = 1.0 - x * x;
        vec2 o = vec2(0.0, x * reach);
        c += (textureLod(uScene, hitUv + o, 0.0).rgb + textureLod(uScene, hitUv - o, 0.0).rgb) * w;
        wsum += 2.0 * w;
    }
    FragColor = vec4(min(c / wsum, vec3(32.0)) * conf, conf);
}
