using System.Diagnostics;
using Kansei.Audio;
using Touge.Formats;

namespace Touge;

/// <summary><c>--audiotest &lt;s&gt;</c>: race BGM streamed with loop, AE86 engine layer revving 1→2× pitch, a backfire every second. Logs once per second.</summary>
internal static class AudioCheck
{
    public static bool Run(Iso9660 iso, float seconds, string track = "NIGHT_OF_FIRE.ADX")
    {
        using var audio = new AudioDevice();
        if (!audio.Enabled) return false;
        audio.Music = 0.6f;

        var bgm = iso.OpenAfs("CDVD/DATA/SOUND/RACEBGM.AFS");
        var adx = new Adx(bgm.Read(bgm.Find(track) ?? throw new FileNotFoundException(track)));
        var reader = adx.Open(loop: true);
        audio.PlayMusic(reader.Read, adx.Channels, adx.SampleRate);

        var carse = iso.OpenAfs("CDVD/DATA/SOUND/CARSE.AFS");
        var (hd, bd) = Vag.Mrg(carse.Read(carse.Find("AE86_U.MRG")!.Value));
        var s = Vag.HdSamples(hd, bd.Length)[3];
        var layer = Vag.Decode(bd.AsSpan(s.Offset, s.Size), s.Rate);
        using var engineClip = audio.CreateClip(layer.Pcm, 1, layer.Rate, layer.Loop);
        using var engine = audio.CreateLoop(engineClip);
        engine.Gain = 0.5f;
        engine.Play();

        var sysse = iso.ReadFile("CDVD/DATA/SOUND/SYSSE.BIN");
        var bf = Vag.SysSe(sysse)[2]; // backfire001.vag
        var shot = Vag.Decode(sysse.AsSpan(bf.Offset, bf.Size), bf.Rate);
        using var backfire = audio.CreateClip(shot.Pcm, 1, shot.Rate);

        Console.WriteLine($"Musik {track}: {adx.SampleCount / (double)adx.SampleRate:0.0} s, Loop {adx.Loop}");
        var sw = Stopwatch.StartNew();
        var nextLog = 1.0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var t = sw.Elapsed.TotalSeconds;
            engine.Pitch = 1 + (float)(0.5 - 0.5 * Math.Cos(t * 2)); // 1–2× like revving
            if (t >= nextLog)
            {
                audio.PlaySfx(backfire, 0.8f);
                Console.WriteLine($"{t:0.0} s: Musik spielt={audio.MusicPlaying}, Musikposition {reader.Position / (double)adx.SampleRate:0.00} s (dekodiert), Motor-Pitch {engine.Pitch:0.00}");
                nextLog += 1;
            }
            Thread.Sleep(16);
        }
        return audio.MusicPlaying;
    }
}
