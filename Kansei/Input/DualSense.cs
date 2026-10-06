namespace Kansei.Input;

/// <summary>
///     PS5 DualSense output effects for <see cref="GamepadState.SendEffect"/> (SDL_GameControllerSendEffect): the 47-byte
///     effects block of the output report, which SDL wraps itself — USB report 0x02 + block (48 B), Bluetooth report 0x31 +
///     0x02 + block, padded to 78 B with a CRC-32 (seed: the HID header byte 0xA2) in the last four bytes. Layout (offsets into
///     the block; a field only takes effect when its enable bit is set, everything else keeps its last state on the pad):
///     <list type="table">
///         <item><term>0</term><description>enable bits 1: 0x01/0x02 rumble (SDL sets them), 0x04 right trigger, 0x08 left trigger,
///         0x20 speaker volume, 0x80 audio control</description></item>
///         <item><term>1</term><description>enable bits 2: 0x01 mic LED, 0x04 lightbar, 0x10 player LEDs (SDL does lightbar/player LEDs)</description></item>
///         <item><term>5</term><description>speaker volume: the pad is audible only from about 0x3D, so 0..1 maps to 0x3D..0x64, 0 = 0 (mute)</description></item>
///         <item><term>7</term><description>audio control: bits 4–5 output path, 3 = X_X_R: headphones off, the right channel of
///         the pad's USB audio device to the speaker</description></item>
///         <item><term>8</term><description>mic LED: 0 off, 1 on, 2 pulsing</description></item>
///         <item><term>10–20 / 21–31</term><description>right / left adaptive trigger: mode byte + parameters (<see cref="Trigger"/>)</description></item>
///     </list>
/// </summary>
public static class DualSense
{
    public const int EffectSize = 47;
    private const int Enable1 = 0, Enable2 = 1, SpeakerVolume = 5, AudioControl = 7, MicLed = 8, RightTrigger = 10, LeftTrigger = 21;

    public enum Mic : byte { Off, On, Pulse }

    /// <summary>
    ///     One adaptive trigger effect, the "simple" modes the pad firmware has always had: 0x05 off, 0x01 resistance from a
    ///     position on (position, force), 0x06 vibration from a position on (frequency Hz, amplitude, position). 0..1 inputs map to 0..255.
    /// </summary>
    public readonly record struct Trigger(byte Mode, byte P1 = 0, byte P2 = 0, byte P3 = 0)
    {
        public static readonly Trigger Off = new(0x05);

        public static Trigger Resistance(float start, float force) =>
            Byte(force) == 0 ? Off : new(0x01, Byte(start), Byte(force));

        public static Trigger Vibration(float hz, float amplitude, float start = 0) =>
            Byte(amplitude) == 0 ? Off : new(0x06, (byte)Math.Clamp(MathF.Round(hz), 1, 255), Byte(amplitude), Byte(start));

        internal void Write(Span<byte> d) => (d[0], d[1], d[2], d[3]) = (Mode, P1, P2, P3);
    }

    /// <summary>0..1 → 0..255, rounded and clamped (NaN = 0).</summary>
    public static byte Byte(float v) => float.IsFinite(v) ? (byte)MathF.Round(Math.Clamp(v, 0, 1) * 255) : (byte)0;

    /// <summary>
    ///     The effects block: both triggers and the mic LED always; the speaker (volume 0..1, path to the speaker) only when
    ///     <paramref name="speaker"/> is set — the audio state stays on the pad and need not be rewritten with every trigger change.
    /// </summary>
    public static byte[] Effect(Trigger right, Trigger left, Mic mic, float? speaker = null)
    {
        var e = new byte[EffectSize];
        Effect(e, right, left, mic, speaker);
        return e;
    }

    /// <summary><see cref="Effect(Trigger, Trigger, Mic, float?)"/> into <paramref name="e"/> (≥ <see cref="EffectSize"/>; per frame without allocating).</summary>
    public static void Effect(Span<byte> e, Trigger right, Trigger left, Mic mic, float? speaker = null)
    {
        e[..EffectSize].Clear();
        e[Enable1] = 0x04 | 0x08;
        e[Enable2] = 0x01;
        right.Write(e[RightTrigger..]);
        left.Write(e[LeftTrigger..]);
        e[MicLed] = (byte)mic;
        if (speaker is { } v)
        {
            e[Enable1] |= 0x20 | 0x80;
            e[SpeakerVolume] = Volume(v);
            e[AudioControl] = (byte)(e[SpeakerVolume] > 0 ? 3 << 4 : 0); // X_X_R, silent: back to L_R_X (headphone jack)
        }
    }

    /// <summary>Speaker volume byte: 0 = mute, else 0x3D (quietest audible) .. 0x64.</summary>
    public static byte Volume(float v) => float.IsFinite(v) && v > 0 ? (byte)MathF.Round(0x3D + Math.Min(v, 1) * (0x64 - 0x3D)) : (byte)0;

    /// <summary>Everything back to the pad's idle state: triggers free, mic LED off, speaker silent.</summary>
    public static byte[] Reset() => Effect(Trigger.Off, Trigger.Off, Mic.Off, 0);
}
