using System.Numerics;

namespace Kansei.Graphics;

/// <summary>
///     Dynamic lights of a frame (world space, linear colours). Read by <see cref="WorldRenderer"/> per draw, which scales
///     what they throw onto surfaces by <see cref="Atmosphere.LocalLightShare"/> (none by day); lighting.glsl caps the
///     irradiance per light and in sum.
/// </summary>
public sealed class SceneLights
{
    /// <summary>Headlight beam peak intensity per lamp (irradiance at 1 m on the beam axis; 0 = off).</summary>
    public Vector3 HeadlightColor;
    /// <summary>Low beam (0: Japanese cut-off, left kerb side raised) … high beam (1: no cut-off, longer, wider, brighter).</summary>
    public float HighBeam;
    public readonly Vector3[] HeadlightPosition = new Vector3[2], HeadlightDirection = new Vector3[2];
    /// <summary>Glow of the lamp lenses on the car (car.frag): headlamps 0 = off … 1 (high beam more), rear lamps' running glow when &gt; 0.</summary>
    public float LampGlow;
    /// <summary>Brake lamps 0..1, reverse lamps 0..1 (glow of the red / white parts of the rear lamp textures).</summary>
    public float Brake, Reverse;
    /// <summary>Rear lamps as small point lights (8 m): light the ground behind the car, streak on a wet road. 0 = off.</summary>
    public Vector3 TailLightColor;
    public readonly Vector3[] TailLightPosition = new Vector3[2];
    /// <summary>All street lights of the course; the 4 nearest to the camera are used.</summary>
    public Vector3[] StreetLights = [];
    public Vector3 StreetLightColor;
    public float StreetLightRadius = 22;
    /// <summary>The player car's body → world (car space: +x left, +y up, +z front), for its contact shadow on the world.</summary>
    public Matrix4x4 Car = Matrix4x4.Identity;
}
