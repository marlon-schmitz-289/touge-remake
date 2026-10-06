using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Kansei.Core;
using Kansei.Graphics;
using Touge.Formats;
using Touge.Ui;

namespace Touge;

/// <summary>
///     A checked disc image: <see cref="Error"/> null = Initial D Special Stage (SLPM-65268) with its data, else why not
///     (missing, unreadable, no ISO 9660, another game, truncated). Reads only a few sectors.
/// </summary>
public sealed record Disc(string Path, string? Version, long Size, string? Error)
{
    public const string Serial = "SLPM_652.68";
    public bool Ok => Error == null;
    public string Title => "INITIAL D SPECIAL STAGE";
    public string Info => FormattableString.Invariant($"SLPM-65268  |  {(Version is { } v ? "VER " + v : "")}  |  {Size / 1e9:0.00} GB");

    /// <summary>
    ///     A typed, pasted or dropped path as a full path: quotes, ~, file:// URLs, Terminal's backslash escapes (not on Windows)
    ///     and a bare file name (Finder's Cmd+C copies only the name) looked up in <paramref name="dir"/> and the scan's places.
    /// </summary>
    public static string Resolve(string text, string? dir = null)
    {
        text = text.Trim().Trim('"', '\'');
        if (text == "") return text;
        if (text.StartsWith('~')) text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + text[1..];
        if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) text = Uri.UnescapeDataString(new Uri(text).LocalPath);
        if (!OperatingSystem.IsWindows() && !System.IO.Path.Exists(text) && text.Contains('\\'))
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\\(.)", "$1");
        if (!System.IO.Path.IsPathRooted(text))
            text = new[] { dir }.Concat(DiscScan.Places()).Where(d => d != null).Select(d => System.IO.Path.Combine(d!, text))
                .FirstOrDefault(System.IO.Path.Exists) ?? text;
        return System.IO.Path.GetFullPath(text);
    }

    public static Disc Check(string path)
    {
        Disc Bad(string why, long size = 0) => new(path, null, size, why);
        try
        {
            path = Resolve(path);
            if (Directory.Exists(path)) return Bad("THAT IS A FOLDER, NOT A DISC IMAGE");
            if (!File.Exists(path)) return Bad("FILE NOT FOUND: " + path);
            long size;
            byte[] pvd = new byte[2048];
            using (var fs = File.OpenRead(path))
            {
                size = fs.Length; // the target's size, also through a symlink (FileInfo.Length would be the link's)
                if (size < 17 * 2048) return Bad("NOT A DISC IMAGE (FILE TOO SMALL)", size);
                fs.Position = 16 * 2048;
                fs.ReadExactly(pvd);
            }
            if (pvd[0] != 1 || !pvd.AsSpan(1, 5).SequenceEqual("CD001"u8)) return Bad("NOT A DISC IMAGE (NO ISO 9660 VOLUME)", size);
            if ((long)BinaryPrimitives.ReadInt32LittleEndian(pvd.AsSpan(80)) * 2048 > size) return Bad("INCOMPLETE IMAGE: THE FILE IS CUT OFF (DOWNLOAD/COPY AGAIN)", size);
            using var iso = new Iso9660(path);
            var cnf = iso.Exists("SYSTEM.CNF") ? Encoding.ASCII.GetString(iso.ReadFile("SYSTEM.CNF")) : "";
            if (!cnf.Contains("BOOT2", StringComparison.OrdinalIgnoreCase)) return Bad("WRONG DISC: NOT A PLAYSTATION 2 GAME", size);
            if (!cnf.Contains(Serial, StringComparison.OrdinalIgnoreCase) || !iso.Exists(Serial))
            {
                var boot = cnf.Split('\\', ':', ';').FirstOrDefault(s => s.Length == 11 && s[4] == '_') ?? "ANOTHER GAME";
                return Bad($"WRONG GAME: {boot.Replace('_', '-').Replace(".", "")} IS NOT INITIAL D SPECIAL STAGE (SLPM-65268)", size);
            }
            if (!iso.Exists("CDVD/DATA/MODEL/COURSE.AFS")) return Bad("INCOMPLETE DISC: CDVD/DATA IS MISSING", size);
            var ver = cnf.Split('\n').Select(l => l.Split('=')).FirstOrDefault(p => p.Length == 2 && p[0].Trim() == "VER")?[1].Trim();
            return new Disc(path, ver, size, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Bad("CANNOT READ THE FILE: " + e.Message);
        }
        catch (Exception e)
        {
            return Bad("NOT A VALID DISC IMAGE: " + e.Message);
        }
    }
}

