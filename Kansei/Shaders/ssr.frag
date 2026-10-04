#version 450

// Half-resolution screen-space reflections on wet ground (PostProcess). Only where the scene marked a reflection
// weight (gbuffer g, world.frag: Fresnel × gloss). The ground is taken as flat (gloss only exists on upward surfaces):
// the view ray is mirrored about +y in camera-relative world space and marched in 20 growing steps (0.2 m … ~70 m)
// against the half-res view distance of ao.frag (cache-friendly; fp16 is fine for the ≥ 0.5 m thickness), then
// refined by bisection. A hit returns the scene colour there, smeared vertically by
// gbuffer b (rough wet asphalt → streaks, puddles → sharp), premultiplied by a confidence that fades at the screen
// edges and with distance. No hit (sky, off screen) = 0: world.frag's own sky reflection stays.

layout(set = 0, binding = 0) uniform sampler2D uScene;
layout(set = 0, binding = 1) uniform sampler2D uDist; // ao.frag output: g = view distance (m) of the half-res pixel
layout(set = 0, binding = 2) uniform sampler2D uGbuf;

layout(push_constant) uniform Push {
    mat4 uViewProj;    // view rotation (no translation) × projection
    mat4 uInvViewProj;
    vec4 uParams;      // x = near plane, y = clip y per uv y (+1 Vulkan/GL, -1 Metal), zw = 1 / full-res size
} pc;

layout(location = 0) out vec4 FragColor;

vec2 toUv(vec4 clip) { return vec2(clip.x, clip.y * pc.uParams.y) / clip.w * 0.5 + 0.5; }
float sceneDist(vec2 uv) { return textureLod(uDist, uv, 0.0).g; }

void main()
{
    vec2 uv = (floor(gl_FragCoord.xy) * 2.0 + 0.5) * pc.uParams.zw; // the full-res texel ao.frag measured
    vec4 g = textureLod(uGbuf, uv, 0.0);
    if (g.g < 0.01)
    {
        FragColor = vec4(0.0);
        return;
    }
    float d = pc.uParams.x / texelFetch(uDist, ivec2(gl_FragCoord.xy), 0).g;
    vec4 h = pc.uInvViewProj * vec4(uv.x * 2.0 - 1.0, (uv.y * 2.0 - 1.0) * pc.uParams.y, d, 1.0);
    vec3 p = h.xyz / h.w;
    vec3 r = reflect(normalize(p), vec3(0.0, 1.0, 0.0));
    p.y += 0.02;

    float t = 0.0, step = 0.2, hit = -1.0;
    vec2 hitUv = vec2(0.0);
    for (int i = 0; i < 20 && hit < 0.0; i++)
    {
        float tn = t + step;
        vec4 clip = pc.uViewProj * vec4(p + r * tn, 1.0);
        if (clip.w <= pc.uParams.x) break;
        vec2 q = toUv(clip);
        if (any(lessThan(q, vec2(0.0))) || any(greaterThan(q, vec2(1.0)))) break;
        float behind = clip.w - sceneDist(q);
        if (behind > 0.02 && behind < max(0.5, step))
        {
            // bisection between t and tn
            float a = t, b = tn;
            for (int k = 0; k < 5; k++)
            {
                float m = 0.5 * (a + b);
                vec4 cm = pc.uViewProj * vec4(p + r * m, 1.0);
                if (cm.w - sceneDist(toUv(cm)) > 0.0) b = m; else a = m;
            }
            hit = b;
            hitUv = toUv(pc.uViewProj * vec4(p + r * b, 1.0));
        }
        t = tn;
        step *= 1.25;
    }
    if (hit < 0.0)
    {
        FragColor = vec4(0.0);
        return;
    }
    vec2 edge = smoothstep(vec2(0.0), vec2(0.08), hitUv) * smoothstep(vec2(0.0), vec2(0.08), 1.0 - hitUv);
    float conf = edge.x * edge.y * (1.0 - smoothstep(40.0, 80.0, hit));
    float spread = g.b * 0.012;
    vec3 c = vec3(0.0);
    for (int k = -2; k <= 2; k++) c += textureLod(uScene, hitUv + vec2(0.0, float(k) * spread), 0.0).rgb;
    FragColor = vec4(min(c * 0.2, vec3(32.0)) * conf, conf);
}
