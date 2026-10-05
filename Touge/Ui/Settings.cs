using System.Text.Json;
using System.Text.Json.Nodes;

namespace Touge.Ui;

/// <summary>
///     Player settings and best runs, as JSON in the user's app-data folder (macOS ~/Library/Application Support/InitialDRemake, Windows
///     %APPDATA%\InitialDRemake). Missing or broken file: defaults. Only used with the menus; CLI test runs never read or write it.
///     <see cref="Version"/> marks the layout; older files are migrated on load (<see cref="Migrate"/>), values out of range clamped
///     (<see cref="Sanitize"/>).
/// </summary>
public sealed class Settings
{
    /// <summary>Layout of this build: 1 = single HighQuality switch (no Version field), 2 = full options (display, graphics toggles, mixer, gameplay).</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    // ------------------------------------------------------------ display (Options: SCREEN)
    public enum DisplayMode { Window, Borderless, Fullscreen }
    public DisplayMode Display { get; set; }
    /// <summary>Window size (OS points) in WINDOW, display mode in FULLSCREEN.</summary>
    public int Width { get; set; } = 1600;
    public int Height { get; set; } = 900;
    public bool VSync { get; set; } = true;
    /// <summary>Frames per second at most, 0 = unlimited.</summary>
    public int FrameCap { get; set; }
    /// <summary>3D scene resolution in percent of the window (<see cref="RenderScales"/>).</summary>
    public int RenderScale { get; set; } = 100;
    public static readonly int[] FrameCaps = [0, 30, 60, 120, 144, 240], RenderScales = [50, 67, 75, 85, 100, 125, 150];

    // ------------------------------------------------------------ graphics (Options: GRAPHICS; the preset is derived, <see cref="QualityPreset"/>)
    public enum Preset { Low, Medium, High, Ultra, Custom }
    public bool Msaa { get; set; } = true;
    public bool Shadows { get; set; } = true;
    public bool Ao { get; set; } = true;
    public bool Bloom { get; set; } = true;
    /// <summary>Screen-space reflections on the wet road.</summary>
    public bool Ssr { get; set; } = true;

    // ------------------------------------------------------------ sound (Options: SOUND), all 0..1
    public float MasterVolume { get; set; } = 1;
    public bool MusicOn { get; set; } = true;
    public float MusicVolume { get; set; } = 0.8f;
    /// <summary>Race songs switched off in Options → PLAYLIST (<see cref="Jukebox.Song.File"/>); all off = silence in races.</summary>
    public HashSet<string> MusicOff { get; set; } = [];
    /// <summary>Game sound effects (engine, tyres, walls, wind).</summary>
    public float SoundVolume { get; set; } = 1;
    /// <summary>Engine on top of <see cref="SoundVolume"/>.</summary>
    public float EngineVolume { get; set; } = 1;
    /// <summary>Menu sounds (cursor, decide, countdown).</summary>
    public float MenuVolume { get; set; } = 1;

    // ------------------------------------------------------------ gameplay (Options: GAME SETTING)
    public bool Mph { get; set; }
    /// <summary>Manual gearbox (the car flow's transmission choice, preselected there).</summary>
    public bool Manual { get; set; }
    /// <summary>Counter-steer help: 0 off, 1 low, 2 full (<see cref="CarSpec.CounterSteerAssist"/>).</summary>
    public int SteerAssist { get; set; } = 2;
    /// <summary>Slide stabiliser: 0 low, 1 normal, 2 high (<see cref="CarSpec.DriftDamping"/>).</summary>
    public int DriftAssist { get; set; } = 1;
    /// <summary>Start camera (C cycles while driving); old files' BumperCam = true maps to BUMPER (<see cref="Migrate"/>).</summary>
    public CameraView Camera { get; set; }
    /// <summary>Time attack ghost: the best run of the course and route drives along see-through (Options → GAME SETTING → GHOST).</summary>
    public bool Ghost { get; set; } = true;
    /// <summary>Chase camera field of view at standstill in degrees (it widens with speed).</summary>
    public int Fov { get; set; } = 60;
    /// <summary>Camera shake on wall hits 0..1.</summary>
    public float CameraShake { get; set; } = 1;
    public const int FovMin = 50, FovMax = 90;

    // ------------------------------------------------------------ HUD (Options: HUD)
    public bool HudOn { get; set; } = true;
    /// <summary>HUD size 0.8..1.3 (Options HUD SIZE, <see cref="Hud.Scale"/>).</summary>
    public float HudScale { get; set; } = 1;
    public Hud.MapMode MapMode { get; set; }
    /// <summary>Stickers and plates of the car.</summary>
    public Touge.Formats.Livery Livery { get; set; } = Touge.Formats.Livery.Rival;

