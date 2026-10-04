#version 450

// 4×4 depth-aware box over the half-res AO (ao.frag): exactly one tap per cell of its 4×4 rotation pattern, so the
// pattern averages out; taps more than ~6 % away in view distance are dropped (no AO bleeding across silhouettes).
// Out: r = visibility, g = view distance (unchanged).

layout(set = 0, binding = 0) uniform sampler2D uAo;

layout(location = 0) out vec4 FragColor;

void main()
{
    ivec2 p = ivec2(gl_FragCoord.xy);
    ivec2 last = textureSize(uAo, 0) - 1;
    float z = texelFetch(uAo, p, 0).g;
    float sum = 0.0, weight = 0.0;
    for (int y = -2; y < 2; y++)
        for (int x = -2; x < 2; x++)
        {
            vec2 s = texelFetch(uAo, clamp(p + ivec2(x, y), ivec2(0), last), 0).rg;
            float w = max(1.0 - abs(s.y - z) / (0.06 * z + 0.05), 0.0);
            sum += s.x * w;
            weight += w;
        }
    FragColor = vec4(weight > 0.0 ? sum / weight : 1.0, z, 0.0, 0.0);
}
