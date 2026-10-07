using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Touge;

/// <summary>
///     Updates from the GitHub releases (.github/workflows/release.yml): <see cref="Check"/> asks for the latest one in the
///     background at a player's start; a newer one puts UPDATE TO x.y.z on the main menu (<see cref="Label"/>), deciding it
///     <see cref="Download"/>s this platform's package (Touge-&lt;rid&gt;.zip/.tar.gz, Tools/publish.sh) and unpacks it next to
///     the install, then <see cref="InstallAndRestart"/> swaps it in once the game has closed and starts the new one.
///     Only packaged builds update: <see cref="Root"/> is null for dotnet run/build (out of bin/). Translations a player put
///     into the app folder move along (Windows/Linux; the packages have none).
/// </summary>
public static class Updater
{
    public const string Repo = "marlon-schmitz-289/touge-remake";

    public enum State { None, Available, Downloading, Ready, Failed }

    public static State Status { get; private set; }
    /// <summary>Version of the newer release (tag without v).</summary>
    public static string? Latest { get; private set; }
    /// <summary>Download progress 0..1.</summary>
    public static float Progress { get; private set; }

    private static string? _url, _staged;

    public static Version Current => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

    /// <summary>
    ///     What gets replaced: the Touge.app bundle (macOS) or the folder with Touge(.exe); null when not a package.
    ///     ponytail: "dev build" = runs out of a bin/ folder; a flag baked in by Tools/publish.sh if that ever guesses wrong
    /// </summary>
    public static string? Root
    {
        get
        {
            var dir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            var sep = Path.DirectorySeparatorChar;
            if (dir.Contains($"{sep}bin{sep}")) return null;
            if (!OperatingSystem.IsMacOS()) return dir;
            return dir.EndsWith($".app{sep}Contents{sep}MacOS") ? Path.GetFullPath(Path.Combine(dir, "..", "..")) : null;
        }
    }

    /// <summary>This platform's package in a release (Tools/publish.sh names).</summary>
    public static string Asset => $"Touge-{RuntimeInformation.RuntimeIdentifier}{(OperatingSystem.IsLinux() ? ".tar.gz" : ".zip")}";

    /// <summary>Main-menu row (FrontEnd.UpdateRow), null = nothing to offer.</summary>
    public static string? Label => Status switch
    {
        State.Available => $"UPDATE TO {Latest}",
        State.Downloading => $"UPDATING {Progress * 100:0}%",
        State.Ready => "RESTARTING...",
        State.Failed => "UPDATE FAILED - RETRY",
        _ => null,
    };

    /// <summary>Release tag <paramref name="tag"/> (v0.3.0) is newer than <paramref name="current"/> (assembly version, 4 parts).</summary>
    public static bool Newer(string tag, Version current) =>
        Version.TryParse(tag.TrimStart('v', 'V'), out var v) &&
        new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

