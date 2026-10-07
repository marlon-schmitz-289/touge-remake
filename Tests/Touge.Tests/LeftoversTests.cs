using System.IO.Compression;
using System.Numerics;
using System.Text;
using Kansei.Graphics;
using Kansei.Physics;
using Touge.Net;
using Touge.Race;
using Touge.Replays;
using Touge.Ui;

namespace Touge.Tests;

/// <summary>Replay labels, the NOW PLAYING toast's place, the --data-dir profile, the secret car and online result values.</summary>
public class LeftoversTests
{
    [Fact]
    public void ReplayLabels_PerMode()
    {
        ReplayInfo Info(string mode, string? result = null, int? chapter = null, string? title = null) => new()
        {
            Mode = mode, Result = result, Chapter = chapter, Title = title,
            Cars = [new ReplayCar("YOU", "AE86T", 0), new ReplayCar("KENJI", "ONE80", 0)],
        };
        Assert.Equal("LEGEND vs KENJI WIN", ReplayMenu.Label(Info("LEGEND", "WIN")));
        Assert.Equal("STORY ch.3 THE GHOST OF AKINA LOSE", ReplayMenu.Label(Info("STORY", "LOSE", 3, "THE GHOST OF AKINA")));
        Assert.Equal("BATTLE vs KENJI DRAW", ReplayMenu.Label(Info("BATTLE", "DRAW")));
        Assert.Equal("TIME ATTACK", ReplayMenu.Label(new ReplayInfo()));
        Assert.Equal("FREE BATTLE vs KENJI WIN", ReplayMenu.Label(Info("FREE BATTLE", "WIN")));
        Assert.Equal("FOUR PASSES 2/4", ReplayMenu.Label(new ReplayInfo { Mode = "FOUR PASSES 2/4" }));
        Assert.Equal("STORY ch.1 TOFU", ReplayMenu.Label(new ReplayInfo { Mode = "STORY", Chapter = 1, Title = "TOFU" })); // a run alone: no rival, no result
    }

