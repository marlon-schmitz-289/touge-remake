using System.Numerics;

namespace Touge.Formats;

/// <summary>Which stickers a car wears: none at all, the game's stock car, or the anime character's car.</summary>
public enum Livery { None, Stock, Rival }

/// <summary>
///     Assembling a car from its CMD parts (names without the "CAR_" prefix) like the game's part setup <c>0x15B110</c>,
///     wheels at the body00 nodes fr_l/fr_r/re_l/re_r. The tire mesh is modelled for the left side (+X); right wheels
///     use it rotated 180° about Y (no mirroring, so decals and winding stay correct).
/// </summary>
public static class CarParts
{
    public static readonly string[] WheelNodes = ["fr_l", "fr_r", "re_l", "re_r"];

    /// <summary>Tuning slots of a setup (car struct +0xB…+0x14, name tables 0x24C950 + 0x40·slot): variant n = part "&lt;slot&gt;%02d".</summary>
    public static readonly string[] Slots = ["Fspoile", "bonnet", "mirror", "skirt", "Rbumper", "Rwing", "muffler", "tire", "parts", "sticker"];

    /// <summary>
    ///     Car setup as the game copies it into the car struct (+0xB…+0x16): <see cref="Slots"/> variants, roll cage, rival
    ///     flag; <paramref name="Driver"/> = first byte of the 16-byte setup record (plate choice, <see cref="PlateNumber"/>).
    /// </summary>
    public sealed record Setup(int Driver, byte[] Bytes)
    {
        public int Slot(int i) => Bytes[i];
        public int Roll => Bytes[10];
        public bool Rival => Bytes[11] != 0;
    }

    /// <summary>
    ///     The characters' cars (player setups per character, ELF 0x2A2EA0, 63 × 16 B: driver/plate, car id, ?, paint,
    ///     12 setup bytes): per car the first character driving it, one with a sticker or rival flag preferred (Keisuke's
    ///     FD over Kyoko's). Digits = setup bytes. Cars nobody drives (MRS, GT-4, R34, S14, S2000, EVO7, FD3SA, NB8C, IMP,
    ///     IMP2) stay stock.
    /// </summary>
    private static readonly Dictionary<string, (int Driver, string Bytes)> Characters = new()
    {
        ["AE86T"] = (1, "100000110100"), ["AE86L"] = (14, "000001110000"), ["AE85"] = (0, "000000110001"),
        ["MR2"] = (21, "000000110001"), ["ALTEZ"] = (5, "100101110001"), ["R32"] = (3, "100000110001"),
        ["ER34"] = (23, "100000110001"), ["S13"] = (13, "100100110001"), ["S14Q"] = (9, "100111110001"),
        ["S15"] = (4, "000000060000"), ["ONE80"] = (12, "100000110001"), ["SIL80"] = (6, "000101110001"),
        ["EK9"] = (16, "001001110001"), ["EG6"] = (2, "100000110001"), ["INTGR"] = (17, "010000110001"),
        ["EVO3"] = (20, "000000111001"), ["EVO4"] = (19, "000001110001"), ["FD3S"] = (10, "100001110001"),
        ["FC3S"] = (11, "101000110001"), ["NA6C"] = (15, "111010110001"), ["IMP3"] = (22, "000000000000"),
        ["CAPPU"] = (7, "100001110011"),
    };

    public static Setup SetupOf(string car, Livery livery) =>
        livery == Livery.Rival && Characters.TryGetValue(car, out var c) ? new Setup(c.Driver, [.. c.Bytes.Select(ch => (byte)(ch - '0'))]) : new Setup(0, new byte[12]);

