using System.Numerics;
using Silk.NET.OpenAL;

namespace Kansei.Audio;

/// <summary>Pulls interleaved PCM16 for streaming; returns sample frames written, 0 = end of stream.</summary>
public delegate int PcmSource(Span<short> dst);

/// <summary>
///     OpenAL output (as MogliEngine's AudioDevice): one streamed music voice (fed by a background thread),
///     a pool of one-shot SFX voices and looping voices with live pitch/gain (engine, tyres).
///     Volumes: <see cref="Master"/> = listener gain; <see cref="Music"/>/<see cref="Sfx"/> scale their voices.
///     Without an output device everything stays a silent no-op.
///     With a <c>loopbackRate</c> nothing is played: <see cref="Render"/> mixes the same voices offline (ALC_SOFT_loopback),
///     the music stream is fed from <see cref="Render"/> instead of a thread.
/// </summary>
public sealed unsafe class AudioDevice : IDisposable
{
    private const int SfxVoices = 16, StreamBuffers = 4, StreamFrames = 8192;
    private const int LoopPointsSoft = 0x2015; // AL_LOOP_POINTS_SOFT (OpenAL Soft)

    private readonly AL _al;
    private readonly ALContext _alc;
    private readonly Device* _device;
    private readonly Context* _context;
    private readonly bool _loopPoints;
    private readonly uint[] _sfx = new uint[SfxVoices];
    private readonly float[] _sfxGain = new float[SfxVoices];
    private readonly List<LoopVoice> _loops = [];
    private readonly Lock _lock = new();
    private readonly Thread? _feeder;
    private volatile bool _quit;
    private float _master = 1, _music = 1, _sfxVolume = 1;
    private int _nextSfx;
    private readonly delegate* unmanaged[Cdecl]<Device*, void*, int, void> _renderSamples;

    // music stream, guarded by _lock
    private uint _musicSource;
    private readonly uint[] _streamBuffers = new uint[StreamBuffers];
    private PcmSource? _stream;
    private BufferFormat _streamFormat;
    private int _streamRate, _streamChannels;
    private readonly short[] _streamPcm = new short[StreamFrames * 2];

    public bool Enabled => _device != null;

    /// <param name="loopbackRate">0 = default output device; &gt; 0 = offline stereo PCM16 mix at this rate via <see cref="Render"/>.</param>
    public AudioDevice(int loopbackRate = 0)
    {
        _alc = ALContext.GetApi(soft: true);
        _al = AL.GetApi(soft: true);
        if (loopbackRate > 0 && _alc.IsExtensionPresent(null, "ALC_SOFT_loopback"))
        {
            var open = (delegate* unmanaged[Cdecl]<byte*, Device*>)_alc.GetProcAddress(null, "alcLoopbackOpenDeviceSOFT");
            _renderSamples = (delegate* unmanaged[Cdecl]<Device*, void*, int, void>)_alc.GetProcAddress(null, "alcRenderSamplesSOFT");
            _device = open(null);
        }
        else if (loopbackRate == 0) _device = _alc.OpenDevice(string.Empty);
        if (_device == null)
        {
            Console.Error.WriteLine("[Kansei] Kein Audiogerät – Ton aus.");
            return;
        }
        // ALC_FORMAT_CHANNELS_SOFT = ALC_STEREO_SOFT, ALC_FORMAT_TYPE_SOFT = ALC_SHORT_SOFT, ALC_FREQUENCY
        var attrs = stackalloc int[] { 0x1990, 0x1501, 0x1991, 0x1402, 0x1007, loopbackRate, 0 };
        _context = _alc.CreateContext(_device, loopbackRate > 0 ? attrs : null);
        _alc.MakeContextCurrent(_context);
        _loopPoints = _al.IsExtensionPresent("AL_SOFT_loop_points");
        Console.WriteLine($"[Kansei] Audio: {_alc.GetContextProperty(_device, GetContextString.DeviceSpecifier)}, {_al.GetStateProperty(StateString.Renderer)}, Loop-Punkte {(_loopPoints ? "ja" : "nein")}");
        for (var i = 0; i < SfxVoices; i++) _sfx[i] = _al.GenSource();
        _musicSource = _al.GenSource();
        for (var i = 0; i < StreamBuffers; i++) _streamBuffers[i] = _al.GenBuffer();
        if (loopbackRate > 0) return;
        _feeder = new Thread(Feed) { IsBackground = true, Name = "Kansei music" };
        _feeder.Start();
    }

