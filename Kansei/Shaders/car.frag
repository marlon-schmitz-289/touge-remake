#version 450

// Car shading, linear light. Diffuse = albedo × (ambient + sun × shadow × N·L + headlights/street lights). Paint and
// glass get a clear coat on top: Schlick Fresnel (F0 0.04 / 0.06) blends towards the reflection of the course's
// environment maps (ENV_TOP/BOTTOM/LEFT/RIGHT of the nearest road point, a crude cube in car space, see envAt)
// plus a sharp sun highlight that feeds the bloom. Glass is darker (the cabin behind it is dark). Rear lamps
// (kind 2) glow: dim with the headlights on, bright when braking.

layout(location = 0) in vec3 vPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vUv;
layout(location = 3) in vec4 vColor;

layout(set = 0, binding = 0) uniform sampler2D uTexture;

#include "scene_push.glsl"
#include "lighting.glsl"

layout(set = 1, binding = 2) uniform sampler2D uEnvTop;
layout(set = 1, binding = 3) uniform sampler2D uEnvBottom;
layout(set = 1, binding = 4) uniform sampler2D uEnvLeft;
layout(set = 1, binding = 5) uniform sampler2D uEnvRight;

layout(location = 0) out vec4 FragColor;

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

void main()
{
    vec4 t = texture(uTexture, vUv);
    if (t.a < 0.5) discard;
    vec3 v = normalize(pc.uEye.xyz - vPos);
    vec3 n = normalize(vNormal);
    if (dot(n, v) < 0.0) n = -n; // no culling: shade the side we see
    float kind = vColor.a;
    bool glass = abs(kind - 0.5) < 0.1, paint = abs(kind - 1.0) < 0.1, lamp = kind > 1.5;
    vec3 base = t.rgb * vColor.rgb * (glass ? 0.25 : 1.0);

    vec3 l = pc.uSun.xyz;
    float sh = shadowAt(vPos, n);
    vec3 spec = vec3(0.0);
    vec3 dyn = dynamicLight(vPos, n, v, 256.0, spec);
    vec3 c = base * (pc.uAmbient.rgb + pc.uSun.w * sh * max(dot(n, l), 0.0) + dyn);

    if (glass || paint)
    {
        float f0 = glass ? 0.06 : 0.04;
        float fresnel = f0 + (1.0 - f0) * pow(1.0 - max(dot(n, v), 0.0), 5.0);
        vec3 env = envAt(reflect(-v, n)) * pc.uParams.z;
        vec3 h = normalize(l + v);
        float sun = pc.uSun.w * sh * max(dot(n, l), 0.0) * (1000.0 + 8.0) / 25.13 * pow(max(dot(n, h), 0.0), 1000.0);
        c = c * (1.0 - fresnel) + fresnel * (env + sun) + f0 * spec;
    }
    if (lamp)
    {
        float on = dot(pc.uSpotColor.rgb, vec3(1.0)) > 0.0 ? 1.0 : 0.0;
        c += t.rgb * vColor.rgb * (1.5 * on + 8.0 * pc.uParams.w);
    }
    FragColor = vec4(c, 1.0);
}
