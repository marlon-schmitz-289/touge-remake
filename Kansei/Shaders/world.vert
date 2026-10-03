#version 450

// Static world geometry (track, prelit). Vertex colour carries the baked lighting (1.0 = neutral), authored in
// gamma space: tex × colour on the PS2 equals tex_lin × colour^2.2 in linear light.

layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec4 aColor;

layout(push_constant) uniform Push {
    mat4 uMvp;
    vec4 uFog; // rgb = fog colour (linear), a = 1 / fog distance (0 = no fog)
    vec4 uEye; // xyz = camera position, w = 1: sky mesh (vertex alpha ignored)
} pc;

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vColor;
layout(location = 2) out float vFog;

void main()
{
    gl_Position = pc.uMvp * vec4(aPos, 1.0);
    vUv = aUv;
    vColor = vec4(pow(aColor.rgb, vec3(2.2)), aColor.a);
    vFog = clamp(length(aPos - pc.uEye.xyz) * pc.uFog.a, 0.0, 1.0);
}