    // ------------------------------------------------------------ last choice and records
    public string Course { get; set; } = "AKINA_DAY";
    public bool Reverse { get; set; }
    /// <summary>Weather FOG over <see cref="Course"/> (a _DAY or _NIT course).</summary>
    public bool Fog { get; set; }
    public string Car { get; set; } = "AE86T";
    public int Paint { get; set; }
    /// <summary>Bindings, wheel/pad tuning and force feedback (Options → CONTROLLER).</summary>
    public ControlSettings Controls { get; set; } = new();
    // ------------------------------------------------------------ versus (Ui/Versus): name shown to others, UDP port, last JOIN address, split layout
    public string PlayerName { get; set; } = "PLAYER";
    public int NetPort { get; set; } = Touge.Net.NetSession.DefaultPort;
    public string JoinAddress { get; set; } = "";
    public bool SplitVertical { get; set; }
    /// <summary>VERSUS → VS CPU: the last free battle lobby (Ui/FreeBattle).</summary>
    public FreeBattleChoice FreeBattle { get; set; } = new();

    /// <summary>Best run per course and direction (<see cref="BestKey"/>): cumulative sector splits, last = total.</summary>
    public Dictionary<string, float[]> Best { get; set; } = [];

    // ------------------------------------------------------------ graphics presets

    /// <summary>MSAA, shadows, AO, bloom, SSR of each preset: LOW none, MEDIUM shadows + bloom, HIGH + MSAA + AO, ULTRA + rain reflections.</summary>
    public static (bool Msaa, bool Shadows, bool Ao, bool Bloom, bool Ssr) Toggles(Preset p) => p switch
    {
        Preset.Low => (false, false, false, false, false),
        Preset.Medium => (false, true, false, true, false),
        Preset.High => (true, true, true, true, false),
        _ => (true, true, true, true, true),
    };

    /// <summary>The preset the toggles match, or Custom.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Preset QualityPreset
    {
        get
        {
            for (var p = Preset.Low; p < Preset.Custom; p++)
                if (Toggles(p) == (Msaa, Shadows, Ao, Bloom, Ssr)) return p;
            return Preset.Custom;
        }
        set
        {
            if (value != Preset.Custom) (Msaa, Shadows, Ao, Bloom, Ssr) = Toggles(value);
        }
    }

    /// <summary>All five quality toggles on (F2 switches ULTRA ↔ LOW; <c>--quality off</c> starts LOW).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HighQuality
    {
        get => QualityPreset == Preset.Ultra;
        set => QualityPreset = value ? Preset.Ultra : Preset.Low;
    }

    // ------------------------------------------------------------ file

