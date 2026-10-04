using System.Numerics;
using Kansei.Physics;
using Touge.Ui;

namespace Touge.Tests;

public class OptionsTests
{
    /// <summary>A v1 file (no Version, one HighQuality switch, SE volume for menus too) keeps its records and choices and maps the switch onto the toggles.</summary>
    [Theory]
    [InlineData(false, Settings.Preset.Low)]
    [InlineData(true, Settings.Preset.Ultra)]
    public void Migrates_V1(bool high, Settings.Preset preset)
    {
        var json = $$"""
            { "HighQuality": {{(high ? "true" : "false")}}, "MusicOn": false, "MusicVolume": 0.3, "SoundVolume": 0.4, "HudOn": true, "MapMode": "Overview",
              "BumperCam": true, "Course": "USUI_NIT", "Car": "FD3S", "Paint": 2, "Manual": true, "Livery": "Stock",
              "Best": { "AKINA_A": [10, 20, 30, 40] } }
            """;
        var s = Settings.FromJson(json);
        Assert.Equal(Settings.CurrentVersion, s.Version);
        Assert.Equal(preset, s.QualityPreset);
        Assert.Equal((0.4f, 0.4f, 0.3f, false), (s.SoundVolume, s.MenuVolume, s.MusicVolume, s.MusicOn));
        Assert.Equal((Hud.MapMode.Overview, true, "USUI_NIT", "FD3S", 2, true, Touge.Formats.Livery.Stock),
            (s.MapMode, s.BumperCam, s.Course, s.Car, s.Paint, s.Manual, s.Livery));
        Assert.Equal([10f, 20, 30, 40], s.Best["AKINA_A"]);
        // new fields at their defaults
        Assert.Equal((1f, 1f, 60, 100, true, Settings.DisplayMode.Window), (s.MasterVolume, s.EngineVolume, s.Fov, s.RenderScale, s.VSync, s.Display));
    }

