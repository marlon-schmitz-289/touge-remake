#version 450

// Bloom upsample: 9-tap tent filter of the smaller level, added (blend One/One) onto the next larger one.

layout(set = 0, binding = 0) uniform sampler2D uSource;

layout(push_constant) uniform Push {
    vec4 uSrc; // xy = 1 / source size
    vec4 uDst; // xy = 1 / target size
} pc;

layout(location = 0) out vec4 FragColor;

void main()
{
    vec2 uv = gl_FragCoord.xy * pc.uDst.xy;
    vec2 d = pc.uSrc.xy;
    vec3 c = 4.0 * texture(uSource, uv).rgb
           + 2.0 * (texture(uSource, uv + vec2(d.x, 0.0)).rgb + texture(uSource, uv - vec2(d.x, 0.0)).rgb
                  + texture(uSource, uv + vec2(0.0, d.y)).rgb + texture(uSource, uv - vec2(0.0, d.y)).rgb)
           + texture(uSource, uv + d).rgb + texture(uSource, uv - d).rgb
           + texture(uSource, uv + vec2(d.x, -d.y)).rgb + texture(uSource, uv + vec2(-d.x, d.y)).rgb;
    FragColor = vec4(c / 16.0, 1.0);
}
