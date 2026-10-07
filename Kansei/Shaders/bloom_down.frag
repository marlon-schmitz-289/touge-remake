#version 450

// Bloom downsample: 4 bilinear taps one source texel off-centre (4×4 box). The first level also applies the soft threshold
// and weights its taps by 1 / (1 + luma) (Karis average): a tiny very bright detail (a lit grass blade, a lamp, a glint)
// no longer turns into a big halo that pulses as it crosses pixels while driving.

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
    vec3 t0 = texture(uSource, uv + vec2(-d.x, -d.y)).rgb, t1 = texture(uSource, uv + vec2(d.x, -d.y)).rgb;
    vec3 t2 = texture(uSource, uv + vec2(-d.x, d.y)).rgb, t3 = texture(uSource, uv + vec2(d.x, d.y)).rgb;
    vec3 c = 0.25 * (t0 + t1 + t2 + t3);
    if (pc.uSrc.z > 0.0)
    {
        const vec3 l = vec3(0.2126, 0.7152, 0.0722);
        vec4 w = 1.0 / (1.0 + vec4(dot(t0, l), dot(t1, l), dot(t2, l), dot(t3, l)));
        c = (t0 * w.x + t1 * w.y + t2 * w.z + t3 * w.w) / (w.x + w.y + w.z + w.w);
        c = min(c, vec3(64.0)); // fp16 fireflies
        float br = max(c.r, max(c.g, c.b));
        float k = pc.uSrc.w;
        float soft = clamp(br - pc.uSrc.z + k, 0.0, 2.0 * k);
        soft = soft * soft / (4.0 * k + 1e-5);
        c *= max(soft, br - pc.uSrc.z) / max(br, 1e-5);
    }
    FragColor = vec4(c, 1.0);
}
