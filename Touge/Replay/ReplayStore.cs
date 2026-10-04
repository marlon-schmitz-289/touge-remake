namespace Touge.Replays;

/// <summary>
///     Replay files in the app-data folder (next to settings.json): <c>Replays/</c> every finished run and battle (the newest
///     <see cref="Keep"/>), <c>Replays/Best/&lt;record key&gt;.rpl</c> the best run per course, direction and assists (its
///     time is the record; the time attack ghost).
/// </summary>
public static class ReplayStore
{
    public const int Keep = 40;

    public static string Root { get; set; } = Path.Combine(Path.GetDirectoryName(Ui.Settings.FilePath)!, "Replays");
    public static string BestDir => Path.Combine(Root, "Best");

    public static string BestPath(string runKey) => Path.Combine(BestDir, runKey + ".rpl");

    /// <summary>Writes <paramref name="r"/> as the newest recent replay, deletes the oldest beyond <see cref="Keep"/>; returns its path.</summary>
    public static string SaveRecent(Replay r)
    {
        var path = Path.Combine(Root, $"{r.Info.Date:yyyyMMdd-HHmmss}_{r.Info.Course}{(r.Info.Reverse ? "_R" : "")}.rpl");
        r.Save(path);
        foreach (var old in Directory.GetFiles(Root, "*.rpl").Order(StringComparer.Ordinal).SkipLast(Keep)) File.Delete(old);
        return path;
    }

    /// <summary>Headers of the replays in <paramref name="dir"/>, newest first; unreadable files (other versions, broken) are left out.</summary>
    public static List<(string Path, ReplayInfo Info)> List(string dir)
    {
        var list = new List<(string, ReplayInfo)>();
        if (!Directory.Exists(dir)) return list;
        foreach (var path in Directory.GetFiles(dir, "*.rpl"))
            try
            {
                using var f = File.OpenRead(path);
                list.Add((path, Replay.ReadInfo(f)));
            }
            catch (Exception e) when (e is InvalidDataException or IOException or System.Text.Json.JsonException or EndOfStreamException)
            {
                Console.WriteLine($"[Replay] {Path.GetFileName(path)} übersprungen: {e.Message}");
            }
        return [.. list.OrderByDescending(x => x.Item2.Date)];
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException e)
        {
            Console.WriteLine($"[Replay] {path} nicht gelöscht: {e.Message}");
        }
    }
}
