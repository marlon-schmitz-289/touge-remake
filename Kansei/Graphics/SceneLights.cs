using System.Numerics;

namespace Kansei.Graphics;

/// <summary>Dynamic lights of a frame (world space, linear colours). Read by <see cref="WorldRenderer"/> per draw.</summary>
public sealed class SceneLights
{
    /// <summary>Headlight intensity per lamp (0 = off; also switches the rear lamps' running glow).</summary>
    public Vector3 HeadlightColor;
    /// <summary>Beam half spreads (the beam fades out towards them), range in metres.</summary>
    public float HeadlightRange = 90, HeadlightSpreadDegrees = 24, HeadlightSpreadVerticalDegrees = 6;
    public readonly Vector3[] HeadlightPosition = new Vector3[2], HeadlightDirection = new Vector3[2];
    /// <summary>Brake lamps 0..1.</summary>
    public float Brake;
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
