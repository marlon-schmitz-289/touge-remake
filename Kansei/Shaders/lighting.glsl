// Sun shadows (cascades side by side in one atlas, see ShadowMap.cs) and the dynamic lights. Needs scene_push.glsl.
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

// Headlights + street-light points: returns the diffuse irradiance and adds an energy-normalised Blinn-Phong
// highlight (exponent `shininess`) to `spec`. Inverse-square falloff windowed to the range. Headlight beams are
// elliptical (wide, flat) around their axis, so the road just in front of the car is not blown out while the
// beam still reaches far.
vec3 dynamicLight(vec3 p, vec3 n, vec3 v, float shininess, inout vec3 spec)
{
    vec3 sum = vec3(0.0);
    float norm = (shininess + 8.0) / 25.13;
    for (int i = 0; i < 6; i++)
    {
        bool spot = i < 2;
        vec4 lp = spot ? pc.uSpotPos[i] : pc.uPointPos[i - 2];
        float range = spot ? pc.uSpotColor.w : lp.w;
        vec3 colour = spot ? pc.uSpotColor.rgb : pc.uPointColor.rgb;
        if (range <= 0.0 || dot(colour, colour) == 0.0) continue;
        vec3 d = lp.xyz - p;
        float d2 = dot(d, d);
        if (d2 >= range * range) continue;
        vec3 l = d * inversesqrt(max(d2, 1e-4));
        float w = 1.0 - (d2 * d2) / (range * range * range * range);
        float att = w * w / (d2 + 1.0);
        if (spot)
        {
            vec3 axis = pc.uSpotDir[i].xyz;
            vec3 right = normalize(cross(axis, vec3(0.0, 1.0, 0.0)));
            vec3 up = cross(right, axis);
            float ahead = dot(-l, axis);
            if (ahead <= 0.0) continue;
            vec2 off = vec2(dot(-l, right) / lp.w, dot(-l, up) / pc.uSpotDir[i].w) / ahead;
            att *= smoothstep(1.6, 0.25, length(off));
        }
        float ndl = max(dot(n, l), 0.0);
        sum += colour * (att * ndl);
        spec += colour * (att * ndl * norm * pow(max(dot(n, normalize(l + v)), 0.0), shininess));
    }
    return sum;
}
