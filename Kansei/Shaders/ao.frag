#version 450

// Half-resolution ambient occlusion (PostProcess) from the resolved scene depth: reversed-Z with an infinite far plane,
// so the view distance is near / depth. Per pixel: view-space position of the top-left full-res texel of its 2×2
// block, normal from the smaller of the left/right and up/down depth differences (no smearing across silhouettes),
// then 12 taps on a spiral within `radius` metres (Alchemy AO: occluders above the tangent plane, weighted by
// 1 / distance²). The spiral turns per pixel by a 4×4 Bayer pattern that ao_blur.frag's 4×4 box averages out again
// (fixed in screen space: no temporal noise). Out: r = visibility 0..1, g = view distance (for the bilateral blur/upsample).

layout(set = 0, binding = 0) uniform sampler2D uDepth;

layout(push_constant) uniform Push {
    vec4 uView; // x/y = tan of the half field of view, z = near plane (m), w = radius (m)
    vec4 uSize; // xy = 1 / full-res size, z = strength
} pc;

layout(location = 0) out vec4 FragColor;

const int Taps = 12;

vec3 viewPos(vec2 uv)
{
    float z = pc.uView.z / max(textureLod(uDepth, uv, 0.0).r, 1e-7);
    return vec3((uv * 2.0 - 1.0) * pc.uView.xy, 1.0) * z;
}

float bayer4(ivec2 p)
{
    int i = (p.x & 3) | ((p.y & 3) << 2);
    const float m[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
    return (m[i] + 0.5) / 16.0;
}

void main()
{
    vec2 texel = pc.uSize.xy;
    vec2 uv = (floor(gl_FragCoord.xy) * 2.0 + 0.5) * texel;
    vec3 p = viewPos(uv);
    if (p.z > 2000.0)
    {
        FragColor = vec4(1.0, min(p.z, 60000.0), 0.0, 0.0);
        return;
    }
    vec3 l = viewPos(uv - vec2(texel.x, 0.0)), r = viewPos(uv + vec2(texel.x, 0.0));
    vec3 u = viewPos(uv - vec2(0.0, texel.y)), d = viewPos(uv + vec2(0.0, texel.y));
    vec3 dx = abs(r.z - p.z) < abs(p.z - l.z) ? r - p : p - l;
    vec3 dy = abs(d.z - p.z) < abs(p.z - u.z) ? d - p : p - u;
    vec3 n = normalize(cross(dx, dy));
    if (dot(n, p) > 0.0) n = -n;

    float radius = pc.uView.w;
    vec2 reach = radius / (p.z * 2.0 * pc.uView.xy); // radius in uv
    reach *= min(1.0, 0.12 / reach.y);                // near the camera: at most 12 % of the screen
    float sum = 0.0;
    float turn = bayer4(ivec2(gl_FragCoord.xy)) * 6.2832;
    for (int i = 0; i < Taps; i++)
    {
        float a = float(i) * 2.39996 + turn;
        vec2 o = vec2(cos(a), sin(a)) * sqrt((float(i) + 0.5) / float(Taps));
        vec3 v = viewPos(uv + o * reach) - p;
        float vv = dot(v, v);
        sum += max(dot(v, n) - 0.002 * p.z, 0.0) / (vv + 0.02) * max(1.0 - vv / (radius * radius * 2.0), 0.0);
    }
    float ao = max(1.0 - pc.uSize.z * sum / float(Taps), 0.0);
    FragColor = vec4(ao, p.z, 0.0, 0.0);
}
