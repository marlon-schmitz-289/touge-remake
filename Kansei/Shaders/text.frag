#version 450

// Signed distance field text (SdfFont): screen-pixel distance to the glyph edge from the R8 atlas, 1-px AA edge
// widened by the softness (shadows), grown by the weight.

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vColor;
layout(location = 2) in vec3 vShape;

layout(set = 0, binding = 0) uniform sampler2D uAtlas;

layout(location = 0) out vec4 FragColor;

void main()
{
    float d = (texture(uAtlas, vUv).r - 0.5) * vShape.x + vShape.y;
    float a = clamp(d / (1.0 + vShape.z) + 0.5, 0.0, 1.0);
    if (a <= 0.0) discard;
    FragColor = vec4(vColor.rgb, vColor.a * a);
}
