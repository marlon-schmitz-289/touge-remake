// Sun shadows (cascades side by side in one atlas, see ShadowMap.cs), the dynamic lights (headlights + street
// lights), their wet-road reflections and their glow in the fog. Needs scene_push.glsl and fog.glsl.
// Bindings are unique across sets: the binding number is also the Metal texture/sampler slot.

layout(set = 1, binding = 1) uniform sampler2DShadow uShadowMap;

float shadowAt(vec3 p, vec3 n)
{
    if (pc.uParams.x < 0.5) return 1.0;
    for (int c = 0; c < 3; c++)
    {
        vec3 s = (pc.uShadow[c] * vec4(p + n * (1.5 * pc.uShadowTexel[c]), 1.0)).xyz;
        if (any(lessThan(s.xy, vec2(0.01))) || any(greaterThan(s.xy, vec2(0.99))) || s.z >= 1.0) continue;
        vec2 uv = vec2((s.x + float(c)) / 3.0, s.y);
        vec2 t = vec2(pc.uShadowTexel.w / 3.0, pc.uShadowTexel.w);
        float sum = 0.0;
        for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
                sum += texture(uShadowMap, vec3(uv + vec2(float(x), float(y)) * t, s.z));
        float lit = sum / 9.0;
        if (c == 2) lit = mix(lit, 1.0, smoothstep(0.8, 0.98, 2.0 * max(abs(s.x - 0.5), abs(s.y - 0.5))));
        return lit;
    }
    return 1.0;
}

// Tint of the shade/ambient for normal n: ground bounce below, sky above (1 = neutral).
vec3 hemisphere(vec3 n)
{
    return mix(pc.uShadeGround.rgb, pc.uShadeSky.rgb, n.y * 0.5 + 0.5);
}

// Ambient occlusion of the ground under the player car (uModel, car space: +y up, origin on the ground): 1 = open,
// darkest under the middle of the footprint, fading out ~0.8 m beyond the body and above the wheel tops.
float contactShadow(vec3 p)
{
    if (pc.uShadeSky.w <= 0.0) return 1.0;
    vec3 q = transpose(mat3(pc.uModel)) * (p - pc.uModel[3].xyz);
    float off = length(max(abs(q.xz) - vec2(0.45, 1.5), 0.0));
    return 1.0 - pc.uShadeSky.w * smoothstep(0.85, 0.0, off) * smoothstep(0.8, 0.0, q.y) * step(-0.5, q.y);
}

// Headlight i's intensity (relative to its peak) in direction `dir` (unit, lamp → point), in tangents across (x, + = right)
// and up (y) of the beam axis. Low beam (uSpotPos.w = 0) with the Japanese cut-off: flat 0.7° below the horizon on the
// oncoming (right) side, rising to 0.3° above it on the kerb side; the light piles up right under the cut-off and thins out
// towards the car (∝ 1 / depth³), so a flat road gets about evenly lit from a few metres to ~60 m instead of a blown-out
// patch in front of the bumper. High beam (1): cut-off 1.7° above the horizon with a soft top edge, wider, 2.5× brighter.
// A dim wide spill lights the verges.
float beam(int i, vec3 dir)
{
    vec3 axis = pc.uSpotDir[i].xyz;
    vec3 right = normalize(cross(axis, vec3(0.0, 1.0, 0.0)));
    vec3 up = cross(right, axis);
    float ahead = dot(dir, axis);
    if (ahead <= 0.05) return 0.0;
    float x = dot(dir, right) / ahead, y = dot(dir, up) / ahead;
    float high = pc.uSpotPos[i].w;
    float cut = mix(-0.012 + 0.017 * smoothstep(0.0, 0.08, -x), 0.03, high);
    float soft = mix(0.005, 0.03, high);
    float below = cut - y;
    float vertical = smoothstep(-soft, soft, below) * pow(0.02 / (0.02 + max(below, 0.0)), 2.75);
    float sx = mix(0.28, 0.36, high), wide = mix(0.9, 1.2, high);
    float across = exp(-x * x / (sx * sx)) + 0.3 * exp(-x * x / (wide * wide));
    return vertical * across * mix(1.0, 2.5, high);
}

