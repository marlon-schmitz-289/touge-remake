// Sun shadows (cascades side by side in one atlas, see ShadowMap.cs), the dynamic lights (headlights + street
// lights), their wet-road reflections and their glow in the fog. Needs scene_push.glsl and fog.glsl.
// Bindings are unique across sets: the binding number is also the Metal texture/sampler slot.

layout(set = 1, binding = 1) uniform sampler2DShadow uShadowMap;
layout(set = 1, binding = 11) uniform sampler2DShadow uLocalShadow;

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

// Shadow of a lamp at p (1 = lit): tile 0 the headlights, 1–4 the street lights (LocalShadows.cs, whose projections this
// rebuilds: clip = (k·x, k·y, A·d + B, d) in the lamp's frame). `bias` moves the compared depth towards the lamp (surfaces;
// 0 in the fog). Outside a tile's frustum: lit. 2×2 compare taps by the sampler.
const float HeadK = 0.8390996, HeadNear = 0.5, HeadFar = 90.0;      // 1 / tan(100° / 2)
const float StreetK = 0.3639702, StreetNear = 0.3, StreetFar = 30.0; // 1 / tan(140° / 2)
const float StreetDrop = 0.35;
float localShadow(int t, vec3 p, float bias)
{
    if (pc.uLocal.x < 0.5) return 1.0;
    vec3 o, r, u, f;
    float k, n, fr;
    if (t == 0)
    {
        o = 0.5 * (pc.uSpotPos[0].xyz + pc.uSpotPos[1].xyz);
        f = pc.uSpotDir[0].xyz;
        r = normalize(cross(f, vec3(0.0, 1.0, 0.0)));
        u = cross(r, f);
        k = HeadK; n = HeadNear; fr = HeadFar;
    }
    else
    {
        o = pc.uPointPos[t - 1].xyz - vec3(0.0, StreetDrop, 0.0);
        r = vec3(1.0, 0.0, 0.0); u = vec3(0.0, 0.0, 1.0); f = vec3(0.0, -1.0, 0.0);
        k = StreetK; n = StreetNear; fr = StreetFar;
    }
    vec3 l = p - o;
    float d = dot(l, f);
    if (d <= n || d >= fr) return 1.0;
    vec2 c = vec2(dot(l, r), dot(l, u)) * (k / d);
    if (any(greaterThan(abs(c), vec2(0.99)))) return 1.0;
    float db = max(d - bias, n);
    float z = fr / (fr - n) * (1.0 - n / db);
    vec2 uv = c * vec2(0.5, pc.uLocal.y > 0.5 ? 0.5 : -0.5) + 0.5;
    return texture(uLocalShadow, vec3((uv.x + float(t)) / 5.0, uv.y, z));
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
    float across = exp(-x * x / (sx * sx)) + 0.1 * exp(-x * x / (wide * wide));
    return vertical * across * mix(1.0, 2.5, high);
}

// The beam as the fog shows it: light scattered more than once blurs the cut-off, so the lit mist is a cone with a soft top
// edge (a little light above the cut, falling off over ~5° below) instead of beam()'s bright sheet right under a hard line
// (the road seen through it looked like a glowing layer lying on the asphalt).
float beamVeil(vec3 dir, vec3 axis, vec3 right, vec3 up, float high)
{
    float ahead = dot(dir, axis);
    if (ahead <= 0.05) return 0.0;
    float x = dot(dir, right) / ahead, y = dot(dir, up) / ahead;
    float over = y - mix(-0.01, 0.03, high);
    float vertical = over > 0.0 ? exp(-over * over / 0.004) : pow(0.09 / (0.09 - over), 1.5);
    float sx = mix(0.45, 0.6, high);
    return 0.3 * vertical * exp(-x * x / (sx * sx)) * mix(1.0, 2.0, high); // 0.3: the wider shape scatters more in all
}

// Irradiance cap: what the local lights throw onto a surface saturates softly towards LightCap — per light and in sum —
// so a wall in front of the bumper, several lamps overlapping or a lamp right next to a surface never blow out (no
// runaway before tonemapping). Applied after N·L: a grazing road keeps its full share. A headlight saturates far lower
// (HeadCap, per lamp): rails and verges facing the beam get ~10× the grazing road's light, at LightCap × night exposure they burn
// flat white; at HeadCap they stay lit with their texture while the road keeps its share.
const float LightCap = 1.0, HeadCap = 0.08; // HeadCap per lamp: two lamps ≈ 0.15

vec3 capped(vec3 e, float cap)
{
    float l = dot(e, vec3(0.2126, 0.7152, 0.0722));
    return l > 1e-6 ? e * (cap * (1.0 - exp(-l / cap)) / l) : e;
}

vec3 capped(vec3 e) { return capped(e, LightCap); }

const int Lights = 8;
const float TailRange = 8.0, StreetRange = 22.0;

// A street lamp shines down: its light within ~75° of straight down, nothing above its head (d = unit lamp → point).
float streetCone(vec3 d)
{
    return smoothstep(0.05, 0.4, -d.y);
}

