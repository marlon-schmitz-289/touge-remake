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