    /// <summary>settings.json; its folder holds every other store (replays, save slots, photos); --data-dir moves it.</summary>
    public static string FilePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InitialDRemake", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static Settings Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            return File.Exists(path) ? FromJson(File.ReadAllText(path)) : new();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            Console.WriteLine($"[Touge] Einstellungen nicht lesbar ({e.Message}), Standardwerte");
            return new();
        }
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // write next to it, then replace: a crash mid-write must not lose the records
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson());
            File.Move(tmp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Touge] Einstellungen nicht gespeichert: {e.Message}");
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses a settings file of any version: migrated to <see cref="CurrentVersion"/>, then <see cref="Sanitize"/>d.</summary>
    public static Settings FromJson(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject o) throw new JsonException("settings.json: kein Objekt");
        Migrate(o);
        var s = o.Deserialize<Settings>(Json) ?? new();
        s.Sanitize();
        return s;
    }

    /// <summary>
    ///     Brings an older layout up to <see cref="CurrentVersion"/> in place. v1 (no Version): HighQuality → the five toggles
    ///     (true = ULTRA, as it switched all of them), SoundVolume also drove the menu sounds → MenuVolume. Newer files keep
    ///     what this build knows (unknown fields are ignored).
    /// </summary>
    public static void Migrate(JsonObject o)
    {
        var version = o["Version"]?.GetValue<int>() ?? 1;
        if (version < 2)
        {
            var high = o["HighQuality"]?.GetValue<bool>() ?? true;
            o.Remove("HighQuality");
            var (msaa, shadows, ao, bloom, ssr) = Toggles(high ? Preset.Ultra : Preset.Low);
            (o["Msaa"], o["Shadows"], o["Ao"], o["Bloom"], o["Ssr"]) = (msaa, shadows, ao, bloom, ssr);
            if (o["SoundVolume"] is { } se) o["MenuVolume"] = se.DeepClone();
        }
        if (o["BumperCam"] is { } bumper) // before the camera list: CHASE or BUMPER
        {
            if (o["Camera"] == null && bumper.GetValue<bool>()) o["Camera"] = nameof(CameraView.Bumper);
            o.Remove("BumperCam");
        }
        if (o["Camera"] is JsonValue cam && !(cam.TryGetValue(out string? name) && Enum.TryParse<CameraView>(name, true, out _))) o.Remove("Camera"); // a view this build lacks
        if (version > CurrentVersion) Console.WriteLine($"[Touge] Einstellungen aus neuerer Version {version}, nur bekannte Felder gelesen");
        o["Version"] = CurrentVersion;
    }

    /// <summary>Clamps everything a hand-edited or foreign file could put out of range.</summary>
    public void Sanitize()
    {
        static float Unit(float v) => float.IsFinite(v) ? Math.Clamp(v, 0, 1) : 1;
        (MasterVolume, MusicVolume, SoundVolume, EngineVolume, MenuVolume, CameraShake) =
            (Unit(MasterVolume), Unit(MusicVolume), Unit(SoundVolume), Unit(EngineVolume), Unit(MenuVolume), Unit(CameraShake));
        if (!Enum.IsDefined(Display)) Display = DisplayMode.Window;
        if (!Enum.IsDefined(MapMode)) MapMode = Hud.MapMode.Rotating;
        if (!Enum.IsDefined(Camera)) Camera = CameraView.Chase;
        if (!Enum.IsDefined(Livery)) Livery = Touge.Formats.Livery.Rival;
        (Width, Height) = Width is >= 640 and <= 16384 && Height is >= 360 and <= 16384 ? (Width, Height) : (1600, 900);
        if (!FrameCaps.Contains(FrameCap)) FrameCap = 0;
        HudScale = float.IsFinite(HudScale) ? MathF.Round(Math.Clamp(HudScale, 0.8f, 1.3f) * 10) / 10 : 1; // the row steps in 10 %
        if (!RenderScales.Contains(RenderScale)) RenderScale = 100;
        (SteerAssist, DriftAssist, Fov) = (Math.Clamp(SteerAssist, 0, 2), Math.Clamp(DriftAssist, 0, 2), (int)Math.Round(Math.Clamp(Fov, FovMin, FovMax) / 5.0) * 5); // the row steps in 5°
        Course ??= "AKINA_DAY";
        Car ??= "AE86T";
        Best ??= [];
        MusicOff ??= [];
        Controls ??= new();
        var name = Touge.Net.Protocol.Clip(PlayerName ?? "").Trim().ToUpperInvariant();
        PlayerName = name.Length == 0 ? "PLAYER" : name[..Math.Min(16, name.Length)];
        if (NetPort is < 1024 or > 65535) NetPort = Touge.Net.NetSession.DefaultPort;
        JoinAddress ??= "";
        FreeBattle ??= new();
        Version = CurrentVersion;
    }

    /// <summary>The car's physics with the chosen assists: counter-steer × 0 / 0.5 / 1, slide damping × 0.5 / 1 / 1.6.</summary>
    public Kansei.Physics.CarSpec Assisted(Kansei.Physics.CarSpec spec) => spec with
    {
        CounterSteerAssist = spec.CounterSteerAssist * SteerAssist / 2f,
        DriftDamping = spec.DriftDamping * DriftAssist switch { 0 => 0.5f, 1 => 1f, _ => 1.6f },
    };

    /// <summary>
    ///     "MYOUGI0", "AKINA_A", "AKINA_R_A": mountain runs are timed arch to arch (<see cref="CourseEnd"/>) since the "_A"
    ///     keys; older ones started up to 57 m earlier and are not comparable, so they stay unused in the file.
    /// </summary>
    public static string BestKey(string course, bool reverse) => course + (reverse ? "_R" : "") + (course.EndsWith('0') ? "" : "_A");

    /// <summary>Record key of a run with these assists: the stock handling (FULL/NORMAL) keeps <see cref="BestKey"/> (RECORDS), others get their own list.</summary>
    public string RunKey(string course, bool reverse) =>
        BestKey(course, reverse) + ((SteerAssist, DriftAssist) == (2, 1) ? "" : $"+S{SteerAssist}D{DriftAssist}");
}
