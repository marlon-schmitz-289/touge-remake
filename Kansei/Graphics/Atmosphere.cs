using System.Numerics;

namespace Kansei.Graphics;

/// <summary>Sky, fog, light and post settings of a scene. Colours are linear; fog uses <see cref="Horizon"/>.</summary>
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
    public float FogDistance = 1600f;
    public float Exposure = 1.2f;
    public float BloomThreshold = 1.4f, BloomStrength = 0.6f;
    public Vector3 Tint = Vector3.One;
    public float Saturation = 1.05f, Vignette = 0.25f;
}
