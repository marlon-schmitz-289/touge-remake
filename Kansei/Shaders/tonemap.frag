#version 450

// HDR scene + bloom → ambient occlusion and wet reflections composited in, exposure, colour grade (tint, saturation,
// contrast), ACES filmic (Narkowicz fit), vignette, gamma 2.2 (the swapchain is UNORM, not sRGB), then ±1 LSB
// triangular dither so dark gradients (night sky, fog) do not band in 8 bits.
// AO (ao.frag → ao_blur.frag, half res) is upsampled bilaterally (bilinear weights × view-distance match against the
// full-res depth) and only darkens the scene's ambient share (gbuffer r, written by the scene shaders): the sun and the
// lamps stay. SSR (ssr.frag, half res, premultiplied) replaces the scene by the reflection where the gbuffer's
// reflection weight (g) says so.

layout(set = 0, binding = 0) uniform sampler2D uScene;
layout(set = 0, binding = 1) uniform sampler2D uBloom;
layout(set = 0, binding = 2) uniform sampler2D uAo;
layout(set = 0, binding = 3) uniform sampler2D uSsr;
layout(set = 0, binding = 4) uniform sampler2D uGbuf;
layout(set = 0, binding = 5) uniform sampler2D uDepth;

layout(push_constant) uniform Push {
    vec4 uA;    // xy = 1 / target size, z = exposure, w = bloom strength
    vec4 uTint; // rgb multiplied before tonemapping, a = saturation
    vec4 uB;    // x = vignette strength, y = width / height, z = contrast (power around mid grey, 1 = none)
    vec4 uC;    // x = AO on, y = near plane (m), z = SSR on, w = dither amplitude (1/255 units)
    vec4 uD;    // xy = origin of the output rectangle in the target (split screen), pixels
} pc;

layout(location = 0) out vec4 FragColor;

vec3 aces(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

// interleaved gradient noise (Jimenez): stable at any pixel coordinate, well spread
float ign(vec2 p) { return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715)))); }

float upsampleAo(vec2 frag)
{
    float z = min(pc.uC.y / max(texelFetch(uDepth, ivec2(frag), 0).r, 1e-7), 60000.0); // as stored by ao.frag
    vec2 hp = (frag - 0.5) * 0.5; // half-res texel i sits on full-res texel 2i (ao.frag)
    ivec2 base = ivec2(floor(hp));
    vec2 f = hp - vec2(base);
    ivec2 last = textureSize(uAo, 0) - 1;
    float sum = 0.0, weight = 1e-4;
    for (int i = 0; i < 4; i++)
    {
        ivec2 o = ivec2(i & 1, i >> 1);
        vec2 s = texelFetch(uAo, clamp(base + o, ivec2(0), last), 0).rg;
        float w = (o.x == 1 ? f.x : 1.0 - f.x) * (o.y == 1 ? f.y : 1.0 - f.y) * max(1.0 - abs(s.y - z) / (0.08 * z + 0.05), 0.001);
        sum += s.x * w;
        weight += w;
    }
    return sum / weight;
}

void main()
{
    vec2 uv = (gl_FragCoord.xy - pc.uD.xy) * pc.uA.xy;
    vec3 c = texture(uScene, uv).rgb;
    if (pc.uC.x > 0.0 || pc.uC.z > 0.0)
    {
        vec2 frag = uv * vec2(textureSize(uGbuf, 0)); // scene pixel (render scale: the scene may be smaller/larger than the output)
        vec4 g = texelFetch(uGbuf, ivec2(frag), 0);
        if (pc.uC.x > 0.0 && g.r > 0.0) c *= 1.0 - g.r * (1.0 - upsampleAo(frag));
        if (pc.uC.z > 0.0 && g.g > 0.0)
        {
            vec4 s = texture(uSsr, uv);
            float k = min(1.5 * g.g, 1.0); // objects in the wet road a bit stronger than the Fresnel says (no roughness model)
            c = c * (1.0 - k * s.a) + k * s.rgb;
        }
    }
    if (pc.uA.w > 0.0) c += texture(uBloom, uv).rgb * pc.uA.w; // bloom off: chain is stale/uninitialised
    c *= pc.uA.z * pc.uTint.rgb;
    float l = dot(c, vec3(0.2126, 0.7152, 0.0722));
    c = max(mix(vec3(l), c, pc.uTint.a), 0.0);
    c = 0.18 * pow(c / 0.18, vec3(pc.uB.z));
    c = aces(c);
    vec2 v = (uv - 0.5) * vec2(pc.uB.y, 1.0);
    c *= mix(1.0, smoothstep(1.1, 0.35, length(v)), pc.uB.x);
    float dither = (ign(gl_FragCoord.xy) + ign(gl_FragCoord.xy + vec2(47.0, 17.0)) - 1.0) * pc.uC.w / 255.0;
    FragColor = vec4(pow(c, vec3(1.0 / 2.2)) + dither, 1.0);
}
