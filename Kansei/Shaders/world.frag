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
layout(location = 1) out vec4 Gbuf; // PostProcess.GbufFormat: r ambient share (AO), g reflection weight (SSR), b streak

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
        // + the sun over the painted dome (warm haze colour; none at night/in rain, where uFogSun is 0), placed along the
        // real view ray (the dome hangs at y = 0, the camera does not): a small HDR core (the bloom turns it into glare)
        // and a Henyey-Greenstein forward-scattering glow (g 0.85) that washes the dome out towards the sun, no hard rim
        float s = dot(normalize(vPos - vec3(0.0, pc.uEye.y, 0.0)), pc.uSun.xyz);
        float hg = 0.2775 / pow(1.7225 - 1.7 * s, 1.5);
        float core = smoothstep(0.99994, 0.99997, s);
        // dark painted skies (night clouds) have only a few 8-bit levels, which the night exposure turns into flat
        // blotches: there the texture is smoothed over ±2 texels so the steps become ramps; bright detail stays sharp
        float dark = 1.0 - smoothstep(0.01, 0.06, dot(t.rgb, vec3(0.2126, 0.7152, 0.0722)));
        if (dark > 0.0)
        {
            vec2 ts = 2.0 / vec2(textureSize(uTexture, 0));
            vec3 blur = t.rgb;
            for (int i = 0; i < 8; i++)
            {
                float ang = float(i) * 0.7854;
                blur += texture(uTexture, vUv + vec2(cos(ang), sin(ang)) * ts * (i % 2 == 0 ? 1.0 : 0.6)).rgb;
            }
            t.rgb = mix(t.rgb, blur / 9.0, dark);
        }
        vec3 sky = mix(t.rgb * baked, fogColour(dir), skyFog(dir));
        vec3 glow = pc.uFogSun.rgb * 0.025 * hg;
        FragColor = vec4(sky + glow + pc.uFogSun.rgb * 120.0 * core, a);
        Gbuf = vec4(0.0);
        return;
    }
    vec3 v = normalize(pc.uEye.xyz - vPos);
    vec3 n = normalize(vNormal);
    if (dot(n, v) < 0.0) n = -n;
    float lum = dot(baked, vec3(0.2126, 0.7152, 0.0722));
    float sh = mix(1.0, shadowAt(vPos, n), smoothstep(0.02, 0.2, lum));

    float rain = pc.uParams.y;
    float up = smoothstep(0.75, 0.95, n.y);
    // material class from a blurrier mip (bias 3): grainy asphalt texels must not flip between glossy and matte
    vec3 tm = texture(uTexture, vUv, 3.0).rgb;
    float hi = max(tm.r, max(tm.g, tm.b)), lo = min(tm.r, min(tm.g, tm.b));
    float grey = 1.0 - smoothstep(0.1, 0.3, (hi - lo) / max(hi, 1e-3));
    float patches = noise(vPos.xz * 0.35) * 0.65 + noise(vPos.xz * 1.3 + 17.0) * 0.35;
    float gloss = rain * up * grey * step(0.95, t.a) * mix(0.75, 1.0, smoothstep(0.3, 0.6, patches));
    float puddle = gloss * smoothstep(0.95, 0.985, n.y) * smoothstep(0.54, 0.64, patches);
    float soak = rain * (0.6 + 0.4 * up);
    // ripple rings are ~3 cm waves: fade them out before they get thinner than ~2 px (they would sparkle with rounding)
    float rippleFade = 1.0 - smoothstep(0.25, 0.5, length(fwidth(vPos.xz)) * 3.0 * 70.0 / 6.2832);
    vec3 albedo = pow(t.rgb, vec3(1.0 + 0.5 * soak)) * (1.0 - 0.3 * soak) * (1.0 - 0.6 * puddle);

    vec3 spec = vec3(0.0);
    vec3 dyn = dynamicLight(vPos, n, v, 16.0, spec);
    float ndl = max(dot(n, pc.uSun.xyz), 0.0);
    float contact = contactShadow(vPos);
    vec3 ambient = baked * pc.uAmbient.w * hemisphere(n) * contact;
    vec3 c = albedo * (ambient + baked * pc.uSun.w * pc.uSunColor.rgb * sh * ndl * contact + dyn) + spec * 0.04;
    if (pc.uSunColor.w > 0.0)
    {
        // dry sun glints on grey hard surfaces (asphalt, guardrails, concrete): broad Blinn-Phong × Schlick, strongest
        // looking into a low sun, gone in baked shade
        vec3 h = normalize(pc.uSun.xyz + v);
        float fresnel = 0.04 + 0.96 * pow(1.0 - max(dot(h, v), 0.0), 5.0);
        float lobe = (24.0 + 8.0) / 25.13 * pow(max(dot(n, h), 0.0), 24.0);
        c += pc.uSunColor.rgb * (pc.uSunColor.w * grey * step(0.95, t.a) * sh * ndl * fresnel * lobe * smoothstep(0.05, 0.3, lum));
    }
    float reflection = 0.0;
    if (gloss > 0.0)
    {
        vec3 nw = n;
        if (puddle > 0.0)
        {
            vec2 s = ripples(vPos.xz * 3.0, pc.uSky.w) * 0.12 * rain * rippleFade;
            nw = normalize(mix(n, vec3(0.0, 1.0, 0.0), puddle) + vec3(s.x, 0.0, s.y) * puddle);
        }
        vec3 r = reflect(-v, nw);
        vec3 skyR = mix(fogColour(r), pc.uSky.rgb, pow(max(r.y, 0.0), 0.45));
        // dark baked light = under trees, next to walls: less sky; and the terrain around a mountain road hides most of the horizon
        float open = smoothstep(0.01, 0.12, lum) * mix(0.4, 1.0, smoothstep(0.0, 0.3, r.y));
        float fresnel = 0.02 + 0.98 * pow(1.0 - max(dot(nw, v), 0.0), 5.0);
        reflection = fresnel * gloss * mix(0.85, 1.0, puddle);
        c = mix(c, skyR * open, reflection);
        c += gloss * wetLights(vPos, nw, v, mix(0.05, 0.012, puddle), mix(0.4, 0.02, puddle));
    }
    vec3 fogged = applyFog(c, vPos);
    float clear = 1.0 - fogAmount(vPos);
    float share = dot(albedo * ambient, vec3(0.2126, 0.7152, 0.0722)) * (1.0 - reflection) * clear / max(dot(fogged, vec3(0.2126, 0.7152, 0.0722)), 1e-5);
    FragColor = vec4(fogged, a);
    Gbuf = vec4(clamp(share, 0.0, 1.0), reflection * clear, 1.0 - puddle, a);
}
