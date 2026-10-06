using System.Numerics;
using Touge.Net;
using Touge.Ui;

namespace Touge.Tests;

public class VersusTests
{
    private static Catalog TestCatalog() => new(
        [
            new Catalog.Course("AKINA", "AKINA", ["DAY", "NIT", "RIN"], [Vector2.Zero, Vector2.One], 7400, 300, true, false),
            new Catalog.Course("IROHA", "IROHAZAKA", ["DAY", "NIT"], [Vector2.Zero, Vector2.One], 4900, 200, true, false),
        ],
        [
            new Catalog.Car("AE86T", "TOYOTA", "TRUENO", "FR", 130, 940, [1, 2, 3]),
            new Catalog.Car("FD3S", "MAZDA", "RX-7", "FR", 255, 1260, [4, 5]),
        ]);

    private static readonly (int, int, bool, bool) None = default, Ok = (0, 0, true, false), Back = (0, 0, false, true), Down = (0, 1, false, false), Right = (1, 0, false, false);

    private static Versus.Action Step(Versus v, (int, int, bool, bool) k1, (int, int, bool, bool) k2 = default, Versus.TextKeys text = default)
    {
        var a = Versus.Action.None;
        for (var i = 0; i < 8; i++) // past the half fade that ignores input after a screen opens
            if (v.Update(i == 7 ? k1 : None, i == 7 ? k2 : None, i == 7 ? text : new Versus.TextKeys("", false, false, false), 1 / 20f) is var x and not Versus.Action.None)
                a = x;
        return a;
    }

    [Fact]
    public void Conditions_PerCourse()
    {
        var c = TestCatalog();
        Assert.Equal(["DAY", "NIGHT", "WET", "DAY FOG", "NIGHT FOG"], Versus.Conditions(c.Courses[0]).Select(x => x.Label));
        Assert.Equal(["DAY", "NIGHT", "DAY FOG", "NIGHT FOG"], Versus.Conditions(c.Courses[1]).Select(x => x.Label));
    }

    /// <summary>
    ///     Split screen: SPLIT opens the lobby, player 1 picks the race (course, conditions), player 2 its own car on its own
    ///     cursor; START beeps until player 2 is ready, then starts. The result offers RETRY / LOBBY / EXIT.
    /// </summary>
    [Fact]
    public void SplitLobby_Flow()
    {
        var sounds = new List<string>();
        var v = new Versus(TestCatalog()) { Sound = sounds.Add };
        v.Open();
        Assert.Equal(Versus.Action.Split, Step(v, Ok));
        v.OpenSplit("AE86T", 0);
        Assert.Equal(Versus.Screen.Lobby, v.Current);
        Assert.Equal(1, v.Seats[1].Car); // player 2 starts on another car
        // course → IROHAZAKA, conditions (row 3) → NIGHT
        Step(v, Right);
        Assert.StartsWith("IROHA_", v.SplitConfig.CourseTime);
        Step(v, Down);
        Step(v, Down);
        Step(v, Right);
        Assert.Equal(("IROHA_NIT", false), (v.SplitConfig.CourseTime, v.SplitConfig.Fog));
        Step(v, Right);
        Assert.Equal(("IROHA_DAY", true), (v.SplitConfig.CourseTime, v.SplitConfig.Fog)); // DAY FOG
        // player 2: paint, then ready
        Step(v, None, (0, 1, false, false));
        Step(v, None, Right);
        Assert.Equal(1, v.Seats[1].Paint);
        // player 1 goes to START (last row) — not everybody ready: beep, no start
        for (var i = 0; i < 10; i++) Step(v, Down);
        sounds.Clear();
        Assert.Equal(Versus.Action.None, Step(v, Ok));
        Assert.Equal(["BEEP001"], sounds);
        Step(v, None, (0, 1, false, false));
        Step(v, None, Ok);
        Assert.True(v.Seats[1].Ready);
        Assert.Equal(Versus.Action.Start, Step(v, Ok));
        v.ShowResult(new Versus.Standings("PLAYER 1 WINS!!", true, "FIRST TO THE GOAL",
            [new Versus.Standing(1, "PLAYER 1", "TRUENO", 200, "3'20.000", false), new Versus.Standing(2, "PLAYER 2", "RX-7", null, "DNF", false)]));
        for (var i = 0; i < 30; i++) v.Update(None, None, default, 1 / 20f); // the buttons come after a second
        Assert.Equal(Versus.Action.Rematch, Step(v, Ok));
        Step(v, Right);
        Assert.Equal(Versus.Action.ToLobby, Step(v, Ok));
    }

