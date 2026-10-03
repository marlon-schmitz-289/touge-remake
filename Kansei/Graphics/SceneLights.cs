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
    /// <summary>All street lights of the course; the 4 nearest to the camera are used.</summary>
    public Vector3[] StreetLights = [];
    public Vector3 StreetLightColor;
    public float StreetLightRadius = 22;
}
