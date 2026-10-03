#version 450

// Depth only; cut-out texels (foliage, fences) cast no shadow.

layout(location = 0) in vec2 vUv;

layout(set = 0, binding = 0) uniform sampler2D uTexture;

layout(push_constant) uniform Push {
    mat4 uMvp;
    vec4 uCutoff;
} pc;

void main()
{
    if (texture(uTexture, vUv).a < pc.uCutoff.x) discard;
}
