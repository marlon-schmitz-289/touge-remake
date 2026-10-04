using Kansei.Audio;
using Touge.Formats;
using Touge.Ui;

namespace Touge;

/// <summary>
///     Race music as a jukebox over RACEBGM.AFS, independent of the course: every song plays once to its end (no loop),
///     then the next comes from <see cref="Shuffle"/> (no repeats until all enabled songs played). Songs switched off in
///     Options (<see cref="Settings.MusicOff"/>) are skipped; all off = silence. <see cref="Stop"/> keeps the song's
///     position, <see cref="Play"/> resumes it (menus with their own music in between, course loads). One instance per game.
/// </summary>
public sealed class Jukebox
{
    /// <summary>A race song: RACEBGM.AFS entry name without ".ADX", display title and artist.</summary>
    public sealed record Song(string File, string Title, string Artist);

    /// <summary>
    ///     The 31 race songs in the ELF's table order (0x1C59D0), titles/artists from the game's soundtrack album
    ///     "Super Eurobeat presents Initial D Special Stage Original Soundtracks" (the disc names files only).
    /// </summary>
    public static readonly Song[] Songs =
    [
        new("SPACEBOY", "SPACE BOY", "DAVE RODGERS"), new("NIGHT_OF_FIRE", "NIGHT OF FIRE", "NIKO"),
        new("DONT_STOP_THE_MUSIC", "DON'T STOP THE MUSIC", "LOU GRANT"), new("LOVE_IS_IN_DANGER", "LOVE IS IN DANGER", "PRISCILLA"),
        new("KILLING_MY_LOVE", "KILLING MY LOVE", "LESLIE PARRISH"), new("RUNNING_IN_THE_90S", "RUNNING IN THE 90'S", "MAX COVERI"),
        new("GRAND_PRIX", "GRAND PRIX", "MEGA NRG MAN"), new("BEAT_OF_THE_RISING_SUN", "BEAT OF THE RISING SUN", "DAVE RODGERS"),
        new("HEART_BEAT", "HEARTBEAT", "NATHALIE"), new("ROCK_ME_TO_THE_TOP", "ROCK ME TO THE TOP", "DUSTY"),
        new("STATION_TO_STATION", "STATION TO STATION", "DERRECK SIMONS"), new("SPEED_SPEED_BOY", "SPEEDY SPEED BOY", "MARKO POLO"),
        new("NO_ONE_SLEEP_IN_TOKYO", "NO ONE SLEEP IN TOKYO", "EDO BOYS"), new("REMEMBER_ME", "REMEMBER ME", "LESLIE PARRISH"),
        new("BACK_ON_THE_ROCKS", "BACK ON THE ROCKS", "MEGA NRG MAN"), new("DONT_STAND_SO_CLOSE", "DON'T STAND SO CLOSE", "DR. LOVE"),
        new("WHITE_LIGHT", "WHITE LIGHT", "MR. GROOVE"), new("SAVE_ME", "SAVE ME", "LESLIE PARRISH"),
        new("BURNING_DESIRE", "BURNING DESIRE", "MEGA NRG MAN"), new("GET_ME_POWER", "GET ME POWER", "MEGA NRG MAN"),
        new("MIKADO", "MIKADO", "DAVE MC LOUD"), new("CRAZY_FOR_LOVE", "CRAZY FOR LOVE", "DUSTY"), new("STAY", "STAY", "VICTORIA"),
        new("100", "100", "DAVE RODGERS"), new("DONT_YOU", "DON'T YOU (FORGET ABOUT MY LOVE)", "SOPHIE"),
        new("WEST_END_GUY", "WEST END GUY", "DIGITAL PLANET"), new("CRAZY_FOR_YOUR_LOVE", "CRAZY FOR YOUR LOVE", "MORRIS"),
        new("I_NEED_YOUR_LOVE", "I NEED YOUR LOVE", "DAVE SIMON"), new("BIG_IN_JAPAN", "BIG IN JAPAN", "ROBERT PATTON"),
        new("CRAZY_NIGHT", "CRAZY NIGHT", "BOYS BAND"), new("EXPRESS_LOVE", "EXPRESS LOVE", "MEGA NRG MAN"),
    ];

    private readonly AudioDevice _dev;
    private readonly Afs _afs;
    private readonly Settings _settings;
    private readonly Shuffle _shuffle;
    private int _request, _count;
    private volatile bool _ended;
    private PcmSource? _source;
    private int _channels, _rate;

