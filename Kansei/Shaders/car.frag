#version 450

// Car shading, linear light. Diffuse = albedo × (ambient + sun × shadow × N·L + headlights/street lights). Paint and
// glass get a clear coat on top: Schlick Fresnel (F0 0.04 / 0.06) blends towards the reflection of the course's
// environment maps (ENV_TOP/BOTTOM/LEFT/RIGHT of the nearest road point, a crude cube in car space, see envAt)
// plus a sharp sun highlight that feeds the bloom. Glass is darker (the cabin behind it is dark). Rear lamps
// (kind 2) glow: dim with the headlights on, bright when braking. Rain: paint a bit darker (wet), beaded with
// droplets — small domes (hashed per 1.8 cm cell in car space, on the plane facing the normal) that only add light:
// a sharp highlight of the sun/lamps and the bright upper environment refracted through the bent surface, over a
// very faint wet spot. Antialiased by derivatives: soft drop edges over ~1 px, and drops fade out (to nothing,
// not to an average) once a cell gets smaller than ~3 px, so distant cars do not sparkle with rounding.
// Fog last (fog.glsl; the light glow follows per pixel in glow.frag).

layout(location = 0) in vec3 vPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vUv;
layout(location = 3) in vec4 vColor;

layout(set = 0, binding = 0) uniform sampler2D uTexture;

#include "scene_push.glsl"
#include "fog.glsl"
#include "lighting.glsl"
#include "noise.glsl"

layout(set = 1, binding = 2) uniform sampler2D uEnvTop;
layout(set = 1, binding = 3) uniform sampler2D uEnvBottom;
layout(set = 1, binding = 4) uniform sampler2D uEnvLeft;
layout(set = 1, binding = 5) uniform sampler2D uEnvRight;

layout(location = 0) out vec4 FragColor;
layout(location = 1) out vec4 Gbuf; // PostProcess.GbufFormat: r ambient share (AO)

// Reflection direction → env colour. The 64×32 maps are small panoramas (v = 0 at the top): up = TOP, down = BOTTOM,
// car left (+x) / right (−x) = LEFT/RIGHT with u running front to back; front/back reflect the side maps' centres.
vec3 envAt(vec3 r)
{
    vec3 d = transpose(mat3(pc.uModel)) * r; // car space: +x left, +y up, +z front
    float up = smoothstep(0.1, 0.6, d.y), down = smoothstep(0.0, 0.4, -d.y);
    vec2 side = vec2(0.5 - 0.5 * d.z, 0.5 - 0.5 * d.y);
    vec3 horizontal = mix(texture(uEnvRight, side).rgb, texture(uEnvLeft, side).rgb, smoothstep(-0.4, 0.4, d.x));
    vec3 top = texture(uEnvTop, vec2(0.5 - 0.5 * d.z, 1.0 - d.y)).rgb;
    vec3 bottom = texture(uEnvBottom, vec2(0.5 + 0.5 * d.x, 0.5 - 0.5 * d.z)).rgb;
    return mix(mix(horizontal, top, up), bottom, down);
}

// Droplet at car-space point q with car-space normal m: xy = slope, z = coverage 0..1 (edge and distance faded by the
// pixel footprint). Cells on the plane the normal faces most.
vec3 droplet(vec3 q, vec3 m)
{
    vec3 am = abs(m);
    vec2 uv = (am.y > max(am.x, am.z) ? q.xz : am.x > am.z ? q.zy : q.xy) * 55.0;
    float px = max(length(fwidth(uv)), 1e-4); // cells per pixel
    float fade = 1.0 - smoothstep(0.2, 0.45, px);
    vec2 cell = floor(uv);
    if (fade <= 0.0 || hash(cell) > 0.25) return vec3(0.0);
    vec2 d = fract(uv) - 0.5 - (vec2(hash(cell + 1.7), hash(cell + 5.3)) - 0.5) * 0.4;
    float radius = 0.12 + 0.18 * hash(cell + 9.1);
    float r = length(d);
    float cover = smoothstep(radius, radius - px, r) * fade;
    return vec3(d / radius * 0.6, cover);
}