const float FogLightCap = 0.3; // most irradiance a lamp lends a surface in dense fog
const float HeadVeil = 0.5, HeadVeilCap = 0.09; // headlight light scattered by dense fog: share of the street lamps' glow, most it adds

// Point light j (0–3 street lights, 4–5 rear lamps): position + radius, colour. A rear lamp lights surfaces by the
// night share (uTailColor.w) but stays `mirror`ed at full strength: a brake light streaks on a wet road by day too.
vec4 pointLight(int j, bool mirror, out vec3 colour)
{
    colour = j < 4 ? pc.uPointColor.rgb * pc.uPointPos[j].w : pc.uTailColor.rgb * (mirror ? 1.0 : pc.uTailColor.w);
    return j < 4 ? vec4(pc.uPointPos[j].xyz, pc.uPointPos[j].w > 0.0 ? StreetRange : 0.0) : vec4(pc.uTailPos[j - 4].xyz, TailRange);
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
    // lamp shadows (headlights: tile 0, street lights 2–5: tiles 1–4; rear lamps none); the bias grows with the distance like a texel
    if (i < 6) att *= localShadow(spot ? 0 : i - 1, p, 0.05 + 0.004 * sqrt(d2));
    if (i >= 2 && i < 6) att *= smoothstep(0.6, 1.2, sqrt(d2)) * streetCone(-l); // a street lamp shines down and not onto its own fixture
    if (i >= 6) att *= smoothstep(-0.1, 0.4, dot(l, pc.uSpotDir[0].xyz)); // rear lamps shine backwards: not along the car onto its own front
    if (att <= 0.0) return vec3(0.0);
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
        vec3 e = lightIn(i, p, false, l);
        if (e == vec3(0.0)) continue; // out of reach (most lights for most pixels): no specular power either
        e = capped(e * max(dot(n, l), 0.0), i < 2 ? HeadCap : LightCap);
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

// The street lamps' glare, seen along the ray to p (sky: far behind): the lamp's glass is a flat disc facing down (~0.3 m),
// so from below it shows as an ellipse squashed by the view's elevation, edge-on from the side and not at all from above;
// a faint wide halo around it. By the angles between the ray and the lamp — smooth at any distance (the bloom spreads it),
// in place of the original's ragged glow cards (CourseLamps.HideGlowCards). A surface nearer than the lamp hides it.
vec3 lampGlare(vec3 p, bool sky)
{
    vec3 o = pc.uEye.xyz;
    vec3 d = p - o;
    float len = length(d);
    vec3 rd = d / max(len, 1e-4);
    vec3 sum = vec3(0.0);
    for (int j = 0; j < 4; j++)
    {
        vec3 colour;
        vec4 lp = pointLight(j, false, colour);
        if (lp.w <= 0.0 || dot(colour, colour) == 0.0) continue;
        vec3 ol = lp.xyz - o;
        float dist = length(ol);
        if (!sky && len < dist - 1.5) continue; // something well in front of the lamp (not its own housing: that flickered at the edge)
        vec3 ld = ol / dist;
        float below = ld.y;                     // sine of the view's elevation: > 0 = the eye is below the lamp
        if (below <= 0.02) continue;
        vec3 off = rd - ld * dot(rd, ld);       // angular offset from the lamp (radians, small angles)
        vec3 up = normalize(vec3(0.0, 1.0, 0.0) - ld * below);
        float ov = dot(off, up), oh2 = max(dot(off, off) - ov * ov, 0.0);
        float r = 0.3 / max(dist, 0.5);         // angular radius of the glass
        // the disc foreshortened, but never thinner than a pixel (a sub-pixel line flickered while moving): wider and
        // dimmer by as much, so it carries the same light
        float px = length(fwidth(rd));
        float rv0 = r * max(below, 0.08), rv = max(rv0, px), rh = max(r, px);
        float core = exp(-(oh2 / (rh * rh) + ov * ov / (rv * rv))) * (rv0 / rv) * (r / rh);
        float halo = 0.03 * (r * r) / (dot(off, off) + r * r * 4.0);
        sum += colour * (core + halo) * smoothstep(0.02, 0.25, below);
    }
    return sum * 0.3; // colour ≈ 50 × tint at night: a core of ~15, a glare for the bloom
}

// Light scattered towards the camera by the fog between the eye and p (`lamps`: the street lights' glow, `beams`: the headlights'). Thin haze (σ = 0 at the eye): the street lights'
// glow only, point sources, ∫ I / (h² + t²) dt along the ray solved exactly; headlights are left to the bloom on their lenses.
// Dense fog: volumetric — the light scatters in the local density (fogDensity: height and drifting banks, relative to
// the eye's, which the overall strength is tuned for) and is dimmed on its way from the lamp and on to the eye. Street
// lights: the exact integral, scaled by the density where the ray passes the lamp closest. Headlight beams (beamVeil):
// 16 steps over the first 60 m with the optical depth summed from the samples, softly capped (a lit cone in the mist,
// never a white wall; brighter fog by day raises the cap). Both saturate like surface irradiance.
vec3 lightGlow(vec3 p, bool lamps, bool beams)
{
    if (pc.uFog.a <= 0.0) return vec3(0.0);
    vec3 o = pc.uEye.xyz;
    vec3 d = p - o;
    float len = length(d);
    vec3 rd = d / max(len, 1e-4);
    vec3 sum = vec3(0.0);
    float sigma = pc.uTailPos[0].w; // fog extinction at the eye (0 = thin haze)
    for (int j = 0; j < (lamps ? 4 : 0); j++)
    {
        vec3 colour;
        vec4 lp = pointLight(j, false, colour);
        if (lp.w <= 0.0 || dot(colour, colour) == 0.0) continue;
        vec3 ol = lp.xyz - o;
        float tc = dot(ol, rd);
        float h = sqrt(max(dot(ol, ol) - tc * tc, 0.0)) + 0.1;
        float ta = -atan(tc / h), tb = atan((len - tc) / h);
        float glow = (tb - ta) / h; // no shadows in the fog: a handful of taps per ray flickered as edges crossed them
        float wh = sqrt(h * h + 64.0), wide = sigma > 0.0 ? 0.6 * (atan((len - tc) / wh) + atan(tc / wh)) / wh : 0.0;
        float fade = sigma > 0.0 ? exp(-sigma * length(ol)) : 1.0;
        if ((glow + wide) * fade * dot(colour, vec3(0.2126, 0.7152, 0.0722)) * pc.uFog.a < 2e-4) continue; // too faint to see: skip the cone
        // the mist lights up under the lamp, not above: the cone averaged along the ray, 6 points spread like the integrand
        // (uniform in the angle seen from the lamp) — at the closest point alone a ray to the sky (closest above the lamp)
        // got nothing and one ending at a leaf all, so foliage against the sky flickered
        float cone = 0.0;
        for (int k = 0; k < 6; k++)
            cone += streetCone(normalize(rd * (tc + h * tan(mix(ta, tb, (float(k) + 0.5) / 6.0))) - ol + vec3(0.0, -0.3, 0.0)));
        // dense fog scatters the light more than once (`wide`): a soft dome around the lamp (the same integral with the ray's
        // distance to the lamp softened by ~8 m), lighting the sky between lit leaves as well — single scattering alone left a
        // narrow halo, and leaves lit by the lamp stood out against black sky and shimmered at every edge while driving
        glow = (glow + wide) * cone / 6.0 * fade;
        if (sigma > 0.0) glow *= fogDensity(o + rd * clamp(tc, 0.0, len)) / sigma;
        sum += colour * glow;
    }
    vec3 veil = vec3(0.0);
    if (beams && sigma > 0.0 && dot(pc.uSpotColor.rgb, pc.uSpotColor.rgb) > 0.0)
    {
        float reach = min(len, 60.0), step = reach / 16.0, optical = 0.0;
        // both lamps share the axis and sit 1.2 m apart: one source between them, twice as strong (the + 9 below already
        // softens the mist near the bumper); the beam's frame once per pixel
        vec3 lamp = 0.5 * (pc.uSpotPos[0].xyz + pc.uSpotPos[1].xyz), axis = pc.uSpotDir[0].xyz;
        vec3 right = normalize(cross(axis, vec3(0.0, 1.0, 0.0))), up = cross(right, axis);
        float high = pc.uSpotPos[0].w;
        // the banks vary over ~40 m: sampled at the ray's start, middle and end, interpolated in between
        float b0 = fogBanks(o), b1 = fogBanks(o + rd * (0.5 * reach)), b2 = fogBanks(o + rd * reach);
        for (int k = 0; k < 16; k++)
        {
            vec3 q = o + rd * ((float(k) + 0.5) * step);
            vec3 dl = q - lamp;
            float d2 = dot(dl, dl);
            float b = beamVeil(dl * inversesqrt(max(d2, 1e-4)), axis, right, up, high);
            float f = (float(k) + 0.5) / 16.0;
            float s = fogHeightDensity(q) * (f < 0.5 ? mix(b0, b1, 2.0 * f) : mix(b1, b2, 2.0 * f - 1.0));
            optical += s * step * 0.5; // to the middle of this step
            // + 9: the lamps are lenses, not points — no hot spot in the mist right at the bumper
            if (b > 0.0) veil += vec3(2.0 * b * exp(-s * sqrt(d2)) / (d2 + 9.0) * (s / sigma) * exp(-optical) * step);
            optical += s * step * 0.5;
        }
        veil *= pc.uSpotColor.rgb * (pc.uFog.a * HeadVeil);
        // the cap grows with the fog's own brightness: against bright day fog the beams still show, as a fainter cone than at night
        float cap = HeadVeilCap + 0.25 * dot(pc.uFog.rgb, vec3(0.2126, 0.7152, 0.0722));
        veil /= 1.0 + dot(veil, vec3(0.2126, 0.7152, 0.0722)) / cap;
    }
    return capped(sum * pc.uFog.a) + veil;
}

// Fog over a lit surface colour at p (fog.glsl); the light glow is added per pixel afterwards (glow.frag).
vec3 applyFog(vec3 c, vec3 p)
{
    return mix(c, fogColour(normalize(p - pc.uEye.xyz)), fogAmount(p));
}
