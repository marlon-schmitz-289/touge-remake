#version 450

// Vertical blur of the half-res wet reflections (ssr.frag) in screen space, as long as gbuffer b says (rough wet asphalt
// → streaks, puddles → sharp). ssr.frag already smears the colour it hits; this smears the hit itself: the edges where
// a ray just finds or just misses an object (car, guardrail, posts) and the 2×2 blocks of half resolution turn into
// soft streaks instead of hard steps that flip from frame to frame. Only reflecting pixels mix (nothing bleeds in
// from the car body above the road). Taps alternate ±½ texel sideways: a little horizontal softening for free.

layout(set = 0, binding = 0) uniform sampler2D uSsr;
layout(set = 0, binding = 1) uniform sampler2D uGbuf; // full res

layout(push_constant) uniform Push {
    vec4 uA; // xy = 1 / half-res size, z = reach on rough asphalt (half-res texels)
} pc;

layout(location = 0) out vec4 FragColor;

float reflecting(vec2 uv) { return step(0.01, textureLod(uGbuf, uv, 0.0).g); }

void main()
{
    vec2 uv = gl_FragCoord.xy * pc.uA.xy;
    vec4 g = texelFetch(uGbuf, ivec2(gl_FragCoord.xy) * 2, 0);
    vec4 c = textureLod(uSsr, uv, 0.0);
    if (g.g < 0.01) // not reflecting: tonemap.frag will not use it
    {
        FragColor = c;
        return;
    }
    float reach = max(g.b, 0.1) * pc.uA.z; // puddles too a little: no 2×2 steps on their edges
    int taps = clamp(int(reach * 0.5), 0, 16); // ≤ 2–3 texels apart: bilinear fills the gaps
    float wsum = 1.0;
    for (int k = 1; k <= taps; k++)
    {
        float x = float(k) / float(taps + 1);
        float w = 1.0 - x;
        for (int s = -1; s <= 1; s += 2)
        {
            vec2 q = uv + vec2(float(s) * 0.5, float(s) * x * reach) * pc.uA.xy;
            float m = w * reflecting(q);
            c += textureLod(uSsr, q, 0.0) * m;
            wsum += m;
        }
    }
    FragColor = c / wsum;
}
