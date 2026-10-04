using System.Buffers.Binary;

namespace Touge.Formats.Tests;

public class AudioTests
{
    [Fact]
    public void Adx_decodes_frames_with_cutoff_predictor_and_loops_from_header()
    {
        Assert.Equal(((short)7334, (short)-3283), Adx.Coefficients(500, 44100)); // CRI's well-known 44.1 kHz pair

        // mono v3 header (data at 0x2C+4 = 0x30), 3 frames = 96 samples, loop 32..96 (block 1 to end)
        var d = new byte[0x30 + 3 * 18];
        BinaryPrimitives.WriteUInt16BigEndian(d, 0x8000);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(2), 0x2C);
        d[4] = 3; d[5] = 18; d[6] = 4; d[7] = 1;
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(8), 44100);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(12), 96);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(16), 500);
        d[18] = 3;
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(0x18), 1);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(0x1C), 32);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(0x20), 0x30 + 18);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(0x24), 96);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(0x28), d.Length);
        for (var f = 0; f < 3; f++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(0x30 + f * 18), (ushort)(100 + f));
            for (var i = 2; i < 18; i++) d[0x30 + f * 18 + i] = (byte)(((i + f) & 7) << 4 | 0xD); // nibbles (i+f)&7, -3
        }

        var adx = new Adx(d);
        Assert.Equal((32, 96), adx.Loop);
        Assert.Equal((0x30 + 18, d.Length), adx.LoopBytes);

        var pcm = adx.DecodeAll();
        Assert.Equal(96, pcm.Length);
        // s = nibble * scale + (c1*h1 + c2*h2) >> 12; bytes 0x2D, 0x3D → 2*100, -3*100 + 358, 3*100 + floor(-56.5)
        Assert.Equal(new short[] { 200, 58, 243 }, pcm[..3]);

        // looping stream: after the end it continues with the loop block decoded from the saved predictor state
        var stream = new short[96 + 64 + 10];
        Assert.Equal(stream.Length, adx.Open(loop: true).Read(stream));
        Assert.Equal(pcm, stream[..96]);
        Assert.Equal(pcm[32..96], stream[96..160]);
        Assert.Equal(pcm[32..42], stream[160..]);
    }

    [Fact]
    public void Vag_decodes_spu_adpcm_with_filter_and_loop_flags()
    {
        // frame 0: filter 0, shift 12 (nibble = sample), loop start; frame 1: filter 2 (115/64, -52/64), end + loop; frame 2 never read
        var d = new byte[48];
        d[0] = 0x0C; d[1] = 4;
        d[2] = 0x21; // low nibble first: 1, 2
        d[15] = 0x40; // sample 27 = 4
        d[16] = 0x2C; d[17] = 3;
        d[32] = 0x0C; d[34] = 0x77;
        var v = Vag.Decode(d, 22050);

        Assert.Equal(56, v.Pcm.Length);
        Assert.Equal(new short[] { 1, 2, 0 }, v.Pcm[..3]);
        // zero nibbles, prediction only: (4*115 + 0*-52 + 32) >> 6 = 7, (7*115 - 4*52 + 32) >> 6 = 9
        Assert.Equal(new short[] { 4, 7, 9 }, v.Pcm[27..30]);
        Assert.Equal((0, 56), v.Loop);
        Assert.Equal(22043, Vag.PitchToRate(0x759)); // SYSSE pitch 0x759 ≈ 22.05 kHz
    }

    [Fact]
    public void Sect_expands_curve_points_piecewise_linear()
    {
        // header + 16 offsets, all entries share one 0x90 block: pitch flat 64, volume window 0→100 (x 10..20) → 0 (x 40), pan 64
        var d = new byte[0x60 + 0x90];
        "SECTver0.502"u8.CopyTo(d);
        for (var e = 0; e < 16; e++) BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(0x20 + e * 4), 0x60);
        int[][] curves = [[0, 64, 51, 64, 102, 64, 153, 64, 204, 64, 255, 64], [0, 0, 10, 0, 20, 100, 30, 100, 40, 0, 255, 0], [0, 64, 51, 64, 102, 64, 153, 64, 204, 64, 255, 64]];
        for (var c = 0; c < 3; c++)
        for (var k = 0; k < 12; k++) BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(0x60 + c * 0x30 + k * 4), curves[c][k]);

        var s = new Sect(d);
        Assert.Equal(64, s.Value(3, Sect.Pitch, 200));
        Assert.Equal([0, 0, 50, 100, 100, 50, 0, 0], new[] { 0, 10, 15, 20, 30, 35, 40, 300 }.Select(x => s.Value(15, Sect.Volume, x)));
        Assert.Equal(64, s.Value(0, Sect.Pan, -5));
    }
}
