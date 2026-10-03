#version 450

// texture (sRGB, decoded by the sampler) × vertex colour, alpha test for foliage/fences, distance fog, all linear.
// The alpha is sharpened around the 0.3 cutoff: with alpha-to-coverage (MSAA) cut-out edges get antialiased,
// without it this is the plain alpha test.

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vColor;
layout(location = 2) in float vFog;

layout(set = 0, binding = 0) uniform sampler2D uTexture;

layout(push_constant) uniform Push {
    mat4 uMvp;
    vec4 uFog;
    vec4 uEye;
} pc;

layout(location = 0) out vec4 FragColor;

void main()
{
    vec4 t = texture(uTexture, vUv);
    float a = pc.uEye.w > 0.5 ? t.a : t.a * vColor.a;
    a = clamp((a - 0.3) / max(fwidth(a), 1e-4) + 0.5, 0.0, 1.0);
    if (a <= 0.0) discard;
    FragColor = vec4(mix(t.rgb * vColor.rgb, pc.uFog.rgb, vFog * vFog), a);
}
