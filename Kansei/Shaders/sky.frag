#version 450

// Analytic sky behind the course's sky mesh: horizon → zenith gradient, darker below the horizon, sun disk + glow (HDR).

layout(push_constant) uniform Push {
    mat4 uInvViewProj; // inverse of (view rotation × projection): clip → camera-centred world direction
    vec4 uZenith;      // rgb, w = 1 / target width
    vec4 uHorizon;     // rgb, w = 1 / target height
    vec4 uSun;         // xyz towards the sun, w = cos of the disk radius
    vec4 uSunColor;    // rgb (0 = no sun), w = clip-space y per gl_FragCoord.y (+1 Vulkan/GL, -1 Metal)
} pc;

layout(location = 0) out vec4 FragColor;

void main()
{
    vec2 ndc = gl_FragCoord.xy * vec2(pc.uZenith.w, pc.uHorizon.w) * 2.0 - 1.0;
    ndc.y *= pc.uSunColor.w;
    vec4 p = pc.uInvViewProj * vec4(ndc, 1.0, 1.0); // reversed-Z: depth 1 = near plane
    vec3 d = normalize(p.xyz / p.w);

    vec3 c = mix(pc.uHorizon.rgb, pc.uZenith.rgb, pow(max(d.y, 0.0), 0.45));
    c = mix(c, pc.uHorizon.rgb * 0.55, smoothstep(0.0, -0.25, d.y));

    float s = max(dot(d, pc.uSun.xyz), 0.0);
    float disk = smoothstep(pc.uSun.w, pc.uSun.w + 0.0004, s);
    c += pc.uSunColor.rgb * (disk + 0.02 * pow(s, 64.0) + 0.004 * pow(s, 6.0));
    FragColor = vec4(c, 1.0);
}
