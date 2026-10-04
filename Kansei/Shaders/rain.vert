#version 450

// Falling rain (EffectsRenderer.DrawRain), no vertex buffer: 6 vertices per drop from gl_VertexIndex. Each drop has a
// fixed random spot in a world-space field that falls with the rain and repeats every box size; it is wrapped into
// the box around the camera, so drops keep their place in the world while the camera moves through them. A drop is a
// camera-facing streak from its position back along its velocity relative to the camera over the exposure time
// (motion blur: driving fast stretches drops towards the car). At least ~1.5 px wide; thinner ones fade instead of
// flickering. Faded in front of the camera and towards the box edges.
// pc.uModel carries the rain (GLSL columns = C# rows): [0].xyz rain velocity (m/s), w time (s); [1].xyz camera
// velocity, w exposure (s); [2].xyz box size (m), w pixel size at 1 m distance (m); [3].x opacity.

#include "scene_push.glsl"

layout(location = 0) out vec2 vUv;    // x across −1..1, y 0 head … 1 tail
layout(location = 1) out vec3 vPos;
layout(location = 2) out float vAlpha;

vec3 hash3(uint n)
{
    uvec3 v = uvec3(n) * uvec3(1597334673u, 3812015801u, 2798796415u);
    v = (v.x ^ v.y ^ v.z) * uvec3(1597334673u, 3812015801u, 2798796415u);
    return vec3(v) * (1.0 / 4294967295.0);
}

void main()
{
    int drop = gl_VertexIndex / 6, corner = gl_VertexIndex % 6;
    vec3 rainVel = pc.uModel[0].xyz, box = pc.uModel[2].xyz;
    vec3 eye = pc.uEye.xyz;
    vec3 lo = eye - 0.5 * box;
    vec3 field = hash3(uint(drop)) * box + rainVel * pc.uModel[0].w;
    vec3 head = lo + mod(field - lo, box);
    vec3 tail = head - (rainVel - pc.uModel[1].xyz) * pc.uModel[1].w;

    vec3 mid = 0.5 * (head + tail);
    float dist = length(mid - eye);
    vec3 side = cross(head - tail, mid - eye);
    side = dot(side, side) > 1e-10 ? normalize(side) : vec3(1.0, 0.0, 0.0);
    float width = max(0.002, dist * pc.uModel[2].w * 0.75); // half width
    float edge = 1.0 - smoothstep(0.3, 0.5, max(abs(head.x - eye.x) / box.x, abs(head.z - eye.z) / box.z));
    // a motion-blurred drop spreads the same light over a longer streak
    float spread = clamp(0.3 / max(length(head - tail), 1e-3), 0.15, 1.0);
    vAlpha = pc.uModel[3].x * min(0.002 / width, 1.0) * spread * smoothstep(0.6, 2.0, dist) * edge;
    if (vAlpha <= 0.002)
    {
        // rain.frag would discard every fragment (too close or at the box edge): no area, nothing rasterised
        gl_Position = vec4(2.0, 2.0, 0.5, 1.0);
        vUv = vec2(0.0);
        vPos = head;
        return;
    }

    bool atTail = corner == 2 || corner == 4 || corner == 5;
    float across = corner == 0 || corner == 3 || corner == 5 ? -1.0 : 1.0;
    vec3 p = (atTail ? tail : head) + side * (width * across);
    vUv = vec2(across, atTail ? 1.0 : 0.0);
    vPos = p;
    gl_Position = pc.uMvp * vec4(p, 1.0);
}
