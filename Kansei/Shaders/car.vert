#version 450

// Car parts (rigid): lit in world space, normals rotate with uModel.

layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUv;
layout(location = 3) in vec4 aColor; // rgb material colour, a = kind (0 matte, 0.5 glass, 1 paint, 2 rear lamp; +4 in the decal pass)

#include "scene_push.glsl"

layout(location = 0) out vec3 vPos;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec2 vUv;
layout(location = 3) out vec4 vColor;

void main()
{
    gl_Position = pc.uMvp * vec4(aPos, 1.0);
    // overlay layer (MeshBatch.Layer): moved uPointColor.w metres towards the camera along the view ray, so it wins
    // against coplanar earlier geometry like the PS2's draw order does. Reversed-Z: depth = near / w.
    float pull = pc.uPointColor.w;
    if (pull > 0.0 && gl_Position.w > 0.0) gl_Position.z *= gl_Position.w / max(gl_Position.w - pull, 0.5 * gl_Position.w);
    vPos = (pc.uModel * vec4(aPos, 1.0)).xyz;
    vNormal = mat3(pc.uModel) * aNormal;
    vUv = aUv;
    vColor = vec4(pow(aColor.rgb, vec3(2.2)), aColor.a); // gamma-space material colour → linear
}
