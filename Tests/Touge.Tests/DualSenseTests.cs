using System.Numerics;
using Kansei.Audio;
using Kansei.Input;
using Touge.Ui;
using static Kansei.Input.DualSense;

namespace Touge.Tests;

public class DualSenseTests
{
    [Fact]
    public void Effect_Packet_Bytes()
    {
        var e = Effect(Trigger.Vibration(30, 1, 0.2f), Trigger.Resistance(0.1f, 0.5f), Mic.Pulse);
        Assert.Equal(EffectSize, e.Length);
        Assert.Equal((0x0C, 0x01), (e[0], e[1])); // both triggers + mic LED, nothing else
        Assert.Equal(2, e[8]);
        Assert.Equal(new byte[] { 0x06, 30, 255, 51 }, e[10..14]);
        Assert.Equal(new byte[] { 0x01, 26, 128, 0 }, e[21..25]);
        Assert.Equal((0, 0), (e[5], e[7])); // speaker untouched

        var spk = Effect(Trigger.Off, Trigger.Off, Mic.Off, 0.5f);
        Assert.Equal((0xAC, 0x05, 0x05), (spk[0], spk[10], spk[21])); // + speaker volume + audio control; off = 0x05
        Assert.Equal((50, 0x30), (spk[5], spk[7])); // 0x64 scale, path X_X_R
        Assert.Equal((0, 0), (Reset()[5], Reset()[7])); // silent: back to the headphone path
    }

    [Fact]
    public void Trigger_Scaling_By_Strength()
    {
        Assert.Equal(Trigger.Off, Trigger.Resistance(0.5f, 0));
        Assert.Equal(Trigger.Off, Trigger.Vibration(20, float.NaN));
        Assert.Equal((Trigger.Off, Trigger.Off), DualSenseFeedback.TriggerEffects(0, 1, 1, true, 1)); // strength 0 = off
        var soft = DualSenseFeedback.TriggerEffects(0.5f, 1, 0, false, 0).Left;
        var hard = DualSenseFeedback.TriggerEffects(1, 1, 0, false, 0).Left;
        Assert.True(hard.P2 > soft.P2 && soft.Mode == 0x01);
        Assert.True(DualSenseFeedback.TriggerEffects(1, 1, 0, false, 0).Left.P2 > DualSenseFeedback.TriggerEffects(1, 0, 0, false, 0).Left.P2); // stiffer with brake pressure
        Assert.Equal(0x06, DualSenseFeedback.TriggerEffects(1, 1, 0, true, 0).Left.Mode); // lock-up: ABS pulse
        Assert.Equal(0x06, DualSenseFeedback.TriggerEffects(1, 0, 0.5f, false, 0).Right.Mode); // wheel spin
        Assert.Equal(60, DualSenseFeedback.TriggerEffects(1, 0, 0, false, 1).Right.P1); // gear kick
    }

    [Fact]
    public void Lightbar_Modes()
    {
        Assert.Equal(new Vector3(0, 1, 0), DualSenseFeedback.Lightbar(DualSenseSettings.Light.Rpm, 0.3f, 0, 0, 0, 0));
        Assert.Equal(new Vector3(1, 0, 0), DualSenseFeedback.Lightbar(DualSenseSettings.Light.Rpm, 0.95f, 0, 0, 0, 0)); // flash on …
        Assert.Equal(Vector3.Zero, DualSenseFeedback.Lightbar(DualSenseSettings.Light.Rpm, 0.95f, 0, 0, 0.07f, 0)); // … and off at 8 Hz
        Assert.Equal(new Vector3(1, 0, 0), DualSenseFeedback.Lightbar(DualSenseSettings.Light.Rpm, 0.95f, 0, 0, 0.07f, 1)); // a hit is red
        Assert.Equal(new Vector3(0x10, 0x20, 0x30) / 255, DualSenseFeedback.Lightbar(DualSenseSettings.Light.CarColour, 0, 0x302010, 0, 0, 0)); // 0xBBGGRR
        Assert.NotEqual(DualSenseFeedback.Lightbar(DualSenseSettings.Light.Player, 0, 0, 0, 0, 0), DualSenseFeedback.Lightbar(DualSenseSettings.Light.Player, 0, 0, 1, 0, 0));
        Assert.Equal(Vector3.Zero, DualSenseFeedback.Lightbar(DualSenseSettings.Light.Off, 1, 0xFFFFFF, 0, 0, 1));
    }