    private static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Touge-Updater");
        return http;
    }

    /// <summary>Looks for a newer release in the background (packages only); offline or rate-limited: nothing happens.</summary>
    public static void Check()
    {
        if (Root == null) return;
        Task.Run(async () =>
        {
            try
            {
                using var http = Http();
                using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
                var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                if (!Newer(tag, Current)) return;
                foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
                    if (a.GetProperty("name").GetString() == Asset)
                    {
                        (_url, Latest, Status) = (a.GetProperty("browser_download_url").GetString(), tag.TrimStart('v', 'V'), State.Available);
                        Console.WriteLine($"[Update] {Latest} available (running {Current.ToString(3)})");
                    }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Update] check failed: {e.Message}");
            }
        });
    }

    /// <summary>Downloads and unpacks the package next to the install (same volume: the swap is a rename); Ready when done.</summary>
    public static void Download()
    {
        if (Status is not (State.Available or State.Failed) || _url == null || Root is not { } root) return;
        (Status, Progress) = (State.Downloading, 0);
        Task.Run(async () =>
        {
            var stage = Stage(root);
            try
            {
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                Directory.CreateDirectory(stage);
                var file = Path.Combine(stage, Asset);
                using (var http = Http())
                using (var res = await http.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead))
                {
                    res.EnsureSuccessStatusCode();
                    var total = res.Content.Headers.ContentLength ?? 0;
                    await using var src = await res.Content.ReadAsStreamAsync();
                    await using var dst = File.Create(file);
                    var buf = new byte[1 << 16];
                    long done = 0;
                    for (int n; (n = await src.ReadAsync(buf)) > 0;)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n));
                        done += n;
                        if (total > 0) Progress = (float)done / total;
                    }
                }
                var unpacked = Path.Combine(stage, "new");
                Unpack(file, unpacked);
                var fresh = Path.Combine(unpacked, OperatingSystem.IsMacOS() ? "Touge.app" : "Touge");
                if (!Directory.Exists(fresh)) throw new IOException($"{Asset} has no {Path.GetFileName(fresh)}");
                // the player's own translations in the app folder (Translation.cs looks there too); not into a macOS bundle:
                // an added file breaks its signature (there they belong in the profile folder, which Translation.cs reads first)
                var mine = Path.Combine(AppContext.BaseDirectory, "Translations");
                var theirs = Path.Combine(fresh, "Translations");
                if (!OperatingSystem.IsMacOS() && Directory.Exists(mine))
                {
                    Directory.CreateDirectory(theirs);
                    foreach (var f in Directory.GetFiles(mine))
                        if (!File.Exists(Path.Combine(theirs, Path.GetFileName(f)))) File.Copy(f, Path.Combine(theirs, Path.GetFileName(f)));
                }
                (_staged, Status) = (fresh, State.Ready);
                Console.WriteLine($"[Update] {Latest} ready: {fresh}");
            }
            catch (Exception e)
            {
                Status = State.Failed;
                Console.WriteLine($"[Update] download failed: {e.Message}");
                try { Directory.Delete(stage, true); }
                catch (Exception) { } // half-written: the next try clears it first
            }
        });
    }

    private static string Stage(string root) => Path.Combine(Path.GetDirectoryName(root)!, ".touge-update");

    private static void Unpack(string file, string dir)
    {
        if (file.EndsWith(".tar.gz"))
        {
            Directory.CreateDirectory(dir);
            using var gz = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, dir, true); // keeps the executable bits
        }
        else if (OperatingSystem.IsMacOS())
        {
            // ditto as in Tools/publish.sh: the bundle's signature and modes come out as packed
            using var p = Process.Start("ditto", ["-x", "-k", file, dir]);
            p.WaitForExit();
            if (p.ExitCode != 0) throw new IOException($"ditto: exit {p.ExitCode}");
        }
        else ZipFile.ExtractToDirectory(file, dir);
    }

    /// <summary>
    ///     Once the process ends (the caller closes the window): the new version replaces <see cref="Root"/> and starts.
    ///     macOS/Linux swap the folders at exit (a running program's files can be moved); Windows locks them, so a small
    ///     script waits for this process to end, mirrors the new files over and starts Touge.exe.
    /// </summary>
    public static void InstallAndRestart()
    {
        if (Status != State.Ready || _staged is not { } fresh || Root is not { } root) return;
        _staged = null; // once
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                var stage = Stage(root);
                if (OperatingSystem.IsWindows())
                {
                    var pid = Environment.ProcessId;
                    var script = Path.Combine(Path.GetTempPath(), "touge-update.cmd");
                    File.WriteAllText(script, $"""
                        @echo off
                        :wait
                        tasklist /fi "PID eq {pid}" 2>nul | find "{pid}" >nul && (timeout /t 1 /nobreak >nul & goto wait)
                        robocopy "{fresh}" "{root}" /mir /xd Translations /njh /njs /nfl /ndl /np >nul
                        rmdir /s /q "{stage}"
                        start "" "{Path.Combine(root, "Touge.exe")}"
                        (goto) 2>nul & del "%~f0"
                        """);
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false });
                    return;
                }
                var old = root + ".old";
                if (Directory.Exists(old)) Directory.Delete(old, true);
                Directory.Move(root, old);
                try { Directory.Move(fresh, root); }
                catch (Exception)
                {
                    Directory.Move(old, root); // the old version stays
                    throw;
                }
                Directory.Delete(old, true);
                Directory.Delete(stage, true);
                if (OperatingSystem.IsMacOS()) Process.Start("open", ["-n", root]);
                else Process.Start(Path.Combine(root, "Touge"));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[Update] install failed: {e.Message}");
            }
        };
    }
}