/// <summary>
///     Finds disc images in the usual places (Downloads, Desktop, Documents, home, current and app folder, mounted volumes and
///     drives) one folder level deep, in the background and within <see cref="Limit"/>; only valid discs are kept.
/// </summary>
public sealed class DiscScan
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(4);
    private readonly ConcurrentDictionary<string, Disc> _found = new(StringComparer.OrdinalIgnoreCase);
    private bool _done;
    private DateTime _until = DateTime.MaxValue;
    /// <summary>Finished, or past its deadline (a hanging network volume keeps its thread, not the screen).</summary>
    public bool Done => _done || DateTime.UtcNow > _until;
    public IReadOnlyList<Disc> Found => [.. _found.Values.OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase)];

    public static IEnumerable<string> Places()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] dirs = [Path.Combine(home, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), home, Environment.CurrentDirectory, AppContext.BaseDirectory];
        return dirs.Concat(Volumes().Select(v => v.Path)).Where(d => d != "" && Directory.Exists(d)).Distinct();
    }

    /// <summary>Mounted volumes and drives (browser root, scan): /Volumes on macOS, /media and /run/media on Linux, drive letters on Windows.</summary>
    public static IEnumerable<(string Name, string Path)> Volumes()
    {
        if (OperatingSystem.IsWindows())
            return DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => (d.Name.TrimEnd('\\'), d.RootDirectory.FullName));
        var user = Environment.UserName;
        string[] roots = OperatingSystem.IsMacOS() ? ["/Volumes"] : [$"/media/{user}", $"/run/media/{user}", "/media", "/mnt"];
        return roots.Where(Directory.Exists).SelectMany(r => Safe(() => Directory.GetDirectories(r))).Select(d => (Path.GetFileName(d), d));
    }

    private static string[] Safe(Func<string[]> list)
    {
        try { return list(); }
        catch (Exception) { return []; }
    }

    /// <summary>Starts the scan of <paramref name="places"/> (default <see cref="Places"/>) on the thread pool.</summary>
    public DiscScan Start(IEnumerable<string>? places = null)
    {
        _until = DateTime.UtcNow + Limit;
        Task.Run(() => Run(places ?? Places(), _until));
        return this;
    }

    /// <summary>The scan itself: each place and its direct subfolders, *.iso only, stops at <paramref name="until"/>.</summary>
    public void Run(IEnumerable<string> places, DateTime until)
    {
        _until = until;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, MaxRecursionDepth = 1, MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = FileAttributes.System };
        try
        {
            foreach (var place in places)
            {
                if (DateTime.UtcNow > until) break;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(place, "*.iso", opts); }
                catch (Exception) { continue; }
                try
                {
                    foreach (var f in files)
                    {
                        if (DateTime.UtcNow > until) break;
                        if (_found.ContainsKey(Path.GetFullPath(f))) continue;
                        var d = Disc.Check(f);
                        if (d.Ok) _found[d.Path] = d;
                    }
                }
                catch (Exception) { } // a folder vanished or locked mid-scan
            }
        }
        finally
        {
            _done = true;
            Console.WriteLine($"[Launcher] scan done: {_found.Count} disc(s){(DateTime.UtcNow > until ? " (time limit)" : "")}");
        }
    }
}