    /// <summary>
    ///     Parts the game shows for <paramref name="s"/> (of those in <paramref name="have"/>): <c>0x15B110</c> hides all,
    ///     shows body00, wind00, grill00, other00, other01 (stock badges), emblem00, the day lamps (Blamp00, closed
    ///     Flight00), per slot its variant or else variant 00, then per-car exceptions (grille/emblem/lamps that a
    ///     spoiler or bonnet replaces), roll01 for a roll cage; the player car
    ///     (<c>0x159510</c>) adds rival00 when the rival flag is set and assi00; <c>0x15D060</c> swaps the AE86T emblem
    ///     by paint. Plates (<c>number</c>, <c>Rnumber00</c> = night glow) are placed separately (<see cref="PlateNodes"/>).
    ///     <paramref name="lit"/>: the night lamps instead (<see cref="Lit"/>), as the game shows them with the lights on.
    /// </summary>
    public static HashSet<string> Visible(string car, IReadOnlySet<string> have, Setup s, int paint, bool lit = false)
    {
        HashSet<string> show = ["body00", "wind00", "grill00", "other00", "other01", "emblem00", "Blamp00", "Flight00", "assi00"];
        for (var k = 0; k < Slots.Length; k++) show.Add(Variant(have, Slots[k], s.Slot(k)));
        int fsp = s.Slot(0), bon = s.Slot(1), ski = s.Slot(3), rbu = s.Slot(4), roll = s.Roll;
        switch (car)
        {
            case "AE86T":
                if (fsp == 1) show.Add("Fspoile00");
                if (bon == 1) Swap(show, "Flight00", "Flight10");
                break;
            case "AE86L" when s.Slot(8) == 1: Swap(show, "grill00", "grill01"); break;
            case "ALTEZ" or "S14Q" when fsp != 0: show.Remove("grill00"); break;
            case "IMP" when fsp == 2: show.Remove("grill00"); break;
            case "S13" when fsp is 2 or 3: show.Remove("grill00"); break;
            case "S14":
                if (fsp == 3) show.Remove("grill00");
                if (ski != 0) show.Remove("emblem00");
                break;
            case "SIL80" when fsp == 3: show.Remove("grill00"); break;
            case "EK9" when bon == 2: show.Remove("emblem00"); break;
            case "EG6" when bon == 3: show.Remove("emblem00"); break;
            case "S2000" when rbu == 1: show.Remove("muffler00"); break;
            case "EVO7" when rbu == 3: show.Remove("emblem00"); break;
            case "FD3S":
                if (fsp == 3) show.Remove("Flight00");
                if (roll != 2) roll = 0;
                break;
            case "NA6C":
                if (bon == 1) Swap(show, "wind00", "wind01");
                if (roll == 1) roll = 0;
                break;
            case "IMP2":
                if (fsp is 2 or 3) show.Remove("grill00");
                if (fsp != 0) show.Remove("emblem00");
                break;
        }
        if (roll != 0) show.Add("roll01");
        if (s.Rival) show.Add("rival00");
        if (car == "AE86T" && paint == 1) Swap(show, "emblem00", "emblem01"); // white letters on paint 1 (red), else black (emblem00)
        show.IntersectWith(have);
        if (lit) show = [.. show.Select(p => Lit(p, have))];
        return show;
    }

    /// <summary>
    ///     Night variant of a lamp part (tables <c>0x24C880</c>/<c>0x24C870</c>): headlamps <c>Flight</c>x0 → x1, rear lamps
    ///     <c>Blamp00</c> → <c>Blamp02</c> — the same mesh with the lit lens textures. Other parts, and lamps without a
    ///     night variant in <paramref name="have"/>, stay.
    /// </summary>
    public static string Lit(string part, IReadOnlySet<string> have)
    {
        var night = part.StartsWith("Flight") && part.EndsWith('0') ? part[..^1] + "1" : part == "Blamp00" ? "Blamp02" : part;
        return have.Contains(night) ? night : part;
    }

    /// <summary>
    ///     Body parts to draw for <paramref name="livery"/> in PAC order (<see cref="Visible"/>, pop-up lamps
    ///     <see cref="Placed"/>) plus the two plates (<c>number</c> at <see cref="PlateNodes"/>, texture renamed to
    ///     <paramref name="plateTexture"/> when given). <see cref="Livery.None"/>: stock parts without decal materials (flag
    ///     0x400; windows are 0xC00 and stay) and without plates. <paramref name="lit"/>: night lamps (<see cref="Lit"/>).
    /// </summary>
    public static List<(string Name, Mesh Mesh)> Body(string car, IReadOnlyDictionary<string, Mesh> parts, Livery livery, int paint, string? plateTexture = null,
        bool lit = false)
    {
        var setup = SetupOf(car, livery);
        var show = Visible(car, parts.Keys.ToHashSet(), setup, paint, lit);
        List<(string Name, Mesh Mesh)> body = [.. parts.Where(p => show.Contains(p.Key)).Select(p => (p.Key, Placed(p.Key, p.Value, parts["body00"])))];
        if (livery == Livery.None)
            return [.. body.Select(p => (p.Name, new Mesh { Textures = p.Mesh.Textures, Nodes = p.Mesh.Nodes, Materials = [.. p.Mesh.Materials.Where(m => (m.Flags & 0xC00) != 0x400)] }))];
        if (parts.TryGetValue("number", out var plate))
        {
            if (plateTexture != null) plate = new Mesh { Textures = [plateTexture], Nodes = plate.Nodes, Materials = plate.Materials };
            body.AddRange(PlateNodes(parts, setup).Select(t => ("number", Transformed(plate, t))));
        }
        return body;
    }

