using System.Numerics;

namespace Touge.Formats;

/// <summary>
///     Assembling a car from its CMD parts (names without the "CAR_" prefix): default variant = suffix "00",
///     wheels sit at the body00 nodes fr_l/fr_r/re_l/re_r. The tire mesh is modelled for the left side (+X);
///     right wheels use it rotated 180° about Y (no mirroring, so decals and winding stay correct).
/// </summary>
public static class CarParts
{
    public static readonly string[] WheelNodes = ["fr_l", "fr_r", "re_l", "re_r"];

    /// <summary>Default body part: "…00", no shadow, tire or brake disk (calipers end in FL/FR/… so they are out too).</summary>
    public static bool IsDefaultBody(string part) =>
        part.EndsWith("00") && !part.Contains("shd") && !part.StartsWith("tire") && !part.StartsWith("Bdisk");

    /// <summary>Wheel transforms in car space, in <see cref="WheelNodes"/> order.</summary>
    public static Matrix4x4[] Wheels(Mesh body) =>
    [
        .. WheelNodes.Select(name =>
        {
            var m = body.Nodes.First(n => n.Name == name).Transform;
            return name.EndsWith("_r") ? Matrix4x4.CreateRotationY(MathF.PI) * m : m;
        }),
    ];
}