    /// <summary>Song playing (or paused by <see cref="Stop"/>); null before the first or when every song is off.</summary>
    public Song? Current { get; private set; }
    /// <summary>Seconds since <see cref="Current"/> started or resumed (now-playing toast).</summary>
    public float Since { get; private set; } = float.MaxValue;
    public bool Playing { get; private set; }
    /// <summary>Time source for the log (seconds).</summary>
    public Func<double>? Clock { get; set; }

    public Jukebox(Iso9660 iso, AudioDevice dev, Settings settings, int seed = 0)
    {
        (_dev, _settings) = (dev, settings);
        _afs = iso.OpenAfs("CDVD/DATA/SOUND/RACEBGM.AFS");
        _shuffle = new Shuffle(Songs.Length, seed == 0 ? new Random() : new Random(seed));
    }

    private bool Enabled(int i) => !_settings.MusicOff.Contains(Songs[i].File) && _afs.Find(Songs[i].File + ".ADX") != null;

    /// <summary>Starts the music: resumes the paused song if it is still enabled, else the next one.</summary>
    public void Play(bool background = true)
    {
        if (Playing) return;
        Playing = true;
        if (_source != null && !_ended && Current != null && !_settings.MusicOff.Contains(Current.File))
        {
            _dev.PlayMusic(_source, _channels, _rate);
            Since = 0;
            Log($"resume {Current.Title}");
        }
        else Next(background);
    }

    /// <summary>Silence, keeping the song's position for <see cref="Play"/>.</summary>
    public void Stop()
    {
        if (!Playing) return;
        Playing = false;
        _request++;
        _dev.StopMusic();
    }

    /// <summary>Next shuffled song (M / pad D-pad right, or the current one ended); silence when none is enabled.</summary>
    public void Next(bool background = true)
    {
        Playing = true;
        var request = ++_request;
        var i = _shuffle.Next(Enabled);
        if (i < 0)
        {
            (Current, _source) = (null, null);
            _dev.StopMusic();
            Log("no song enabled, silence");
            return;
        }
        var song = Songs[i];
        var n = ++_count;
        void Start()
        {
            var adx = new Adx(_afs.Read(_afs.Find(song.File + ".ADX")!.Value));
            if (request != _request) return; // superseded (next / stop) while loading
            var reader = adx.Open(loop: false);
            PcmSource source = dst =>
            {
                var k = reader.Read(dst);
                if (k == 0) _ended = true;
                return k;
            };
            _dev.PlayMusic(source, adx.Channels, adx.SampleRate); // stops the old stream first, so its end flag can be cleared
            _ended = false;
            (Current, _source, _channels, _rate, Since) = (song, source, adx.Channels, adx.SampleRate, 0);
            Log($"#{n} {song.Title} - {song.Artist} ({adx.SampleCount / (float)adx.SampleRate:0.0} s)");
        }
        if (background) Task.Run(Start);
        else Start();
    }

    /// <summary>Per frame: toast clock; a song that played out (and drained) moves on to the next.</summary>
    public void Update(float dt, bool background = true)
    {
        Since += dt;
        if (Playing && _ended && !_dev.MusicPlaying)
        {
            _ended = false;
            Log($"end {Current?.Title}");
            Next(background);
        }
    }

    private void Log(string s) => Console.WriteLine($"[Music] {(Clock is { } c ? $"{c():0.00} s " : "")}{s}");

    /// <summary>
    ///     Random order without repeats: picks among enabled songs not yet played this round; when all are played a new
    ///     round starts (never with the song that just ended). Songs switched on mid-round join it.
    /// </summary>
    public sealed class Shuffle(int count, Random rng)
    {
        private readonly HashSet<int> _played = [];
        private int _last = -1;

        /// <summary>Index of the next song, −1 when <paramref name="enabled"/> allows none.</summary>
        public int Next(Func<int, bool> enabled)
        {
            var pool = Enumerable.Range(0, count).Where(i => enabled(i) && !_played.Contains(i)).ToList();
            if (pool.Count == 0)
            {
                _played.Clear();
                pool = [.. Enumerable.Range(0, count).Where(i => enabled(i) && i != _last)];
                if (pool.Count == 0 && _last >= 0 && enabled(_last)) pool.Add(_last); // the only song left
            }
            if (pool.Count == 0) return -1;
            _last = pool[rng.Next(pool.Count)];
            _played.Add(_last);
            return _last;
        }
    }
}