    /// <summary>Tire part (left front) of the setup's tire slot, falling back to tire00FL.</summary>
    public static string Tire(IReadOnlyDictionary<string, Mesh> parts, Setup s) => parts.ContainsKey($"tire{s.Slot(7):00}FL") ? $"tire{s.Slot(7):00}FL" : "tire00FL";

    /// <summary>Slot part of variant <paramref name="v"/>, or variant 00 when the car has no such part (as the game falls back).</summary>
    public static string Variant(IReadOnlySet<string> have, string slot, int v) => have.Contains($"{slot}{v:00}") ? $"{slot}{v:00}" : $"{slot}00";

    private static void Swap(HashSet<string> show, string hide, string add)
    {
        show.Remove(hide);
        show.Add(add);
    }

    /// <summary>
    ///     Plate positions (<c>0x15B110</c> end, <c>0x1940F0</c>): node fr_num of the front spoiler variant, else of
    ///     body00, else of Fspoile00; re_num likewise from the rear bumper. The game draws CAR_number (NUMBER.PAC, same
    ///     quad as the car's own <c>number</c> part) there with a composed texture (<see cref="Plate"/>).
    /// </summary>
    public static IEnumerable<Matrix4x4> PlateNodes(IReadOnlyDictionary<string, Mesh> parts, Setup s)
    {
        foreach (var (slot, node) in new[] { (0, "fr_num"), (4, "re_num") })
            foreach (var part in new[] { Variant(parts.Keys.ToHashSet(), Slots[slot], s.Slot(slot)), "body00", $"{Slots[slot]}00" })
                if (parts.TryGetValue(part, out var m) && m.Nodes.FirstOrDefault(n => n.Name == node) is { Name: not null } n)
                {
                    yield return n.Transform;
                    break;
                }
    }

    /// <summary>
    ///     Plate digits (<c>0x15C1C0</c>): driver 1 (Takumi) has 13-954 (AE85: entry 0, ONE80: entry 26), everyone else
    ///     the car's entry of the 5-digit table at 0x2C7670 (28 entries; IMP and later read past it → null, keep the PAC's
    ///     own plate texture).
    /// </summary>
    public static int[]? PlateNumber(string car, int driver)
    {
        const string table = "11009 13954 46037 26037 34628 17919 78547 35218 77148 04842 63887 13137 08776 51745 73212 86596 56838 32094 10457 46637 30395 37597 13600 95085 13954 03465 77282 13600";
        var i = driver == 1 ? car switch { "AE85" => 0, "ONE80" => 26, _ => 1 } : Array.IndexOf(CarPaint.Cars, car);
        return i is < 0 or >= 28 ? null : [.. table.Substring(i * 6, 5).Select(c => c - '0')];
    }

    /// <summary>
    ///     Plate texture as <c>0x15C500</c> renders it: NUM_PLATE_W (NUM_PLATE_Y for the kei car CAPPU), 64×32, and the
    ///     five 12×16 digit cells of NUM_TEX (128×16, digit d at u = 12·d) at x 10, 18, 33, 42, 51 (gp−0x7C98), y 13,
    ///     alpha-blended. All RGBA8.
    /// </summary>
    public static byte[] Plate(byte[] plate, byte[] digits, int[] number)
    {
        var px = (byte[])plate.Clone();
        ReadOnlySpan<int> xs = [10, 18, 33, 42, 51];
        for (var i = 0; i < 5; i++)
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 12; x++)
        {
            int s = (y * 128 + number[i] * 12 + x) * 4, d = ((13 + y) * 64 + xs[i] + x) * 4, a = digits[s + 3];
            for (var c = 0; c < 3; c++) px[d + c] = (byte)((digits[s + c] * a + px[d + c] * (255 - a)) / 255);
        }
        return px;
    }

    /// <summary>Copy of <paramref name="mesh"/> with positions and normals transformed by <paramref name="t"/>.</summary>
    public static Mesh Transformed(Mesh mesh, Matrix4x4 t) => new()
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

    /// <summary>
    ///     Pop-up headlamp parts (<c>Flight…</c>) of cars whose body00 has the nodes <c>fr_rk_close/open</c> (AE86T, MR2,
    ///     FD3S, FC3S, ONE80, NA6C …) are modelled around the origin; closed they sit at <c>fr_rk_close</c>. Other parts
    ///     (and Flight parts of cars without the node, e.g. S2000) come back unchanged.
    /// </summary>
    public static Mesh Placed(string part, Mesh mesh, Mesh body) =>
        part.StartsWith("Flight") && body.Nodes.FirstOrDefault(n => n.Name == "fr_rk_close") is { Name: not null } node ? Transformed(mesh, node.Transform) : mesh;

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