    public float Master
    {
        get => _master;
        set { _master = value; if (Enabled) _al.SetListenerProperty(ListenerFloat.Gain, value); }
    }

    public float Music
    {
        get => _music;
        set { _music = value; if (Enabled) lock (_lock) _al.SetSourceProperty(_musicSource, SourceFloat.Gain, value); }
    }

    public float Sfx
    {
        get => _sfxVolume;
        set
        {
            _sfxVolume = value;
            if (!Enabled) return;
            for (var i = 0; i < SfxVoices; i++) _al.SetSourceProperty(_sfx[i], SourceFloat.Gain, _sfxGain[i] * value);
            foreach (var l in _loops) l.Apply();
        }
    }

    /// <summary>Uploads PCM16 (1 or 2 channels). <paramref name="loop"/> (sample frames, end exclusive) is used by looping voices.</summary>
    public Clip CreateClip(short[] pcm, int channels, int rate, (int Start, int End)? loop = null)
    {
        if (!Enabled) return new Clip(this, 0, pcm.Length / channels / (float)rate);
        var buf = _al.GenBuffer();
        fixed (short* p = pcm)
            _al.BufferData(buf, channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16, p, pcm.Length * 2, rate);
        if (loop is var (s, e) && _loopPoints)
        {
            var pts = stackalloc int[2] { s, e };
            _al.SetBufferProperty(buf, (BufferInteger)LoopPointsSoft, pts);
        }
        // ponytail: without AL_SOFT_loop_points (only non-OpenAL-Soft drivers) the whole clip loops
        return new Clip(this, buf, pcm.Length / channels / (float)rate);
    }

    /// <summary>Fire-and-forget on the next SFX voice (round robin; a busy voice is cut).</summary>
    public void PlaySfx(Clip clip, float gain = 1, float pitch = 1)
    {
        if (!Enabled) return;
        var i = _nextSfx;
        for (var k = 0; k < SfxVoices; k++)
        {
            var j = (_nextSfx + k) % SfxVoices;
            _al.GetSourceProperty(_sfx[j], GetSourceInteger.SourceState, out var st);
            if ((SourceState)st != SourceState.Playing) { i = j; break; }
        }
        _nextSfx = (i + 1) % SfxVoices;
        var src = _sfx[i];
        _al.SourceStop(src);
        _al.SetSourceProperty(src, SourceInteger.Buffer, (int)clip.Buffer);
        _sfxGain[i] = gain;
        _al.SetSourceProperty(src, SourceFloat.Gain, gain * _sfxVolume);
        _al.SetSourceProperty(src, SourceFloat.Pitch, pitch);
        _al.SourcePlay(src);
    }

    /// <summary>A voice that loops <paramref name="clip"/> until disposed; set Gain/Pitch every frame.</summary>
    public LoopVoice CreateLoop(Clip clip)
    {
        var v = new LoopVoice(this, clip);
        _loops.Add(v);
        return v;
    }

    /// <summary>Starts streaming music from <paramref name="source"/> (replaces the current track).</summary>
    public void PlayMusic(PcmSource source, int channels, int rate)
    {
        if (!Enabled) return;
        lock (_lock)
        {
            StopMusicLocked();
            (_stream, _streamChannels, _streamRate) = (source, channels, rate);
            _streamFormat = channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16;
            var queued = 0;
            foreach (var b in _streamBuffers)
                if (Fill(b)) { var bb = b; _al.SourceQueueBuffers(_musicSource, 1, &bb); queued++; }
            if (queued > 0) _al.SourcePlay(_musicSource);
        }
    }

    public void StopMusic()
    {
        if (!Enabled) return;
        lock (_lock) StopMusicLocked();
    }

    /// <summary>True while the music stream has queued audio.</summary>
    public bool MusicPlaying
    {
        get
        {
            if (!Enabled) return false;
            lock (_lock)
            {
                _al.GetSourceProperty(_musicSource, GetSourceInteger.SourceState, out var st);
                return (SourceState)st == SourceState.Playing;
            }
        }
    }

    private void StopMusicLocked()
    {
        _al.SourceStop(_musicSource);
        _al.SetSourceProperty(_musicSource, SourceInteger.Buffer, 0); // unqueues everything
        _stream = null;
    }

    private bool Fill(uint buffer)
    {
        var frames = _stream!(_streamPcm.AsSpan(0, StreamFrames * _streamChannels));
        if (frames == 0) return false;
        fixed (short* p = _streamPcm) _al.BufferData(buffer, _streamFormat, p, frames * _streamChannels * 2, _streamRate);
        return true;
    }