// Irradiance cap: what the local lights throw onto a surface saturates softly towards LightCap — per light and in sum —
// so a wall in front of the bumper, several lamps overlapping or a lamp right next to a surface never blow out (no
// runaway before tonemapping). Applied after N·L: a grazing road keeps its full share.
const float LightCap = 1.0;

vec3 capped(vec3 e)
{
    float l = dot(e, vec3(0.2126, 0.7152, 0.0722));
    return l > 1e-6 ? e * (LightCap * (1.0 - exp(-l / LightCap)) / l) : e;
}

const int Lights = 8;
const float TailRange = 8.0;
const float FogLightCap = 0.3; // most irradiance a lamp lends a surface in dense fog

// Point light j (0–3 street lights, 4–5 rear lamps): position + radius, colour. A rear lamp lights surfaces by the
// night share (uTailColor.w) but stays `mirror`ed at full strength: a brake light streaks on a wet road by day too.
vec4 pointLight(int j, bool mirror, out vec3 colour)
{
    colour = j < 4 ? pc.uPointColor.rgb : pc.uTailColor.rgb * (mirror ? 1.0 : pc.uTailColor.w);
    return j < 4 ? pc.uPointPos[j] : vec4(pc.uTailPos[j - 4].xyz, TailRange);
}

// Light i (0–1 headlights, 2–7 point lights) arriving at p: irradiance on a surface facing the lamp (inverse square
// windowed to the range, beam shape; not capped yet) and the unit direction l towards the lamp. Zero when off or out of range.
vec3 lightIn(int i, vec3 p, bool mirror, out vec3 l)
{
    l = vec3(0.0, 1.0, 0.0);
    bool spot = i < 2;
    vec3 colour = pc.uSpotColor.rgb;
    vec4 lp = spot ? pc.uSpotPos[i] : pointLight(i - 2, mirror, colour);
    float range = spot ? pc.uSpotColor.w : lp.w;
    if (range <= 0.0 || dot(colour, colour) == 0.0) return vec3(0.0);
    vec3 d = lp.xyz - p;
    float d2 = dot(d, d);
    if (d2 >= range * range) return vec3(0.0);
    l = d * inversesqrt(max(d2, 1e-4));
    float w = 1.0 - (d2 * d2) / (range * range * range * range);
    float att = w * w / (d2 + 1.0);
    if (spot) att *= beam(i, -l);
    vec3 e = colour * att;
    if (pc.uTailPos[0].w <= 0.0) return e;
    // dense fog swallows the light on its way and scatters the rest: soft cap, so a beam reads as a veil, not a white spot
    e *= exp(-pc.uTailPos[0].w * sqrt(d2));
    return e / (1.0 + max(e.r, max(e.g, e.b)) / FogLightCap);
}

// Diffuse irradiance of all dynamic lights (capped in sum); adds an energy-normalised Blinn-Phong highlight (exponent
// `shininess`) to `spec`.
vec3 dynamicLight(vec3 p, vec3 n, vec3 v, float shininess, inout vec3 spec)
{
    vec3 sum = vec3(0.0), hl = vec3(0.0);
    float norm = (shininess + 8.0) / 25.13;
    for (int i = 0; i < Lights; i++)
    {
        vec3 l;
        vec3 e = capped(lightIn(i, p, false, l) * max(dot(n, l), 0.0));
        sum += e;
        hl += e * (norm * pow(max(dot(n, normalize(l + v)), 0.0), shininess));
    }
    vec3 total = capped(sum);
    spec += hl * (dot(total, vec3(1.0)) / max(dot(sum, vec3(1.0)), 1e-6));
    return total;
}

