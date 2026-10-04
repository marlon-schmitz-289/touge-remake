#version 450

// Driving effects (Effects.cs), vertices from world.vert (colour linearised there). pc.uEye.w = mode:
// 0 smoke: soft round puff with value-noise breakup, lit like a fuzzy sphere (wrapped sun × shadow + ambient +
//   headlights/street lights) and glowing when backlit by the sun; uv = seed + 0..1 corner.
// 1 skid mark: dark rubber, soft edges across the strip, streaky along it, lit like the road.
// 2 spark: emissive streak (additive blend), soft across.
// Everything fogs like the world (sparks only fade: they are additive).

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vColor;
layout(location = 2) in vec3 vPos;
layout(location = 3) in vec3 vNormal;

#include "scene_push.glsl"
#include "fog.glsl"
#include "lighting.glsl"
#include "noise.glsl"

layout(location = 0) out vec4 FragColor;
layout(location = 1) out vec4 Gbuf; // blends the AO share/reflection weight underneath towards 0 by the alpha (sparks: additive, none)

vec3 lit(vec3 albedo, vec3 n, float wrap)
{
    vec3 v = normalize(pc.uEye.xyz - vPos);
    vec3 spec = vec3(0.0);
    vec3 dyn = dynamicLight(vPos, n, v, 8.0, spec);
    float ndl = clamp((dot(n, pc.uSun.xyz) + wrap) / (1.0 + wrap), 0.0, 1.0);
    return albedo * (pc.uAmbient.rgb * hemisphere(n) + pc.uSun.w * pc.uSunColor.rgb * shadowAt(vPos, n) * ndl + dyn);
}

void main()
{
    float mode = pc.uEye.w;
    if (mode > 1.5)
    {
        float a = 1.0 - vUv.y * vUv.y;
        FragColor = vec4(vColor.rgb * (1.0 - fogAmount(vPos)), a);
        Gbuf = vec4(0.0);
        return;
    }
    if (mode > 0.5)
    {
        float edge = smoothstep(1.0, 0.55, abs(vUv.y));
        float streak = 0.65 + 0.35 * noise(vec2(vUv.x * 1.5, vUv.y * 4.0 + 7.0));
        float a = vColor.a * edge * streak;
        if (a <= 0.003) discard;
        FragColor = vec4(applyFog(lit(vColor.rgb, normalize(vNormal), 0.0), vPos), a);
        Gbuf = vec4(0.0, 0.0, 0.0, a);
        return;
    }
    vec2 local = fract(vUv) * 2.0 - 1.0;
    float seed = floor(vUv.x);
    float r2 = dot(local, local);
    if (r2 >= 1.0) discard;
    vec2 q = local * 1.6 + vec2(seed * 3.7, seed * 1.3);
    float n = 0.6 * noise(q) + 0.4 * noise(q * 2.3 + 5.0);
    float a = vColor.a * pow(1.0 - r2, 1.5) * (0.15 + 1.1 * smoothstep(0.25, 0.8, n));
    if (a <= 0.003) discard;
    vec3 v = normalize(pc.uEye.xyz - vPos);
    vec3 c = lit(vColor.rgb, normalize(vNormal), 0.6);
    // forward scattering: thin smoke lights up when the sun is behind it
    c += vColor.rgb * pc.uSun.w * 0.8 * pow(max(dot(-v, pc.uSun.xyz), 0.0), 6.0) * (1.0 - a);
    FragColor = vec4(applyFog(c, vPos), a);
    Gbuf = vec4(0.0, 0.0, 0.0, a);
}
