using System.Numerics;

namespace Kansei.Physics;

/// <summary>Ray hit on the drivable surface. <see cref="Surface"/> = material id of the hit face (game-defined).</summary>
public readonly record struct GroundHit(Vector3 Point, Vector3 Normal, float Distance, int Surface);

/// <summary>Penetration of a probe sphere into a wall: push the probe by <see cref="Normal"/> × <see cref="Depth"/>.</summary>
public readonly record struct WallContact(Vector3 Point, Vector3 Normal, float Depth, int ProbeIndex);

/// <summary>
///     What the vehicle simulation needs from the world. World space, metres, right-handed, +Y up.
///     Implementations must be allocation-free per call (runs at the fixed tick for every wheel/probe).
/// </summary>
public interface IGround
{
    /// <summary>Closest hit along <paramref name="direction"/> (normalised) within <paramref name="maxDistance"/>.</summary>
    bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out GroundHit hit);

    /// <summary>
    ///     Tests probe spheres (e.g. body corners) against walls/guardrails. Writes up to
    ///     <c>contacts.Length</c> contacts, returns how many.
    /// </summary>
    int CollideWalls(ReadOnlySpan<Vector3> probes, float radius, Span<WallContact> contacts);
}
