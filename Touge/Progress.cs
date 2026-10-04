using System.Text.Json;

namespace Touge;

/// <summary>
///     What the player has done in the game modes with a career (Story, Legend of the Streets …): cleared keys and counters,
///     one JSON next to the settings (<c>progress.json</c>), saved through a temp file like <see cref="Ui.Settings"/>. Keys are
///     "&lt;mode&gt;/&lt;item&gt;", e.g. "story/03"; each mode owns its prefix. Test runs keep it in memory.
/// </summary>
public sealed class Progress
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    /// <summary>Cleared items ("story/03").</summary>
    public SortedSet<string> Cleared { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Counters per key ("story/03/tries", "story/03/losses" …).</summary>
    public SortedDictionary<string, int> Counters { get; set; } = new(StringComparer.Ordinal);

    public bool IsCleared(string key) => Cleared.Contains(key);

    /// <summary>Marks <paramref name="key"/> cleared; true the first time.</summary>
    public bool Clear(string key) => Cleared.Add(key);

    public int Count(string key) => Counters.GetValueOrDefault(key);

    public int Add(string key, int by = 1) => Counters[key] = Count(key) + by;

    // ------------------------------------------------------------ file

    public static string FilePath { get; } = Path.Combine(Path.GetDirectoryName(Ui.Settings.FilePath)!, "progress.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Progress Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (!File.Exists(path)) return new();
            var p = JsonSerializer.Deserialize<Progress>(File.ReadAllText(path), Json) ?? new();
            // a missing or null collection (hand-edited file) comes back empty
            p.Cleared = new SortedSet<string>(p.Cleared ?? [], StringComparer.Ordinal);
            p.Counters = new SortedDictionary<string, int>(p.Counters ?? [], StringComparer.Ordinal);
            return p;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Touge] Fortschritt nicht lesbar ({e.Message}), neu");
            return new();
        }
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Touge] Fortschritt nicht gespeichert: {e.Message}");
        }
    }
}
