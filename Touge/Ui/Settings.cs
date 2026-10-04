using System.Text.Json;

namespace Touge.Ui;

/// <summary>
///     Player settings and best runs, as JSON in the user's app-data folder (macOS ~/Library/Application Support/InitialDRemake, Windows
///     %APPDATA%\InitialDRemake). Missing or broken file: defaults. Only used with the menus; CLI test runs never read or write it.
/// </summary>
public sealed class Settings
{
    public bool HighQuality { get; set; } = true;
    public bool MusicOn { get; set; } = true;
    public float MusicVolume { get; set; } = 0.8f;
    public bool HudOn { get; set; } = true;
    public Hud.MapMode MapMode { get; set; }
    public bool BumperCam { get; set; }
    public string Course { get; set; } = "AKINA_DAY";
    public bool Reverse { get; set; }
    public string Car { get; set; } = "AE86T";
    public int Paint { get; set; }
    /// <summary>Best run per course and direction ("AKINA", "AKINA_R"): cumulative sector splits, last = total.</summary>
    public Dictionary<string, float[]> Best { get; set; } = [];

    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InitialDRemake", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static Settings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Touge] Einstellungen nicht lesbar ({e.Message}), Standardwerte");
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Touge] Einstellungen nicht gespeichert: {e.Message}");
        }
    }

    public static string BestKey(string course, bool reverse) => course + (reverse ? "_R" : "");
}
