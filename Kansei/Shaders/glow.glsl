// Glow of the dynamic lights in the fog (lighting.glsl lightGlow) and the street lamps' glare (lampGlare), added onto the
// resolved HDR scene once per pixel (WorldRenderer.DrawGlow) instead of in every shaded fragment of world, car and effects.
// Two passes: HALF (glow_fog.frag) sums the headlights' light in the fog at half resolution — it is soft, and 16 beam steps
// per full-res pixel cost ~5 ms at 3200 × 1800. The full pass (glow.frag) adds that, bilinearly upsampled, the street lights'
// glow (analytic, cheap, sharp near the lamp: upsampled it wobbled) and their glare. Both take the mean distance of the
// pixel's neighbourhood (main).
// The sky (depth 0, reversed-Z) gets the fog's light only in dense fog, along the ray 1 km out (the mist between eye and
// sky glows like the mist in front of the trees); in thin haze the sky stays clear.
// scene_push.glsl as for the world, but uMvp = inverse of (view rotation, no translation) × projection and uModel[0] =
// (1 / width, 1 / height of this pass's target, clip y per gl_FragCoord.y: +1 Vulkan/GL, −1 Metal, 0).

layout(set = 0, binding = 0) uniform sampler2D uDepth;
#ifndef HALF
layout(set = 0, binding = 12) uniform sampler2D uFogLight; // binding numbers are Metal slots, unique across sets (1–11: the scene group)
#endif

#include "scene_push.glsl"
#include "noise.glsl"
#include "fog.glsl"
#include "lighting.glsl"

layout(location = 0) out vec4 FragColor;

// Distance from the eye to the surface at full-res pixel q (sky: Far). Reversed-Z with an infinite far plane: depth =
// near / view depth, so the distance along a ray is its distance at the near plane (depth 1) over the depth — `nearDist`,
// the centre pixel's (the 3 × 3 / 4 × 4 neighbours' rays differ by a pixel: ~1e-4 off). One divide instead of an inverse
// projection per tap: the 9 matrix products were most of this pass at 3200 × 1800.
const float Far = 150.0;
float distanceAt(ivec2 q, ivec2 size, float nearDist)
{
    float d = texelFetch(uDepth, clamp(q, ivec2(0), size - 1), 0).r;
    return d <= 0.0 ? Far : min(nearDist / d, Far);
}

void main()
{
    // the glow sums light along the ray up to the surface, but the depth is one sample per pixel while the scene is
    // antialiased: a leaf next to a gap (a lamp or the sky behind) made a hard pixel step that crawled and flickered while
    // driving. The mean distance over the pixel's neighbourhood (half res: its 2 × 2 block and around) smooths those edges
    // like antialiasing and changes nothing inside a surface; the sky counts as Far (150 m, the glows barely grow beyond).
    ivec2 size = textureSize(uDepth, 0);
    vec2 uv = gl_FragCoord.xy * pc.uModel[0].xy;
    vec2 ndc = uv * 2.0 - 1.0;
    ndc.y *= pc.uModel[0].z;
    vec4 near = pc.uMvp * vec4(ndc, 1.0, 1.0); // the near plane: the ray's direction
    vec3 toNear = near.xyz / near.w; // camera-relative
    float nearDist = length(toNear);
    vec3 rd = toNear / nearDist;
    float sum = 0.0, n = 0.0;
    bool skyCentre;
#ifdef HALF
    ivec2 px = ivec2(gl_FragCoord.xy) * 2;
    for (int y = -1; y <= 2; y++)
        for (int x = -1; x <= 2; x++, n++) sum += distanceAt(px + ivec2(x, y), size, nearDist);
    skyCentre = texelFetch(uDepth, min(px, size - 1), 0).r <= 0.0;
#else
    ivec2 px = ivec2(gl_FragCoord.xy);
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++, n++) sum += distanceAt(px + ivec2(x, y), size, nearDist);
    skyCentre = texelFetch(uDepth, min(px, size - 1), 0).r <= 0.0;
#endif
    float dist = sum / n;
    bool sky = skyCentre && dist >= Far - 1.0;
    vec3 at = pc.uEye.xyz + rd * (sky ? 1000.0 : dist);
    bool clear = sky && pc.uTailPos[0].w <= 0.0; // thin haze: the sky stays clear
#ifdef HALF
    FragColor = vec4(clear ? vec3(0.0) : lightGlow(at, false, true), 0.0);
#else
    FragColor = vec4(texture(uFogLight, uv).rgb + (clear ? vec3(0.0) : lightGlow(at, true, false)) + lampGlare(at, sky), 0.0);
#endif
}