    /// <summary>Split screen: player 2's DECIDE on CAR opens the car select for player 2 alone (player 1's keys wait), the pick lands on its seat.</summary>
    [Fact]
    public void SplitLobby_CarSelectPerSeat()
    {
        var v = new Versus(TestCatalog());
        v.Open();
        v.OpenSplit("AE86T", 0);
        Step(v, None, Ok); // player 2 on CAR (its first row)
        Assert.True(v.Picking);
        Step(v, Down); // player 1: ignored
        Step(v, None, (0, -4, false, false)); // the select opens on its FD3S (MAZDA): four makers up to TOYOTA
        Step(v, None, Ok);
        Assert.Equal("AE86T", v.PickCarId);
        Step(v, None, Right);
        Step(v, None, Ok);
        Assert.False(v.Picking);
        Assert.Equal((0, 1), (v.Seats[1].Car, v.Seats[1].Paint));
        Assert.Equal((0, 0), (v.Seats[0].Car, v.Seats[0].Paint));
        Assert.Equal(Versus.Screen.Lobby, v.Current);
    }

    /// <summary>ONLINE: typing an address and ENTER joins; BACK from the online lobby leaves the session.</summary>
    [Fact]
    public void Online_JoinByAddress()
    {
        var v = new Versus(TestCatalog());
        v.Open();
        Step(v, Right);
        Step(v, Ok); // ONLINE
        Assert.Equal(Versus.Screen.Online, v.Current);
        Step(v, Down); // JOIN BY ADDRESS
        Step(v, Ok);
        Assert.Equal(Versus.Action.None, Step(v, None, default, new Versus.TextKeys("192.168.1.2x:4786!0", false, false, false)));
        Step(v, None, default, new Versus.TextKeys("", true, false, false)); // backspace
        Assert.Equal("192.168.1.2x:4786", v.Address); // '!' is not part of an address, the last character was erased
        Assert.Equal(Versus.Action.Join, Step(v, None, default, new Versus.TextKeys("", false, true, false)));
        Assert.Null(v.JoinLan);
        // UDP PORT: digits only, out of range is refused (BEEP, old port kept)
        Step(v, Down);
        Step(v, Down);
        Step(v, Ok);
        Step(v, None, default, new Versus.TextKeys("5x0123", false, true, false));
        Assert.Equal(50123, v.Port);
        Step(v, Ok);
        Step(v, None, default, new Versus.TextKeys("80", false, true, false));
        Assert.Equal(50123, v.Port);
        using var host = NetSession.Host(0, "HOST");
        v.OpenLobby(host, "FD3S", 1);
        Assert.Equal(("FD3S", (byte)1, true), (host.Local.Car, host.Local.Paint, host.Local.Ready));
        Assert.Equal(Versus.Action.Leave, Step(v, Back));
    }

    [Fact]
    public void Standings_Values()
    {
        Assert.Equal("3'05.250", Versus.ValueOf(185.25f, 1, 4900, 4900, false));
        Assert.Equal("WIN", Versus.ValueOf(null, 1, 3361, 3361, true));
        Assert.Equal("90 m BEHIND", Versus.ValueOf(null, 2, 3271, 3361, true));
        Assert.Equal("DNF", Versus.ValueOf(null, 2, 3271, 4900, false));
    }
}
