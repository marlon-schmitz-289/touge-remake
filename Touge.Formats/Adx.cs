using System.Buffers.Binary;

namespace Touge.Formats;

/// <summary>
///     CRI ADX (type 3, 18-byte frames, 4 bit): big-endian header <c>0x8000</c>, u16 copyright offset (data at +4),
///     u8 encoding 3, u8 frame size, u8 bits, u8 channels, u32 rate, u32 samples, u16 high-pass cutoff, u8 version, u8 flags.
///     Version 3 loop block at 0x14: u16 alignment, u16 ?, u32 enabled, u32 start sample, u32 start byte, u32 end sample, u32 end byte
///     (version 4: same block 0x0C later). Frames interleave per channel: u16 scale, 32 signed nibbles (high first).
/// </summary>
public sealed class Adx
{
    public const int FrameSamples = 32;
    private const int FrameBytes = 18;

    public int Channels { get; }
    public int SampleRate { get; }
    public int SampleCount { get; }
    public int Cutoff { get; }
    /// <summary>Loop in samples (end exclusive), or null.</summary>
    public (int Start, int End)? Loop { get; }
    /// <summary>Byte offsets of the loop as stored (start = first frame of the loop block, end = file position after the loop).</summary>
    public (int Start, int End)? LoopBytes { get; }
    public short Coef1 { get; }
    public short Coef2 { get; }

    private readonly byte[] _data;
    private readonly int _dataOffset;

    public static bool IsAdx(ReadOnlySpan<byte> d) =>
        d.Length >= 0x14 && d[0] == 0x80 && d[1] == 0 && d[4] == 3 && d[5] == FrameBytes && d[6] == 4;

    public Adx(byte[] data)
    {
        if (!IsAdx(data)) throw new InvalidDataException("no ADX type-3 header");
        _data = data;
        var h = data.AsSpan();
        _dataOffset = BinaryPrimitives.ReadUInt16BigEndian(h[2..]) + 4;
        Channels = h[7];
        SampleRate = BinaryPrimitives.ReadInt32BigEndian(h[8..]);
        SampleCount = BinaryPrimitives.ReadInt32BigEndian(h[12..]);
        Cutoff = BinaryPrimitives.ReadUInt16BigEndian(h[16..]);
        if (h[19] != 0) throw new NotSupportedException($"ADX flags 0x{h[19]:X2} (encrypted)");
        if (Channels is < 1 or > 2) throw new NotSupportedException($"ADX with {Channels} channels");

        var loop = h[18] switch { 3 => 0x14, 4 => 0x20, _ => -1 };
        if (loop > 0 && loop + 0x18 <= _dataOffset - 4 && BinaryPrimitives.ReadInt32BigEndian(h[(loop + 4)..]) != 0)
        {
            Loop = (BinaryPrimitives.ReadInt32BigEndian(h[(loop + 8)..]), BinaryPrimitives.ReadInt32BigEndian(h[(loop + 16)..]));
            LoopBytes = (BinaryPrimitives.ReadInt32BigEndian(h[(loop + 12)..]), BinaryPrimitives.ReadInt32BigEndian(h[(loop + 20)..]));
        }
        (Coef1, Coef2) = Coefficients(Cutoff, SampleRate);
    }

    /// <summary>Second-order predictor from the high-pass cutoff (as CRI/ffmpeg: 12-bit fixed point).</summary>
    public static (short, short) Coefficients(int cutoff, int rate)
    {
        var a = Math.Sqrt(2) - Math.Cos(2 * Math.PI * cutoff / rate);
        var b = Math.Sqrt(2) - 1;
        var c = (a - Math.Sqrt((a + b) * (a - b))) / b;
        return ((short)Math.Round(c * 8192), (short)Math.Round(-c * c * 4096));
    }

    /// <summary>Whole file as interleaved PCM16, no looping.</summary>
    public short[] DecodeAll()
    {
        var pcm = new short[SampleCount * Channels];
        var n = Open(loop: false).Read(pcm);
        return n * Channels == pcm.Length ? pcm : pcm[..(n * Channels)];
    }

    /// <summary>Streaming decoder; with <paramref name="loop"/> it wraps from loop end to loop start forever.</summary>
    public Reader Open(bool loop) => new(this, loop && Loop != null);

    public sealed class Reader
    {
        private readonly Adx _adx;
        private readonly bool _loop;
        private readonly int[] _hist;
        private readonly short[] _frame;
        private int[]? _loopHist;
        private int _sample, _frameAt = -1;

        internal Reader(Adx adx, bool loop) =>
            (_adx, _loop, _hist, _frame) = (adx, loop, new int[adx.Channels * 2], new short[FrameSamples * adx.Channels]);

        /// <summary>Position in samples per channel.</summary>
        public int Position => _sample;

        /// <summary>Fills <paramref name="dst"/> with interleaved samples; returns sample frames written (0 = end).</summary>
        public int Read(Span<short> dst)
        {
            var ch = _adx.Channels;
            var end = _loop ? _adx.Loop!.Value.End : _adx.SampleCount;
            var written = 0;
            while (written < dst.Length / ch)
            {
                if (_sample >= end)
                {
                    if (!_loop) break;
                    // predictor state as it was at the loop start on the first pass (like vgmstream)
                    _sample = _adx.Loop!.Value.Start;
                    _frameAt = _sample / FrameSamples - 1;
                    _loopHist?.CopyTo(_hist, 0);
                }
                var block = _sample / FrameSamples;
                if (block != _frameAt && !DecodeBlock(block)) break;
                var i = _sample % FrameSamples;
                var n = Math.Min(Math.Min(FrameSamples - i, end - _sample), dst.Length / ch - written);
                _frame.AsSpan(i * ch, n * ch).CopyTo(dst[(written * ch)..]);
                written += n;
                _sample += n;
            }
            return written;
        }

        private bool DecodeBlock(int block)
        {
            var ch = _adx.Channels;
            var at = _adx._dataOffset + block * FrameBytes * ch;
            if (at + FrameBytes * ch > _adx._data.Length) return false;
            if (_loop && _loopHist == null && block == _adx.Loop!.Value.Start / FrameSamples) _loopHist = (int[])_hist.Clone();
            for (var c = 0; c < ch; c++)
            {
                var f = _adx._data.AsSpan(at + c * FrameBytes, FrameBytes);
                int scale = BinaryPrimitives.ReadUInt16BigEndian(f);
                if ((scale & 0x8000) != 0) return false; // end marker frame
                int h1 = _hist[c * 2], h2 = _hist[c * 2 + 1];
                for (var i = 0; i < FrameSamples; i++)
                {
                    var b = f[2 + i / 2];
                    var nib = ((i & 1) == 0 ? b >> 4 : b & 15) ^ 8;
                    var s = Math.Clamp((nib - 8) * scale + ((_adx.Coef1 * h1 + _adx.Coef2 * h2) >> 12), short.MinValue, short.MaxValue);
                    _frame[i * ch + c] = (short)s;
                    (h2, h1) = (h1, s);
                }
                (_hist[c * 2], _hist[c * 2 + 1]) = (h1, h2);
            }
            _frameAt = block;
            return true;
        }
    }
}
