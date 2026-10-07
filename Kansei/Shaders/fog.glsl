// Atmospheric fog, per pixel. Needs a push block `pc` with uEye, uSun, uFog, uFogParams, uFogSun (scene_push.glsl,
// sky.frag). Amount = 1 − (1 − linear) · exp(−height): linear distance fog (start/end like the original's per-course
// fog) combined with exponential height fog, density k · exp(−(y − base) / H), integrated analytically along the view
// ray — valleys below the road fill with mist while the sky above stays clear. Colour: fog colour + sun light
// scattered towards the sun (warm haze by day). Scene shaders (SCENE_PUSH, after noise.glsl) let slow noise drift
// through the density (fog banks in the fog weather; the sky shader has none).

float fogAmount(vec3 p)
{
    vec3 d = p - pc.uEye.xyz;
    float dist = length(d);
    float lin = clamp((dist - pc.uFogParams.x) * pc.uFogParams.y, 0.0, 1.0);
    float depth = 0.0;
    if (pc.uFogParams.z > 0.0)
    {
        float e = clamp((pc.uEye.y - pc.uFogSun.w) * pc.uFogParams.w, -4.0, 40.0);
        float dy = clamp(d.y * pc.uFogParams.w, -30.0, 30.0);
        float g = abs(dy) > 1e-3 ? (1.0 - exp(-dy)) / dy : 1.0;
        depth = pc.uFogParams.z * dist * exp(-e) * g;
#ifdef SCENE_PUSH
        if (pc.uShadeGround.w > 0.0)
        {
            // fog weather: ±drift/2 by ~40 m noise sampled in the volume (15 and 45 m along the ray, not at the surface,
            // so surfaces at one distance share it), moving at ~2 m/s: smooth, nothing flickers. The first 3 m are
            // clear, so the player car keeps its contrast.
            vec3 rd = d / max(dist, 1e-4);
            vec2 t = pc.uSky.w * vec2(0.05, 0.02);
            float n = noise((pc.uEye.xz + rd.xz * min(dist, 15.0)) * 0.025 + t) + noise((pc.uEye.xz + rd.xz * min(dist, 45.0)) * 0.025 + t);
            depth *= (1.0 + pc.uShadeGround.w * (0.5 * n - 0.5)) * max(1.0 - 3.0 / max(dist, 1e-4), 0.0);
        }
#endif
    }
    return 1.0 - (1.0 - lin) * exp(-depth);
}

// Extinction (1/m) of the height fog at q, with the fog weather's drifting banks (the same ~40 m noise as fogAmount,
// sampled at q itself): the volumetric light (lighting.glsl) scatters in this density, so denser banks light up.
float fogDensity(vec3 q)
{
    float s = pc.uFogParams.z * exp(-clamp((q.y - pc.uFogSun.w) * pc.uFogParams.w, -4.0, 40.0));
#ifdef SCENE_PUSH
    if (pc.uShadeGround.w > 0.0) s *= 1.0 + pc.uShadeGround.w * (noise(q.xz * 0.025 + pc.uSky.w * vec2(0.05, 0.02)) - 0.5);
#endif
    return s;
}

vec3 fogColour(vec3 dir)
{
    return pc.uFog.rgb + pc.uFogSun.rgb * pow(max(dot(dir, pc.uSun.xyz), 0.0), 6.0);
}

// Sky (no surface): the fog of a point far out along dir, fading out above the horizon so clouds stay visible — but
// never less than the fog of the first 150 m, so dense fog hides the sky in every direction.
float skyFog(vec3 dir)
{
    return max(fogAmount(pc.uEye.xyz + dir * 2500.0) * (1.0 - smoothstep(0.0, 0.45, dir.y)), fogAmount(pc.uEye.xyz + dir * 150.0));
}
