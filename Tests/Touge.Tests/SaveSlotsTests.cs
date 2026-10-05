using Touge.Replays;

namespace Touge.Tests;

public class SaveSlotsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "idss-save-" + Guid.NewGuid().ToString("N"));

    public SaveSlotsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private string P(string f) => Path.Combine(_root, f);

    [Fact]
    public void SaveLoadRestoresEveryProgressFile()
    {
        File.WriteAllText(P("settings.json"), """{ "Best": { "AKINA_A": [1, 2, 3, 200.5], "IROHA_A": [1, 2, 3, 150] } }""");
        File.WriteAllText(P("legend.json"), """{ "Stage": 3 }""");
        var best = Path.Combine(_root, SaveSlots.Folders[0]);
        Directory.CreateDirectory(best);
        File.WriteAllText(Path.Combine(best, "AKINA_A.rpl"), "takumi");
        var slots = new SaveSlots(_root);
        slots.Save(1, "TAKUMI", 3600);
        var meta = slots.Read(1)!;
        Assert.Equal("TAKUMI", meta.Name);
        Assert.Equal(2, meta.Records);
        Assert.Equal(["legend", "settings"], meta.Files);
        Assert.Equal(1, slots.ReadState().Active);
        Assert.Null(slots.Read(0));

        // play on: more progress, a new store appears, then load the slot
        File.WriteAllText(P("settings.json"), "{}");
        File.WriteAllText(P("story.json"), "{}");
        File.WriteAllText(Path.Combine(best, "AKINA_A.rpl"), "other profile");
        File.WriteAllText(Path.Combine(best, "IROHA_A.rpl"), "other profile");
        var loaded = 0;
        SaveSlots.Loaded += () => loaded++;
        Assert.True(slots.Load(1));
        Assert.Contains("200.5", File.ReadAllText(P("settings.json")));
        Assert.Equal("""{ "Stage": 3 }""", File.ReadAllText(P("legend.json")));
        Assert.False(File.Exists(P("story.json")), "progress the slot did not have");
        Assert.Equal("takumi", File.ReadAllText(Path.Combine(best, "AKINA_A.rpl"))); // its ghost, not the other profile's
        Assert.False(File.Exists(Path.Combine(best, "IROHA_A.rpl")));
        Assert.True(loaded >= 1);
        Assert.Equal(3600, slots.ReadState().PlaySeconds);

        // overwriting keeps one slot, deleting empties it and clears the active one
        slots.Save(1, "TAKUMI", 4000);
        Assert.Equal(4000, slots.Read(1)!.PlaySeconds);
        Assert.False(Directory.Exists(slots.SlotDir(1) + ".tmp"));
        slots.Delete(1);
        Assert.Null(slots.Read(1));
        Assert.Equal(-1, slots.ReadState().Active);
        Assert.False(slots.Load(1));
        Assert.DoesNotContain("saves.json", slots.ProgressFiles());
    }

    /// <summary>One progress store: Story and Legend in progress.json (v1's legend.json taken over), the slot shows both.</summary>
    [Fact]
    public void SlotShowsStoryAndLegend_LegacyLegendMigrates()
    {
        var p = new Progress();
        p.Clear("story/00");
        p.Clear("story/01");
        p.Save(P("progress.json"));
        File.WriteAllText(P("legend.json"), """{ "Version": 1, "Rivals": { "AKINA/kenji": { "Wins": 1, "Losses": 2, "BestGap": 1.5 }, "NOWHERE/x": { "Wins": 3 } } }""");
        var slots = new SaveSlots(_root);
        slots.Save(0, "TAKUMI", 10);
        var meta = slots.Read(0)!;
        Assert.Equal((2, 1), (meta.Story, meta.Legend));
        Assert.Equal("STORY 2/31  LEGEND 1/34", Touge.Ui.SaveLoadScreen.ProgressText(meta));
        Assert.Equal("TIME ATTACK", Touge.Ui.SaveLoadScreen.ProgressText(new SaveSlots.Meta()));

        var q = Progress.Load(P("progress.json"));
        Assert.True(q.Beaten("AKINA/kenji"));
        Assert.Equal(2, q.Get("AKINA/kenji").Losses);
        Assert.True(q.IsCleared("story/01"));
        Assert.Single(q.Rivals); // unknown rival dropped
        q.Save(P("progress.json"));
        Assert.False(File.Exists(P("legend.json")), "taken over into progress.json");
        Assert.True(Progress.Load(P("progress.json")).Beaten("AKINA/kenji"));
    }

    /// <summary>Two profiles in one session: save → progress → save → load the first (nothing of the second) → load the second (all back) → delete.</summary>
    [Fact]
    public void TwoProfilesRoundTrip_ProgressSettingsAndBestRuns()
    {
        var slots = new SaveSlots(_root);
        var best = Path.Combine(_root, SaveSlots.Folders[0]);
        new Touge.Ui.Settings { Car = "AE86T" }.Save(P("settings.json"));
        slots.Save(0, "TAKUMI", 10);
        Assert.Equal(["settings"], slots.Read(0)!.Files);

        // a Legend win, a record and its best run
        var p = new Progress();
        p.Add("AKINA/kenji", Touge.Race.BattleOutcome.Win, 4.5f);
        p.Save(P("progress.json"));
        new Touge.Ui.Settings { Car = "FD3S", Best = { ["AKINA_R_A"] = [100, 200, 300, 366.1f] } }.Save(P("settings.json"));
        Directory.CreateDirectory(best);
        File.WriteAllText(Path.Combine(best, "AKINA_R_A.rpl"), "run");
        slots.Save(1, "RYOSUKE", 200);
        Assert.Equal((1, 1, 1), (slots.Read(1)!.Legend, slots.Read(1)!.Records, slots.ReadState().Active));

        Assert.True(slots.Load(0));
        Assert.False(File.Exists(P("progress.json")), "slot 1 had no progress");
        Assert.False(Progress.Load(P("progress.json")).Beaten("AKINA/kenji"));
        var s = Touge.Ui.Settings.Load(P("settings.json"));
        Assert.Equal(("AE86T", 0), (s.Car, s.Best.Count));
        Assert.False(Directory.Exists(best) && Directory.GetFiles(best).Length > 0, "no best runs in slot 1");
        Assert.Equal((0, 10.0), (slots.ReadState().Active, slots.ReadState().PlaySeconds));

        Assert.True(slots.Load(1));
        Assert.True(Progress.Load(P("progress.json")).Beaten("AKINA/kenji"));
        s = Touge.Ui.Settings.Load(P("settings.json"));
        Assert.Equal(("FD3S", 366.1f), (s.Car, s.Best["AKINA_R_A"][^1]));
        Assert.Equal("run", File.ReadAllText(Path.Combine(best, "AKINA_R_A.rpl")));
        Assert.Equal(1, slots.ReadState().Active);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "Saves"), "*.tmp"));

        slots.Delete(0);
        Assert.Equal(1, slots.ReadState().Active); // deleting another slot keeps the one in use
        slots.Delete(1);
        Assert.Equal(-1, slots.ReadState().Active);
    }

    /// <summary>A load that cannot finish leaves the progress as it was: an unreadable slot changes nothing, a file that cannot be replaced is rolled back.</summary>
    [Fact]
    public void FailedLoadLeavesNoPartialCopy()
    {
        if (OperatingSystem.IsWindows()) return; // file modes below are Unix
        var slots = new SaveSlots(_root);
        File.WriteAllText(P("progress.json"), "slot");
        File.WriteAllText(P("settings.json"), "slot");
        slots.Save(0, "A", 1);
        File.WriteAllText(P("progress.json"), "now");
        File.WriteAllText(P("settings.json"), "now");

        var locked = Path.Combine(slots.SlotDir(0), "settings.json");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(() => slots.Load(0));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        Assert.Equal(("now", "now"), (File.ReadAllText(P("progress.json")), File.ReadAllText(P("settings.json"))));

        // progress.json goes first, settings.json then fails (its temp name is taken by a folder): progress.json is put back
        Directory.CreateDirectory(P("settings.json.tmp"));
        Assert.ThrowsAny<SystemException>(() => slots.Load(0)); // IOException or UnauthorizedAccessException, by platform
        Assert.Equal(("now", "now"), (File.ReadAllText(P("progress.json")), File.ReadAllText(P("settings.json"))));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "Saves"), "*.tmp"));
    }

    [Fact]
    public void LoadedProfileKeepsThisMachinesControlsAndDisplay()
    {
        var live = new Touge.Ui.Settings { Width = 2560, Height = 1440, Car = "AE86T", Mph = false };
        var controls = live.Controls;
        var loaded = new Touge.Ui.Settings { Width = 1024, Height = 768, Car = "FD3S", Mph = true, Best = { ["AKINA_A"] = [1, 2, 3, 4] } };
        SaveSlots.CopyProfile(loaded, live);
        Assert.Equal(("FD3S", true, 2560, 1440), (live.Car, live.Mph, live.Width, live.Height));
        Assert.Same(controls, live.Controls);
        Assert.Equal(4, live.Best["AKINA_A"][^1]);
    }

    [Fact]
    public void RecentReplaysArePrunedAndBrokenOnesSkipped()
    {
        var old = ReplayStore.Root;
        ReplayStore.Root = _root;
        try
        {
            var r = new Replay { Info = { Cars = [new("YOU", "AE86T", 0)] } };
            r.Info.Date = new DateTime(2025, 1, 1);
            var kept = ReplayStore.ToggleKept(ReplayStore.SaveRecent(r)); // the oldest, but kept
            Assert.True(ReplayStore.IsKept(kept));
            for (var i = 0; i < ReplayStore.Keep + 3; i++)
            {
                r.Info.Date = new DateTime(2026, 1, 1).AddMinutes(i);
                ReplayStore.SaveRecent(r);
            }
            File.WriteAllText(Path.Combine(_root, "zz_broken.rpl"), "nope");
            var list = ReplayStore.List(_root);
            Assert.Equal(ReplayStore.Keep + 1, list.Count);
            Assert.Equal(new DateTime(2026, 1, 1).AddMinutes(ReplayStore.Keep + 2), list[0].Info.Date); // newest first, oldest gone
            Assert.Equal(kept, list[^1].Path);
            Assert.False(ReplayStore.IsKept(ReplayStore.ToggleKept(kept)));
        }
        finally
        {
            ReplayStore.Root = old;
        }
    }
}
