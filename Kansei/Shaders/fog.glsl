// Atmospheric fog, per pixel. Needs a push block `pc` with uEye, uSun, uFog, uFogParams, uFogSun (scene_push.glsl,
// sky.frag). Amount = 1 − (1 − linear) · exp(−height): linear distance fog (start/end like the original's per-course
// fog) combined with exponential height fog, density k · exp(−(y − base) / H), integrated analytically along the view
// ray — valleys below the road fill with mist while the sky above stays clear. Colour: fog colour + sun light
// scattered towards the sun (warm haze by day).

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
    }
    return 1.0 - (1.0 - lin) * exp(-depth);
}

vec3 fogColour(vec3 dir)
{
    return pc.uFog.rgb + pc.uFogSun.rgb * pow(max(dot(dir, pc.uSun.xyz), 0.0), 6.0);
}

// Sky (no surface): the fog of a point far out along dir, fading out above the horizon so clouds stay visible.
float skyFog(vec3 dir)
{
    return fogAmount(pc.uEye.xyz + dir * 2500.0) * (1.0 - smoothstep(0.0, 0.45, dir.y));
}
