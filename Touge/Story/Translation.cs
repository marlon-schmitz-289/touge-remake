using System.Text.Json;
using Touge.Formats;

namespace Touge.Story;

/// <summary>
///     The game's text in one language, from <c>Translations/&lt;code&gt;.json</c> (<c>en.json</c>, <c>de.json</c> …) in the profile folder
///     (next to settings.json; kept across updates) or next to the program. Not in the repo (the scenes translate the original's script):
///     <c>Tools/texts.sh</c> fetches the private texts, the build copies them next to the program. Layout:
///     <c>{"name": "English", "scenes": {"&lt;chapter&gt;": [[part 0 lines], [part 1 lines], …]}, "manga": {"&lt;timeline&gt;":
///     ["seconds|SPEAKER|text", …]}, "ui": {"&lt;English on screen&gt;": "&lt;translation&gt;", …}}</c>, scene lines "SPEAKER|text"
///     (<see cref="StoryText"/>, <see cref="MangaText"/>). <c>ui</c> swaps any text the game draws whole (menus, titles, blurbs, hints;
///     <see cref="Map"/>); texts it lacks stay English and are listed in <c>&lt;code&gt;.missing.json</c> in the profile's
///     <c>Translations</c> folder, ready to fill in. Without a scene file the story shows placeholders and the dramas run without
///     subtitles. <c>ja</c> without a file is the original's Japanese script read from the disc (<see cref="Disc"/>).
/// </summary>
public sealed class Translation
{
    // before Current: static fields start in file order
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Name { get; set; } = "";
    public Dictionary<int, string[][]> Scenes { get; set; } = [];
    public Dictionary<int, string[]> Manga { get; set; } = [];
    public Dictionary<string, string> Ui { get; set; } = [];

    /// <summary>The disc of this start, for <c>ja</c> without a file (set by the game).</summary>
    public static string? Disc { get; set; }

    /// <summary>The language in use (<see cref="Use"/>; English if there is one until the settings say otherwise).</summary>
    public static Translation Current { get; private set; } = Load("en");

    public static string Code { get; private set; } = "en";

    /// <summary>After <see cref="Use"/>: the font needs the new language's characters.</summary>
    public static event Action? Changed;

    /// <summary>Folders searched, the profile's first.</summary>
    public static string[] Dirs =>
        [Path.Combine(Path.GetDirectoryName(Touge.Ui.Settings.FilePath)!, "Translations"), Path.Combine(AppContext.BaseDirectory, "Translations")];

    /// <summary>Language codes with a file, and <c>ja</c> with a disc (files read once; a new one needs a restart).</summary>
    public static IReadOnlyList<string> Available =>
        [.. (_files ??= [.. Dirs.Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.json"))
                .Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant()).Where(c => !c.EndsWith(".missing"))])
            .Concat(Disc != null ? ["ja"] : []).Distinct().Order()];

    private static List<string>? _files;

    public static void Use(string code)
    {
        SaveMissing();
        (Current, Code) = (Load(code), code);
        _lookup = Current.Ui.GetAlternateLookup<ReadOnlySpan<char>>();
        Changed?.Invoke();
    }

    /// <summary>The file of <paramref name="code"/> (or the disc's script for <c>ja</c>), empty when there is none or it is broken.</summary>
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
        return code == "ja" && Disc != null ? FromDisc(Disc) : new();
    }

    /// <summary>
    ///     The original's scenes (MG_OBJ STRnn.BIN) as on the disc. The script names a speaker only now and then; the others come from
    ///     the English file's line (lined up one for one, <c>--story-check</c>), in Japanese where <see cref="StoryText.Speakers"/> knows them.
    /// </summary>
    public static Translation FromDisc(string isoPath)
    {
        var t = new Translation { Name = "日本語" };
        var en = Load("en");
        var japanese = StoryText.Speakers.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.First().Key);
        string Speaker(int n, int part, int i, string disc) =>
            disc != "" ? disc : en.Scenes.GetValueOrDefault(n) is { } e && part < e.Length && i < e[part].Length && StoryText.Split(e[part][i]).Who is var who
                ? japanese.GetValueOrDefault(who, who) : "";
        try
        {
            using var iso = new Iso9660(isoPath);
            var objects = iso.OpenAfs("CDVD/DATA/MANGA/MG_OBJ.AFS");
            for (var n = 0; n < StoryScript.Chapters; n++)
                if (StoryHeadless.Script(objects, n) is { } script)
                    t.Scenes[n] = [.. script.Select((part, p) => part.Select((l, i) => $"{Speaker(n, p, i, l.Speaker)}|{l.Text.Replace("\n", "")}").ToArray())];
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Touge] Japanisches Skript nicht lesbar ({e.Message})");
        }
        return t;
    }

    /// <summary>Every character the language shows, for the font atlas.</summary>
    public string Chars() =>
        string.Concat(Scenes.Values.SelectMany(p => p).SelectMany(l => l).Concat(Manga.Values.SelectMany(l => l)).Concat(Ui.Values).SelectMany(s => s).Distinct());

    // ------------------------------------------------------------ on-screen text

    private static Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _lookup = Current.Ui.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly HashSet<string> Missing = [];

    /// <summary>
    ///     <see cref="Kansei.Graphics.SdfFont.Map"/>: the translation of a drawn text, else the text. Outside English an untranslated text
    ///     with capitals and no digits (not a time, score or count) is noted for <c>&lt;code&gt;.missing.json</c>.
    /// </summary>
    public static ReadOnlySpan<char> Map(ReadOnlySpan<char> text)
    {
        if (_lookup.TryGetValue(text, out var t)) return t;
        if (Code != "en" && text.ContainsAnyInRange('A', 'Z') && !text.ContainsAnyInRange('0', '9'))
            lock (Missing)
                if (!Missing.GetAlternateLookup<ReadOnlySpan<char>>().Contains(text)) Missing.Add(text.ToString());
        return text;
    }

    /// <summary>Adds the texts the language lacks to <c>Translations/&lt;code&gt;.missing.json</c> in the profile folder (English → "").</summary>
    public static void SaveMissing()
    {
        lock (Missing)
        {
            if (Missing.Count == 0) return;
            try
            {
                var path = Path.Combine(Dirs[0], Code + ".missing.json");
                var all = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [] : [];
                foreach (var m in Missing) all.TryAdd(m, "");
                Missing.Clear();
                Directory.CreateDirectory(Dirs[0]);
                File.WriteAllText(path, JsonSerializer.Serialize(new SortedDictionary<string, string>(all), Json));
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[Touge] Fehlende Texte nicht gespeichert ({e.Message})");
            }
        }
    }
}