    [Fact]
    public void SaveLoad_RoundTrips_WithoutDerivedFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"touge-settings-{Guid.NewGuid():N}", "settings.json");
        try
        {
            var s = new Settings
            {
                Display = Settings.DisplayMode.Borderless, Width = 1920, Height = 1080, VSync = false, FrameCap = 144, RenderScale = 75,
                MasterVolume = 0.5f, EngineVolume = 0.7f, MenuVolume = 0.2f, Mph = true, SteerAssist = 0, DriftAssist = 2, Fov = 75, CameraShake = 0.3f,
            };
            s.QualityPreset = Settings.Preset.Medium;
            s.Best["AKINA_A"] = [1, 2, 3, 4];
            s.Save(path);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("HighQuality", text);
            Assert.DoesNotContain("QualityPreset", text);
            Assert.Contains("\"Version\": 2", text);
            Assert.False(File.Exists(path + ".tmp"));
            var t = Settings.Load(path);
            Assert.Equal(s.ToJson(), t.ToJson());
            Assert.Equal(Settings.Preset.Medium, t.QualityPreset);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void Load_BrokenOrForeignFiles_FallBackOrClamp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"touge-broken-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Equal(new Settings().ToJson(), Settings.Load(path).ToJson());
            File.WriteAllText(path, "[1, 2]");
            Assert.Equal(new Settings().ToJson(), Settings.Load(path).ToJson());
            Assert.Equal(new Settings().ToJson(), Settings.Load(path + ".missing").ToJson());
        }
        finally
        {
            File.Delete(path);
        }
        // out of range values, a future version with unknown fields
        var s = Settings.FromJson("""
            { "Version": 9, "Hologram": true, "MasterVolume": 7, "MusicVolume": -1, "Fov": 170, "RenderScale": 33, "FrameCap": 59,
              "SteerAssist": 5, "Width": 10, "Height": 10, "Display": "Fullscreen", "Course": null }
            """);
        Assert.Equal((Settings.CurrentVersion, 1f, 0f, Settings.FovMax, 100, 0, 2), (s.Version, s.MasterVolume, s.MusicVolume, s.Fov, s.RenderScale, s.FrameCap, s.SteerAssist));
        Assert.Equal((1600, 900, Settings.DisplayMode.Fullscreen, "AKINA_DAY"), (s.Width, s.Height, s.Display, s.Course));
        Assert.Equal(65, Settings.FromJson("""{ "Fov": 63 }""").Fov); // on the row's 5 degree grid
    }

    [Fact]
    public void Presets_DeriveFromToggles()
    {
        var s = new Settings();
        Assert.Equal(Settings.Preset.Ultra, s.QualityPreset);
        Assert.True(s.HighQuality);
        s.QualityPreset = Settings.Preset.High;
        Assert.Equal((true, true, true, true, false), (s.Msaa, s.Shadows, s.Ao, s.Bloom, s.Ssr));
        s.Ao = false;
        Assert.Equal(Settings.Preset.Custom, s.QualityPreset);
        s.HighQuality = false; // F2
        Assert.Equal((false, false, false, false, false), (s.Msaa, s.Shadows, s.Ao, s.Bloom, s.Ssr));
        // every preset is distinct, each adds to the one below
        var all = Enum.GetValues<Settings.Preset>().Where(p => p != Settings.Preset.Custom).Select(Settings.Toggles).ToArray();
        Assert.Equal(all.Length, all.Distinct().Count());
    }

    [Fact]
    public void Assists_ScaleThePhysicsKnobs()
    {
        var spec = CarSpecs.All["AE86T"];
        var s = new Settings();
        Assert.Equal(spec, s.Assisted(spec)); // defaults leave the car as tuned
        (s.SteerAssist, s.DriftAssist) = (0, 0);
        var a = s.Assisted(spec);
        Assert.Equal((0f, spec.DriftDamping * 0.5f), (a.CounterSteerAssist, a.DriftDamping));
        Assert.Equal(spec.MaxSteer, a.MaxSteer);
        // records: stock assists race the RECORDS list, any other mix its own
        Assert.Equal("AKINA_A+S0D0", s.RunKey("AKINA", false));
        (s.SteerAssist, s.DriftAssist) = (2, 1);
        Assert.Equal(Settings.BestKey("AKINA", false), s.RunKey("AKINA", false));
    }

    /// <summary>Section list → page → change values (wrap, slider ends) → back to the list → leave; sounds as the other menus.</summary>
    [Fact]
    public void Navigation_ChangesSettingsLive()
    {
        var s = new Settings();
        var o = new Options(s) { Resolutions = () => [(1280, 720), (1920, 1080)] };
        var sounds = new List<string>();
        (int, int, bool, bool) up = (0, -1, false, false), down = (0, 1, false, false), left = (-1, 0, false, false), right = (1, 0, false, false),
            ok = (0, 0, true, false), back = (0, 0, false, true);
        Assert.Equal("OPTIONS", o.Title);
        Assert.Equal(["GAME SETTING", "HUD", "SCREEN", "GRAPHICS", "SOUND", "CONTROLLER"], o.Pages.Select(p => p.Title));
        Assert.Equal(Options.Result.None, o.Update(up, sounds.Add)); // wraps to CONTROLLER
        Assert.Equal(5, o.Section);
        o.Update(up, sounds.Add);
        o.Update(ok, sounds.Add);
        Assert.Equal("SOUND", o.Title);
        Assert.Equal(Options.Result.Changed, o.Update(left, sounds.Add)); // MASTER 1.0 → 0.9
        Assert.Equal(0.9f, s.MasterVolume, 3);
        Assert.Equal(Options.Result.Changed, o.Update(right, sounds.Add));
        Assert.Equal(Options.Result.None, o.Update(right, sounds.Add)); // already full
        o.Update(back, sounds.Add);
        Assert.Null(o.Current);
        Assert.Equal(["SYS005", "SYS005", "SYS006", "SYS005", "SYS005", "BEEP001"], sounds);

        Assert.True(o.OpenPage("screen"));
        // the current window size (1600 x 900) is offered between the display's modes
        Assert.Equal(["1280 x 720", "1600 x 900", "1920 x 1080"], o.Current!.Rows[1].Values!());
        o.Update(down, null);
        o.Update(right, null);
        Assert.Equal((1920, 1080), (s.Width, s.Height));
        o.Update(right, null); // wraps
        Assert.Equal((1280, 720), (s.Width, s.Height));

        Assert.True(o.OpenPage("GRAPHICS"));
        o.Update(right, null); // ULTRA wraps to LOW
        Assert.Equal(Settings.Preset.Low, s.QualityPreset);
        o.Update(down, null);
        o.Update(ok, null); // MSAA on: no preset has only that
        Assert.Equal(Settings.Preset.Custom, s.QualityPreset);
        Assert.Equal("CUSTOM", o.Current!.Rows[0].Values!()[(int)s.QualityPreset]);

        o.Open();
        Assert.Equal(Options.Result.Leave, o.Update(back, null));
    }

    /// <summary>The extension point: rows added to a page, and a page with its own screen that leaves on its own.</summary>
    [Fact]
    public void ExtensionPages()
    {
        var s = new Settings();
        var o = new Options(s);
        var scale = 3;
        o.Find("HUD")!.Rows.Add(Options.Row.Choice("HUD SCALE", ["S", "M", "L", "XL"], () => scale, i => scale = i, "Size of the HUD."));
        var keys = 0;
        o.Pages.Add(new Options.Page("MUSIC LIST", "Pick the race tracks.")
        {
            Input = (k, _) =>
            {
                if (k.Back) return Options.Result.Leave;
                keys++;
                return Options.Result.None;
            },
            Draw = (_, _) => { },
        });
        Assert.True(o.OpenPage("hud"));
        o.Update((0, -1, false, false), null); // HUD SCALE, the last row (up wraps)
        o.Update((1, 0, false, false), null);
        Assert.Equal(0, scale);
        Assert.True(o.OpenPage("MUSICLIST"));
        o.Update((0, 1, false, false), null);
        Assert.Equal(1, keys);
        o.Update((0, 0, false, true), null);
        Assert.Null(o.Current);
    }

    [Fact]
    public void Units_ClusterReadsMph()
    {
        var r = new Cluster.Reading(5000, 160.934f, 3, true, 0, false, 0, Mph: true);
        Assert.Equal((100f, "mph"), (MathF.Round(r.Speed, 2), r.Unit));
        Assert.Equal((160.934f, "km/h"), ((r with { Mph = false }).Speed, (r with { Mph = false }).Unit));
    }

    [Fact]
    public void Menu_OptionsLeaveToTheMainMenu()
    {
        Vector2[] line = [new(0, 0), new(100, 0)];
        var catalog = new Catalog([new Catalog.Course("AKINA", "AKINA", ["DAY"], line, 7700, 465, true, false)],
            [new Catalog.Car("AE86T", "TOYOTA", "TRUENO", "FR", 130, 940, [1])]);
        var m = new Menu(catalog, new Settings());
        m.Open(Menu.Screen.Options, "AKINA_DAY", false, "AE86T", 0);
        m.Update((0, 0, true, false), 1 / 60f);
        Assert.Equal("GAME SETTING", m.Options.Title);
        Assert.Equal(Menu.Action.SettingsChanged, m.Update((1, 0, false, false), 1 / 60f));
        m.Update((0, 0, false, true), 1 / 60f);
        m.Update((0, 0, false, true), 1 / 60f);
        var actions = Enumerable.Range(0, 40).Select(_ => m.Update(default, 1 / 60f)).ToList();
        Assert.Contains(Menu.Action.Exit, actions);
        // reopening starts on the section list
        m.Open(Menu.Screen.Options, "AKINA_DAY", false, "AE86T", 0);
        Assert.Null(m.Options.Current);
    }
}
