#version 450

// Coverage from the distance to the shape's centre line/point (vSdf, pixels) against its half width: 1-px AA edge.
// Filled triangles have vSdf = 0. Cut to the clip circle with the same 1-px edge.

layout(location = 0) in vec2 vSdf;
layout(location = 1) in float vRadius;
layout(location = 2) in vec4 vColor;
layout(location = 3) in vec2 vPx;

layout(push_constant) uniform Push {
    vec4 uScale;
    vec4 uClip;
} pc;

layout(location = 0) out vec4 FragColor;

void main()
{
    float a = clamp(vRadius - length(vSdf) + 0.5, 0.0, 1.0) * clamp(pc.uClip.z - length(vPx - pc.uClip.xy) + 0.5, 0.0, 1.0);
    if (a <= 0.0) discard;
    FragColor = vec4(vColor.rgb, vColor.a * a);
}