    /// <summary>A v1 file from before the STORY fields (header JSON without Chapter/Title) still lists and loads.</summary>
    [Fact]
    public void OldReplayHeader_StillReads()
    {
        var json = Encoding.UTF8.GetBytes("""{"Course":"AKINA_NIT","Mode":"BATTLE","Result":"WIN","Cars":[{"Name":"YOU","Car":"AE86T","Paint":0},{"Name":"BUNTA FUJIWARA","Car":"IMP3","Paint":0}],"Ticks":0}""");
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true))
        using (var w = new BinaryWriter(gz))
        {
            w.Write("IDRP"u8);
            w.Write(Replay.Version);
            w.Write((ushort)Vehicle.StateBytes);
            w.Write(json.Length);
            w.Write(json);
            w.Write(0);
            w.Write(2);
            w.Write(0);
        }
        ms.Position = 0;
        var r = Replay.Read(ms);
        Assert.Null(r.Info.Chapter);
        Assert.Equal(1, r.Info.Cars[1].Power);
        Assert.Equal("BATTLE vs BUNTA FUJIWARA WIN", ReplayMenu.Label(r.Info));
    }

    private static SdfFont Font()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "InitialDRemake.slnx"))) dir = Path.GetDirectoryName(dir)!;
        return new SdfFont(File.ReadAllBytes(Path.Combine(dir, "Touge/Assets/Fonts/Rajdhani-Bold.ttf")), string.Concat(Enumerable.Range(32, 95).Select(c => (char)c)) + "°");
    }

    private static bool Overlap((Vector2 Min, Vector2 Max) a, (Vector2 Min, Vector2 Max) b) =>
        a.Min.X < b.Max.X && b.Min.X < a.Max.X && a.Min.Y < b.Max.Y && b.Min.Y < a.Max.Y;

    /// <summary>
    ///     The toast never covers the battle panel, the Story goal panel, the versus position panel (whole screen, left/right and
    ///     top/bottom split) or the HUD's timing and drift panels, for HUD SIZE 80–130 %, 16:9, 4:3, 21:9 and 32:9, with the
    ///     longest song; it stays on screen and its text fits the plate.
    /// </summary>
    [Fact]
    public void NowPlaying_ClearOfTheHudPanels()
    {
        var o = new Overlay { Font = Font() };
        var song = Jukebox.Songs.MaxBy(s => o.Font!.Measure(s.Title, 28))!;
        var problems = new List<string>();
        foreach (var (w, h) in new[] { (1280, 720), (1920, 1080), (1024, 768), (2560, 1080), (3440, 1440), (5120, 1440) })
        foreach (var scale in new[] { 0.8f, 0.9f, 1, 1.1f, 1.2f, 1.3f })
        foreach (var (name, battle, story, players, split) in new[]
                 {
                     ("plain", false, 0f, 0, ""), ("battle", true, 0f, 0, ""), ("battle+story", true, 78f, 0, ""), ("story", false, 78f, 0, ""),
                     ("versus2", false, 0f, 2, ""), ("versus4", false, 0f, 4, ""), ("split-lr", false, 0f, 2, "lr"), ("split-tb", false, 0f, 2, "tb"),
                 })
        {
            var g = Style.Safe(w, h);
            var u = g.U;
            var views = split switch
            {
                "lr" => new[] { (X: 0, Y: 0, W: (w - 2) / 2, H: h), (X: w - (w - 2) / 2, Y: 0, W: (w - 2) / 2, H: h) },
                "tb" => [(0, 0, w, (h - 2) / 2), (0, h - (h - 2) / 2, w, (h - 2) / 2)],
                _ => [(0, 0, w, h)],
            };
            var panels = new List<(string, (Vector2, Vector2))>();
            var clear = new List<(Vector2 Min, Vector2 Max)>();
            foreach (var v in views)
            {
                var at = new Vector2(v.X, v.Y);
                foreach (var b in Hud.TopBoxes(v.W, v.H, scale))
                {
                    clear.Add((b.Min + at, b.Max + at));
                    panels.Add(("hud", (b.Min + at, b.Max + at)));
                }
                if (players > 0)
                {
                    var vg = Style.Safe(v.W, v.H);
                    panels.Add(("versus", (at + new Vector2(vg.Right - VersusHud.W * vg.U, vg.Top), at + new Vector2(vg.Right, vg.Top + VersusHud.Height(players) * vg.U))));
                }
            }
            if (battle) panels.Add(("battle", (new Vector2(g.Right - BattleHud.W * u, g.Top), new Vector2(g.Right, g.Top + BattleHud.H * u))));
            if (story > 0)
            {
                var top = g.Top + (battle ? BattleHud.H + 14 : 0) * u;
                panels.Add(("story", (new Vector2(g.Right - 300 * u, top), new Vector2(g.Right, top + 64 * u))));
            }
            var below = NowPlaying.Below(battle, players > 0 ? VersusHud.Height(players) : 0, split == "tb", story);
            var (min, max, k) = NowPlaying.Box(o, w, h, song, 1, below, clear.ToArray());
            var where = $"{w}x{h} {scale:P0} {name}";
            foreach (var (what, box) in panels)
                if (Overlap((min, max), box)) problems.Add($"{where}: toast over {what}");
            if (min.X < 0 || max.X > w || max.Y > h) problems.Add($"{where}: toast off screen");
            if (o.Font!.Measure(song.Title, 28 * u * k) > max.X - min.X - 40 * u) problems.Add($"{where}: title wider than the plate");
        }
        Assert.True(problems.Count == 0, string.Join("; ", problems.Take(20)));
    }

    /// <summary>--data-dir is a whole profile: settings.json with the records next to Replays/Best; other scripted runs get a throwaway folder.</summary>
    [Fact]
    public void DataDir_RoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"idr-datadir-{Guid.NewGuid():N}");
        try
        {
            var file = Settings.RunFile(dir, false);
            Assert.Equal(Path.Combine(dir, "settings.json"), file);
            var key = Settings.BestKey("AKINA", false);
            var s = new Settings { HudScale = 1.2f, NowPlaying = Settings.Toast.PauseOnly };
            s.Best[key] = [80, 160, 250, 340.5f];
            s.Save(file);
            var back = Settings.Load(file);
            Assert.Equal(340.5f, back.Best[key][^1]);
            Assert.Equal(Settings.Toast.PauseOnly, back.NowPlaying);
            // a scripted run without --data-dir never points at the real profile
            var real = Path.GetDirectoryName(Settings.FilePath)!;
            var scratch = Settings.RunFile(null, false);
            Assert.False(scratch.StartsWith(real, StringComparison.Ordinal));
            Assert.StartsWith(Path.GetTempPath(), scratch);
            Assert.Equal(Settings.FilePath, Settings.RunFile(null, true));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>The original hides the Impreza (IMP3) in every car select until the Story's last chapter is cleared; Bunta's defeat opens it too.</summary>
    [Fact]
    public void SecretCar_OpensWithTheStoryFinale()
    {
        var p = new Progress();
        Assert.True(Legend.CarLocked(Legend.SecretCar, p));
        p.Clear(Story.StoryMode.Key(Story.StoryText.Chapters.Length - 2));
        Assert.True(Legend.CarLocked(Legend.SecretCar, p));
        p.Clear(Story.StoryMode.Key(Story.StoryText.Chapters.Length - 1));
        Assert.False(Legend.CarLocked(Legend.SecretCar, p));
    }

    /// <summary>A breakaway winner (no goal time) is the winner with his lead, not DNF; the other is metres behind.</summary>
    [Fact]
    public void OnlineResult_BreakawayWinner()
    {
        var r = new Result(1, "BREAKAWAY", [new ResultEntry(1, 1, -1, 3673), new ResultEntry(0, 2, -1, 3361)]);
        Assert.Equal("WIN  +312 m", Versus.ValueOf(r, r.Entries[0]));
        Assert.Equal("312 m BEHIND", Versus.ValueOf(r, r.Entries[1]));
        var goal = new Result(2, "GOAL", [new ResultEntry(0, 1, 185.25f, 4900), new ResultEntry(1, 2, -1, 4700)]);
        Assert.Equal("3'05.250", Versus.ValueOf(goal, goal.Entries[0]));
        Assert.Equal("DNF", Versus.ValueOf(goal, goal.Entries[1]));
    }
}
