#version 450

// texture (sRGB, decoded by the sampler) × baked vertex light, all linear. The baked light is split into a part that
// stays in shadow and a part scaled by the dynamic sun: baked × (keep + sun × shadow × N·L), so a typical lit surface
// keeps its original brightness while shadows (car, trees) and slopes away from the sun gain depth. Where the baked
// light is already dark (baked shadows), the dynamic shadow fades out instead of darkening twice. Headlights/street
// lights add on top (albedo × irradiance, not scaled by the baked light). Alpha test for foliage/fences: the alpha is
// sharpened around the 0.3 cutoff, so with alpha-to-coverage (MSAA) cut-out edges get antialiased. Fog last
// (fog.glsl, per pixel, + light glow). The sky mesh only gets the horizon fog.
//
// Rain (uParams.y): everything soaks (darker, more saturated albedo), but only upward-facing hard surfaces — grey,
// opaque textures like asphalt and concrete, not grass/foliage/rock with colour — turn glossy, patchy by noise. Glossy
// ground mirrors the sky blurred (Fresnel of water, dimmed where the baked light says the sky is occluded) and the
// lamps as long streaks; puddles (noise mask on flat ground) are dark, flat mirrors with sharp lamp reflections and
// rain ripples.

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vColor;
layout(location = 2) in vec3 vPos;
layout(location = 3) in vec3 vNormal;

layout(set = 0, binding = 0) uniform sampler2D uTexture;

#include "scene_push.glsl"
#include "fog.glsl"
#include "lighting.glsl"
#include "noise.glsl"

layout(location = 0) out vec4 FragColor;

// Expanding rings of rain drops hitting water, one per 0.33 m cell; returns the slope (xz) of the water surface.
vec2 ripples(vec2 p, float t)
{
    vec2 cell = floor(p);
    vec2 d = fract(p) - 0.5 - (vec2(hash(cell + 3.1), hash(cell + 7.7)) - 0.5) * 0.5;
    float r = length(d);
    float phase = fract(t * 1.3 + hash(cell));
    float ring = phase * 0.45;
    float wave = sin((r - ring) * 70.0) * smoothstep(0.06, 0.0, abs(r - ring)) * (1.0 - phase);
    return d / max(r, 1e-3) * wave;
}

void main()
{
    vec4 t = texture(uTexture, vUv);
    bool sky = pc.uEye.w > 0.5;
    float a = sky ? t.a : t.a * vColor.a;
    a = clamp((a - 0.3) / max(fwidth(a), 1e-4) + 0.5, 0.0, 1.0);
    if (a <= 0.0) discard;
    vec3 baked = vColor.rgb;
    if (sky)
    {
        // sky mesh: a dome around the camera (x/z follow it, y does not), so its own direction is what it shows
        vec3 dir = normalize(vPos);
        FragColor = vec4(mix(t.rgb * baked, fogColour(dir), skyFog(dir)), a);
        return;
    }
    vec3 v = normalize(pc.uEye.xyz - vPos);
    vec3 n = normalize(vNormal);
    if (dot(n, v) < 0.0) n = -n;
    float lum = dot(baked, vec3(0.2126, 0.7152, 0.0722));
    float sh = mix(1.0, shadowAt(vPos, n), smoothstep(0.02, 0.2, lum));

    float rain = pc.uParams.y;
    float up = smoothstep(0.75, 0.95, n.y);
    float hi = max(t.r, max(t.g, t.b)), lo = min(t.r, min(t.g, t.b));
    float grey = 1.0 - smoothstep(0.1, 0.3, (hi - lo) / max(hi, 1e-3));
    float patches = noise(vPos.xz * 0.35) * 0.65 + noise(vPos.xz * 1.3 + 17.0) * 0.35;
    float gloss = rain * up * grey * step(0.95, t.a) * mix(0.55, 1.0, smoothstep(0.3, 0.6, patches));
    float puddle = gloss * smoothstep(0.95, 0.985, n.y) * smoothstep(0.54, 0.64, patches);
    float soak = rain * (0.6 + 0.4 * up);
    vec3 albedo = pow(t.rgb, vec3(1.0 + 0.5 * soak)) * (1.0 - 0.3 * soak) * (1.0 - 0.6 * puddle);

    vec3 spec = vec3(0.0);
    vec3 dyn = dynamicLight(vPos, n, v, 16.0, spec);
    vec3 c = albedo * (baked * (pc.uAmbient.w + pc.uSun.w * sh * max(dot(n, pc.uSun.xyz), 0.0)) + dyn) + spec * 0.04;
    if (gloss > 0.0)
    {
        vec3 nw = n;
        if (puddle > 0.0)
        {
            vec2 s = ripples(vPos.xz * 3.0, pc.uSky.w) * 0.12 * rain;
            nw = normalize(mix(n, vec3(0.0, 1.0, 0.0), puddle) + vec3(s.x, 0.0, s.y) * puddle);
        }
        vec3 r = reflect(-v, nw);
        vec3 skyR = mix(fogColour(r), pc.uSky.rgb, pow(max(r.y, 0.0), 0.45));
        // dark baked light = under trees, next to walls: less sky; and the terrain around a mountain road hides most of the horizon
        float open = smoothstep(0.01, 0.12, lum) * mix(0.4, 1.0, smoothstep(0.0, 0.3, r.y));
        float fresnel = 0.02 + 0.98 * pow(1.0 - max(dot(nw, v), 0.0), 5.0);
        c = mix(c, skyR * open, fresnel * gloss * mix(0.7, 1.0, puddle));
        c += gloss * wetLights(vPos, nw, v, mix(0.05, 0.012, puddle), mix(0.4, 0.02, puddle));
    }
    FragColor = vec4(applyFog(c, vPos), a);
}
