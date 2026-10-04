namespace Touge.Formats;

/// <summary>Minimal RIFF/WAVE PCM16 writer (exports and offline audio captures).</summary>
public static class Wav
{
    public static void Write(string path, ReadOnlySpan<short> pcm, int channels, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8);
        w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * channels * 2);
        w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length * 2);
        foreach (var s in pcm) w.Write(s);
    }
}
