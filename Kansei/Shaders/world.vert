#version 450

// Static world geometry (track, prelit). Vertex colour carries the baked lighting (1.0 = neutral), authored in
// gamma space: tex × colour on the PS2 equals tex_lin × colour^2.2 in linear light. Normals are computed at load
// (Normals.Smooth). Positions are world space already (uMvp = view × projection).

layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec4 aColor;
layout(location = 3) in vec3 aNormal;

#include "scene_push.glsl"

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vColor;
layout(location = 2) out vec3 vPos;
layout(location = 3) out vec3 vNormal;

void main()
{
    gl_Position = pc.uMvp * vec4(aPos, 1.0);
    vUv = aUv;
    vColor = vec4(pow(aColor.rgb, vec3(2.2)), aColor.a);
    vPos = aPos;
    vNormal = aNormal;
}