    [Fact]
    public void Tilt_Sign_DeadZone_Sensitivity()
    {
        static Vector3 Rolled(float deg) => new(-9.81f * MathF.Sin(deg * MathF.PI / 180), 9.81f * MathF.Cos(deg * MathF.PI / 180), 0);
        Assert.Equal(0, DualSenseFeedback.Tilt(Vector3.Zero, 0.5f)); // no data
        Assert.Equal(0, DualSenseFeedback.Tilt(Rolled(1.5f), 0.5f));
        Assert.Equal(1, DualSenseFeedback.Tilt(Rolled(45), 0.5f), 3); // right side down = right, full lock at 40°
        Assert.Equal(-1, DualSenseFeedback.Tilt(Rolled(-45), 0.5f), 3);
        Assert.True(DualSenseFeedback.Tilt(Rolled(15), 1) > DualSenseFeedback.Tilt(Rolled(15), 0));
        // pitched towards the player (gravity partly on Z): the roll is the same
        var r = Rolled(20);
        Assert.Equal(DualSenseFeedback.Tilt(r, 0.5f), DualSenseFeedback.Tilt(new Vector3(r.X, r.Y * MathF.Cos(0.6f), r.Y * MathF.Sin(0.6f)), 0.5f), 3);
    }

    [Fact]
    public void Speaker_Frames_Resample_To_Front_Channels()
    {
        var f = PadSpeaker.Frames([1000, 2000], 24000, 0.5f);
        Assert.Equal(4 * 4, f.Length); // 2 samples at 24 kHz = 4 frames at 48 kHz × 4 channels
        Assert.Equal(new short[] { 500, 500, 0, 0, 750, 750, 0, 0 }, f[..8]);
    }

    [Fact]
    public void Settings_Old_File_Gets_Defaults_And_Clamps()
    {
        var s = Settings.FromJson("""{ "Version": 2 }""");
        Assert.Equal((DualSenseSettings.Light.Rpm, true, false), (s.DualSense.Lightbar, s.DualSense.Triggers, s.DualSense.Gyro));
        var t = Settings.FromJson("""{ "DualSense": { "Brightness": 7, "TriggerStrength": -1, "Lightbar": "Player", "Mic": 9 } }""");
        Assert.Equal((1f, 0f, DualSenseSettings.Light.Player, DualSenseSettings.MicLight.MusicOff),
            (t.DualSense.Brightness, t.DualSense.TriggerStrength, t.DualSense.Lightbar, t.DualSense.Mic));
        Assert.Equal(DualSenseSettings.Light.Player, Settings.FromJson(t.ToJson()).DualSense.Lightbar);
    }

    [Fact]
    public void Options_Page_Shows_And_Hides()
    {
        var o = new Options(new Settings());
        var page = new Options.Page("DUALSENSE", "");
        o.Show(page, true);
        Assert.Equal(o.Pages.FindIndex(p => p.Title == "CONTROLLER") + 1, o.Pages.IndexOf(page));
        Assert.True(o.OpenPage("dualsense"));
        o.Show(page, false);
        Assert.Null(o.Current);
        Assert.DoesNotContain(page, o.Pages);
        var ran = 0;
        Assert.False(Options.Step(Options.Row.Action("TEST", "TEST", () => ran++), 1)); // runs, nothing to save
        Assert.Equal(1, ran);
    }

    [Fact]
    public void Feedback_Without_Device_Is_Silent()
    {
        var pad = new GamepadState();
        Assert.Equal(-1, pad.SetLed(1, 2, 3));
        Assert.Equal(-1, pad.SendEffect(Reset()));
        var f = new DualSenseFeedback(new DualSenseSettings(), null);
        f.Update([pad], _ => default, _ => 0, 1 / 60f);
        Assert.False(f.Connected); // not a DualSense
        f.Test("SPEAKER"); // no speaker: nothing happens
    }
}
