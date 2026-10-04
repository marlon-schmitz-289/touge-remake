#version 450

// HDR scene + bloom → exposure, colour grade (tint, saturation, contrast), ACES filmic (Narkowicz fit), vignette, gamma 2.2
// (the swapchain is UNORM, not sRGB).

layout(set = 0, binding = 0) uniform sampler2D uScene;
layout(set = 0, binding = 1) uniform sampler2D uBloom;

layout(push_constant) uniform Push {
    vec4 uA;    // xy = 1 / target size, z = exposure, w = bloom strength
    vec4 uTint; // rgb multiplied before tonemapping, a = saturation
    vec4 uB;    // x = vignette strength, y = width / height, z = contrast (power around mid grey, 1 = none)
} pc;

layout(location = 0) out vec4 FragColor;

vec3 aces(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

void main()
{
    vec2 uv = gl_FragCoord.xy * pc.uA.xy;
    vec3 c = texture(uScene, uv).rgb;
    if (pc.uA.w > 0.0) c += texture(uBloom, uv).rgb * pc.uA.w; // bloom off: chain is stale/uninitialised
    c *= pc.uA.z * pc.uTint.rgb;
    float l = dot(c, vec3(0.2126, 0.7152, 0.0722));
    c = max(mix(vec3(l), c, pc.uTint.a), 0.0);
    c = 0.18 * pow(c / 0.18, vec3(pc.uB.z));
    c = aces(c);
    vec2 v = (uv - 0.5) * vec2(pc.uB.y, 1.0);
    c *= mix(1.0, smoothstep(1.1, 0.35, length(v)), pc.uB.x);
    FragColor = vec4(pow(c, vec3(1.0 / 2.2)), 1.0);
}
