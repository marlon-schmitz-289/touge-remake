#version 450

// Shadow casters: position (+ uv for the alpha test) into a cascade tile.

layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUv;

layout(push_constant) uniform Push {
    mat4 uMvp;
    vec4 uCutoff; // x = alpha cutoff
} pc;

layout(location = 0) out vec2 vUv;

void main()
{
    gl_Position = pc.uMvp * vec4(aPos, 1.0);
    vUv = aUv;
}
