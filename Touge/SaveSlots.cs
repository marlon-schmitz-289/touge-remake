using System.Text.Json;

namespace Touge;

/// <summary>
///     SAVE &amp; LOAD: <see cref="Count"/> save slots (profiles) as folders <c>Saves/Slot&lt;n&gt;</c> in the app-data folder. A slot holds
///     a copy of every progress file — all <c>*.json</c> directly in the app-data folder: settings.json (options, last choice,
///     records) and whatever other modes keep there (Legend, Story …: no registration, a new store is saved by just living
///     there) — plus <c>slot.json</c> (name, play time, date). Loading copies them back (and removes progress files the slot
///     did not have), the profile's best runs too (<see cref="Folders"/>), and raises <see cref="Loaded"/> for stores that keep their state in memory. <c>Saves/saves.json</c> keeps
///     the active slot, autosave and the play time of the current profile (it is not a progress file itself).
/// </summary>
public sealed class SaveSlots(string root)
{
    public const int Count = 3, NameLength = 10;

    public sealed class Meta
    {
        public string Name { get; set; } = "";
        public double PlaySeconds { get; set; }
        public DateTime Saved { get; set; }
        /// <summary>Progress files in the slot (without .json).</summary>
        public List<string> Files { get; set; } = [];
        /// <summary>Records (best runs) in its settings.json.</summary>
        public int Records { get; set; }
    }

    public sealed class State
    {
        /// <summary>The slot last saved to or loaded from (autosave goes there), -1 none.</summary>
        public int Active { get; set; } = -1;
        public bool Autosave { get; set; } = true;
        public double PlaySeconds { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>After a load: the progress files are back in the app-data folder, stores should re-read them.</summary>
    public static event Action? Loaded;

    public static SaveSlots Default => new(Path.GetDirectoryName(Ui.Settings.FilePath)!);

    public string Root => root;
    private string Dir => Path.Combine(root, "Saves");
    public string SlotDir(int i) => Path.Combine(Dir, $"Slot{i + 1}");
    private string StatePath => Path.Combine(Dir, "saves.json");

    public State ReadState() => ReadJson<State>(StatePath) ?? new State();

    public void WriteState(State s) => WriteJson(StatePath, s);

    public Meta? Read(int i) => ReadJson<Meta>(Path.Combine(SlotDir(i), "slot.json"));

    /// <summary>Progress folders (relative to the app-data folder) a slot holds as well: the best runs (their times are the profile's records, they are its ghosts).</summary>
    public static readonly string[] Folders = [Path.Combine("Replays", "Best")];

    /// <summary>The progress files now: every *.json directly in the app-data folder.</summary>
    public string[] ProgressFiles() => Directory.Exists(root) ? [.. Directory.GetFiles(root, "*.json").Select(Path.GetFileName).Order().OfType<string>()] : [];

    /// <summary>Copies the progress files into slot <paramref name="i"/> (replacing what it held) with its name and play time; makes it the active slot.</summary>
    public void Save(int i, string name, double playSeconds)
    {
        var dir = SlotDir(i);
        var tmp = dir + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        var files = ProgressFiles();
        foreach (var f in files) File.Copy(Path.Combine(root, f), Path.Combine(tmp, f));
        foreach (var d in Folders) CopyDir(Path.Combine(root, d), Path.Combine(tmp, d));
        WriteJson(Path.Combine(tmp, "slot.json"), new Meta
        {
            Name = name, PlaySeconds = playSeconds, Saved = DateTime.Now, Files = [.. files.Select(Path.GetFileNameWithoutExtension).OfType<string>()],
            Records = RecordsIn(Path.Combine(root, "settings.json")),
        });
        // the old slot goes only once the new one is complete
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.Move(tmp, dir);
        var s = ReadState();
        (s.Active, s.PlaySeconds) = (i, playSeconds);
        WriteState(s);
    }

    /// <summary>Copies slot <paramref name="i"/>'s progress files back (progress files it did not have are removed); false if the slot is empty.</summary>
    public bool Load(int i)
    {
        var meta = Read(i);
        if (meta == null) return false;
        var dir = SlotDir(i);
        var files = Directory.GetFiles(dir, "*.json").Select(Path.GetFileName).OfType<string>().Where(f => f != "slot.json").ToHashSet();
        foreach (var f in ProgressFiles().Where(f => !files.Contains(f))) File.Delete(Path.Combine(root, f));
        foreach (var f in files)
        {
            var dst = Path.Combine(root, f);
            File.Copy(Path.Combine(dir, f), dst + ".tmp", true);
            File.Move(dst + ".tmp", dst, true);
        }
        foreach (var d in Folders)
        {
            var dst = Path.Combine(root, d);
            if (Directory.Exists(dst)) Directory.Delete(dst, true);
            CopyDir(Path.Combine(dir, d), dst);
        }
        var s = ReadState();
        (s.Active, s.PlaySeconds) = (i, meta.PlaySeconds);
        WriteState(s);
        Loaded?.Invoke();
        return true;
    }

    public void Delete(int i)
    {
        if (Directory.Exists(SlotDir(i))) Directory.Delete(SlotDir(i), true);
        var s = ReadState();
        if (s.Active == i)
        {
            s.Active = -1;
            WriteState(s);
        }
    }

    /// <summary>Settings properties that belong to this machine, not to a profile: a loaded slot leaves them as they are.</summary>
    public static readonly string[] MachineSettings =
    [
        nameof(Ui.Settings.Controls), nameof(Ui.Settings.Display), nameof(Ui.Settings.Width), nameof(Ui.Settings.Height), nameof(Ui.Settings.VSync),
        nameof(Ui.Settings.FrameCap), nameof(Ui.Settings.RenderScale),
    ];

    /// <summary>A loaded profile's settings into the live object (others hold references to it): all but <see cref="MachineSettings"/>.</summary>
    public static void CopyProfile(Ui.Settings from, Ui.Settings to)
    {
        foreach (var p in typeof(Ui.Settings).GetProperties())
            if (p is { CanRead: true, CanWrite: true } && !MachineSettings.Contains(p.Name)
                && p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false).Length == 0)
                p.SetValue(to, p.GetValue(from));
    }

    private static void CopyDir(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
    }

    private static int RecordsIn(string settings)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settings));
            return doc.RootElement.TryGetProperty("Best", out var best) && best.ValueKind == JsonValueKind.Object ? best.EnumerateObject().Count() : 0;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Save] {path} nicht lesbar: {e.Message}");
            return null;
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
        File.Move(path + ".tmp", path, true);
    }
}
