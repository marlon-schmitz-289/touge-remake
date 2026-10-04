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

    /// <summary>
    ///     Paint-dependent emblem of the AE86 Trueno (0x15D060, car id 0): paint 1 shows <c>emblem00</c> and hides
    ///     <c>emblem01</c>, every other paint the reverse. Other cars: nothing to swap (null, null).
    ///     ponytail: the other special case there (SIL80/S2000/FD3S/S13 with car byte 0x13 = 5 get hard-coded RGB) hangs on
    ///     a state the default parts never reach, so it is left out.
    /// </summary>
    public static (string? Hide, string? Show) Emblem(string car, int paint) =>
        car != "AE86T" ? (null, null) : paint == 1 ? ("emblem01", "emblem00") : ("emblem00", "emblem01");

    /// <summary>
    ///     Pop-up headlamp parts (<c>Flight…</c>) of cars whose body00 has the nodes <c>fr_rk_close/open</c> (AE86T, MR2,
    ///     FD3S, FC3S, ONE80, NA6C …) are modelled around the origin; closed they sit at <c>fr_rk_close</c>. Other parts
    ///     (and Flight parts of cars without the node, e.g. S2000) come back unchanged.
    /// </summary>
    public static Mesh Placed(string part, Mesh mesh, Mesh body)
    {
        if (!part.StartsWith("Flight") || body.Nodes.FirstOrDefault(n => n.Name == "fr_rk_close") is not { Name: not null } node) return mesh;
        var t = node.Transform;
        return new Mesh
        {
            Textures = mesh.Textures, Nodes = mesh.Nodes,
            Materials =
            [
                .. mesh.Materials.Select(m => m with
                {
                    Triangles = [.. m.Triangles.Select(v => v with { Position = Vector3.Transform(v.Position, t), Normal = Vector3.Normalize(Vector3.TransformNormal(v.Normal, t)) })],
                }),
            ],
        };
    }

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
