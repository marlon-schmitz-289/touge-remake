#version 450

// texture (sRGB, decoded by the sampler) × baked vertex light, all linear. The baked light is split into a part that
// stays in shadow and a part scaled by the dynamic sun: baked × (keep + sun × shadow × N·L), so a typical lit surface
// keeps its original brightness while shadows (car, trees) and slopes away from the sun gain depth. Where the baked
// light is already dark (baked shadows), the dynamic shadow fades out instead of darkening twice. Headlights/street
// lights add on top (albedo × irradiance, not scaled by the baked light). Rain: flat surfaces get darker and
// reflect the horizon colour (damped Fresnel) and light highlights. Alpha test for foliage/fences: the alpha is sharpened
// around the 0.3 cutoff, so with alpha-to-coverage (MSAA) cut-out edges get antialiased. Distance fog last.

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vColor;
layout(location = 2) in float vFog;
layout(location = 3) in vec3 vPos;
layout(location = 4) in vec3 vNormal;

layout(set = 0, binding = 0) uniform sampler2D uTexture;

#include "scene_push.glsl"
#include "lighting.glsl"

layout(location = 0) out vec4 FragColor;

void main()
{
    vec4 t = texture(uTexture, vUv);
    bool sky = pc.uEye.w > 0.5;
    float a = sky ? t.a : t.a * vColor.a;
    a = clamp((a - 0.3) / max(fwidth(a), 1e-4) + 0.5, 0.0, 1.0);
    if (a <= 0.0) discard;
    vec3 baked = vColor.rgb;
    vec3 c = t.rgb * baked;
    if (!sky)
    {
        vec3 v = normalize(pc.uEye.xyz - vPos);
        vec3 n = normalize(vNormal);
        if (dot(n, v) < 0.0) n = -n;
        float lum = dot(baked, vec3(0.2126, 0.7152, 0.0722));
        float sh = mix(1.0, shadowAt(vPos, n), smoothstep(0.02, 0.2, lum));
        float wet = pc.uParams.y * smoothstep(0.7, 0.95, n.y);
        vec3 albedo = t.rgb * (1.0 - 0.5 * wet);
        vec3 spec = vec3(0.0);
        vec3 dyn = dynamicLight(vPos, n, v, mix(16.0, 400.0, wet), spec);
        c = albedo * (baked * (pc.uAmbient.w + pc.uSun.w * sh * max(dot(n, pc.uSun.xyz), 0.0)) + dyn);
        // rough wet asphalt: Schlick Fresnel damped (a blurred, partial mirror), reflecting the horizon colour
        float fresnel = 0.02 + 0.3 * pow(1.0 - max(dot(n, v), 0.0), 5.0);
        c += spec * mix(0.04, 1.0, wet) + wet * fresnel * pc.uFog.rgb;
    }
    FragColor = vec4(mix(c, pc.uFog.rgb, vFog * vFog), a);
}
