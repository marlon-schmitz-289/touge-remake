#version 450

// Premultiplied picture times the tint (rgb scaled by its alpha too: premultiplied blending).

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vColor;

layout(set = 0, binding = 0) uniform sampler2D uImage;

layout(location = 0) out vec4 FragColor;

void main()
{
    FragColor = texture(uImage, vUv) * vec4(vColor.rgb * vColor.a, vColor.a);
}