    private void Feed()
    {
        while (!_quit)
        {
            lock (_lock) PumpMusicLocked();
            Thread.Sleep(20); // 4 × 8192 frames ≥ 680 ms buffered at 48 kHz
        }
    }

    private void PumpMusicLocked()
    {
        if (_stream == null) return;
        _al.GetSourceProperty(_musicSource, GetSourceInteger.BuffersProcessed, out var done);
        for (; done > 0; done--)
        {
            uint b;
            _al.SourceUnqueueBuffers(_musicSource, 1, &b);
            if (Fill(b)) _al.SourceQueueBuffers(_musicSource, 1, &b);
        }
        _al.GetSourceProperty(_musicSource, GetSourceInteger.SourceState, out var st);
        _al.GetSourceProperty(_musicSource, GetSourceInteger.BuffersQueued, out var queued);
        if ((SourceState)st != SourceState.Playing && queued > 0) _al.SourcePlay(_musicSource); // underrun: resume
        if (queued == 0) _stream = null; // track ended
    }

    /// <summary>Loopback only: mixes <c>dst.Length / 2</c> stereo frames of everything playing (music fed first).</summary>
    public void Render(Span<short> dst)
    {
        if (_renderSamples == null) throw new InvalidOperationException("Render needs a loopback AudioDevice");
        lock (_lock) PumpMusicLocked();
        fixed (short* p = dst) _renderSamples(_device, p, dst.Length / 2);
    }

    public void Dispose()
    {
        if (!Enabled) return;
        _quit = true;
        _feeder?.Join();
        StopMusicLocked();
        foreach (var l in _loops.ToArray()) l.Dispose();
        _al.DeleteSource(_musicSource);
        foreach (var b in _streamBuffers) _al.DeleteBuffer(b);
        foreach (var s in _sfx) { _al.SourceStop(s); _al.DeleteSource(s); }
        _alc.MakeContextCurrent(null);
        _alc.DestroyContext(_context);
        _alc.CloseDevice(_device);
    }

    /// <summary>Uploaded PCM. Dispose loop voices using it first; one-shots still playing it are stopped.</summary>
    public sealed class Clip(AudioDevice owner, uint buffer, float seconds) : IDisposable
    {
        internal uint Buffer => buffer;
        public float Seconds => seconds;
        public void Dispose()
        {
            if (buffer == 0) return;
            foreach (var src in owner._sfx) // a one-shot still holding the buffer would make the delete fail
            {
                owner._al.GetSourceProperty(src, GetSourceInteger.Buffer, out var b);
                if (b != buffer) continue;
                owner._al.SourceStop(src);
                owner._al.SetSourceProperty(src, SourceInteger.Buffer, 0);
            }
            owner._al.DeleteBuffer(buffer);
        }
    }

    /// <summary>Looping voice in the SFX group with real-time gain and pitch.</summary>
    public sealed class LoopVoice : IDisposable
    {
        private readonly AudioDevice _o;
        private readonly uint _src;
        private float _gain = 1, _pitch = 1;

        internal LoopVoice(AudioDevice owner, Clip clip)
        {
            _o = owner;
            if (!owner.Enabled) return;
            _src = owner._al.GenSource();
            owner._al.SetSourceProperty(_src, SourceInteger.Buffer, (int)clip.Buffer);
            owner._al.SetSourceProperty(_src, SourceBoolean.Looping, true);
            Apply();
        }

        public float Gain { get => _gain; set { _gain = value; Apply(); } }

        /// <summary>Playback rate factor (1 = recorded rate).</summary>
        public float Pitch
        {
            get => _pitch;
            set { _pitch = value; if (_o.Enabled) _o._al.SetSourceProperty(_src, SourceFloat.Pitch, value); }
        }

        /// <summary>World position for later 3D voices (other cars); the default origin with the listener there = plain 2D.</summary>
        public Vector3 Position
        {
            set { if (_o.Enabled) _o._al.SetSourceProperty(_src, SourceVector3.Position, in value); }
        }

        public void Play() { if (_o.Enabled) _o._al.SourcePlay(_src); }
        public void Stop() { if (_o.Enabled) _o._al.SourceStop(_src); }

        internal void Apply() { if (_o.Enabled) _o._al.SetSourceProperty(_src, SourceFloat.Gain, _gain * _o._sfxVolume); }

        public void Dispose()
        {
            _o._loops.Remove(this);
            if (!_o.Enabled) return;
            _o._al.SourceStop(_src);
            _o._al.DeleteSource(_src);
        }
    }
}
