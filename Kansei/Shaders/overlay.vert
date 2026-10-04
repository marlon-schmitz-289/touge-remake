#version 450

// HUD overlay (Overlay.cs, OverlayRenderer.cs): pixel positions, top-left origin; colour sRGB RGBA8 passed through
// (the swapchain is UNORM, the tonemapper applies gamma).

layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aSdf;
layout(location = 2) in float aRadius;
layout(location = 3) in vec4 aColor;

layout(push_constant) uniform Push {
    vec4 uScale; // xy = pixel → clip scale (y negative: clip space Y-up)
    vec4 uClip;  // xy = clip circle centre (pixels), z = radius (huge = no clip)
} pc;

layout(location = 0) out vec2 vSdf;
layout(location = 1) out float vRadius;
layout(location = 2) out vec4 vColor;
layout(location = 3) out vec2 vPx;

void main()
{
    gl_Position = vec4(aPos * pc.uScale.xy + vec2(-1.0, pc.uScale.y < 0.0 ? 1.0 : -1.0), 0.0, 1.0);
    vSdf = aSdf;
    vRadius = aRadius;
    vColor = aColor;
    vPx = aPos;
}
