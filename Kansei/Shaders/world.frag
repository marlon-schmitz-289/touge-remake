#version 450

// texture × vertex colour, alpha test for foliage/fences, linear distance fog.

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
    vec4 c = texture(uTexture, vUv) * vColor;
    if (c.a < 0.3) discard;
    FragColor = vec4(mix(c.rgb, pc.uFog.rgb, vFog * vFog), 1.0);
}
