#version 450

// Glow of the dynamic lights in the fog (lighting.glsl lightGlow) along the view ray to the resolved scene depth, added
// onto the resolved HDR scene once per pixel (WorldRenderer.EndScene) instead of in every shaded fragment of world,
// car and effects (overdraw × 16 beam taps). The sky (depth 0, reversed-Z) gets none, as before.
// scene_push.glsl as for the world, but uMvp = inverse of view × projection and uModel[0] = (1 / width, 1 / height,
// clip y per gl_FragCoord.y: +1 Vulkan/GL, −1 Metal, 0).

layout(set = 0, binding = 0) uniform sampler2D uDepth;

#include "scene_push.glsl"
#include "noise.glsl"
#include "fog.glsl"
#include "lighting.glsl"

layout(location = 0) out vec4 FragColor;

void main()
{
    float depth = texelFetch(uDepth, ivec2(gl_FragCoord.xy), 0).r;
    if (depth <= 0.0) discard;
    vec2 ndc = gl_FragCoord.xy * pc.uModel[0].xy * 2.0 - 1.0;
    ndc.y *= pc.uModel[0].z;
    vec4 p = pc.uMvp * vec4(ndc, depth, 1.0);
    FragColor = vec4(lightGlow(p.xyz / p.w), 0.0);
}
