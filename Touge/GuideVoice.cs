using Kansei.Audio;
using Touge.Formats;

namespace Touge;

/// <summary>
///     Iketani's spoken car introductions of the original's car guide: SOUND/IKETANI.AFS INTRO_&lt;car&gt;.ADX (mono 24 kHz,
///     34–66 s; name built as the ELF's "INTRO_" + id + ".ADX"), decoded whole and played once on a UI voice.
/// </summary>
public sealed class GuideVoice(Iso9660 iso, AudioDevice dev) : IDisposable
{
    private readonly Afs _afs = iso.OpenAfs("CDVD/DATA/SOUND/IKETANI.AFS");
    private AudioDevice.Clip? _clip;

    /// <summary>Stops what plays and starts <paramref name="car"/>'s talk (null: only stop); returns its length in seconds (0 = none).</summary>
    public float Play(string? car, float volume)
    {
        _clip?.Dispose(); // stops the voice still holding it
        _clip = null;
        if (car == null || _afs.Find($"INTRO_{car}.ADX") is not { } e) return 0;
        var adx = new Adx(_afs.Read(e));
        _clip = dev.CreateClip(adx.DecodeAll(), adx.Channels, adx.SampleRate);
        dev.PlaySfx(_clip, volume, ui: true);
        Console.WriteLine($"[Menu] Iketani INTRO_{car} ({_clip.Seconds:0.0} s)");
        return _clip.Seconds;
    }

    public void Dispose() => _clip?.Dispose();
}
