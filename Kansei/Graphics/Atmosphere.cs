using System.Numerics;

namespace Kansei.Graphics;

/// <summary>Sky, fog, light and post settings of a scene. Colours are linear.</summary>
public sealed class Atmosphere
{
    public Vector3 Zenith = new(0.18f, 0.36f, 0.75f);
    public Vector3 Horizon = new(0.55f, 0.66f, 0.80f);
    /// <summary>Towards the sun, world space.</summary>
    public Vector3 SunDirection = Vector3.Normalize(new Vector3(0.4f, 0.8f, 0.3f));
    /// <summary>Sun disk radiance in the sky (HDR, 0 = no disk).</summary>
    public Vector3 SunDisk = new(40f, 36f, 30f);
    public float SunIntensity = 1.1f;
    /// <summary>Ambient light on lit objects (cars) and the colour they reflect.</summary>
    public Vector3 Ambient = new(0.30f, 0.34f, 0.40f);
    /// <summary>
    ///     Track lighting: baked vertex light × (<see cref="BakedKeep"/> + <see cref="BakedSun"/> × shadow × N·L).
    ///     Keep + sun × (N·L of a flat road) ≈ 1 preserves the original brightness in the sun.
    /// </summary>
    public float BakedKeep = 0.45f, BakedSun = 0.7f;
    /// <summary>Colour of the direct sun (world, car, smoke); 1 = white as before.</summary>
    public Vector3 SunColor = Vector3.One;
    /// <summary>
    ///     Hemisphere tint of the shade (the baked keep share on the world, the ambient on cars): <see cref="ShadeSky"/>
    ///     on upward normals, <see cref="ShadeGround"/> (bounce) on downward ones. 1 = neutral as before.
    /// </summary>
    public Vector3 ShadeSky = Vector3.One, ShadeGround = Vector3.One;
    /// <summary>Sun glints on grey, hard world surfaces (asphalt, guardrails, concrete); 0 = none.</summary>
    public float Specular;
    /// <summary>Darkening of the ground right under the car (0 = none … 1 = black at the centre).</summary>
    public float ContactShadow;
    /// <summary>Cascaded sun shadows (also needs <see cref="WorldRenderer.HighQuality"/>).</summary>
    public bool Shadows = true;
    /// <summary>Rain: 0 dry … 1 pouring (soaked albedo, glossy ground and puddles, droplets on cars, falling rain).</summary>
    public float Wetness;
    /// <summary>Brightness of the env-map reflections on cars (the maps are LDR).</summary>
    public float EnvStrength = 5f;
    /// <summary>
    ///     Fog (fog.glsl): linear from <see cref="FogStart"/> to <see cref="FogEnd"/> (m from the camera, start may be
    ///     negative = some haze right away) combined with height fog of <see cref="HeightFogDensity"/> (1/m) at
    ///     <see cref="HeightFogBase"/> (world altitude), falling off ×1/e every <see cref="HeightFogScale"/> m upwards.
    ///     <see cref="FogSun"/> is added towards the sun, <see cref="LightGlow"/> scales the glow of the dynamic lights.
    ///     The height fog's extinction at the camera also dims the lamps' light on its way; <see cref="FogDrift"/> lets slow
    ///     noise vary its density by ±drift/2 (fog banks).
    /// </summary>
    public Vector3 FogColor = new(0.55f, 0.62f, 0.70f), FogSun = new(0.25f, 0.20f, 0.12f);
    public float FogStart = 30, FogEnd = 9000, HeightFogDensity = 0.0004f, HeightFogBase, HeightFogScale = 60, LightGlow, FogDrift;
    public float Exposure = 1.2f;
    public float BloomThreshold = 1.4f, BloomStrength = 0.6f;
    public Vector3 Tint = Vector3.One;
    public float Saturation = 1.05f, Vignette = 0.25f;
    /// <summary>Filmic contrast around mid grey before the tonemap (power in log space, 1 = none).</summary>
    public float Contrast = 1f;
}
