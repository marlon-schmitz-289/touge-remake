// Value noise for procedural detail (smoke breakup, puddles, rain ripples, droplets).

// Integer hash of the float bits: exact on every GPU. (The old fract(sin(…) · 43758) depended on each driver's sin
// precision at large arguments — the puddle pattern differed between Metal and Mesa's Vulkan driver.)
float hash(vec2 p)
{
    uvec2 q = floatBitsToUint(p);
    uint h = q.x * 1597334677u ^ q.y * 3812015801u;
    h = (h ^ (h >> 16)) * 2246822519u;
    h = (h ^ (h >> 13)) * 3266489917u;
    return float(h >> 8) * (1.0 / 16777216.0);
}

float noise(vec2 p)
{
    vec2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0)), f.x), f.y);
}