/// <summary>The last disc that started the game: one line with its path in last-disc.txt next to settings.json (not *.json: save slots copy those).</summary>
public static class LastDisc
{
    public static string File => Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, "last-disc.txt");

    public static string? Load(string? file = null)
    {
        file ??= File;
        try { return System.IO.File.Exists(file) && System.IO.File.ReadAllText(file).Trim() is { Length: > 0 } p ? p : null; }
        catch (Exception) { return null; }
    }

    public static void Save(string path, string? file = null)
    {
        file ??= File;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            System.IO.File.WriteAllText(file, path + Environment.NewLine);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Launcher] {file}: {e.Message}");
        }
    }
}

/// <summary>
///     The operating system's own open dialog for one .iso, run as a child process (osascript on macOS, zenity or kdialog on
///     Linux, PowerShell + WinForms on Windows); null result = cancelled or no dialog available.
/// </summary>
public static class NativeDialog
{
    public static Task<string?> PickIso() => Task.Run(() =>
    {
        (string File, string[] Args)[] tries = OperatingSystem.IsMacOS()
            ? [("osascript", ["-e", "POSIX path of (choose file with prompt \"Choose the Initial D Special Stage disc image (ISO)\")"])]
            : OperatingSystem.IsWindows()
                ? [("powershell", ["-NoProfile", "-STA", "-Command",
                    "Add-Type -AssemblyName System.Windows.Forms; $d = New-Object System.Windows.Forms.OpenFileDialog; $d.Filter = 'Disc image (*.iso)|*.iso|All files|*.*'; if ($d.ShowDialog() -eq 'OK') { $d.FileName }"])]
                : [("zenity", ["--file-selection", "--title=Choose the game disc (ISO)", "--file-filter=*.iso *.ISO"]), ("kdialog", ["--getopenfilename", ".", "*.iso *.ISO"])];
        foreach (var (file, args) in tries)
        {
            try
            {
                var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                return p.ExitCode == 0 && output != "" ? output : null;
            }
            catch (System.ComponentModel.Win32Exception) { } // not installed: next one
        }
        throw new PlatformNotSupportedException("NO SYSTEM FILE DIALOG FOUND, USE BROWSE");
    });
}

