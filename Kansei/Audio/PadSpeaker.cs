using Silk.NET.SDL;

namespace Kansei.Audio;

/// <summary>
///     The DualSense's built-in speaker. Over USB the pad is also a 4-channel audio output (CoreAudio/WASAPI/ALSA: front L/R =
///     headphone jack or speaker, rear L/R = the haptic actuators); with the output path X_X_R (<see cref="Kansei.Input.DualSense.Effect"/>)
///     its right channel plays on the speaker. Played through SDL audio (queued, no callback), independent of the OpenAL mix.
///     Over Bluetooth there is no audio device on a PC: <see cref="Available"/> stays false and everything is a no-op.
/// </summary>
public sealed unsafe class PadSpeaker(Sdl sdl) : IDisposable
{
    private const int Rate = 48000, Channels = 4;
    private uint _device;
    private bool _init;
    private double _nextScan;

    /// <summary>Name of the pad's audio device in use, null = none found.</summary>
    public string? Device { get; private set; }
    public bool Available => _device != 0;

    /// <summary>Looks for the pad's audio device (at most every 2 s of <paramref name="now"/>; hot-plug); true if one is open.</summary>
    public bool Scan(double now)
    {
        if (_device != 0 && sdl.GetAudioDeviceStatus(_device) == AudioStatus.Stopped) Close(); // unplugged
        if (_device != 0 || now < _nextScan) return _device != 0;
        _nextScan = now + 2;
        if (!_init) _init = sdl.InitSubSystem(Sdl.InitAudio) == 0;
        if (!_init) return false;
        for (var i = 0; i < sdl.GetNumAudioDevices(0); i++)
        {
            var name = sdl.GetAudioDeviceNameS(i, 0);
            if (name == null || !name.Contains("DualSense", StringComparison.OrdinalIgnoreCase)) continue;
            var want = new AudioSpec { Freq = Rate, Format = 0x8010 /* AUDIO_S16LSB */, Channels = Channels, Samples = 1024 };
            _device = sdl.OpenAudioDevice(name, 0, &want, null, 0); // SDL converts if the device wants another format
            Console.WriteLine($"[Kansei] Pad-Lautsprecher: {name} -> {(_device != 0 ? $"Gerät {_device}" : sdl.GetErrorS())}");
            if (_device == 0) continue;
            Device = name;
            sdl.PauseAudioDevice(_device, 0);
            return true;
        }
        return false;
    }

    /// <summary>Plays mono PCM16 at <paramref name="rate"/> on the speaker at <paramref name="gain"/> (cuts what is still queued).</summary>
    public void Play(ReadOnlySpan<short> pcm, int rate, float gain)
    {
        if (_device == 0 || pcm.Length == 0) return;
        var frames = Frames(pcm, rate, gain);
        sdl.ClearQueuedAudio(_device);
        fixed (short* p = frames)
            if (sdl.QueueAudio(_device, p, (uint)(frames.Length * 2)) != 0) Console.WriteLine($"[Kansei] Pad-Lautsprecher: {sdl.GetErrorS()}");
    }

    /// <summary>
    ///     Mono PCM16 → interleaved 4-channel 48 kHz frames (linear resampling) with the sound on front left and right (the speaker
    ///     takes the right one; the left keeps it audible where the pad maps them the other way), the haptic channels silent.
    /// </summary>
    public static short[] Frames(ReadOnlySpan<short> pcm, int rate, float gain)
    {
        var n = (int)((long)pcm.Length * Rate / rate);
        var o = new short[n * Channels];
        gain = Math.Clamp(gain, 0, 1);
        for (var i = 0; i < n; i++)
        {
            var x = (double)i * rate / Rate;
            var k = (int)x;
            var a = pcm[Math.Min(k, pcm.Length - 1)];
            var b = pcm[Math.Min(k + 1, pcm.Length - 1)];
            var s = (short)Math.Clamp((a + (b - a) * (x - k)) * gain, short.MinValue, short.MaxValue);
            (o[i * Channels], o[i * Channels + 1]) = (s, s);
        }
        return o;
    }

    private void Close()
    {
        sdl.CloseAudioDevice(_device);
        (_device, Device) = (0, null);
    }

    public void Dispose()
    {
        if (_device != 0) Close();
        if (_init) sdl.QuitSubSystem(Sdl.InitAudio);
    }
}
