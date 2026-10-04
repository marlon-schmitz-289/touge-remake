#version 450

// Rain streaks (rain.vert), alpha-blended: soft across, fading towards the tail. A drop shows the overcast sky it
// refracts (fog colour, a bit brighter) plus the dynamic lights at its position — headlights make drops sparkle at night.

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec3 vPos;
layout(location = 2) in float vAlpha;

#include "scene_push.glsl"
#include "noise.glsl"
#include "fog.glsl"
#include "lighting.glsl"

layout(location = 0) out vec4 FragColor;
layout(location = 1) out vec4 Gbuf; // blends the AO share/reflection weight underneath towards 0 by its alpha

void main()
{
    float a = vAlpha * (1.0 - vUv.x * vUv.x) * (1.0 - 0.7 * vUv.y);
    if (a <= 0.002) discard;
    vec3 c = pc.uFog.rgb * 1.6 + pc.uAmbient.rgb * 0.3 + lightAt(vPos) * 0.6;
    FragColor = vec4(mix(c, fogColour(normalize(vPos - pc.uEye.xyz)), fogAmount(vPos)), a);
    Gbuf = vec4(0.0, 0.0, 0.0, a);
}
