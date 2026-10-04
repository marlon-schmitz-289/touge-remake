using Kansei.Audio;
using Touge.Formats;

namespace Touge;

/// <summary>
///     Front-end sound as in the original: UI sounds from SYSSE.BIN, played by name like its sub_1781D0 (SYS005 cursor,
///     SYS006 decide, BEEP001 back/blocked, sys002 PRESS START, alarm_02 pause opens) and menu music from BGM.AFS
///     (sub_177AD0: gam/WORRY/TOKYO/JOY.adx …), streamed with the ADX loop points. UI sounds ignore the device's Sfx
///     scale (the game's loops are muted in menus) and play at <see cref="Volume"/>. Every trigger is logged.
/// </summary>
public sealed class MenuAudio : IDisposable
{
    private static readonly string[] Names = ["SYS005", "SYS006", "BEEP001", "sys002", "alarm_02", "CAR010", "CAR011", "NAME001"];
    private readonly AudioDevice _dev;
    private readonly Afs _bgm, _themes;
    private readonly Dictionary<string, AudioDevice.Clip> _se = new(StringComparer.OrdinalIgnoreCase);
    private int _request;

    /// <summary>UI sound volume 0..1 (Settings).</summary>
    public float Volume { get; set; } = 1;
    /// <summary>BGM.AFS (else MG_BGM.AFS) entry playing (or loading), null = none.</summary>
    public string? Track { get; private set; }
    /// <summary>Time source for the log (seconds); the offline capture sets its own.</summary>
    public Func<double>? Clock { get; set; }

    public MenuAudio(Iso9660 iso, AudioDevice dev)
    {
        _dev = dev;
        var sys = GameAudio.SysSe(iso);
        foreach (var n in Names)
        {
            var s = sys(n + ".vag");
            _se[n] = dev.CreateClip(s.Pcm, 1, s.Rate);
        }
        _bgm = iso.OpenAfs("CDVD/DATA/SOUND/BGM.AFS");
        _themes = iso.OpenAfs("CDVD/DATA/MANGA/MG_BGM.AFS"); // the characters' themes (Legend VS card)
    }

    private string Stamp => Clock is { } c ? $"{c():0.00} s " : "";

    public void Play(string name)
    {
        Console.WriteLine($"[Menu] {Stamp}SE {name}");
        _dev.PlaySfx(_se[name], Volume, ui: true);
    }

    /// <summary>
    ///     Switches the menu music to <paramref name="track"/> (BGM.AFS name, null = stop); the same track keeps playing.
    ///     The ADX is read off the main thread unless <paramref name="background"/> is false.
    /// </summary>
    public void Music(string? track, bool background = true)
    {
        if (track == Track) return;
        Track = track;
        var request = ++_request;
        if (track == null)
        {
            _dev.StopMusic();
            Console.WriteLine($"[Menu] {Stamp}BGM stop");
            return;
        }
        var stamp = Stamp;
        void Start()
        {
            var adx = new Adx(_bgm.Find(track) is { } e ? _bgm.Read(e) : _themes.Read(_themes.Find(track)!.Value));
            if (request != _request) return; // superseded while loading
            var reader = adx.Open(loop: true);
            var once = Jingle(track);
            _dev.PlayMusic(dst =>
            {
                var n = reader.Read(dst);
                if (n == 0 && adx.Loop == null && !once) n = (reader = adx.Open(loop: false)).Read(dst); // no loop point (gam.adx): from the top
                return n;
            }, adx.Channels, adx.SampleRate);
            var loop = adx.Loop is var (a, b) ? $"loop {a / (float)adx.SampleRate:0.00}–{b / (float)adx.SampleRate:0.00} s"
                : once ? "jingle, once" : "no loop point, repeats whole";
            Console.WriteLine($"[Menu] {stamp}BGM {track} ({adx.SampleCount / (float)adx.SampleRate:0.0} s, {loop})");
        }
        if (background) Task.Run(Start);
        else Start();
    }

    /// <summary>Race-end jingles (WIN/LOSE/TIMEUP, the original's group 2 without loop) play once.</summary>
    private static bool Jingle(string track) => track is "WIN.adx" or "LOSE.adx" or "TIMEUP.adx";

    public void Dispose()
    {
        foreach (var c in _se.Values) c.Dispose();
    }
}
