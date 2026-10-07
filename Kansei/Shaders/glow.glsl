// Glow of the dynamic lights in the fog (lighting.glsl lightGlow) and the street lamps' glare (lampGlare), added onto the
// resolved HDR scene once per pixel (WorldRenderer.DrawGlow) instead of in every shaded fragment of world, car and effects.
// Two passes: HALF (glow_fog.frag) sums the fog's light at half resolution — it is soft, and 16 beam steps per full-res pixel
// cost ~5 ms at 3200 × 1800 — over the nearest depth of the 2 × 2 block it covers and its neighbours: the depth is not
// antialiased, and per pixel a leaf (10 m of lit fog) next to a gap (60 m) flickered while driving; this way small gaps in
// foliage count as foliage. The full pass (glow.frag) adds that, bilinearly upsampled, and the glare (sharp, cheap).
// The sky (depth 0, reversed-Z) gets the fog's light only in dense fog, along the ray 1 km out (the mist between eye and
// sky glows like the mist in front of the trees); in thin haze the sky stays clear.
// scene_push.glsl as for the world, but uMvp = inverse of view × projection and uModel[0] = (1 / width, 1 / height of this
// pass's target, clip y per gl_FragCoord.y: +1 Vulkan/GL, −1 Metal, 0).

layout(set = 0, binding = 0) uniform sampler2D uDepth;
#ifndef HALF
layout(set = 0, binding = 12) uniform sampler2D uFogLight; // binding numbers are Metal slots, unique across sets (1–11: the scene group)
#endif

#include "scene_push.glsl"
#include "noise.glsl"
#include "fog.glsl"
#include "lighting.glsl"

layout(location = 0) out vec4 FragColor;

void main()
{
    ivec2 top = textureSize(uDepth, 0) - 1;
    float depth = 0.0;
#ifdef HALF
    ivec2 px = ivec2(gl_FragCoord.xy) * 2;
    for (int y = -1; y <= 2; y++)
        for (int x = -1; x <= 2; x++) depth = max(depth, texelFetch(uDepth, clamp(px + ivec2(x, y), ivec2(0), top), 0).r);
#else
    depth = texelFetch(uDepth, min(ivec2(gl_FragCoord.xy), top), 0).r;
#endif
    bool sky = depth <= 0.0;
    vec2 uv = gl_FragCoord.xy * pc.uModel[0].xy;
    vec2 ndc = uv * 2.0 - 1.0;
    ndc.y *= pc.uModel[0].z;
    vec4 p = pc.uMvp * vec4(ndc, sky ? 1.0 : depth, 1.0); // sky: the near plane, only for the ray's direction
    vec3 at = p.xyz / p.w;
    if (sky) at = pc.uEye.xyz + normalize(at - pc.uEye.xyz) * 1000.0;
#ifdef HALF
    FragColor = vec4(sky && pc.uTailPos[0].w <= 0.0 ? vec3(0.0) : lightGlow(at), 0.0);
#else
    FragColor = vec4(texture(uFogLight, uv).rgb + lampGlare(at, sky), 0.0);
#endif
}