// Irradiance from all dynamic lights regardless of orientation (rain drops, spray).
vec3 lightAt(vec3 p)
{
    vec3 sum = vec3(0.0), l;
    for (int i = 0; i < Lights; i++) sum += capped(lightIn(i, p, false, l));
    return capped(sum);
}

// Lamps mirrored in a wet surface: a lobe around the mirror direction r, `across` wide sideways and `along` wide in
// the plane of reflection — on wet asphalt (along ≫ across) lamps stretch into the long streaks of a rainy night road,
// a puddle (both small) mirrors them sharply. Normalised so the lobe's solid angle carries the light's energy × Fresnel.
vec3 wetLights(vec3 p, vec3 n, vec3 v, float across, float along)
{
    vec3 r = reflect(-v, n);
    vec3 side = cross(r, n);
    side = dot(side, side) > 1e-6 ? normalize(side) : vec3(1.0, 0.0, 0.0);
    vec3 up = cross(side, r);
    vec3 sum = vec3(0.0);
    for (int i = 0; i < Lights; i++)
    {
        vec3 l;
        vec3 e = capped(lightIn(i, p, true, l));
        if (dot(l, r) <= 0.0) continue;
        float x = dot(l, side) / across, y = dot(l, up) / along;
        sum += e * exp(-(x * x + y * y));
    }
    float fresnel = 0.02 + 0.98 * pow(1.0 - max(dot(n, v), 0.0), 5.0);
    return sum * (fresnel / (3.14159 * across * along));
}

// Light scattered towards the camera by the fog between the eye and p: the street lights' glow, point sources,
// ∫ I / (h² + t²) dt along the ray solved exactly. Headlight beams scatter only in dense fog (σ > 0, 8 steps over the
// first 60 m); in thin haze they are left to the bloom on their lenses, there the cones read as solid. Rear lamps: bloom.
vec3 lightGlow(vec3 p)
{
    if (pc.uFog.a <= 0.0) return vec3(0.0);
    vec3 o = pc.uEye.xyz;
    vec3 d = p - o;
    float len = length(d);
    vec3 rd = d / max(len, 1e-4);
    vec3 sum = vec3(0.0);
    float sigma = pc.uTailPos[0].w; // fog extinction (0 = thin haze): dense fog swallows the lamps' light on its way
    for (int j = 0; j < 4; j++)
    {
        vec3 colour;
        vec4 lp = pointLight(j, false, colour);
        if (lp.w <= 0.0 || dot(colour, colour) == 0.0) continue;
        vec3 ol = lp.xyz - o;
        float tc = dot(ol, rd);
        float h = sqrt(max(dot(ol, ol) - tc * tc, 0.0)) + 0.1;
        float glow = (atan((len - tc) / h) + atan(tc / h)) / h;
        sum += colour * (sigma > 0.0 ? glow * exp(-sigma * length(ol)) : glow);
    }
    if (sigma > 0.0 && dot(pc.uSpotColor.rgb, pc.uSpotColor.rgb) > 0.0)
    {
        float reach = min(len, 60.0), step = reach / 8.0;
        // lamp -> sample -> eye is about twice the sample's distance (the lamps sit near the camera): exp(-2 sigma t), stepped
        float fade = exp(-sigma * step), decay = fade * fade;
        for (int k = 0; k < 8; k++, fade *= decay)
        {
            vec3 q = o + rd * ((float(k) + 0.5) * step);
            for (int i = 0; i < 2; i++)
            {
                vec3 dl = q - pc.uSpotPos[i].xyz;
                float d2 = dot(dl, dl);
                sum += pc.uSpotColor.rgb * (beam(i, dl * inversesqrt(max(d2, 1e-4))) * step * fade / (d2 + 1.0));
            }
        }
    }
    return sum * pc.uFog.a;
}

// Fog over a lit surface colour at p (fog.glsl + light glow).
vec3 applyFog(vec3 c, vec3 p)
{
    return mix(c, fogColour(normalize(p - pc.uEye.xyz)), fogAmount(p)) + lightGlow(p);
}
