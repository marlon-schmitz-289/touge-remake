using System.Text.Json;

namespace Touge;

/// <summary>
///     What the player has done in the game modes with a career — the one progress store of the game, shared by Story,
///     Legend of the Streets and SAVE &amp; LOAD (a slot carries this file): cleared keys and counters ("&lt;mode&gt;/&lt;item&gt;",
///     e.g. "story/03"; each mode owns its prefix) and Legend's rival records. One JSON next to the settings
///     (<c>progress.json</c>), saved through a temp file like <see cref="Ui.Settings"/>. v1 had Legend in its own legend.json:
///     <see cref="Load"/> takes it over, the next <see cref="Save"/> removes it. Test runs keep it in memory.
/// </summary>
public sealed class Progress
{
    /// <summary>1 = Story only (Legend in legend.json), 2 = with <see cref="Rivals"/>.</summary>
    public const int CurrentVersion = 2;
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

    // ------------------------------------------------------------ Legend of the Streets

    /// <summary>A Legend rival's record: wins, losses, largest winning gap in seconds (0: none yet).</summary>
    public sealed class Record
    {
        public int Wins { get; set; }
        public int Losses { get; set; }
        public float BestGap { get; set; }
    }

    /// <summary>Legend of the Streets per rival key ("AKINA/takumi", <see cref="Race.Legend.Entry.Key"/>).</summary>
    public Dictionary<string, Record> Rivals { get; set; } = [];

    /// <summary>A rival counts as beaten after one win.</summary>
    public bool Beaten(string key) => Rivals.TryGetValue(key, out var r) && r.Wins > 0;

    public Record Get(string key) => Rivals.GetValueOrDefault(key) ?? new Record();

    /// <summary>Counts a decided battle against <paramref name="key"/> (a draw counts as neither).</summary>
    public void Add(string key, Race.BattleOutcome outcome, float gap)
    {
        if (outcome is not (Race.BattleOutcome.Win or Race.BattleOutcome.Lose)) return;
        if (!Rivals.TryGetValue(key, out var r)) Rivals[key] = r = new Record();
        if (outcome == Race.BattleOutcome.Lose) r.Losses++;
        else
        {
            r.Wins++;
            r.BestGap = MathF.Max(r.BestGap, MathF.Abs(gap));
        }
    }

    /// <summary>Story chapters cleared and Legend rivals beaten (SAVE &amp; LOAD shows them per slot).</summary>
    public (int Story, int Legend) Summary() => (Cleared.Count(k => k.StartsWith("story/", StringComparison.Ordinal)), Rivals.Values.Count(r => r.Wins > 0));

    // ------------------------------------------------------------ file

    /// <summary>progress.json next to settings.json (follows --data-dir).</summary>
    public static string FilePath => Path.Combine(Path.GetDirectoryName(Ui.Settings.FilePath)!, "progress.json");

    /// <summary>v1's Legend file next to <paramref name="path"/>, taken over by <see cref="Load"/>.</summary>
    private static string LegacyLegend(string path) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "legend.json");

    private sealed class LegacyLegendFile
    {
        public Dictionary<string, Record>? Rivals { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Progress Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            var p = File.Exists(path) ? JsonSerializer.Deserialize<Progress>(File.ReadAllText(path), Json) ?? new() : new();
            // a missing or null collection (hand-edited file) comes back empty
            p.Cleared = new SortedSet<string>(p.Cleared ?? [], StringComparer.Ordinal);
            p.Counters = new SortedDictionary<string, int>(p.Counters ?? [], StringComparer.Ordinal);
            p.Rivals ??= [];
            var legacy = LegacyLegend(path);
            if (p.Rivals.Count == 0 && File.Exists(legacy))
            {
                try
                {
                    p.Rivals = JsonSerializer.Deserialize<LegacyLegendFile>(File.ReadAllText(legacy), Json)?.Rivals ?? [];
                    Console.WriteLine($"[Touge] Legend-Fortschritt aus {legacy} übernommen");
                }
                catch (JsonException e)
                {
                    Console.WriteLine($"[Touge] {legacy} nicht lesbar ({e.Message}), ignoriert");
                }
            }
            p.Rivals = p.Rivals.Where(kv => kv.Value != null && Race.Legend.Find(kv.Key) != null).ToDictionary(); // drop unknown rivals
            p.Version = CurrentVersion;
            return p;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Touge] Fortschritt nicht lesbar ({e.Message}), neu");
            Ui.Settings.KeepBroken(path);
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
            if (File.Exists(LegacyLegend(path))) File.Delete(LegacyLegend(path)); // taken over by Load
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Touge] Fortschritt nicht gespeichert: {e.Message}");
        }
    }
}
