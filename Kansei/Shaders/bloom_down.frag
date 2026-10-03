#version 450

// Bloom downsample: 4 bilinear taps one source texel off-centre (4×4 box). The first level also applies the soft threshold.

layout(set = 0, binding = 0) uniform sampler2D uSource;

layout(push_constant) uniform Push {
    vec4 uSrc; // xy = 1 / source size, z = threshold (0 = none), w = soft knee
    vec4 uDst; // xy = 1 / target size
} pc;

layout(location = 0) out vec4 FragColor;

void main()
{
    vec2 uv = gl_FragCoord.xy * pc.uDst.xy;
    vec2 d = pc.uSrc.xy;
    vec3 c = 0.25 * (texture(uSource, uv + vec2(-d.x, -d.y)).rgb + texture(uSource, uv + vec2(d.x, -d.y)).rgb
                   + texture(uSource, uv + vec2(-d.x, d.y)).rgb + texture(uSource, uv + vec2(d.x, d.y)).rgb);
    if (pc.uSrc.z > 0.0)
    {
        c = min(c, vec3(64.0)); // fp16 fireflies
        float br = max(c.r, max(c.g, c.b));
        float k = pc.uSrc.w;
        float soft = clamp(br - pc.uSrc.z + k, 0.0, 2.0 * k);
        soft = soft * soft / (4.0 * k + 1e-5);
        c *= max(soft, br - pc.uSrc.z) / max(br, 1e-5);
    }
    FragColor = vec4(c, 1.0);
}