void main()
{
    vec4 t = texture(uTexture, vUv);
    float kind = vColor.a;
    bool decal = kind > 3.5; // decal pass (CarRenderer, alpha-blended): kind + 4, soft edges instead of the 0.5 alpha test
    if (decal) kind -= 4.0;
    if (t.a < (decal ? 0.02 : 0.5)) discard;
    vec3 v = normalize(pc.uEye.xyz - vPos);
    vec3 n = normalize(vNormal);
    if (dot(n, v) < 0.0) n = -n; // no culling: shade the side we see
    bool glass = abs(kind - 0.5) < 0.1, paint = abs(kind - 1.0) < 0.1, lamp = kind > 1.5;
    float rain = pc.uParams.y;
    vec3 base = t.rgb * vColor.rgb * (glass ? 0.25 : 1.0) * (paint ? 1.0 - 0.2 * rain : 1.0);

    vec3 l = pc.uSun.xyz;
    float sh = shadowAt(vPos, n);
    vec3 spec = vec3(0.0);
    vec3 dyn = dynamicLight(vPos, n, v, 256.0, spec);
    vec3 ambient = base * pc.uAmbient.rgb * hemisphere(n);
    vec3 c = ambient + base * (pc.uSun.w * pc.uSunColor.rgb * sh * max(dot(n, l), 0.0) + dyn);

    if (glass || paint)
    {
        float f0 = glass ? 0.06 : 0.04;
        float fresnel = f0 + (1.0 - f0) * pow(1.0 - max(dot(n, v), 0.0), 5.0);
        vec3 env = envAt(reflect(-v, n)) * pc.uParams.z;
        vec3 h = normalize(l + v);
        float sun = pc.uSun.w * sh * max(dot(n, l), 0.0) * (1000.0 + 8.0) / 25.13 * pow(max(dot(n, h), 0.0), 1000.0);
        c = c * (1.0 - fresnel) + fresnel * (env + sun * pc.uSunColor.rgb) + f0 * spec;
        ambient = ambient * (1.0 - fresnel) + fresnel * env;
        if (rain > 0.0)
        {
            mat3 rot = mat3(pc.uModel);
            vec3 m = transpose(rot) * n;
            vec3 drop = droplet(transpose(rot) * (vPos - pc.uModel[3].xyz), m) * vec3(1.0, 1.0, rain);
            if (drop.z > 0.0)
            {
                vec3 am = abs(m);
                vec3 bump = am.y > max(am.x, am.z) ? vec3(drop.x, 0.0, drop.y) : am.x > am.z ? vec3(0.0, drop.y, drop.x) : vec3(drop.x, drop.y, 0.0);
                vec3 nd = normalize(n + rot * bump);
                // the dome mirrors the sky above it, never the dark road: reflection folded into the upper hemisphere
                vec3 rd = reflect(-v, nd);
                rd.y = abs(rd.y);
                float fd = 0.02 + 0.98 * pow(1.0 - max(dot(nd, v), 0.0), 5.0);
                vec3 hd = normalize(l + v);
                float glint = pc.uSun.w * sh * max(dot(nd, l), 0.0) * (300.0 + 8.0) / 25.13 * pow(max(dot(nd, hd), 0.0), 300.0);
                vec3 lamps = vec3(0.0);
                dynamicLight(vPos, nd, v, 300.0, lamps);
                vec3 lit = envAt(rd) * pc.uParams.z * (0.15 + fd) + glint * pc.uSunColor.rgb + 0.5 * lamps;
                c = c * (1.0 - 0.12 * drop.z) + lit * (0.6 * drop.z);
            }
        }
    }
    if (lamp)
    {
        float on = dot(pc.uSpotColor.rgb, vec3(1.0)) > 0.0 ? 1.0 : 0.0;
        c += t.rgb * vColor.rgb * (1.5 * on + 8.0 * pc.uParams.w);
    }
    vec3 fogged = applyFog(c, vPos);
    float share = dot(ambient, vec3(0.2126, 0.7152, 0.0722)) * (1.0 - fogAmount(vPos)) / max(dot(fogged, vec3(0.2126, 0.7152, 0.0722)), 1e-5);
    float alpha = decal ? t.a : 1.0;
    FragColor = vec4(fogged, alpha);
    Gbuf = vec4(clamp(share, 0.0, 1.0), 0.0, 0.0, alpha);
}
