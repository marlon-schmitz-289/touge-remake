#version 450

// Car parts, lit. Lighting vectors come in model space, so normals need no transform.

layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUv;
layout(location = 3) in vec4 aColor; // rgb material colour, a gloss

layout(push_constant) uniform Push {
    mat4 uMvp;
    vec4 uSun; // xyz towards the sun (model space)
    vec4 uEye; // xyz camera (model space)
    vec4 uUp;  // xyz world up (model space)
} pc;

layout(location = 0) out vec3 vPos;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec2 vUv;
layout(location = 3) out vec4 vColor;

void main()
{
    gl_Position = pc.uMvp * vec4(aPos, 1.0);
    vPos = aPos;
    vNormal = aNormal;
    vUv = aUv;
    vColor = aColor;
}