/// <summary>
///     Window host of the launcher (Touge started without an ISO): shows <see cref="LauncherScreen"/> until a valid disc is
///     chosen (or starts the remembered one right away), then loads the game in the same window and process and hands it every
///     tick; Options → GAME DISC comes back here. --shot takes one launcher frame (after the scan) as PNG and quits.
/// </summary>
public sealed class LauncherGame(Func<string, Action, TougeGame> newGame, bool pickAgain, string? shot, (int W, int H) shotSize, string? startMenu, string? drop, string? browse)
    : KanseiGame
{
    private readonly Overlay _overlay = new();
    private OverlayRenderer _overlayRenderer = null!;
    private TextRenderer _textRenderer = null!;
    private LauncherScreen _screen = null!;
    private TougeGame? _game;
    private string? _loading;
    private bool _back;
    private FrameCapture? _capture;
    private int _shotState;
    private float _shotWait;
    // TOUGE_AUTOPICK=1|change (end-to-end checks of a start without arguments; synthetic key presses need macOS accessibility rights):
    // once the scan is done, the first found disc is chosen as if picked in the list; "change" also leaves the game after 8 s
    // the way Options → GAME DISC does
    private bool _autoPick = Environment.GetEnvironmentVariable("TOUGE_AUTOPICK") is "1" or "change";
    private bool _autoChange = Environment.GetEnvironmentVariable("TOUGE_AUTOPICK") == "change";
    private float _autoT;

    public override void Load()
    {
        _overlay.Font = new SdfFont(System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Rajdhani-Bold.ttf")),
            Style.Glyphs);
        _overlayRenderer = new OverlayRenderer(Device);
        _textRenderer = new TextRenderer(Device, _overlay.Font);
        _screen = new LauncherScreen(new DiscScan().Start());
        if (LastDisc.Load() is { } last && !pickAgain && drop == null && startMenu != "browse")
        {
            var d = Disc.Check(last);
            if (d.Ok)
            {
                Console.WriteLine($"[Launcher] remembered disc OK: {d.Path} ({d.Info}) → starting the game");
                StartGame(d.Path);
                if (_game != null) return;
            }
            else
            {
                Console.WriteLine($"[Launcher] remembered disc unusable: {last}: {d.Error}");
                _screen.Message = ("LAST DISC: " + d.Error, true);
            }
        }
        if (startMenu == "browse") _screen.Browse(browse);
        if (drop != null) _screen.Picked = drop;
        if (shot != null) _capture = new FrameCapture(Device, shotSize.W, shotSize.H);
        Console.WriteLine("[Launcher] choose a disc");
    }

    private void StartGame(string path)
    {
        try
        {
            _game = newGame(path, () => _back = true);
            Share(_game);
            _game.Load();
            LastDisc.Save(path);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Launcher] {path}: {e}");
            try { _game?.Dispose(); }
            catch (Exception) { } // half-loaded: whatever was made goes with the process
            _game = null;
            _screen.Message = ("THE GAME COULD NOT START FROM THIS DISC: " + e.Message, true);
        }
    }

    public override void Tick(float dt) => _game?.Tick(dt);

    public override void Update(in GameTime time)
    {
        if (_game != null)
        {
            _game.Update(time);
            FrameCap = _game.FrameCap;
            if (_autoChange && (_autoT += time.DeltaTime) > 8)
            {
                Console.WriteLine("[Launcher] TOUGE_AUTOPICK=change: leaving the game as Options → GAME DISC does");
                (_autoChange, _back) = (false, true);
            }
            if (!_back) return;
            // Options → GAME DISC: the game goes, the launcher comes back
            Device.WaitIdle();
            _game.Dispose();
            (_game, _back, FrameCap) = (null, false, 0);
            _screen.Message = ("CHOOSE ANOTHER DISC", false);
            Console.WriteLine("[Launcher] back from the game");
            return;
        }
        if (_loading is { } path)
        {
            _loading = null;
            StartGame(path);
            return;
        }
        if (_shotState == 2)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(shot!))!);
            Png.Write(shot!, _capture!.Width, _capture.Height, _capture.ReadRgba());
            Console.WriteLine($"[Launcher] shot → {shot}");
            Window.ShouldClose = true;
            return;
        }
        if (Input.DroppedFile is { } dropped) _screen.Picked = dropped;
        if (_autoPick && _screen.Scan.Done && _screen.Scan.Found is [var first, ..])
        {
            _autoPick = false;
            Console.WriteLine($"[Launcher] TOUGE_AUTOPICK: {first.Path}");
            _screen.Picked = first.Path;
        }
        var s = Window.DpiScale;
        var r = _screen.Update(Input, time.DeltaTime, (Input.Mouse.X * s, Input.Mouse.Y * s));
        if (r == LauncherScreen.Result.Quit) Window.ShouldClose = true;
        else if (r == LauncherScreen.Result.Start) _loading = _screen.Chosen!.Path;
        // --shot: once the scan is done (or 6 s), settled
        if (_capture != null && _shotState == 0 && (_shotWait += time.DeltaTime) > 0.3f && (_screen.Scan.Done || _shotWait > 6)) _shotState = 1;
    }

    public override void Render(in FrameContext ctx)
    {
        if (_game != null)
        {
            _game.Render(ctx);
            return;
        }
        var cap = _shotState == 1 ? _capture : null;
        if (cap == null && Device.Offscreen) return; // hidden run: nothing to show until the screenshot
        var (w, h) = cap != null ? (cap.Width, cap.Height) : (Device.SwapchainWidth, Device.SwapchainHeight);
        var target = cap?.View ?? Device.CurrentSwapchainView;
        _overlay.Clear();
        _screen.Build(_overlay, w, h, _loading != null);
        _overlayRenderer.Draw(ctx.Encoder, _overlay, target, w, h);
        _textRenderer.Draw(ctx.Encoder, _overlay, target, w, h);
        if (cap == null) return;
        cap.Copy(ctx.Encoder);
        _shotState = 2;
    }

    public override void Dispose()
    {
        _game?.Dispose();
        _capture?.Dispose();
        _overlayRenderer.Dispose();
        _textRenderer.Dispose();
    }
}
