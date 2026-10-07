using System.Text.Json;

namespace Touge.Story;

/// <summary>
///     The text of the story scenes and manga dramas in one language, from <c>Translations/&lt;code&gt;.json</c> (<c>en.json</c>, <c>de.json</c> …)
///     in the profile folder (next to settings.json; kept across updates) or next to the program. Not in the repo (a translation of the
///     original's script): <c>Tools/texts.sh</c> fetches the private texts, the build copies them next to the program. Without a file the
///     story shows placeholders and the dramas run without subtitles. Layout: <c>{"name": "English", "scenes": {"&lt;chapter&gt;": [[part 0
///     lines], [part 1 lines], …]}, "manga": {"&lt;timeline&gt;": ["seconds|SPEAKER|text", …]}}</c>, scene lines "SPEAKER|text"
///     (<see cref="StoryText"/>, <see cref="MangaText"/>).
/// </summary>
public sealed class Translation
{
    // before Current: static fields start in file order
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public string Name { get; set; } = "";
    public Dictionary<int, string[][]> Scenes { get; set; } = [];
    public Dictionary<int, string[]> Manga { get; set; } = [];

    /// <summary>The language in use (<see cref="Use"/>; English if there is one until the settings say otherwise).</summary>
    public static Translation Current { get; private set; } = Load("en");

    /// <summary>Folders searched, the profile's first.</summary>
    public static string[] Dirs =>
        [Path.Combine(Path.GetDirectoryName(Ui.Settings.FilePath)!, "Translations"), Path.Combine(AppContext.BaseDirectory, "Translations")];

    /// <summary>Language codes with a file (read once; a new file needs a restart).</summary>
    public static IReadOnlyList<string> Available => _available ??=
        [.. Dirs.Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.json")).Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant()).Distinct().Order()];

    private static IReadOnlyList<string>? _available;

    public static void Use(string code) => Current = Load(code);

    /// <summary>The file of <paramref name="code"/>, empty when there is none or it is broken (the game runs on, with placeholders).</summary>
    public static Translation Load(string code)
    {
        foreach (var dir in Dirs)
        {
            var path = Path.Combine(dir, code + ".json");
            if (!File.Exists(path)) continue;
            try
            {
                return JsonSerializer.Deserialize<Translation>(File.ReadAllText(path), Json) ?? new();
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[Touge] Übersetzung {path} nicht lesbar ({e.Message})");
            }
        }
        return new();
    }
}
