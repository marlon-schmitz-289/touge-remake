#version 450

// HUD text (Overlay.Text, TextRenderer.cs): positions already in clip space (converted on the CPU, no push constants),
// atlas uv, sRGB colour, distance-field scale/weight/softness in pixels.

layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec4 aColor;
layout(location = 3) in vec3 aShape; // x = pixels per field unit around 0.5, y = weight (px), z = softness (px)

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vColor;
layout(location = 2) out vec3 vShape;

void main()
{
    gl_Position = vec4(aPos, 0.0, 1.0);
    vUv = aUv;
    vColor = aColor;
    vShape = aShape;
}
