#version 450

// Pictures under the HUD overlay (SpriteRenderer.cs): positions already in clip space, uv, sRGB tint (alpha fades).

layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec4 aColor;

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vColor;

void main()
{
    gl_Position = vec4(aPos, 0.0, 1.0);
    vUv = aUv;
    vColor = aColor;
}
