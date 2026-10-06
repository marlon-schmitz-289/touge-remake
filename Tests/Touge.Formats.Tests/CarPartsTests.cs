using System.Numerics;

namespace Touge.Formats.Tests;

public class CarPartsTests
{
    /// <summary>Right wheels = left tire turned 180° about Y: its outer face (+X) ends up outside on the right (−X), not mirrored.</summary>
    [Fact]
    public void Right_wheels_are_rotated_not_mirrored()
    {
        var body = new Mesh
        {
            Textures = [], Materials = [],
            Nodes = [.. CarParts.WheelNodes.Select(n => (n, Matrix4x4.CreateTranslation(n.EndsWith("_l") ? 0.7f : -0.7f, 0, n.StartsWith("fr") ? 1.3f : -1.1f)))],
        };
        var w = CarParts.Wheels(body);
        var outer = new Vector3(0.1f, 0, 0);
        Assert.Equal(0.8f, Vector3.Transform(outer, w[0]).X, 5);
        Assert.Equal(-0.8f, Vector3.Transform(outer, w[1]).X, 5);
        Assert.Equal(-0.8f, Vector3.Transform(outer, w[3]).X, 5);
        Assert.True(w[1].GetDeterminant() > 0);

        // pop-up lamps sit at fr_rk_close; other parts and bodies without the node stay put
        var lamp = new Mesh { Textures = [], Nodes = [], Materials = [new Mesh.Material(-1, 0, 0, [new Mesh.Vertex(Vector3.Zero, Vector3.UnitY, default, default)])] };
        var popUp = new Mesh { Textures = [], Materials = [], Nodes = [("fr_rk_close", Matrix4x4.CreateTranslation(0, 0.6f, 1.9f))] };
        Assert.Equal(new Vector3(0, 0.6f, 1.9f), CarParts.Placed("Flight00", lamp, popUp).Materials[0].Triangles[0].Position);
        Assert.Same(lamp, CarParts.Placed("Flight00", lamp, body));
        Assert.Same(lamp, CarParts.Placed("grill00", lamp, popUp));
    }

    /// <summary>Part choice of 0x15B110/0x159510 for stock and character setups, plate digits and plate texture compositing.</summary>
    [Fact]
    public void Livery_selects_parts_and_plate_like_the_game()
    {
        HashSet<string> have = ["body00", "bodyshd00", "wind00", "grill00", "other00", "other01", "emblem00", "emblem01", "sticker01", "rival00",
            "Fspoile00", "Fspoile01", "Flight00", "Flight10", "Blamp00", "Blamp02", "muffler00", "muffler01", "number", "Rnumber00", "tire00FL"];
        string[] stock = [.. CarParts.Visible("AE86T", have, CarParts.SetupOf("AE86T", Livery.Stock), 0).Order(StringComparer.Ordinal)];
        Assert.Equal(["Blamp00", "Flight00", "Fspoile00", "body00", "emblem00", "grill00", "muffler00", "other00", "other01", "wind00"], stock);
        // lights on: night lamps where the PAC has them (no Flight01 here, so the day headlamp stays); AE86 bonnet 1 lamp Flight10 → Flight11
        var lit = CarParts.Visible("AE86T", have, CarParts.SetupOf("AE86T", Livery.Stock), 0, true);
        Assert.Contains("Blamp02", lit);
        Assert.DoesNotContain("Blamp00", lit);
        Assert.Contains("Flight00", lit);
        Assert.Equal("Flight11", CarParts.Lit("Flight10", new HashSet<string> { "Flight10", "Flight11" }));
        Assert.Equal("grill00", CarParts.Lit("grill00", have));

        // Takumi's AE86: front spoiler 01 (plus 00, AE86 case), muffler 01, Fujiwara tofu sticker, no rival flag; paint 1 swaps the emblem
        var takumi = CarParts.Visible("AE86T", have, CarParts.SetupOf("AE86T", Livery.Rival), 1);
        Assert.Superset(new HashSet<string> { "sticker01", "Fspoile00", "Fspoile01", "muffler01", "emblem01" }, takumi);
        Assert.DoesNotContain("muffler00", takumi);
        Assert.DoesNotContain("rival00", takumi);
        Assert.DoesNotContain("emblem00", takumi);
        Assert.Contains("rival00", CarParts.Visible("FD3S", have, CarParts.SetupOf("FD3S", Livery.Rival), 0));
        Assert.DoesNotContain("rival00", CarParts.Visible("FD3S", have, CarParts.SetupOf("FD3S", Livery.Stock), 0));

        Assert.Equal([1, 3, 9, 5, 4], CarParts.PlateNumber("AE86T", 1)!);
        Assert.Equal([1, 1, 0, 0, 9], CarParts.PlateNumber("AE86T", 0)!);
        Assert.Null(CarParts.PlateNumber("CAPPU", 0));

        // digit 3 opaque white, everything else transparent: lands at x 10…21, y 13…28 of the plate only
        var plate = new byte[64 * 32 * 4];
        var digits = new byte[128 * 16 * 4];
        for (var y = 0; y < 16; y++)
            digits.AsSpan((y * 128 + 36) * 4, 12 * 4).Fill(255);
        var px = CarParts.Plate(plate, digits, [3, 0, 0, 0, 0]);
        Assert.Equal(255, px[(13 * 64 + 10) * 4]);
        Assert.Equal(255, px[(28 * 64 + 21) * 4]);
        Assert.Equal(0, px[(12 * 64 + 10) * 4]);
        Assert.Equal(0, px[(13 * 64 + 33) * 4]);
    }
}
